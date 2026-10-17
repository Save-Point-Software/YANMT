using Microsoft.AspNetCore.Components.Authorization;
using Yanmt.Infrastructure.Data;

namespace Yanmt.Web.Hosting;

/// <summary>
/// The signed-in Windows user, for the audit log. Inside a Blazor circuit the user comes from
/// the AuthenticationStateProvider (HttpContext is not reliable there); for plain HTTP endpoints
/// such as the CSV export it comes from HttpContext.
/// </summary>
public sealed class WebCurrentUser(AuthenticationStateProvider authState, IHttpContextAccessor httpContext) : ICurrentUser
{
    public async ValueTask<string> GetNameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await authState.GetAuthenticationStateAsync();
            if (state.User.Identity is { IsAuthenticated: true, Name: { } name })
            {
                return name;
            }
        }
        catch (InvalidOperationException)
        {
            // Not in a circuit: the provider was never initialized for this scope.
        }

        return httpContext.HttpContext?.User.Identity is { IsAuthenticated: true, Name: { } httpName }
            ? httpName
            : "unknown";
    }
}
