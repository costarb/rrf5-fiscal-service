using Rrf5.Fiscal.Infrastructure;
using Rrf5.Fiscal.Infrastructure.Observability;
using Rrf5.Fiscal.Worker;
using Rrf5.Fiscal.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.UseRedactingJsonLogs();
builder.Services.AddRrf5Telemetry(builder.Configuration, "rrf5-fiscal-worker");

// "migrate" applies the embedded SQL migrations with the owner connection and
// exits (ADR-014). The owner credential is only given to this one-shot command.
if (args is ["migrate", ..])
{
    var owner = builder.Configuration.GetConnectionString("Owner")
        ?? throw new InvalidOperationException("ConnectionStrings:Owner is required for migrate.");
    var applied = await SqlMigrator.MigrateAsync(owner).ConfigureAwait(false);
    Console.WriteLine(applied.Count == 0
        ? "Database is up to date."
        : $"Applied: {string.Join(", ", applied.Select(m => m.Name))}");
    return;
}

// "bootstrap-platform-admin" creates the platform tenant (once) and a platform.admin
// application, printing its credential a single time to stdout. It is the only way
// to obtain the first credential; store it in a secret manager immediately.
if (args is ["bootstrap-platform-admin", ..])
{
    var owner = builder.Configuration.GetConnectionString("Owner")
        ?? throw new InvalidOperationException("ConnectionStrings:Owner is required for bootstrap-platform-admin.");
    var credential = await PlatformBootstrap.CreatePlatformAdminAsync(owner, "Platform administrator").ConfigureAwait(false);
    Console.WriteLine($"client_id={credential.ClientId}");
    Console.WriteLine($"client_secret={credential.ClientSecret}");
    Console.WriteLine("The secret is shown only now and cannot be recovered.");
    return;
}

// "local-app-login" (local stack only) lets rrf5_app and rrf5_worker log in with RRF5_LOCAL_APP_PASSWORD.
if (args is ["local-app-login", ..])
{
    var owner = builder.Configuration.GetConnectionString("Owner")
        ?? throw new InvalidOperationException("ConnectionStrings:Owner is required for local-app-login.");
    var password = Environment.GetEnvironmentVariable("RRF5_LOCAL_APP_PASSWORD")
        ?? throw new InvalidOperationException("RRF5_LOCAL_APP_PASSWORD is required for local-app-login.");
    await PlatformBootstrap.EnableLocalLoginAsync(owner, "rrf5_app", password).ConfigureAwait(false);
    await PlatformBootstrap.EnableLocalLoginAsync(owner, "rrf5_worker", password).ConfigureAwait(false);
    Console.WriteLine("rrf5_app and rrf5_worker can log in.");
    return;
}

// "ensure-bucket" creates the artifact bucket with versioning and object lock. It is an
// operator step run once with the storage admin identity (scripts/local/hardened-stack.ps1):
// the runtime identities of the API and the worker cannot create buckets.
if (args is ["ensure-bucket", ..])
{
    builder.Services.AddRrf5Foundation(builder.Configuration, "Worker");
    using var tools = builder.Build();
    var locked = await tools.Services.GetRequiredService<Rrf5.Fiscal.Infrastructure.Storage.S3ArtifactStore>().EnsureBucketAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"bucket ready; object lock {(locked ? "COMPLIANCE available" : "UNAVAILABLE")}");
    Environment.ExitCode = locked ? 0 : 1;
    return;
}

// "load-catalog" loads the shipped catalog into schema_version and prints the report.
// "store-artifact <file> <catalogReferenceId>" stores an official artifact after
// checking its hash against the catalog (T035). Both run as rrf5_worker.
if (args is ["load-catalog", ..] or ["store-artifact", _, _, ..])
{
    builder.Services.AddRrf5Foundation(builder.Configuration, "Worker");
    using var tools = builder.Build();
    if (args[0] == "load-catalog")
    {
        var report = await tools.Services.GetRequiredService<Rrf5.Fiscal.Application.Regulatory.CatalogLoader>().LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"inserted={report.Inserted} updated={report.Updated} unchanged={report.Unchanged} refused={report.Refused.Count}");
        foreach (var refused in report.Refused)
        {
            Console.WriteLine($"refused {refused.CatalogReferenceId}: {refused.Reason}");
        }
    }
    else
    {
        var content = await File.ReadAllBytesAsync(args[1]).ConfigureAwait(false);
        try
        {
            var stored = await tools.Services.GetRequiredService<Rrf5.Fiscal.Application.Regulatory.ArtifactService>().StoreAsync(args[2], content, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"stored {stored.CatalogReferenceId} sha256={stored.Sha256} key={stored.ObjectKey} lock={stored.ObjectLock} version={stored.ObjectVersion ?? "none"}");
        }
        catch (Rrf5.Fiscal.Application.Errors.DomainException exception)
        {
            Console.Error.WriteLine($"refused ({exception.Code.Value}): {exception.Message}");
            Environment.ExitCode = 1;
        }
    }

    return;
}

// Internal jobs run as rrf5_worker (ConnectionStrings:Worker); there is no fiscal
// transmission in the foundation.
builder.Services.AddRrf5Foundation(builder.Configuration, "Worker");
builder.Services.AddHostedService<CertificateExpiryAlertService>();
builder.Services.AddHostedService<CatalogLoadService>();
var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
