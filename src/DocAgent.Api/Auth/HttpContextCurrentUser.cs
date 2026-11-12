using System.Security.Claims;
using DocAgent.Core.Abstractions;

namespace DocAgent.Api.Auth;

/// <summary>Current user taken from the authenticated principal of the active request.</summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            return Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
        }
    }
}
