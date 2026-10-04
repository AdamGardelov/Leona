using Harness.Services;

namespace Harness.Endpoints;

public static class JobEndpoints
{
    private record EmployerInput(string? Url, string? Name);

    // The employers the job radar watches; each profile has its own list.
    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs/employers");
        group.MapGet("", (JobEmployers employers, CancellationToken ct) => employers.ListAsync(ct));
        group.MapPost("", async (EmployerInput input, JobEmployers employers, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await employers.AddAsync(input.Url ?? "", input.Name, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or HttpRequestException or System.Xml.XmlException or
                                           System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return Results.BadRequest(new
                {
                    error = ex is ArgumentException ? ex.Message : "That career page could not be read. Check the address."
                });
            }
        });
        group.MapDelete("/{id}", async (string id, JobEmployers employers, CancellationToken ct) =>
            await employers.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
