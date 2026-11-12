using DocAgent.Core.Auth;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace DocAgent.Api.Auth;

public static class ExternalAuthEndpoints
{
    public const string CallbackPath = "/api/v1/auth/external/callback";
    private const string ReturnUrlItem = "returnUrl";

    // URL segment -> authentication scheme (also the Identity login provider name).
    private static readonly Dictionary<string, string> Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["google"] = "Google",
        ["github"] = "GitHub",
    };

    public static void MapExternalAuth(this RouteGroupBuilder auth)
    {
        auth.MapGet("/external/callback", Callback).ExcludeFromDescription();
        auth.MapPost("/external/exchange", Exchange);
        auth.MapGet("/external/{provider}", Start);
    }

    private static async Task<IResult> Start(
        string provider,
        string? returnUrl,
        SignInManager<AppUser> signInManager,
        IAuthenticationSchemeProvider schemes,
        IOptions<ExternalAuthOptions> options)
    {
        if (!Providers.TryGetValue(provider, out var scheme) || await schemes.GetSchemeAsync(scheme) is null)
        {
            return ProblemResults.NotFound("Unknown or unavailable sign-in provider.");
        }

        if (!ReturnUrlValidator.TryValidate(returnUrl, options.Value.AllowedReturnUrls, out var validated))
        {
            return ProblemResults.BadRequest("returnUrl is missing or not allowed.");
        }

        var properties = signInManager.ConfigureExternalAuthenticationProperties(scheme, CallbackPath);
        properties.Items[ReturnUrlItem] = validated;
        return Results.Challenge(properties, [scheme]);
    }

    private static async Task<IResult> Callback(
        HttpContext http,
        SignInManager<AppUser> signInManager,
        ExternalAccountResolver resolver,
        ExternalLoginCodeService codes,
        IOptions<ExternalAuthOptions> options,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DocAgent.Api.Auth.ExternalCallback");
        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            return ProblemResults.BadRequest("External sign-in could not be completed.");
        }

        // Re-validate: the value round-trips through a cookie and must not be trusted blindly.
        string? returnUrlRaw = null;
        info.AuthenticationProperties?.Items.TryGetValue(ReturnUrlItem, out returnUrlRaw);
        if (!ReturnUrlValidator.TryValidate(returnUrlRaw, options.Value.AllowedReturnUrls, out var returnUrl))
        {
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            return ProblemResults.BadRequest("returnUrl is missing or not allowed.");
        }

        var identity = ExternalIdentityMapper.From(info);
        var result = await resolver.ResolveAsync(identity!);

        // The external cookie has served its purpose either way.
        await http.SignOutAsync(IdentityConstants.ExternalScheme);

        if (!result.Succeeded)
        {
            var error = result.Error switch
            {
                ExternalAccountError.EmailNotVerified => "email_not_verified",
                ExternalAccountError.NoVerifiedEmail => "no_verified_email",
                _ => "external_login_failed",
            };
            logger.LogInformation("External sign-in via {Provider} rejected: {Error}", info.LoginProvider, error);
            return Results.Redirect(QueryHelpers.AddQueryString(returnUrl!, "error", error));
        }

        var code = await codes.IssueAsync(result.User!.Id);
        return Results.Redirect(QueryHelpers.AddQueryString(returnUrl!, "code", code));
    }

    private sealed record ExchangeRequest(string? Code);

    private static async Task<IResult> Exchange(
        ExchangeRequest request,
        ExternalLoginCodeService codes,
        UserManager<AppUser> users,
        SignInManager<AppUser> signInManager)
    {
        var invalid = ProblemResults.BadRequest("The code is invalid or has expired.");

        var userId = await codes.RedeemAsync(request.Code);
        if (userId is null)
        {
            return invalid;
        }

        var user = await users.FindByIdAsync(userId.Value.ToString());
        if (user is null)
        {
            return invalid;
        }

        // Same response as /login: writes the AccessTokenResponse JSON body.
        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        await signInManager.SignInAsync(user, isPersistent: false);
        return Results.Empty;
    }

    /// <summary>
    /// Used by the provider handlers when the remote sign-in fails (e.g. user denied consent):
    /// redirects back to the validated return URL with an error code instead of throwing.
    /// </summary>
    public static Task HandleRemoteFailure(Microsoft.AspNetCore.Authentication.RemoteFailureContext context)
    {
        var allowed = context.HttpContext.RequestServices.GetRequiredService<IOptions<ExternalAuthOptions>>().Value;
        string? raw = null;
        context.Properties?.Items.TryGetValue(ReturnUrlItem, out raw);
        context.HandleResponse();
        if (ReturnUrlValidator.TryValidate(raw, allowed.AllowedReturnUrls, out var returnUrl))
        {
            context.Response.Redirect(QueryHelpers.AddQueryString(returnUrl!, "error", "external_login_failed"));
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }

        return Task.CompletedTask;
    }
}
