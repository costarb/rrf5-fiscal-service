using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Rrf5.Fiscal.Application.Errors;

namespace Rrf5.Fiscal.Infrastructure.Vault;

/// <summary>
/// The OpenBao token of this process. With <see cref="OpenBaoOptions.RoleId"/> and
/// <see cref="OpenBaoOptions.SecretId"/> it logs in through AppRole (one role per process,
/// least-privilege policy in <c>deploy/compose/openbao/policies</c>) and logs in again
/// when two thirds of the token lifetime have passed or the vault refuses the token.
/// Otherwise it uses the static <see cref="OpenBaoOptions.Token"/> (test stack only).
/// Without either, every call fails closed with <see cref="ErrorCodes.Unavailable"/>.
/// </summary>
public sealed class OpenBaoTokenSource : IDisposable
{
    private readonly OpenBaoOptions _options;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Token, DateTimeOffset RefreshAt)? _current;

    public OpenBaoTokenSource(OpenBaoOptions options, TimeProvider clock, HttpMessageHandler? transport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _clock = clock;
        _http = transport is null ? new HttpClient() : new HttpClient(transport, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public bool UsesAppRole => !string.IsNullOrEmpty(_options.RoleId);

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (!UsesAppRole)
        {
            return string.IsNullOrEmpty(_options.Token)
                ? throw new DomainException(ErrorCodes.Unavailable, "The vault is not configured.")
                : _options.Token;
        }

        if (_current is { } valid && _clock.GetUtcNow() < valid.RefreshAt)
        {
            return valid.Token;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is { } fresh && _clock.GetUtcNow() < fresh.RefreshAt)
            {
                return fresh.Token;
            }

            _current = await LoginAsync(cancellationToken).ConfigureAwait(false);
            return _current.Value.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops a token the vault refused, so the next call logs in again.</summary>
    public void Invalidate(string token)
    {
        if (_current is { } current && current.Token == token)
        {
            _current = null;
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _http.Dispose();
    }

    private async Task<(string, DateTimeOffset)> LoginAsync(CancellationToken cancellationToken)
    {
        if (_options.Address is null || string.IsNullOrEmpty(_options.SecretId))
        {
            throw new DomainException(ErrorCodes.Unavailable, "The vault is not configured.");
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(
                new Uri(_options.Address, $"v1/auth/{_options.AppRoleMount}/login"),
                new { role_id = _options.RoleId, secret_id = _options.SecretId },
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new DomainException(ErrorCodes.Unavailable, "The vault refused the AppRole login.");
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var auth = document.RootElement.GetProperty("auth");
            var token = auth.GetProperty("client_token").GetString();
            var lifetime = TimeSpan.FromSeconds(auth.GetProperty("lease_duration").GetInt64());
            if (string.IsNullOrEmpty(token) || lifetime <= TimeSpan.Zero)
            {
                throw new DomainException(ErrorCodes.Unavailable, "The vault returned an unusable token.");
            }

            return (token, _clock.GetUtcNow() + (lifetime * 2 / 3));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DomainException(ErrorCodes.Unavailable, "The vault is unavailable.", exception);
        }
    }
}

/// <summary>Adds the process token to every OpenBao call; one new login and retry when an AppRole token is refused.</summary>
public sealed class OpenBaoAuthHandler(OpenBaoTokenSource tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await tokens.GetAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Remove("X-Vault-Token");
        request.Headers.Add("X-Vault-Token", token);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Forbidden || !tokens.UsesAppRole)
        {
            return response;
        }

        // A permission denial with a fresh token is final; an expired or revoked token is retried once.
        tokens.Invalidate(token);
        var retryToken = await tokens.GetAsync(cancellationToken).ConfigureAwait(false);
        if (retryToken == token)
        {
            return response;
        }

        response.Dispose();
        request.Headers.Remove("X-Vault-Token");
        request.Headers.Add("X-Vault-Token", retryToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
