using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Tests.Contract;

/// <summary>
/// T036: the OpenAPI contract and the running API describe the same surface — every
/// route and method, the closed error-code and scope catalogs, and the closed document
/// state enum of the database (L10).
/// </summary>
public sealed partial class ContractTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string Contract = File.ReadAllText(Path.Combine(Root(), "specs", "002-fiscal-foundation", "contracts", "openapi.yaml")).Replace("\r\n", "\n", StringComparison.Ordinal);

    [GeneratedRegex(@"^  (/[^\s:]*(?:\{[^}]+\}[^\s:]*)*):\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PathLine();

    [GeneratedRegex(@"^    (get|post|put|delete|patch):\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MethodLine();

    [GeneratedRegex(@":[a-z]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex RouteConstraint();

    [Fact]
    public void EveryRouteIsInTheContractAndNothingElse()
    {
        using var client = factory.CreateClient();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(m => $"{m.ToUpperInvariant()} {RouteConstraint().Replace("/" + e.RoutePattern.RawText!.TrimStart('/'), "}")}"))
            .ToHashSet(StringComparer.Ordinal);

        // Operations marked x-rrf5-planned are specified ahead of their code (005 T009) and
        // must not be routed yet; the marker goes when the endpoint is implemented.
        var documented = DocumentedOperations();
        var implemented = documented.Where(o => !o.Planned).Select(o => o.Operation).ToHashSet(StringComparer.Ordinal);
        var planned = documented.Where(o => o.Planned).Select(o => o.Operation).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(routes.Except(implemented).Order(StringComparer.Ordinal));
        Assert.Empty(implemented.Except(routes).Order(StringComparer.Ordinal));
        Assert.Empty(planned.Intersect(routes).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ErrorCodeEnumEqualsTheCatalog() =>
        Assert.Equal(ErrorCodes.All.Select(c => c.Value).Order(StringComparer.Ordinal), Enum("ErrorCode").Order(StringComparer.Ordinal));

    [Fact]
    public void ScopeEnumEqualsTheCatalog() =>
        Assert.Equal(Scopes.All.Order(StringComparer.Ordinal), Enum("Scope").Order(StringComparer.Ordinal));

    [Fact]
    public void DocumentStateEnumEqualsTheDatabaseCheck()
    {
        var migration = File.ReadAllText(Path.Combine(Root(), "db", "migrations", "0003_numbering_and_documents.sql"));
        var check = Regex.Match(migration, @"state text not null check \(state in \((?<list>[^)]*)\)\)", RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Groups["list"].Value;
        var states = Regex.Matches(check, "'([A-Z_]+)'", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Groups[1].Value);
        Assert.Equal(states.Order(StringComparer.Ordinal), Enum("DocumentState").Order(StringComparer.Ordinal));
    }

    private static List<(string Operation, bool Planned)> DocumentedOperations()
    {
        var operations = new List<(string, bool)>();
        var paths = PathLine().Matches(Contract).ToList();
        for (var i = 0; i < paths.Count; i++)
        {
            var start = paths[i].Index;
            var end = i + 1 < paths.Count ? paths[i + 1].Index : Contract.IndexOf("\ncomponents:", StringComparison.Ordinal);
            var block = Contract[start..end];
            var methods = MethodLine().Matches(block).ToList();
            for (var m = 0; m < methods.Count; m++)
            {
                var body = block[methods[m].Index..(m + 1 < methods.Count ? methods[m + 1].Index : block.Length)];
                operations.Add(($"{methods[m].Groups[1].Value.ToUpperInvariant()} {paths[i].Groups[1].Value}",
                    body.Contains("\n      x-rrf5-planned: true\n", StringComparison.Ordinal)));
            }
        }

        return operations;
    }

    private static List<string> Enum(string schema)
    {
        var start = Contract.IndexOf($"\n    {schema}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"schema {schema} not found");
        var block = Contract[start..];
        var inline = Regex.Match(block, @"enum: \[(?<items>[^\]]*)\]", RegexOptions.None, TimeSpan.FromSeconds(1));
        var list = Regex.Match(block, @"enum:\n(?<items>(?:\s+- .+\n)+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (list.Success && (!inline.Success || list.Index < inline.Index))
        {
            return list.Groups["items"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().TrimStart('-').Trim()).ToList();
        }

        return inline.Groups["items"].Value.Split(',').Select(i => i.Trim().Trim('"')).ToList();
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rrf5.Fiscal.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
