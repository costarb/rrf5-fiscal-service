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

// Internal jobs run as rrf5_worker (ConnectionStrings:Worker); there is no fiscal
// transmission in the foundation.
builder.Services.AddRrf5Foundation(builder.Configuration, "Worker");
builder.Services.AddHostedService<CertificateExpiryAlertService>();
var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
