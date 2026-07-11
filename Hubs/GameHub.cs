using System.Collections.Concurrent;
using LazerTagWebServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace LazerTagWebServer.Hubs;

/// <summary>
/// SignalR hub for real-time game state updates.
/// Pushes current state and telemetry to newly connected clients immediately.
/// </summary>
public class GameHub : Hub
{
    private static readonly ConcurrentDictionary<string, byte> ActiveConnections = new();
    private readonly HostGunService _service;

    public static bool HasClients => !ActiveConnections.IsEmpty;

    public GameHub(HostGunService service) => _service = service;

    public override async Task OnConnectedAsync()
    {
        ActiveConnections.TryAdd(Context.ConnectionId, 0);

        try
        {
            await base.OnConnectedAsync();

            // Send current state immediately so the TV/phone shows the right screen on connect.
            await Task.WhenAll(
                Clients.Caller.SendAsync("State",     _service.GetGameState()),
                Clients.Caller.SendAsync("Telemetry", _service.Telemetry));
        }
        catch
        {
            // Removal is idempotent; OnDisconnectedAsync may also run after a failed connect.
            ActiveConnections.TryRemove(Context.ConnectionId, out _);
            throw;
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        ActiveConnections.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}
