using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>
/// The official ITI bundle, when it is on this machine (kept outside Git): the hash
/// matches the catalog, the six current roots are found, and a real ICP-Brasil
/// intermediate AC builds a chain to one of them with the production code path.
/// </summary>
public sealed class IcpBrasilBundleTests
{
    private static readonly string BundlePath = Environment.GetEnvironmentVariable("RRF5_ICP_BUNDLE")
        ?? @"C:\Users\rob_b\rrf5-private\icp-brasil\ACcompactado.zip";

    [Fact]
    public void OfficialBundleMatchesTheCatalogAndChainsToIcpBrasilRoots()
    {
        if (!File.Exists(BundlePath))
        {
            return;
        }

        var bundle = File.ReadAllBytes(BundlePath);
        var expected = RegulatoryCatalog.LoadEmbedded().ById[IcpBrasilTrustProvider.CatalogReferenceId].ArtifactSha256;
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(bundle)));

        var trust = CertificateTrust.FromIcpBrasilBundle(bundle, X509RevocationMode.NoCheck);
        Assert.Equal(6, trust.Roots.Count);
        Assert.All(trust.Roots, r => Assert.StartsWith("Autoridade Certificadora Raiz Brasileira", r.GetNameInfo(X509NameType.SimpleName, false), StringComparison.Ordinal));
        Assert.Equal(176, trust.Intermediates.Count);

        // An intermediate issued directly by a current root, still valid today.
        var intermediate = trust.Intermediates.First(i =>
            i.NotAfter > DateTime.UtcNow && i.NotBefore < DateTime.UtcNow
            && trust.Roots.Any(r => r.SubjectName.RawData.AsSpan().SequenceEqual(i.IssuerName.RawData)));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trust.Roots.ToArray());
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(intermediate));
        Assert.Contains(trust.Roots, r => r.RawData.AsSpan().SequenceEqual(chain.ChainElements[^1].Certificate.RawData));
    }
}
