using Harness.Services;

namespace Harness.Endpoints;

public static class ModelEndpoints
{
    public static void MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/models");
        group.MapGet("", GetModelsAsync);
        group.MapGet("/capabilities", GetCapabilitiesAsync);
    }

    private static async Task<IResult> GetModelsAsync(OllamaClient ollama, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await ollama.GetModelsAsync(ct));
        }
        catch (HttpRequestException)
        {
            return Results.Problem("Cannot reach Ollama. Check that it is running.", statusCode: 502);
        }
    }

    private static async Task<IResult> GetCapabilitiesAsync(string model, OllamaClient ollama, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await ollama.GetCapabilitiesAsync(model, ct));
        }
        catch (HttpRequestException)
        {
            return Results.Problem("Could not read model capabilities.", statusCode: 502);
        }
    }
}
