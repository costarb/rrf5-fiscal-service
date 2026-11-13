namespace Rrf5.Fiscal.Nfce;

/// <summary>A QR Code payload found in <c>infNFeSupl/qrCode</c>.</summary>
public sealed record NfceQrPayload(int Version, string AccessKey, string Environment);

/// <summary>
/// QR Code of the NFC-e, "Manual de Padrões DANFE NFC-e / QR-Code versões 2.00 e 3.00",
/// v6.0, março/2025 (catalog id <c>danfe-nfce-qr</c>). Only v3 online is produced and
/// accepted (ADR-016 item 8): it carries no CSC. v2 (CSC hash) and the offline layouts
/// are refused until a decision enables them; offline contingency is out (ADR-010).
/// </summary>
public static class NfceQrCode
{
    /// <summary>§4.4.1, v3 online: <c>&lt;url&gt;?p=&lt;chave&gt;|3|&lt;tpAmb&gt;</c>.</summary>
    public static string V3Online(string baseUrl, string accessKey, string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        RequireKey(accessKey);
        RequireEnvironment(environment);
        return $"{baseUrl}?p={accessKey}|3|{environment}";
    }

    /// <summary>Reads a v3 online payload that starts with <paramref name="expectedBaseUrl"/>; anything else is refused.</summary>
    public static NfceQrPayload ParseV3Online(string qrCode, string expectedBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(qrCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBaseUrl);
        var prefix = expectedBaseUrl + "?p=";
        if (!qrCode.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("qrCode does not start with the catalogued QR Code URL of the authorizer.");
        }

        var parameters = qrCode[prefix.Length..].Split('|');
        if (parameters.Length != 3 || parameters[1] != "3")
        {
            throw new InvalidDataException("qrCode is not the v3 online layout (§4.4.1).");
        }

        RequireKey(parameters[0]);
        RequireEnvironment(parameters[2]);
        return new NfceQrPayload(3, parameters[0], parameters[2]);
    }

    private static void RequireKey(string key)
    {
        // NT Conjunta 2025.001: positions 7 to 18 carry the CNPJ, which may be alphanumeric.
        if (key is not { Length: 44 } || !key.Select((c, i) => char.IsAsciiDigit(c) || (i is >= 6 and < 18 && char.IsAsciiLetterUpper(c))).All(ok => ok))
        {
            throw new InvalidDataException("The access key must have 44 characters as NT Conjunta 2025.001 defines.");
        }
    }

    private static void RequireEnvironment(string environment)
    {
        if (environment is not ("1" or "2"))
        {
            throw new InvalidDataException("tpAmb must be 1 or 2.");
        }
    }
}
