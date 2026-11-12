using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using DocAgent.Api.Auth;
using DocAgent.Core.Abstractions;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Llm;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Api.Llm;

/// <summary>Bring-your-own-key management (SRS KEY-1..5, KEY-7, KEY-8, SEC-1, SEC-2).</summary>
public static partial class LlmKeyEndpoints
{
    public const string RateLimitPolicy = "llm-key";
    public const string KeyHeader = "X-Llm-Api-Key";
    public const int MaxModelLength = 200;
    public const int MinKeyLength = 8;
    public const int MaxKeyLength = 512;

    public sealed record SaveKeyRequest(string? Provider, string? Model, string? ApiKey);

    public sealed record KeyResponse(string Provider, string Model, string Last4, DateTimeOffset UpdatedAt);

    public sealed record ModelsResponse(string Provider, IReadOnlyList<LlmModelInfo> Models);

    public static IServiceCollection AddLlmKeyRateLimit(this IServiceCollection services, IConfiguration config)
    {
        var permitLimit = config.GetValue("RateLimiting:LlmKey:PermitLimit", 10);
        var windowSeconds = config.GetValue("RateLimiting:LlmKey:WindowSeconds", 60);
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(RateLimitPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromSeconds(windowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })));
        return services;
    }

    public static void MapLlmKeys(this IEndpointRouteBuilder app)
    {
        var key = app.MapGroup("/api/v1/me/llm-key").RequireAuthorization(AuthPolicies.EmailConfirmed);

        // Validation calls cost the user money and hit a third party: limited per user.
        key.MapPut("", SaveAsync).RequireRateLimiting(RateLimitPolicy);
        key.MapGet("", GetAsync);
        key.MapDelete("", DeleteAsync);

        app.MapGet("/api/v1/providers/{provider}/models", ListModelsAsync)
            .RequireAuthorization(AuthPolicies.EmailConfirmed)
            .RequireRateLimiting(RateLimitPolicy);
    }

    private static async Task<IResult> SaveAsync(
        SaveKeyRequest request, AppDbContext db, ICurrentUser user, ILlmKeyProtector protector, LlmKeyValidator validator,
        TimeProvider clock, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        if (!LlmProviders.TryParse(request.Provider, out var provider))
        {
            return ProblemResults.BadRequest("'provider' must be one of: " + string.Join(", ", LlmProviders.All.Select(LlmProviders.Name)) + ".");
        }

        var model = request.Model?.Trim();
        if (string.IsNullOrEmpty(model) || model.Length > MaxModelLength || !ModelPattern().IsMatch(model))
        {
            return ProblemResults.BadRequest($"'model' is required, at most {MaxModelLength} characters, and may contain letters, digits and . _ - : / only.");
        }

        var apiKey = request.ApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length < MinKeyLength || apiKey.Length > MaxKeyLength || !KeyPattern().IsMatch(apiKey))
        {
            return ProblemResults.BadRequest($"'apiKey' is required ({MinKeyLength}-{MaxKeyLength} printable characters, no spaces).");
        }

        try
        {
            await validator.ValidateAsync(provider, model, apiKey, ct); // KEY-2: nothing is saved unless the provider accepts it
        }
        catch (LlmAccessException ex)
        {
            loggerFactory.CreateLogger("DocAgent.Api.LlmKey").LogInformation(
                "LLM key validation failed for provider {Provider}: {Kind}", LlmProviders.Name(provider), ex.Kind);
            return LlmProblems.ToResult(ex, onSave: true);
        }

        var now = clock.GetUtcNow();
        var credential = await db.UserLlmCredentials.FirstOrDefaultAsync(ct); // tenant-filtered: this user's row
        if (credential is null)
        {
            credential = new UserLlmCredential { UserId = userId, Model = model, EncryptedKey = "", KeyLast4 = "", CreatedAt = now };
            db.UserLlmCredentials.Add(credential);
        }

        credential.Provider = provider;
        credential.Model = model;
        credential.EncryptedKey = protector.Protect(userId, apiKey);
        credential.KeyLast4 = apiKey[^4..];
        credential.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ProblemResults.Conflict("The key was saved by another request at the same time. Try again.");
        }

        return Results.Ok(ToResponse(credential));
    }

    private static async Task<IResult> GetAsync(AppDbContext db, CancellationToken ct)
    {
        var credential = await db.UserLlmCredentials.AsNoTracking().FirstOrDefaultAsync(ct);
        return credential is null ? ProblemResults.NotFound("You have not saved an LLM key.") : Results.Ok(ToResponse(credential));
    }

    private static async Task<IResult> DeleteAsync(AppDbContext db, CancellationToken ct)
    {
        await db.UserLlmCredentials.ExecuteDeleteAsync(ct); // tenant filter: only this user's row; idempotent
        return Results.NoContent();
    }

    /// <summary>
    /// Models that support tool calling (KEY-5). The key comes from the <c>X-Llm-Api-Key</c> header when given, so the model
    /// can be chosen before the first save (the model is part of the save request); otherwise the saved key is used if it
    /// belongs to this provider. A header, not a query string: URLs end up in access logs and traces.
    /// </summary>
    private static async Task<IResult> ListModelsAsync(
        string provider, HttpContext http, AppDbContext db, ICurrentUser user, ILlmKeyProtector protector, LlmModelCatalog catalog,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        if (!LlmProviders.TryParse(provider, out var parsed))
        {
            return ProblemResults.NotFound("Unknown provider. Use one of: " + string.Join(", ", LlmProviders.All.Select(LlmProviders.Name)) + ".");
        }

        string? apiKey = null;
        if (http.Request.Headers.TryGetValue(KeyHeader, out var header) && header.ToString().Trim() is { Length: > 0 } given)
        {
            if (given.Length is < MinKeyLength or > MaxKeyLength || !KeyPattern().IsMatch(given))
            {
                return ProblemResults.BadRequest($"The {KeyHeader} header must hold a {MinKeyLength}-{MaxKeyLength} character key without spaces.");
            }

            apiKey = given;
        }
        else
        {
            var saved = await db.UserLlmCredentials.AsNoTracking().FirstOrDefaultAsync(ct);
            if (saved is not null && saved.Provider == parsed)
            {
                apiKey = protector.Unprotect(userId, saved.EncryptedKey);
                if (apiKey is null)
                {
                    return LlmProblems.ToResult(new StoredLlmKeyUnreadableException());
                }
            }
        }

        if (apiKey is null)
        {
            return ProblemResults.Coded(StatusCodes.Status400BadRequest, "LLM key required", 
                $"Send the provider key in the {KeyHeader} header, or save a key for this provider first.", "llm_key_required");
        }

        try
        {
            return Results.Ok(new ModelsResponse(LlmProviders.Name(parsed), await catalog.ListToolModelsAsync(parsed, apiKey, ct)));
        }
        catch (LlmAccessException ex)
        {
            loggerFactory.CreateLogger("DocAgent.Api.LlmKey").LogInformation(
                "Model listing failed for provider {Provider}: {Kind}", LlmProviders.Name(parsed), ex.Kind);
            return LlmProblems.ToResult(ex, onSave: true);
        }
    }

    private static KeyResponse ToResponse(UserLlmCredential c) => new(LlmProviders.Name(c.Provider), c.Model, c.KeyLast4, c.UpdatedAt);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/\-]*$")]
    private static partial Regex ModelPattern();

    [GeneratedRegex(@"^[\x21-\x7E]+$")]
    private static partial Regex KeyPattern();
}
