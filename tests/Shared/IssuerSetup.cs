using System.Net;

namespace Rrf5.Fiscal.Tests.Stack;

/// <summary>Synthetic issuers built through the API. CNPJs are random and synthetic, never a real taxpayer.</summary>
public static class IssuerSetup
{
    private const string Alphabet = "0123456789ABCDEGHJKLMNPRSTVWXYZ";

    /// <summary>A random alphanumeric CNPJ root (8 characters) for a new synthetic company.</summary>
    public static string NewRoot() => new(Enumerable.Range(0, 8).Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)]).ToArray());

    /// <summary>Valid synthetic CNPJ for <paramref name="root"/> and branch <paramref name="branch"/> (4 characters).</summary>
    public static string Cnpj(string root, string branch = "0001")
    {
        var prefix = root + branch;
        var first = global::Rrf5.Fiscal.Domain.Issuer.Cnpj.CheckDigit(prefix);
        var second = global::Rrf5.Fiscal.Domain.Issuer.Cnpj.CheckDigit(prefix + first);
        return prefix + first + second;
    }

    public static object Establishment(string taxId, string uf = "SP", string municipality = "3550308", int crt = 1) => new
    {
        taxId,
        stateRegistration = "ISENTO",
        uf,
        municipalityCode = municipality,
        crt,
        address = new { street = "Rua Sintetica", number = "100", district = "Centro", municipalityName = "Sao Paulo", postalCode = "01001-000" },
    };

    /// <summary>Company, establishment and homologation environment; returns their ids.</summary>
    public static async Task<(Guid Company, Guid Establishment, Guid Environment)> HomologationIssuerAsync(HttpClient client, string token, string? root = null)
    {
        root ??= NewRoot();
        var (company, companyBody) = await ApiClient.SendAsync(client, HttpMethod.Post, "/v1/companies", token,
            new { legalName = "Empresa Sintetica", taxId = Cnpj(root) }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        var companyId = companyBody.GetProperty("id").GetGuid();

        var (establishment, establishmentBody) = await ApiClient.SendAsync(client, HttpMethod.Post, $"/v1/companies/{companyId}/establishments", token,
            Establishment(Cnpj(root)), ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, establishment.StatusCode);
        var establishmentId = establishmentBody.GetProperty("id").GetGuid();

        var (environment, environmentBody) = await ApiClient.SendAsync(client, HttpMethod.Post, $"/v1/establishments/{establishmentId}/environments", token,
            new { environment = "HOMOLOGATION" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, environment.StatusCode);
        return (companyId, establishmentId, environmentBody.GetProperty("id").GetGuid());
    }
}
