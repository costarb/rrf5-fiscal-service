using System.IO.Compression;
using System.Xml;
using System.Xml.Schema;

namespace Rrf5.Fiscal.Nfce;

/// <summary>
/// XSD validation against one official schema package (005 T014, ADR-016 item 6, TM-09).
/// <list type="bullet">
/// <item>Schemas come only from the package ZIP the caller verified by hash (ArtifactService).</item>
/// <item>Includes and imports resolve inside that ZIP; nothing outside it is reachable.</item>
/// <item>Documents are parsed with DTDs prohibited, no resolver and a size limit, so external
/// entities, entity expansion and oversized inputs are refused before validation.</item>
/// </list>
/// </summary>
public sealed class NfceSchema
{
    /// <summary>Generous upper bound for an NFC-e (990 items) in characters.</summary>
    public const long MaxDocumentCharacters = 5_000_000;

    private const string Namespace = "http://www.portalfiscal.inf.br/nfe";
    private readonly XmlSchemaSet _schemas;

    private NfceSchema(XmlSchemaSet schemas) => _schemas = schemas;

    /// <summary>Loads <c>nfe_v4.00.xsd</c> and its dependencies from the verified package ZIP.</summary>
    public static NfceSchema FromPackage(byte[] packageZip)
    {
        ArgumentNullException.ThrowIfNull(packageZip);
        var resolver = new PackageResolver(packageZip);
        var schemas = new XmlSchemaSet { XmlResolver = resolver };
        using (var reader = XmlReader.Create("package:///nfe_v4.00.xsd", new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = resolver,
        }))
        {
            schemas.Add(Namespace, reader);
        }

        schemas.Compile();
        return new NfceSchema(schemas);
    }

    /// <summary>Hardened reader settings for any NF-e XML this service parses.</summary>
    public static XmlReaderSettings SafeReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxDocumentCharacters,
        MaxCharactersFromEntities = 0,
        IgnoreProcessingInstructions = true,
        IgnoreComments = true,
    };

    /// <summary>Schema errors of <paramref name="xml"/>; empty when valid. Unsafe input throws <see cref="XmlException"/>.</summary>
    public IReadOnlyList<string> Validate(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var errors = new List<string>();
        var settings = SafeReaderSettings();
        settings.ValidationType = ValidationType.Schema;
        settings.Schemas = _schemas;
        settings.ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings;
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read())
        {
        }

        return errors;
    }

    private sealed class PackageResolver(byte[] zip) : XmlResolver
    {
        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            ArgumentNullException.ThrowIfNull(absoluteUri);
            if (absoluteUri.Scheme != "package")
            {
                throw new XmlException("Only schemas inside the verified package can be loaded.");
            }

            var name = Path.GetFileName(absoluteUri.AbsolutePath);
            using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/NFe/" + name, StringComparison.Ordinal))
                ?? throw new XmlException("A schema is not in the verified package.");
            var buffer = new MemoryStream();
            using (var source = entry.Open())
            {
                source.CopyTo(buffer);
            }

            buffer.Position = 0;
            return buffer;
        }

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri) =>
            new("package:///" + Path.GetFileName(relativeUri ?? string.Empty));
    }
}
