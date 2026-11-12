using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Schema;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>
/// 005 T001: the golden NFC-e is pinned by hash. It carries no element exclusive to
/// 010g (E4: SP accepts only content common to 010e-010g until the probe is accepted).
/// When the official 010g v1.03 ZIP is on this machine (kept outside Git), its hash
/// matches the catalog and the golden is schema-valid except for the missing Signature
/// (the golden is unsigned; signing is T015).
/// </summary>
public sealed class GoldenXmlTests
{
    private const string GoldenSha256 = "82e3ab6f83b85555f551d597afad93cba6a9d6557ad03b4ac34e77b255a504b1";
    private const string Nfe = "http://www.portalfiscal.inf.br/nfe";
    private const string Dsig = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly string ZipPath = Environment.GetEnvironmentVariable("RRF5_XSD_010G_ZIP")
        ?? @"C:\Users\rob_b\rrf5-private\sefaz\schemas\PL_010g_v1.03.zip";

    [Fact]
    public void GoldenIsPinnedAndCarriesNo010gExclusiveElement()
    {
        var bytes = File.ReadAllBytes(GoldenPath());
        Assert.Equal(GoldenSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        var document = Load(bytes);
        Assert.Null(document.DocumentType);
        var exclusive = PackageExclusiveElements.Of("010g");
        var found = document.SelectNodes("//*")!.Cast<XmlElement>()
            .Where(e => e.NamespaceURI == Nfe && exclusive.Contains(e.LocalName))
            .Select(e => e.LocalName)
            .ToList();
        Assert.Empty(found);
    }

    [Fact]
    public void GoldenIsSchemaValidAgainstThe010gPackageExceptTheSignature()
    {
        if (!File.Exists(ZipPath))
        {
            return;
        }

        var zip = File.ReadAllBytes(ZipPath);
        var expected = RegulatoryCatalog.LoadEmbedded().ById["xsd-nfe-010g-1-03"].ArtifactSha256;
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(zip)));

        var schemas = new XmlSchemaSet { XmlResolver = new ZipResolver(zip) };
        using (var reader = XmlReader.Create(new Uri("zip:///nfe_v4.00.xsd").ToString(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = new ZipResolver(zip),
        }))
        {
            schemas.Add(Nfe, reader);
        }

        schemas.Compile();
        var errors = new List<string>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = schemas,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using (var stream = File.OpenRead(GoldenPath()))
        using (var reader = XmlReader.Create(stream, settings))
        {
            while (reader.Read())
            {
            }
        }

        // The only violation is the missing ds:Signature after infNFe.
        var error = Assert.Single(errors);
        Assert.Contains(Dsig, error, StringComparison.Ordinal);
    }

    private static XmlDocument Load(byte[] bytes)
    {
        var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        document.Load(reader);
        return document;
    }

    private static string GoldenPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "pocs", "fixtures", "golden", "nfce-minimal-sp-sn-v1.unsigned.xml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Golden XML not found above the test binaries.");
    }

    /// <summary>Resolves xs:include/xs:import by file name inside the verified ZIP only; nothing else is reachable.</summary>
    private sealed class ZipResolver(byte[] zip) : XmlResolver
    {
        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            ArgumentNullException.ThrowIfNull(absoluteUri);
            var name = Path.GetFileName(absoluteUri.AbsolutePath);
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("NFe/" + name, StringComparison.Ordinal))
                ?? throw new XmlException($"Schema {name} is not in the verified package.");
            var buffer = new MemoryStream();
            using (var source = entry.Open())
            {
                source.CopyTo(buffer);
            }

            buffer.Position = 0;
            return buffer;
        }

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri) => new(new Uri("zip:///"), Path.GetFileName(relativeUri ?? string.Empty));
    }
}
