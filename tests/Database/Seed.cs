using Npgsql;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>A synthetic issuer graph for one tenant. Tax IDs are placeholders, not real taxpayers.</summary>
public sealed record Issuer(
    Guid TenantId,
    Guid AuthorApplicationId,
    Guid ApproverApplicationId,
    Guid CompanyId,
    Guid EstablishmentId,
    Guid HomologationId,
    Guid ProductionId);

/// <summary>Seeds rows through the owner connection (RLS does not apply to it on the local stack).</summary>
public static class Seed
{
    private static int _taxIdCounter;

    public static async Task<Issuer> IssuerAsync(NpgsqlConnection owner)
    {
        var tenant = Guid.NewGuid();
        await Sql.ExecuteAsync(owner, "insert into tenant (id, name, status) values ($1, 'Synthetic tenant', 'ACTIVE')", tenant);
        var author = await ApplicationAsync(owner, tenant, ["fiscal.rules.write"]);
        var approver = await ApplicationAsync(owner, tenant, ["fiscal.rules.approve"]);

        var taxId = NextTaxId();
        var company = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            "insert into company (id, tenant_id, legal_name, tax_id_canonical) values ($1, $2, 'Synthetic company', $3)",
            company, tenant, taxId);

        var establishment = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            """
            insert into establishment (id, tenant_id, company_id, tax_id_canonical, uf, municipality_code, crt, status)
            values ($1, $2, $3, $4, 'SP', '3550308', 1, 'ACTIVE')
            """,
            establishment, tenant, company, taxId);

        var homologation = await EnvironmentAsync(owner, tenant, establishment, "HOMOLOGATION");
        var production = await EnvironmentAsync(owner, tenant, establishment, "PRODUCTION");
        return new Issuer(tenant, author, approver, company, establishment, homologation, production);
    }

    public static async Task<Guid> ApplicationAsync(NpgsqlConnection owner, Guid tenant, string[] scopes)
    {
        var id = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            """
            insert into application (id, tenant_id, client_id, name, secret_hash, secret_salt, secret_iterations, scopes, status)
            values ($1, $2, $3, 'Synthetic app', $4, $5, 600000, $6, 'ACTIVE')
            """,
            id, tenant, $"client_{id:N}", new byte[32], new byte[16], scopes);
        return id;
    }

    public static async Task<Guid> SeriesAsync(
        NpgsqlConnection owner,
        Issuer issuer,
        Guid environmentId,
        string model = "65",
        int series = 1,
        long numberMax = 999999999,
        Guid? localAgentOwner = null)
    {
        var id = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            $"""
            insert into fiscal_series (id, tenant_id, establishment_id, environment_id, model, series,
              number_min, number_max, next_number, allocation_owner, owner_id, status)
            values ($1, $2, $3, $4, '{model}', $5, 1, $6, 1, $7::series_owner_type, $8, 'ACTIVE')
            """,
            id, issuer.TenantId, issuer.EstablishmentId, environmentId, series, numberMax,
            localAgentOwner is null ? "CLOUD" : "LOCAL_AGENT", localAgentOwner);
        return id;
    }

    private static async Task<Guid> EnvironmentAsync(NpgsqlConnection owner, Guid tenant, Guid establishment, string environment)
    {
        var id = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            $"""
            insert into fiscal_environment (id, tenant_id, establishment_id, environment, status)
            values ($1, $2, $3, '{environment}', 'ACTIVE')
            """,
            id, tenant, establishment);
        return id;
    }

    private static string NextTaxId() =>
        Interlocked.Increment(ref _taxIdCounter).ToString("D14", System.Globalization.CultureInfo.InvariantCulture);
}
