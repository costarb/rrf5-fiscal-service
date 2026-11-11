using System.Net;
using System.Text.Json;

namespace Rrf5.Fiscal.Tests.Stack;

/// <summary>An application created through the API, with its one-time secret.</summary>
public sealed record TestApplication(Guid TenantId, Guid Id, string ClientId, string Secret);

public static class TenantSetup
{
    public static async Task<string> PlatformTokenAsync(HttpClient client, StackFixture stack) =>
        await ApiClient.TokenAsync(client, stack.PlatformAdmin.ClientId, stack.PlatformAdmin.ClientSecret);

    /// <summary>Creates a tenant and its first tenant.admin application as the platform administrator.</summary>
    public static async Task<TestApplication> TenantWithAdminAsync(HttpClient client, StackFixture stack, string name)
    {
        var platform = await PlatformTokenAsync(client, stack);
        var (tenantResponse, tenant) = await ApiClient.SendAsync(client, HttpMethod.Post, "/v1/tenants", platform, new { name }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, tenantResponse.StatusCode);
        var tenantId = tenant.GetProperty("id").GetGuid();

        var (appResponse, app) = await ApiClient.SendAsync(
            client, HttpMethod.Post, $"/v1/tenants/{tenantId}/applications", platform, new { name = name + " admin", scopes = new[] { "tenant.admin" } }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, appResponse.StatusCode);
        return Read(app);
    }

    /// <summary>Creates an application in the caller's tenant with a tenant.admin token.</summary>
    public static async Task<TestApplication> ApplicationAsync(HttpClient client, string adminToken, string name, params string[] scopes)
    {
        var (response, app) = await ApiClient.SendAsync(client, HttpMethod.Post, "/v1/applications", adminToken, new { name, scopes }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Read(app);
    }

    public static TestApplication Read(JsonElement app) => new(
        app.GetProperty("tenantId").GetGuid(),
        app.GetProperty("id").GetGuid(),
        app.GetProperty("clientId").GetString()!,
        app.GetProperty("clientSecret").GetString()!);
}
