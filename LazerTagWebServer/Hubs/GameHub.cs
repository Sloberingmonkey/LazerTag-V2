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
    private readonly HostGunService _service;
    private readonly GameHubConnections _connections;

    public GameHub(HostGunService service, GameHubConnections connections)
    {
        _service = service;
        _connections = connections;
    }

    public override async Task OnConnectedAsync()
    {
        try
        {
            await base.OnConnectedAsync();
            var request = Context.GetHttpContext()?.Request;
            var token = request == null ? null
                : OperatorAuthorizeAttribute.ReadBearerToken(request) ?? request.Query["access_token"].ToString();
            _connections.Connect(Context.ConnectionId, token);

            // Send current state immediately so the TV/phone shows the right screen on connect.
            await Task.WhenAll(
                Clients.Caller.SendAsync("State",     _service.GetGameState()),
                Clients.Caller.SendAsync("Telemetry", _service.Telemetry));
        }
        catch
        {
            // Removal is idempotent; OnDisconnectedAsync may also run after a failed connect.
            _connections.Disconnect(Context.ConnectionId);
            throw;
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Disconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
