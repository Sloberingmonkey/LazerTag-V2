using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LazerTagWebServer.Tests;

public sealed class ArenaWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arena:OperatorPin"] = "73918426",
                ["Arena:DisableRelayReconnect"] = "true",
                ["Arena:RelayAddress"] = "127.0.0.1:9",
            });
        });
    }
}

public sealed class ArenaApiTests : IClassFixture<ArenaWebApplicationFactory>
{
    private readonly ArenaWebApplicationFactory _factory;

    public ArenaApiTests(ArenaWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Published_operator_and_tv_assets_are_publicly_served()
    {
        using var client = _factory.CreateClient();

        foreach (string path in new[] { "/", "/index.html", "/tv.html", "/css/app.css", "/js/app.js" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task Public_status_endpoints_are_available_without_operator_token()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/game/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/game/gametypes")).StatusCode);
    }

    [Fact]
    public async Task Operator_data_and_game_mutations_reject_anonymous_clients()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/game/savedplayers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/game/countdown", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/game/endgame", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/game/cleanup", null)).StatusCode);
    }

    [Fact]
    public async Task Login_issues_token_and_logout_revokes_it()
    {
        using var client = _factory.CreateClient();

        var denied = await client.PostAsJsonAsync("/api/operator/login", new { pin = "wrong-pin" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        string token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/operator/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/operator/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/operator/status")).StatusCode);
    }

    [Fact]
    public async Task Team_rosters_enforce_protocol_capacity_before_hardware_is_touched()
    {
        using var client = _factory.CreateClient();
        string token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var onePlayer = StartRequest(3, 1);
        var tooManyForTwoTeams = StartRequest(3, 17);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/game/start", onePlayer)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/game/start", tooManyForTwoTeams)).StatusCode);

        // An idle console can always be reset without opening a relay connection.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/game/next", null)).StatusCode);
    }

    private static object StartRequest(int gameType, int playerCount) => new
    {
        gameType,
        gameTypeLabel = "ignored client label",
        minutes = 10,
        countdownSeconds = 30,
        globalHp = 10,
        globalShields = 0,
        globalReloads = 0,
        globalMega = 0,
        globalStartingAmmo = 10,
        globalSleepTimeout = 0,
        players = Enumerable.Range(1, playerCount).Select(slot => new
        {
            slot,
            name = $"Player {slot}",
            teamPreference = (int?)null,
        }),
    };

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/operator/login", new { pin = "73918426" });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Login response did not contain a token.");
    }
}
