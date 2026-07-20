using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LazerTagWebServer.Models;
using LazerTagWebServer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LazerTagWebServer.Tests;

public sealed class GameHistoryTests
{
    [Fact]
    public async Task Store_commits_immutable_records_and_applies_retention()
    {
        string dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = CreateStore(dataDirectory, retention: 2);
            var first = CreateRecord(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-3), "First");
            var second = CreateRecord(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-2), "Second");
            var third = CreateRecord(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), "Third");

            Assert.True(await store.SaveAsync(first));
            string firstPath = Path.Combine(dataDirectory, "game-history", first.GameId + ".json");
            byte[] originalBytes = await File.ReadAllBytesAsync(firstPath);

            // Re-saving the same stable game ID must never replace the first result.
            Assert.True(await store.SaveAsync(first with { GameTypeLabel = "Changed later" }));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(firstPath));

            await Task.Delay(20);
            Assert.True(await store.SaveAsync(second));
            await Task.Delay(20);
            Assert.True(await store.SaveAsync(third));

            var recent = await store.GetRecentAsync(50);
            Assert.Equal(2, recent.Count);
            Assert.Equal(new[] { third.GameId, second.GameId }, recent.Select(item => item.GameId));
            Assert.All(recent, item => Assert.True(item.ResultsFinalized));
            Assert.False(File.Exists(firstPath));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(dataDirectory, "game-history"), "*.tmp"));
        }
        finally
        {
            TryDeleteDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Forced_or_incomplete_results_are_never_marked_finalized()
    {
        string dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = CreateStore(dataDirectory, retention: 10);
            var completedAt = DateTimeOffset.UtcNow;
            var forced = CreateRecord(Guid.NewGuid(), completedAt, "Forced") with
            {
                Forced = true,
                // Even if every report arrived just before the force action, the library's
                // forced path does not run its score finalization routine.
                AllPlayersDebriefed = true,
            };
            var incomplete = CreateRecord(Guid.NewGuid(), completedAt.AddSeconds(1), "Incomplete") with
            {
                AllPlayersDebriefed = false,
            };

            Assert.True(await store.SaveAsync(forced));
            Assert.True(await store.SaveAsync(incomplete));

            Assert.False((await store.GetAsync(forced.GameId))!.ResultsFinalized);
            Assert.False((await store.GetAsync(incomplete.GameId))!.ResultsFinalized);
            Assert.All(await store.GetRecentAsync(10), item => Assert.False(item.ResultsFinalized));

            byte[] forcedJson = (await store.GetRawJsonAsync(forced.GameId))!;
            using var document = JsonDocument.Parse(forcedJson);
            Assert.False(document.RootElement.GetProperty("resultsFinalized").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Storage_failure_is_reported_without_throwing()
    {
        string root = CreateTemporaryDirectory();
        string unusableDataPath = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(unusableDataPath, "occupied");

        try
        {
            var store = CreateStore(unusableDataPath, retention: 10);
            bool saved = await store.SaveAsync(CreateRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "Test"));
            Assert.False(saved);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task History_endpoints_require_operator_login_and_export_saved_result()
    {
        string dataDirectory = CreateTemporaryDirectory();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Arena:OperatorPin"] = "73918426",
                    ["Arena:DisableRelayReconnect"] = "true",
                    ["Arena:RelayAddress"] = "127.0.0.1:9",
                    ["Arena:DataDirectory"] = dataDirectory,
                });
            });
        });

        try
        {
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/history")).StatusCode);

            var saved = CreateRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "Public Game");
            var store = factory.Services.GetRequiredService<GameHistoryStore>();
            Assert.True(await store.SaveAsync(saved));

            string token = await LoginAsync(client);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var recent = await client.GetFromJsonAsync<List<CompletedGameSummary>>("/api/history?limit=10");
            Assert.Single(recent!);
            Assert.Equal(saved.GameId, recent![0].GameId);
            Assert.True(recent[0].ResultsFinalized);

            var result = await client.GetFromJsonAsync<CompletedGameRecord>($"/api/history/{saved.GameId}");
            Assert.Equal("Public Game", result!.GameTypeLabel);
            Assert.True(result.ResultsFinalized);

            var jsonDownload = await client.GetAsync($"/api/history/{saved.GameId}/download.json");
            Assert.Equal(HttpStatusCode.OK, jsonDownload.StatusCode);
            Assert.Equal("application/json", jsonDownload.Content.Headers.ContentType?.MediaType);

            var csvDownload = await client.GetAsync($"/api/history/{saved.GameId}/download.csv");
            Assert.Equal(HttpStatusCode.OK, csvDownload.StatusCode);
            string csv = await csvDownload.Content.ReadAsStringAsync();
            Assert.Contains("Results finalized", csv);
            Assert.Contains("Player One", csv);
        }
        finally
        {
            await factory.DisposeAsync();
            TryDeleteDirectory(dataDirectory);
        }
    }

    private static GameHistoryStore CreateStore(string dataDirectory, int retention)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arena:DataDirectory"] = dataDirectory,
                ["Arena:HistoryRetention"] = retention.ToString(),
            })
            .Build();
        return new GameHistoryStore(configuration, NullLogger<GameHistoryStore>.Instance);
    }

    private static CompletedGameRecord CreateRecord(Guid id, DateTimeOffset endedAt, string label)
        => new()
        {
            GameId = id.ToString("N"),
            ProtocolGameId = "0x42",
            SoftwareVersion = "1.0.1+test",
            GameTypeId = 3,
            GameTypeName = "CustomLazerTagTwoTeams",
            GameTypeLabel = label,
            IsTeamGame = true,
            HostedAtUtc = endedAt.AddMinutes(-11),
            StartedAtUtc = endedAt.AddMinutes(-10),
            EndedAtUtc = endedAt,
            AllPlayersDebriefed = true,
            Settings = new GameHistorySettings { Minutes = 10, GlobalHp = 10 },
            Players =
            [
                new GameHistoryPlayer
                {
                    Id = "T1:P1",
                    Team = 1,
                    Slot = 1,
                    Name = "Player One",
                    TaggerId = 66,
                    Rank = 1,
                    Score = 8,
                    HitsScored = 8,
                    HitsTaken = 5,
                    DebriefComplete = true,
                    Hp = 10,
                },
            ],
            Teams = [new GameHistoryTeam { Team = 1, Rank = 1, Score = 8, PlayerCount = 1 }],
        };

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/operator/login", new { pin = "73918426" });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Login response did not contain a token.");
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lazertag-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Test cleanup is best-effort on Windows where antivirus may briefly retain a handle.
        }
    }
}
