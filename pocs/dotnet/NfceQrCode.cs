using System.Security.Cryptography;
using System.Text;

// T020: QR Code payload rules from "Manual de Padrões DANFE NFC-e / QR-Code
// versões 2.00 e 3.00", v6.0, março/2025 (catalog id `danfe-nfce-qr`, SHA-256
// bf906cc2…5db5e). Section numbers below refer to that manual.
static class NfceQrCode
{
    // §4.4.1 (v3 online): <url>?p=<chave>|3|<tpAmb>. No CSC (§4.4, Observação 1).
    public static string V3Online(string baseUrl, string accessKey, string tpAmb)
    {
        RequireKey(accessKey);
        RequireTpAmb(tpAmb);
        return $"{baseUrl}?p={accessKey}|3|{tpAmb}";
    }

    // §4.4.2 (v3 offline contingency): parameters 1..7 joined by "|" (empty
    // recipient fields keep only the separator), then "|" + Base64 RSA-SHA1
    // signature of that string with the same certificate that signs the NFC-e.
    // Built here only to answer the CSC question in T020; offline contingency is
    // not enabled (pocs/evidence/contingency-gates.md).
    public static string V3OfflineSignedText(string accessKey, string tpAmb, int emissionDay, string vNF, string? recipientType, string? recipientId)
    {
        RequireKey(accessKey);
        RequireTpAmb(tpAmb);
        if (emissionDay is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(emissionDay));
        RequireDotDecimal(vNF);
        if (recipientType is not (null or "1" or "2" or "3")) throw new ArgumentException("tp_idDest must be 1, 2, 3 or absent.");
        // Foreign recipient (3) or unidentified: identification is only the separator.
        var id = recipientType is "1" or "2" ? recipientId ?? throw new ArgumentException("idDest required for CNPJ/CPF.") : "";
        return $"{accessKey}|3|{tpAmb}|{emissionDay:00}|{vNF}|{recipientType}|{id}";
    }

    public static string V3Offline(string baseUrl, string signedText, RSA key)
    {
        var signature = key.SignData(Encoding.UTF8.GetBytes(signedText), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        return $"{baseUrl}?p={signedText}|{Convert.ToBase64String(signature)}";
    }

    // §4.3.4 (v2 online): SHA-1 hex (upper) of "<chave>|2|<tpAmb>|<idCSC>" + CSC.
    public static string V2OnlineHash(string accessKey, string tpAmb, string cscId, string csc) =>
        Sha1Hex($"{accessKey}|2|{tpAmb}|{TrimCscId(cscId)}{csc}");

    // §4.3.5 (v2 offline): digVal "converted to HEXA" is the hex of the Base64
    // text itself (manual example §4.3.6.2), not of the decoded digest bytes.
    public static string V2OfflineDigestHex(string digestValueBase64) =>
        Convert.ToHexString(Encoding.ASCII.GetBytes(digestValueBase64)).ToLowerInvariant();

    public static string V2OfflineHash(string accessKey, string tpAmb, string day, string vNF, string digestValueBase64, string cscId, string csc) =>
        Sha1Hex($"{accessKey}|2|{tpAmb}|{day}|{vNF}|{V2OfflineDigestHex(digestValueBase64)}|{TrimCscId(cscId)}{csc}");

    public sealed record Parsed(int Version, string AccessKey, string TpAmb, bool Offline);

    // Classifies a qrCode found in infNFeSupl against the expected key/environment.
    // v2 hashes cannot be checked without the CSC; only the shape is checked.
    public static Parsed Parse(string qrCode, string expectedBaseUrl)
    {
        var prefix = expectedBaseUrl + "?p=";
        if (!qrCode.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("qrCode does not start with the configured QR consultation URL.");
        var p = qrCode[prefix.Length..].Split('|');
        if (p.Length < 3) throw new InvalidDataException("qrCode has fewer than 3 parameters.");
        RequireKey(p[0]);
        RequireTpAmb(p[2]);
        return (p[1], p.Length) switch
        {
            ("3", 3) => new Parsed(3, p[0], p[2], false),
            ("3", 8) => new Parsed(3, p[0], p[2], true),
            ("2", 5) when p[4].Length == 40 => new Parsed(2, p[0], p[2], false),
            ("2", 8) when p[7].Length == 40 => new Parsed(2, p[0], p[2], true),
            _ => throw new InvalidDataException($"Unrecognized QR Code layout (version '{p[1]}', {p.Length} parameters).")
        };
    }

    static string TrimCscId(string cscId)
    {
        // §4.3.1: identifier without non-significant zeros, 1 to 6 digits.
        var trimmed = cscId.TrimStart('0');
        if (trimmed.Length is 0 or > 6 || !trimmed.All(char.IsAsciiDigit)) throw new ArgumentException("Invalid CSC identifier.");
        return trimmed;
    }

    static string Sha1Hex(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)));

    static void RequireKey(string key)
    {
        if (key.Length != 44 || !key.All(char.IsAsciiDigit)) throw new InvalidDataException("Access key must be 44 digits.");
    }

    static void RequireTpAmb(string tpAmb)
    {
        if (tpAmb is not ("1" or "2")) throw new InvalidDataException("tpAmb must be 1 or 2.");
    }

    static void RequireDotDecimal(string value)
    {
        if (value.Length is 0 or > 15 || !value.All(c => char.IsAsciiDigit(c) || c == '.') || value.Count(c => c == '.') > 1)
            throw new ArgumentException("vNF must use '.' as decimal separator, without thousands separator or sign.");
    }
}
