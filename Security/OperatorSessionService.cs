using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace LazerTagWebServer.Security;

public sealed record OperatorSession(string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Validates the configured operator PIN and issues short-lived, in-memory bearer tokens.
/// Tokens intentionally expire on server restart so a lost control device cannot remain
/// authorized indefinitely.
/// </summary>
public sealed class OperatorSessionService
{
    private static readonly HashSet<string> WeakProductionPins =
    [
        "000000", "111111", "123456", "12345678", "654321", "999999",
    ];

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly byte[] _pinHash;
    private readonly TimeSpan _sessionLifetime;
    private readonly TimeProvider _timeProvider;

    public OperatorSessionService(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<OperatorSessionService> logger,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;

        var pin = configuration["Arena:OperatorPin"]?.Trim();
        if (string.IsNullOrEmpty(pin))
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "Arena:OperatorPin is required in Production. Set Arena__OperatorPin in /etc/lazertag/lazertag.env.");
            }

            pin = "123456";
            logger.LogWarning("Arena:OperatorPin is not configured. Using the development-only PIN 123456.");
        }

        if (pin.Length is < 6 or > 128)
            throw new InvalidOperationException("Arena:OperatorPin must contain between 6 and 128 characters.");

        if (environment.IsProduction() && WeakProductionPins.Contains(pin))
            throw new InvalidOperationException("Arena:OperatorPin is too easy to guess for a public deployment.");

        _pinHash = SHA256.HashData(Encoding.UTF8.GetBytes(pin));

        var configuredHours = configuration.GetValue<double?>("Arena:OperatorSessionHours") ?? 12;
        _sessionLifetime = TimeSpan.FromHours(Math.Clamp(configuredHours, 0.25, 24));
    }

    public OperatorSession? TryCreateSession(string? suppliedPin)
    {
        if (string.IsNullOrEmpty(suppliedPin) || suppliedPin.Length > 128) return null;

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedPin));
        if (!CryptographicOperations.FixedTimeEquals(_pinHash, suppliedHash)) return null;

        RemoveExpiredSessions();
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = _timeProvider.GetUtcNow().Add(_sessionLifetime);
        _sessions[token] = expiresAt;
        return new OperatorSession(token, expiresAt);
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return false;
        if (!_sessions.TryGetValue(token, out var expiresAt)) return false;

        if (expiresAt > _timeProvider.GetUtcNow()) return true;
        _sessions.TryRemove(token, out _);
        return false;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _sessions.TryRemove(token, out _);
    }

    private void RemoveExpiredSessions()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var session in _sessions)
        {
            if (session.Value <= now) _sessions.TryRemove(session.Key, out _);
        }
    }
}
