using System.Buffers.Text;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Infrastructure.Vault;

/// <summary>OpenBao connection settings. The token comes from the environment, never from the repository.</summary>
public sealed class OpenBaoOptions
{
    public const string Section = "OpenBao";

    public Uri? Address { get; set; }

    public string? Token { get; set; }

    public string TokenSigningKey { get; set; } = "rrf5-token-signing";

    /// <summary>Transit key (aes256-gcm96) that wraps the per-secret data keys.</summary>
    public string EnvelopeKey { get; set; } = "rrf5-envelope";

    /// <summary>KV v2 mount holding the envelopes.</summary>
    public string KvMount { get; set; } = "secret";
}

/// <summary>A public verification key published in the JWKS.</summary>
public sealed record SigningPublicKey(string KeyId, ECParameters Parameters);

/// <summary>
/// Token signing with an OpenBao transit <c>ecdsa-p256</c> key (ADR-013 item 3):
/// the private key never leaves the vault. Public keys are read from the key's
/// metadata and cached for five minutes; an unknown key id forces a refresh, so
/// rotation in the vault is picked up without restart. Any vault failure fails
/// closed with <see cref="ErrorCodes.Unavailable"/>.
/// </summary>
public sealed class OpenBaoTransitSigner : ITokenSigner, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly OpenBaoOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private KeySnapshot? _snapshot;

    public OpenBaoTransitSigner(HttpClient http, OpenBaoOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
        _clock = clock;
    }

    public async Task<string> CurrentKeyIdAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        return KeyId(snapshot.LatestVersion);
    }

    public async Task<byte[]> SignAsync(string keyId, byte[] signingInput, CancellationToken cancellationToken)
    {
        var version = ParseVersion(keyId);
        var body = new Dictionary<string, object>
        {
            ["input"] = Convert.ToBase64String(signingInput),
            ["key_version"] = version,
            ["marshaling_algorithm"] = "jws",
        };

        using var request = Request(HttpMethod.Post, $"v1/transit/sign/{_options.TokenSigningKey}/sha2-256");
        request.Content = JsonContent.Create(body);
        using var document = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var signature = document.RootElement.GetProperty("data").GetProperty("signature").GetString() ?? string.Empty;

        // Format: vault:v<version>:<base64url r||s>
        var parts = signature.Split(':');
        if (parts.Length != 3 || parts[1] != "v" + version.ToString(CultureInfo.InvariantCulture))
        {
            throw new DomainException(ErrorCodes.Unavailable, "The vault returned an unexpected signature.");
        }

        var raw = Base64Url.DecodeFromChars(parts[2]);
        if (raw.Length != 64)
        {
            throw new DomainException(ErrorCodes.Unavailable, "The vault returned an unexpected signature.");
        }

        return raw;
    }

    /// <summary>Public keys of every version still in the vault, for the JWKS and for validation.</summary>
    public async Task<IReadOnlyList<SigningPublicKey>> GetPublicKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        return snapshot.Keys;
    }

    public void Dispose() => _refresh.Dispose();

    private async Task<KeySnapshot> GetSnapshotAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var current = _snapshot;
        if (!forceRefresh && current is not null && _clock.GetUtcNow() - current.LoadedAt < CacheLifetime)
        {
            return current;
        }

        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (_snapshot is { } fresh && fresh != current && _clock.GetUtcNow() - fresh.LoadedAt < CacheLifetime)
            {
                return fresh;
            }

            using var request = Request(HttpMethod.Get, $"v1/transit/keys/{_options.TokenSigningKey}");
            using var document = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var data = document.RootElement.GetProperty("data");
            if (data.GetProperty("type").GetString() != "ecdsa-p256")
            {
                throw new DomainException(ErrorCodes.Unavailable, "The token signing key is not ecdsa-p256.");
            }

            var keys = new List<SigningPublicKey>();
            foreach (var version in data.GetProperty("keys").EnumerateObject())
            {
                var pem = version.Value.GetProperty("public_key").GetString() ?? string.Empty;
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(pem);
                keys.Add(new SigningPublicKey(KeyId(int.Parse(version.Name, CultureInfo.InvariantCulture)), ecdsa.ExportParameters(false)));
            }

            _snapshot = new KeySnapshot(data.GetProperty("latest_version").GetInt32(), keys, _clock.GetUtcNow());
            return _snapshot;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (_options.Address is null || string.IsNullOrEmpty(_options.Token))
        {
            throw new DomainException(ErrorCodes.Unavailable, "The vault is not configured.");
        }

        var request = new HttpRequestMessage(method, new Uri(_options.Address, path));
        request.Headers.Add("X-Vault-Token", _options.Token);
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new DomainException(ErrorCodes.Unavailable, "The vault refused the request.");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new DomainException(ErrorCodes.Unavailable, "The vault is unavailable.", exception);
        }
    }

    private string KeyId(int version) => $"{_options.TokenSigningKey}-v{version.ToString(CultureInfo.InvariantCulture)}";

    private int ParseVersion(string keyId)
    {
        var prefix = _options.TokenSigningKey + "-v";
        if (keyId is null || !keyId.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(keyId.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new ArgumentException("Unknown signing key id.", nameof(keyId));
        }

        return version;
    }

    private sealed record KeySnapshot(int LatestVersion, IReadOnlyList<SigningPublicKey> Keys, DateTimeOffset LoadedAt);
}
