using System.Collections.Concurrent;
using LazerTagWebServer.Security;
using Microsoft.AspNetCore.SignalR;

namespace LazerTagWebServer.Hubs;

public sealed class GameHubConnections(OperatorSessionService sessions)
{
    // An empty token denotes a public display. Each tab has its own connection ID.
    private readonly ConcurrentDictionary<string, string> _connections = new();

    public bool HasClients => !_connections.IsEmpty;

    public void Connect(string connectionId, string? token)
        => _connections[connectionId] = sessions.IsValid(token) ? token! : string.Empty;

    public void Disconnect(string connectionId) => _connections.TryRemove(connectionId, out _);

    public Task SendLogAsync(IHubContext<GameHub> hub, string message)
    {
        List<string>? recipients = null;
        foreach (var (id, token) in _connections)
        {
            if (token.Length == 0) continue;
            if (sessions.IsValid(token)) (recipients ??= []).Add(id);
            else _connections.TryUpdate(id, string.Empty, token);
        }

        return recipients == null ? Task.CompletedTask : hub.Clients.Clients(recipients).SendAsync("Log", message);
    }
}
