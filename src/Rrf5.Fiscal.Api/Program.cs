var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Liveness only: says the process answers. It never reports SEFAZ availability (FR-024).
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can host the API in memory.</summary>
public partial class Program;
