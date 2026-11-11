using Rrf5.Fiscal.Infrastructure.Observability;
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

// Internal jobs (certificate expiry alerts, catalog loading) are registered here
// by their tasks; there is no fiscal transmission in the foundation.
var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
