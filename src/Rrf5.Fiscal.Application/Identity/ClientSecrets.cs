using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Rrf5.Fiscal.Application.Identity;

/// <summary>PBKDF2 output stored for an application secret (ADR-013 item 2).</summary>
public sealed record SecretHash(byte[] Hash, byte[] Salt, int Iterations);

/// <summary>
/// Client identifiers and secrets. A secret is 32 random bytes, shown once with the
/// <c>rrf5s_</c> prefix (so secret scanners can recognise it), and stored only as
/// PBKDF2-SHA256 with a per-application salt and 600 000 iterations.
/// </summary>
public static class ClientSecrets
{
    public const int Iterations = 600_000;
    public const string SecretPrefix = "rrf5s_";

    private const int HashBytes = 32;
    private const int SaltBytes = 16;

    // Used to spend the same PBKDF2 time when the client_id is unknown.
    private static readonly SecretHash Decoy = Hash("decoy-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

    public static string NewClientId() => "app_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static string NewSecret() => SecretPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static SecretHash Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new SecretHash(Derive(secret, salt, Iterations), salt, Iterations);
    }

    /// <summary>Constant-time check; pass <c>null</c> for an unknown client to burn the same time.</summary>
    public static bool Verify(string? presented, SecretHash? stored)
    {
        var target = stored ?? Decoy;
        var derived = Derive(presented ?? string.Empty, target.Salt, target.Iterations);
        return CryptographicOperations.FixedTimeEquals(derived, target.Hash) && stored is not null;
    }

    private static byte[] Derive(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
