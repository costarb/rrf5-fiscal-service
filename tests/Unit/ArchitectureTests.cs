using System.Reflection;
using System.Xml.Linq;

namespace Rrf5.Fiscal.Tests.Unit;

public sealed class ArchitectureTests
{
    [Fact]
    public void DomainReferencesNoOtherProductAssembly()
    {
        var domain = typeof(Rrf5.Fiscal.Domain.AssemblyMarker).Assembly;
        var product = domain.GetReferencedAssemblies()
            .Where(name => name.Name!.StartsWith("Rrf5.", StringComparison.Ordinal))
            .Select(name => name.Name)
            .ToList();

        Assert.Empty(product);
    }

    [Fact]
    public void NoProductProjectReferencesThePocs()
    {
        var root = RepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(project => XDocument.Load(project).Descendants("ProjectReference")
                .Any(reference => (reference.Attribute("Include")?.Value ?? string.Empty)
                    .Replace('\\', '/').Contains("pocs/", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rrf5.Fiscal.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
