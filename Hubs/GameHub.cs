using System.Collections.Concurrent;
using LazerTagWebServer.Security;
using LazerTagWebServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace LazerTagWebServer.Hubs;

/// <summary>
/// SignalR hub for real-time game state updates.
/// Pushes current state and telemetry to newly connected clients immediately.
/// </summary>
public class GameHub : Hub
{
    public const string OperatorsGroup = "operators";
    private static readonly ConcurrentDictionary<string, byte> ActiveConnections = new();
    private readonly HostGunService _service;
    private readonly OperatorSessionService _operatorSessions;

    public static bool HasClients => !ActiveConnections.IsEmpty;

    public GameHub(HostGunService service, OperatorSessionService operatorSessions)
    {
        _service = service;
        _operatorSessions = operatorSessions;
    }

    public override async Task OnConnectedAsync()
    {
        ActiveConnections.TryAdd(Context.ConnectionId, 0);

        try
        {
            await base.OnConnectedAsync();

            // State and telemetry drive the public TV. Detailed operational logs are only
            // delivered to a control console that supplied a valid operator session token.
            var accessToken = Context.GetHttpContext()?.Request.Query["access_token"].ToString();
            if (_operatorSessions.IsValid(accessToken))
                await Groups.AddToGroupAsync(Context.ConnectionId, OperatorsGroup);

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
