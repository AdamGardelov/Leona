namespace Harness.Endpoints;

// The services report invalid input with ArgumentException (shown to the user as a 400 with its message)
// and a missing item with KeyNotFoundException (404). Added to the routes whose services work that way.
public static class InputErrors
{
    public static RouteHandlerBuilder WithInputErrors(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
}
