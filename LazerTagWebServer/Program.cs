using LazerTagWebServer;
using LazerTagWebServer.Hubs;
using LazerTagWebServer.Security;
using LazerTagWebServer.Services;
using System.Threading.RateLimiting;

// A systemd service may deliberately use a writable data directory as its
// working directory. Published web assets live beside the executable, so use
// the packaged application directory as the content root when wwwroot exists
// there. Local development and WebApplicationFactory keep their normal roots.
var packagedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builderOptions = Directory.Exists(packagedWebRoot)
    ? new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
        WebRootPath = "wwwroot",
    }
    : new WebApplicationOptions { Args = args };

var builder = WebApplication.CreateBuilder(builderOptions);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 32 * 1024;
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});
builder.Services.AddSingleton<HostGunService>();
builder.Services.AddSingleton<OperatorSessionService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("operator-login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy("operator-control", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));

builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://0.0.0.0:5000");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});

PlayerSettings.ConfigureDataDirectory(builder.Configuration["Arena:DataDirectory"]);

var app = builder.Build();

// Force production configuration validation during startup instead of on the first login.
_ = app.Services.GetRequiredService<OperatorSessionService>();
_ = app.Services.GetRequiredService<HostGunService>();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        if (context.Request.Path.StartsWithSegments("/api"))
            headers["Cache-Control"] = "no-store";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
            "connect-src 'self' ws: wss:; img-src 'self' data:; font-src 'self'; object-src 'none'; " +
            "base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
        return Task.CompletedTask;
    });
    await next();
});

app.UseRateLimiter();
app.UseWebSockets();
app.UseDefaultFiles();

// Disable caching for JS/HTML/CSS so phones always get the latest version after a deploy
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var ext = Path.GetExtension(ctx.File.Name).ToLowerInvariant();
        if (ext is ".js" or ".html" or ".css")
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"]        = "no-cache";
            ctx.Context.Response.Headers["Expires"]       = "0";
        }
    }
});

app.MapControllers();
app.MapHub<GameHub>("/gamehub");
app.MapGet("/healthz", (HostGunService service) => Results.Ok(new
{
    Status = "ok",
    Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown",
    RelayConnected = service.IsRelayConnected,
    Hosting = service.IsHosting,
    Utc = DateTimeOffset.UtcNow,
}));
app.MapGet("/healthz/live", () => Results.Ok(new { Status = "ok" }));

app.Run();

public partial class Program { }
