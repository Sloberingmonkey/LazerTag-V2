using LazerTagWebServer.Hubs;
using LazerTagWebServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddSingleton<HostGunService>();

// Listen on all interfaces so phones and the Pi can reach it
builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();

app.UseWebSockets();   // must be before MapHub
app.UseDefaultFiles(); // serves index.html automatically

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

app.Run();