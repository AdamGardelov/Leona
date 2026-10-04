using Harness.Models;
using Harness.Services;

namespace Harness.Endpoints;

public static class MailEndpoints
{
    private record ManageRequest(string[]? Ids, string? Action);

    public static void MapMailEndpoints(this IEndpointRouteBuilder app)
    {
        // The mail list in a chat: the user ticks messages and presses a button, so the click is the
        // approval. Only the current profile's mailboxes can be reached.
        app.MapPost("/api/mail/manage", async (ManageRequest request, AccountService accounts, MailService mail,
            CancellationToken ct) =>
        {
            var ids = request.Ids?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? [];
            if (ids.Count is 0 or > 50 || request.Action is not { } action || !MailService.ManageActions.Contains(action))
                return Results.BadRequest(new { error = "Choose between 1 and 50 e-mails and a valid action." });

            try
            {
                var mailboxes = await accounts.OfKindAsync(AccountKind.Mail, ct);
                var done = new List<string>();
                foreach (var group in ids.GroupBy(id => MailService.ParseId(id).AccountId))
                {
                    var account = mailboxes.FirstOrDefault(a => a.Id == group.Key);
                    if (account is null)
                        return Results.NotFound(new { error = "That mailbox is not connected." });
                    done.Add(await mail.ManageAsync(account, group.ToList(), action, ct));
                }

                return Results.Ok(new { message = string.Join(" ", done) });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
