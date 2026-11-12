namespace Rrf5.Fiscal.Api.Http;

/// <summary>
/// A group of endpoints registered in DI and mapped at startup. Feature slices
/// (F1-F5) add their modules here; tests add probe modules the same way, so the
/// production pipeline (correlation, problem details, telemetry) is what they exercise.
/// </summary>
public interface IEndpointModule
{
    void Map(IEndpointRouteBuilder endpoints);
}
