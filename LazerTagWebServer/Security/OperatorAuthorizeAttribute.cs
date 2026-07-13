using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LazerTagWebServer.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OperatorAuthorizeAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var sessions = context.HttpContext.RequestServices.GetRequiredService<OperatorSessionService>();
        var token = ReadBearerToken(context.HttpContext.Request);

        if (!sessions.IsValid(token))
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                Error = "Operator authorization is required. Unlock the control console with the operator PIN.",
            });
        }

        return Task.CompletedTask;
    }

    public static string? ReadBearerToken(HttpRequest request)
    {
        var value = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].Trim()
            : null;
    }
}
