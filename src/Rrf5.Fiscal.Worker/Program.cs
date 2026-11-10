var builder = Host.CreateApplicationBuilder(args);

// Internal jobs (certificate expiry alerts, catalog loading) are registered here
// by their tasks; there is no fiscal transmission in the foundation.
var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
