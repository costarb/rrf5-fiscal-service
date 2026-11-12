using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Infrastructure.Persistence;
using Rrf5.Fiscal.Infrastructure.Vault;

namespace Rrf5.Fiscal.Infrastructure;

public static class ServiceRegistration
{
    /// <summary>
    /// Runtime data source (<c>ConnectionStrings:App</c>, the rrf5_app login), the unit
    /// of work, the vault signer and the F1 services. Nothing connects at startup:
    /// a missing setting surfaces as <see cref="ErrorCodes.Unavailable"/> on first use.
    /// </summary>
    public static IServiceCollection AddRrf5Foundation(this IServiceCollection services, IConfiguration configuration, string connectionName = "App")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString(connectionName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new DomainException(ErrorCodes.Unavailable, "The database is not configured.");
            }

            return NpgsqlDataSource.Create(PostgresConnectionStrings.WithServiceDefaults(connectionString));
        });
        services.AddSingleton<IUnitOfWorkFactory, PostgresUnitOfWorkFactory>();

        var openBao = new OpenBaoOptions();
        configuration.GetSection(OpenBaoOptions.Section).Bind(openBao);
        services.AddSingleton(openBao);
        services.AddSingleton(sp => new OpenBaoTokenSource(sp.GetRequiredService<OpenBaoOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new OpenBaoTransitSigner(
            VaultClient(sp), sp.GetRequiredService<OpenBaoOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITokenSigner>(sp => sp.GetRequiredService<OpenBaoTransitSigner>());
        services.AddSingleton(sp => new OpenBaoEnvelopeVault(VaultClient(sp), sp.GetRequiredService<OpenBaoOptions>()));
        services.AddSingleton<Application.Certificates.ICertificateVault>(sp => sp.GetRequiredService<OpenBaoEnvelopeVault>());
        services.AddSingleton<Application.Certificates.ISecretStore>(sp => sp.GetRequiredService<OpenBaoEnvelopeVault>());

        services.AddSingleton(new TokenSettings(
            configuration["Auth:Issuer"] ?? "rrf5-fiscal",
            configuration["Auth:Audience"] ?? "rrf5-fiscal-api",
            TokenSettings.DefaultLifetime));
        services.AddSingleton<TokenIssuer>();
        services.AddSingleton<IdentityAdministration>();
        services.AddSingleton<Application.Issuer.IssuerAdministration>();
        services.AddSingleton<Application.Issuer.NumberAllocation>();
        services.AddSingleton<Application.Certificates.CertificateAdministration>();
        services.AddSingleton<Application.Certificates.CertificateExpiryAlerts>();
        services.AddSingleton<Application.TaxRules.TaxRuleAdministration>();

        services.AddSingleton(_ => Regulatory.RegulatoryCatalog.LoadEmbedded());
        services.AddSingleton(_ => Regulatory.PaymentMethods.LoadEmbedded());
        services.AddSingleton<Application.Documents.PaymentMappingAdministration>();
        services.AddSingleton<Application.Documents.DocumentPrevalidation>();
        var storage = new Storage.ObjectStorageOptions();
        configuration.GetSection(Storage.ObjectStorageOptions.Section).Bind(storage);
        services.AddSingleton(storage);
        services.AddSingleton<Storage.S3ArtifactStore>();
        services.AddSingleton<Application.Regulatory.IArtifactStore>(sp => sp.GetRequiredService<Storage.S3ArtifactStore>());
        services.AddSingleton<Application.Regulatory.CatalogLoader>();
        services.AddSingleton<Application.Regulatory.SchemaSelectionService>();
        services.AddSingleton<Application.Regulatory.ArtifactService>();

        var certificates = new Application.Certificates.CertificateOptions();
        configuration.GetSection(Application.Certificates.CertificateOptions.Section).Bind(certificates);
        services.AddSingleton(certificates);
        services.AddSingleton<Application.Certificates.ICertificateTrustProvider, Application.Certificates.IcpBrasilTrustProvider>();
        return services;
    }

    private static HttpClient VaultClient(IServiceProvider services) =>
        new(new OpenBaoAuthHandler(services.GetRequiredService<OpenBaoTokenSource>()) { InnerHandler = new HttpClientHandler() })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
}
