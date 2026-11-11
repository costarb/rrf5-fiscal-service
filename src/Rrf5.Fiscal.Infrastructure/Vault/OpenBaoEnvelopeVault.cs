using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Application.Errors;

namespace Rrf5.Fiscal.Infrastructure.Vault;

/// <summary>
/// Envelope encryption over OpenBao (T022, FF-006):
/// <list type="number">
/// <item>a fresh 256-bit data key per secret from <c>transit/datakey/plaintext</c>, wrapped by the
/// non-exportable transit key <c>rrf5-envelope</c>;</item>
/// <item>AES-256-GCM in process, with associated data binding the ciphertext to its tenant,
/// kind and id; the plaintext data key is zeroed right after use;</item>
/// <item>only the wrapped key, nonce, ciphertext and tag go to the KV v2 engine.</item>
/// </list>
/// Opening reverses it through <c>transit/decrypt</c>. Any vault failure is
/// <see cref="ErrorCodes.Unavailable"/>: callers fail closed and store nothing.
/// </summary>
public sealed class OpenBaoEnvelopeVault(HttpClient http, OpenBaoOptions options) : ICertificateVault, ISecretStore
{
    private const string CertificateKind = "certificates";
    private const string CscKind = "csc";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public async Task<string> StoreCertificateAsync(Guid tenantId, Guid certificateId, A1Material material, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(material);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["pkcs12"] = Convert.ToBase64String(material.Pkcs12),
            ["password"] = material.Password,
        });
        try
        {
            return await SealAsync(tenantId, CertificateKind, certificateId, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async Task<A1Material> OpenCertificateAsync(Guid tenantId, Guid certificateId, string vaultRef, CancellationToken cancellationToken)
    {
        var plaintext = await OpenAsync(tenantId, CertificateKind, certificateId, vaultRef, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            return new A1Material(
                Convert.FromBase64String(document.RootElement.GetProperty("pkcs12").GetString()!),
                document.RootElement.GetProperty("password").GetString()!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<string> StoreCscAsync(Guid tenantId, Guid secretId, string csc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(csc);
        var payload = Encoding.UTF8.GetBytes(csc);
        try
        {
            return await SealAsync(tenantId, CscKind, secretId, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async Task<string> ReadCscAsync(Guid tenantId, Guid secretId, string vaultRef, CancellationToken cancellationToken)
    {
        var plaintext = await OpenAsync(tenantId, CscKind, secretId, vaultRef, cancellationToken).ConfigureAwait(false);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task DeleteAsync(string vaultRef, CancellationToken cancellationToken)
    {
        var path = PathFrom(vaultRef);
        using var request = Request(HttpMethod.Delete, $"v1/{options.KvMount}/metadata/{path}");
        using var _ = await SendAsync(request, allowNotFound: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The KV path a reference points to (for tests that inspect what is stored).</summary>
    public static string PathFrom(string vaultRef)
    {
        const string prefix = "openbao-kv:";
        if (vaultRef is null || !vaultRef.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Not a vault reference of this store.", nameof(vaultRef));
        }

        return vaultRef[prefix.Length..];
    }

    private static string AssociatedData(Guid tenantId, string kind, Guid id) => $"rrf5|{tenantId:D}|{kind}|{id:D}";

    private static string Path(Guid tenantId, string kind, Guid id) => $"rrf5/{tenantId:D}/{kind}/{id:D}";

    private async Task<string> SealAsync(Guid tenantId, string kind, Guid id, byte[] plaintext, CancellationToken cancellationToken)
    {
        using var keyRequest = Request(HttpMethod.Post, $"v1/transit/datakey/plaintext/{options.EnvelopeKey}");
        keyRequest.Content = JsonContent.Create(new { bits = 256 });
        using var keyDocument = await SendAsync(keyRequest, allowNotFound: false, cancellationToken).ConfigureAwait(false);
        var data = keyDocument!.RootElement.GetProperty("data");
        var wrapped = data.GetProperty("ciphertext").GetString()!;
        var dataKey = Convert.FromBase64String(data.GetProperty("plaintext").GetString()!);

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(dataKey, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(AssociatedData(tenantId, kind, id)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }

        var path = Path(tenantId, kind, id);
        using var write = Request(HttpMethod.Post, $"v1/{options.KvMount}/data/{path}");
        write.Content = JsonContent.Create(new
        {
            options = new { cas = 0 },
            data = new
            {
                alg = "AES-256-GCM",
                wrappedKey = wrapped,
                nonce = Convert.ToBase64String(nonce),
                ciphertext = Convert.ToBase64String(ciphertext),
                tag = Convert.ToBase64String(tag),
            },
        });
        using var _ = await SendAsync(write, allowNotFound: false, cancellationToken).ConfigureAwait(false);
        return "openbao-kv:" + path;
    }

    private async Task<byte[]> OpenAsync(Guid tenantId, string kind, Guid id, string vaultRef, CancellationToken cancellationToken)
    {
        var path = PathFrom(vaultRef);
        if (path != Path(tenantId, kind, id))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }

        using var read = Request(HttpMethod.Get, $"v1/{options.KvMount}/data/{path}");
        using var document = await SendAsync(read, allowNotFound: true, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.NotFound);
        var stored = document.RootElement.GetProperty("data").GetProperty("data");

        using var unwrap = Request(HttpMethod.Post, $"v1/transit/decrypt/{options.EnvelopeKey}");
        unwrap.Content = JsonContent.Create(new { ciphertext = stored.GetProperty("wrappedKey").GetString() });
        using var unwrapped = await SendAsync(unwrap, allowNotFound: false, cancellationToken).ConfigureAwait(false);
        var dataKey = Convert.FromBase64String(unwrapped!.RootElement.GetProperty("data").GetProperty("plaintext").GetString()!);

        var ciphertext = Convert.FromBase64String(stored.GetProperty("ciphertext").GetString()!);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(dataKey, TagBytes);
            aes.Decrypt(
                Convert.FromBase64String(stored.GetProperty("nonce").GetString()!),
                ciphertext,
                Convert.FromBase64String(stored.GetProperty("tag").GetString()!),
                plaintext,
                Encoding.UTF8.GetBytes(AssociatedData(tenantId, kind, id)));
            return plaintext;
        }
        catch (AuthenticationTagMismatchException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new DomainException(ErrorCodes.Unavailable, "The vault entry failed integrity verification.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (options.Address is null || string.IsNullOrEmpty(options.Token))
        {
            throw new DomainException(ErrorCodes.Unavailable, "The vault is not configured.");
        }

        var request = new HttpRequestMessage(method, new Uri(options.Address, path));
        request.Headers.Add("X-Vault-Token", options.Token);
        return request;
    }

    private async Task<JsonDocument?> SendAsync(HttpRequestMessage request, bool allowNotFound, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DomainException(ErrorCodes.Unavailable, "The vault refused the request.");
            }

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new DomainException(ErrorCodes.Unavailable, "The vault is unavailable.", exception);
        }
    }
}
