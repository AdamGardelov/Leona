using System.Net;
using Harness.Data;
using Harness.Endpoints;
using Harness.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ChatDb>(options => options.UseSqlite(
    builder.Configuration.GetConnectionString("ChatDb") ?? "Data Source=harness.db"));
// Scheduled runs wait until nobody has used the model for this long (Background:QuietSeconds).
builder.Services.AddSingleton(new GenerationGate
{
    QuietPeriod = TimeSpan.FromSeconds(builder.Configuration.GetValue("Background:QuietSeconds", 60))
});
builder.Services.AddSingleton<RunManager>();
builder.Services.AddHostedService(services => services.GetRequiredService<RunManager>());
builder.Services.AddSingleton<TokenCalibration>();
builder.Services.AddSingleton<RemoteAccess>();
builder.Services.AddScoped<CurrentProfile>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddSingleton<PersonalFiles>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<FolderService>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<SkillService>();
builder.Services.AddScoped<TrustedSiteService>();
builder.Services.AddSingleton<SpeechService>();
builder.Services.AddScoped<ToolRegistry>();
builder.Services.AddSingleton<WorkspaceFiles>();
builder.Services.AddSingleton<UploadStore>();
// Account passwords and the push key are encrypted with keys kept in backend/keys (DataProtection:KeysPath).
builder.Services.AddDataProtection()
    .SetApplicationName("Leona")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeysPath"] ??
                                               Path.Combine(builder.Environment.ContentRootPath, "keys")));
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<MailService>();
builder.Services.AddScoped<CalendarService>();
builder.Services.AddScoped<HomeAssistantService>();
builder.Services.AddScoped<AutomationService>();
builder.Services.AddScoped<PersonalTools>();
builder.Services.AddScoped<PhotoTools>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<ResearchService>();
builder.Services.AddScoped<DocumentIndex>();
builder.Services.AddSingleton<DocumentIndexer>();
builder.Services.AddHostedService(services => services.GetRequiredService<DocumentIndexer>());
builder.Services.AddHttpClient<Embeddings>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434");
    client.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddScoped<SpotifyService>();
builder.Services.AddSingleton<SpotifyLogins>();
builder.Services.AddSingleton<ConcertService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddHostedService(services => services.GetRequiredService<SchedulerService>());
builder.Services.AddSingleton<WatchService>();
builder.Services.AddHostedService(services => services.GetRequiredService<WatchService>());
builder.Services.AddHttpClient("calendar", client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("home", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("push", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("spotify", client => client.Timeout = TimeSpan.FromSeconds(20));
// The photo service on this computer; the first edit after a while loads its models, which takes a moment.
builder.Services.AddHttpClient("photo", client => client.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddHttpClient("jobs", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Leona/1.0 (personal assistant)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<CareerBoards>();
builder.Services.AddSingleton<ActivityService>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddScoped<JobEmployers>();
builder.Services.AddHttpClient("concerts", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Leona/1.0 (personal assistant)");
});
builder.Services.AddSingleton<PublicWebClient>();
builder.Services.AddHttpClient("search", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.MaxResponseContentBufferSize = 1_000_000;
});
builder.Services.AddHttpClient<OllamaClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434");
    client.Timeout = TimeSpan.FromMinutes(10);
});

// Phone access listens on every interface; requests from other devices still need pairing (below).
if (builder.Configuration.GetValue("Remote:Enabled", false))
{
    builder.WebHost.ConfigureKestrel(options =>
        options.ListenAnyIP(builder.Configuration.GetValue("Remote:Port", 5080)));
}

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
    db.Database.Migrate();
    await scope.ServiceProvider.GetRequiredService<UploadStore>().CleanupAsync(db, CancellationToken.None);
}

var remote = app.Services.GetRequiredService<RemoteAccess>();
await remote.LoadAsync(CancellationToken.None);

// Tailscale Serve proxies the tailnet to localhost. Trust its forwarding headers only from this
// machine, so proxied requests carry the real client address (and need pairing) and HTTPS is known.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto |
                       ForwardedHeaders.XForwardedHost
};
forwarded.KnownProxies.Add(IPAddress.Loopback);
forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
forwarded.KnownProxies.Add(IPAddress.Loopback.MapToIPv6());
// Remember whether the request came straight to this port, before the proxy headers replace the address.
app.Use(async (context, next) =>
{
    context.Items["direct"] = !RemoteAccess.IsLocal(context);
    await next(context);
});
app.UseForwardedHeaders(forwarded);

// The Tailscale name only works over HTTPS through Tailscale Serve. Someone who types it with :5080 lands
// on plain HTTP, where pairing, passwords and push are refused, so send them to the HTTPS address.
app.Use(async (context, next) =>
{
    if (context.Items["direct"] is true && remote.TailscaleHost() is { } tailscale &&
        context.Request.Host.Host.Equals(tailscale, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect($"https://{tailscale}{context.Request.Path}{context.Request.QueryString}");
        return;
    }

    await next(context);
});

// A local, single-user service. Reject unknown hosts (DNS rebinding) and cross-origin writes.
app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    var allowedOrigin = string.IsNullOrEmpty(origin) ||
                        (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && remote.IsAllowedOrigin(uri));
    if (!remote.IsAllowedHost(context.Request.Host.Host) ||
        (!HttpMethods.IsGet(context.Request.Method) && !allowedOrigin))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next(context);
});

// The computer itself needs no sign-in and uses the profile it chose (the owner's by default). Other
// devices may load the app and pair, but every other API call needs the session cookie a pairing
// created, and acts as the profile the device was paired for.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api"))
    {
        await next(context);
        return;
    }

    var local = RemoteAccess.IsLocal(context);
    var profiles = context.RequestServices.GetRequiredService<ProfileService>();
    // A Siri key (Authorization: Bearer) signs in only for asking a question.
    int? wanted = local
        ? int.TryParse(context.Request.Cookies[ProfileService.CookieName], out var chosen) ? chosen : null
        : remote.ProfileOf(context.Request.Cookies[RemoteAccess.CookieName]) ??
          (path.StartsWithSegments("/api/ask") ? remote.KeyProfileOf(AskEndpoints.Bearer(context.Request)) : null);
    var signedIn = (local || wanted is not null) && await profiles.UseAsync(wanted, local, context.RequestAborted);
    if (!signedIn && !path.StartsWithSegments("/api/session") && !path.StartsWithSegments("/api/pair"))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});

// Serve the built frontend (npm run build) so other devices need only this port.
var dist = new[]
    {
        app.Configuration["Frontend:DistPath"],
        Path.Combine(app.Environment.ContentRootPath, "..", "frontend", "dist"),
        Path.Combine(app.Environment.ContentRootPath, "frontend", "dist")
    }
    .Where(p => !string.IsNullOrWhiteSpace(p))
    .Select(p => Path.GetFullPath(p!))
    .FirstOrDefault(p => File.Exists(Path.Combine(p, "index.html")));
if (dist is not null)
{
    var files = new PhysicalFileProvider(dist);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.MapModelEndpoints();
app.MapConversationEndpoints();
app.MapRunEndpoints();
app.MapSettingsEndpoints();
app.MapFolderEndpoints();
app.MapMemoryEndpoints();
app.MapRemoteEndpoints();
app.MapProfileEndpoints();
app.MapSkillEndpoints();
app.MapTrustedSiteEndpoints();
app.MapProjectEndpoints();
app.MapDocumentEndpoints();
app.MapImageEndpoints();
app.MapMailEndpoints();
app.MapSpeechEndpoints();
app.MapAskEndpoints();
app.MapJobEndpoints();
app.MapSpotifyEndpoints();
app.MapUploadEndpoints();
app.MapAutomationEndpoints();

app.Run();
