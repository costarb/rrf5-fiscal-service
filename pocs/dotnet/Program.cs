using System.Text.Json;

var result = new
{
    candidate = "dotnet",
    runtime = Environment.Version.ToString(),
    scope = "fiscal-discovery-poc",
    productionReady = false
};

Console.WriteLine(JsonSerializer.Serialize(result));

