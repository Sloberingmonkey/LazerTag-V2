using LazerTagWebServer.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LazerTagWebServer.Controllers;

[ApiController]
[Route("api/operator")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OperatorController : ControllerBase
{
    private readonly OperatorSessionService _sessions;

    public OperatorController(OperatorSessionService sessions) => _sessions = sessions;

    public sealed record LoginRequest(string? Pin);

    [HttpPost("login")]
    [EnableRateLimiting("operator-login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        var session = _sessions.TryCreateSession(request.Pin);
        if (session is null)
            return Unauthorized(new { Error = "The operator PIN is incorrect." });

        return Ok(new
        {
            session.Token,
            ExpiresAt = session.ExpiresAtUtc,
        });
    }

    [HttpGet("status")]
    [OperatorAuthorize]
    public IActionResult Status() => Ok(new { Authenticated = true });

    [HttpPost("logout")]
    [OperatorAuthorize]
    public IActionResult Logout()
    {
        _sessions.Revoke(OperatorAuthorizeAttribute.ReadBearerToken(Request));
        return Ok(new { Success = true });
    }
}
