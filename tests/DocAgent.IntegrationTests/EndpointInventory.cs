using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.IntegrationTests;

/// <summary>How an endpoint decides who may call it.</summary>
public enum AuthDecision
{
    /// <summary>Anyone, signed in or not. Each of these must be a deliberate choice (login, health probes, ...).</summary>
    Public,

    /// <summary>Any signed-in user (also with an unconfirmed email).</summary>
    SignedIn,

    /// <summary>Signed in with a confirmed email (the EmailConfirmed policy).</summary>
    ConfirmedEmail,
}

/// <summary>
/// The explicit list of every endpoint the API maps and its authentication decision. A new endpoint that is not in this table
/// fails <c>HardeningTests.Every_mapped_endpoint_has_an_explicit_auth_decision</c>: adding one means deciding here whether it is public.
/// </summary>
public static class EndpointInventory
{
    public static readonly IReadOnlyDictionary<string, AuthDecision> Expected = new Dictionary<string, AuthDecision>
    {
        // ASP.NET Core Identity API (mapped under /api/v1/auth): credentials, tokens and email flows are public by nature; /manage needs a token.
        ["POST /api/v1/auth/register"] = AuthDecision.Public,
        ["POST /api/v1/auth/login"] = AuthDecision.Public,
        ["POST /api/v1/auth/refresh"] = AuthDecision.Public,
        ["GET /api/v1/auth/confirmEmail"] = AuthDecision.Public,
        ["POST /api/v1/auth/resendConfirmationEmail"] = AuthDecision.Public,
        ["POST /api/v1/auth/forgotPassword"] = AuthDecision.Public,
        ["POST /api/v1/auth/resetPassword"] = AuthDecision.Public,
        ["POST /api/v1/auth/manage/2fa"] = AuthDecision.SignedIn,
        ["GET /api/v1/auth/manage/info"] = AuthDecision.SignedIn,
        ["POST /api/v1/auth/manage/info"] = AuthDecision.SignedIn,

        // External sign-in (Google, GitHub): the browser is not signed in yet; the one-time code is the credential.
        ["GET /api/v1/auth/external/callback"] = AuthDecision.Public,
        ["POST /api/v1/auth/external/exchange"] = AuthDecision.Public,
        ["GET /api/v1/auth/external/{provider}"] = AuthDecision.Public,

        // Account.
        ["GET /api/v1/me"] = AuthDecision.SignedIn,
        ["DELETE /api/v1/me"] = AuthDecision.SignedIn,
        ["PUT /api/v1/me/llm-key"] = AuthDecision.ConfirmedEmail,
        ["GET /api/v1/me/llm-key"] = AuthDecision.ConfirmedEmail,
        ["DELETE /api/v1/me/llm-key"] = AuthDecision.ConfirmedEmail,
        ["GET /api/v1/providers/{provider}/models"] = AuthDecision.ConfirmedEmail,

        // Documents and questions.
        ["POST /api/v1/documents"] = AuthDecision.ConfirmedEmail,
        ["GET /api/v1/documents"] = AuthDecision.ConfirmedEmail,
        ["GET /api/v1/documents/{id}"] = AuthDecision.ConfirmedEmail,
        ["DELETE /api/v1/documents/{id}"] = AuthDecision.ConfirmedEmail,
        ["POST /api/v1/documents/{id}/summary"] = AuthDecision.ConfirmedEmail,
        ["POST /api/v1/documents/compare"] = AuthDecision.ConfirmedEmail,
        ["POST /api/v1/ask"] = AuthDecision.ConfirmedEmail,
        ["POST /api/v1/ask/stream"] = AuthDecision.ConfirmedEmail,

        // Probes (Azure Container Apps): status text only, no details.
        ["GET /health/ready"] = AuthDecision.Public,
        ["GET /health/live"] = AuthDecision.Public,
    };

    /// <summary>"METHOD route-pattern" (route constraints removed) with the decision the endpoint's metadata actually carries.</summary>
    public static IReadOnlyDictionary<string, AuthDecision> Discover(IServiceProvider services)
    {
        var found = new Dictionary<string, AuthDecision>();
        foreach (var endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"]; // the health probes carry no method metadata and answer GET
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var decision = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null || authorize.Count == 0
                ? AuthDecision.Public
                : authorize.Any(a => a.Policy == Api.Auth.AuthPolicies.EmailConfirmed) ? AuthDecision.ConfirmedEmail : AuthDecision.SignedIn;
            var pattern = System.Text.RegularExpressions.Regex.Replace(endpoint.RoutePattern.RawText ?? "", @"\{(\w+)(:[^}]*)?\}", "{$1}").TrimEnd('/');
            foreach (var method in methods.Where(m => m != "HEAD" && m != "OPTIONS"))
            {
                found[$"{method} {pattern}"] = decision;
            }
        }

        return found;
    }
}
