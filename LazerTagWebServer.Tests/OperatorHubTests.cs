using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LazerTagHostLibrary;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace LazerTagWebServer.Tests;

public sealed partial class ArenaRelayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_consoles_receive_private_logs_only_during_their_operator_session(bool expire)
    {
        var clock = new SessionClock();
        await using var arena = new Arena(clock: clock);
        using var firstOperator = arena.CreateConsole();
        var firstToken = await arena.LoginAsync(firstOperator);
        using var firstTab = await arena.OpenDisplayAsync(firstToken);
        using var secondTab = await arena.OpenDisplayAsync(firstToken, bearerHeader: true);
        using var tv = await arena.OpenDisplayAsync();

        clock.AdvanceMinutes(8);
        using var otherOperator = await arena.OpenDisplayAsync(await arena.LoginAsync());

        async Task CheckDeliveryAsync(string marker, bool firstSessionActive)
        {
            Log.Add(Log.Severity.Information, marker);
            // Next on an idle arena broadcasts fresh state and telemetry after the log.
            using var response = await arena.PostAsync("next");
            response.EnsureSuccessStatusCode();
            foreach (var (display, expected) in new[] { (firstTab, firstSessionActive), (secondTab, firstSessionActive), (tv, false), (otherOperator, true) })
            {
                var logs = await display.ReadUpdateAsync();
                Assert.Equal(expected ? 1 : 0, logs.Count(line => line.EndsWith(marker, StringComparison.Ordinal)));
            }
        }

        await CheckDeliveryAsync("Before session change", true);
        if (expire)
        {
            clock.AdvanceMinutes(7);
        }
        else
        {
            using var logout = await firstOperator.PostAsync("/api/operator/logout", null);
            logout.EnsureSuccessStatusCode();
        }
        await CheckDeliveryAsync("After session change", false);
    }

    private sealed class SessionClock : TimeProvider
    {
        private long _minutes;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMinutes(Interlocked.Read(ref _minutes));
        public void AdvanceMinutes(int minutes) => Interlocked.Add(ref _minutes, minutes);
    }

    private sealed class HubDisplay(WebSocket socket, CancellationToken deadline) : IDisposable
    {
        private readonly Queue<JsonElement> _records = new();

        public static async Task<HubDisplay> ConnectAsync(TestServer server, string? token, bool bearerHeader, CancellationToken deadline)
        {
            var client = server.CreateWebSocketClient();
            if (bearerHeader) client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
            var url = new Uri("ws://localhost/gamehub" + (token != null && !bearerHeader ? "?access_token=" + token : ""));
            var socket = await client.ConnectAsync(url, deadline);
            var display = new HubDisplay(socket, deadline);
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"),
                    WebSocketMessageType.Text, true, deadline);
                await display.ReadUpdateAsync();
                return display;
            }
            catch
            {
                display.Dispose();
                throw;
            }
        }

        public async Task<List<string>> ReadUpdateAsync()
        {
            var logs = new List<string>();
            bool state = false, telemetry = false;
            while (!state || !telemetry)
            {
                var message = await ReadRecordAsync();
                if (!message.TryGetProperty("target", out var target)) continue;
                switch (target.GetString())
                {
                    case "State": state = true; break;
                    case "Telemetry": telemetry = true; break;
                    case "Log": logs.Add(message.GetProperty("arguments")[0].GetString()!); break;
                }
            }
            return logs;
        }

        private async Task<JsonElement> ReadRecordAsync()
        {
            while (_records.Count == 0)
            {
                using var message = new MemoryStream();
                var buffer = new byte[4096];
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline);
                    Assert.Equal(WebSocketMessageType.Text, part.MessageType);
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                foreach (var record in Encoding.UTF8.GetString(message.ToArray()).Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                    _records.Enqueue(JsonSerializer.Deserialize<JsonElement>(record));
            }
            return _records.Dequeue();
        }

        public void Dispose() => socket.Dispose();
    }
}
