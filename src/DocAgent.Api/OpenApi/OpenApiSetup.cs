using System.Text.Json.Nodes;
using DocAgent.Api.Auth;
using DocAgent.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

namespace DocAgent.Api.OpenApi;

/// <summary>
/// OpenAPI document (SRS 4.1): bearer security scheme, a security requirement on every endpoint that needs a signed-in user,
/// the Problem Details responses every endpoint can return (401/403/429/500), summaries for the Identity endpoints, and
/// request examples for the main calls. Endpoint-specific results (200, 400, 404, ...) are declared where each endpoint is mapped.
/// The document is served only in Development unless <c>OpenApi:Enabled</c> is true.
/// </summary>
public static class OpenApiSetup
{
    public const string BearerScheme = "Bearer";

    public static IServiceCollection AddDocAgentOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "DocAgent API",
                    Version = "v1",
                    Description =
                        "Upload documents (PDF, Word, text, Markdown) and ask questions about them. Answers are cited and verified against the documents. "
                        + "Sign in with POST /api/v1/auth/login (or Google/GitHub), then send the access token as 'Authorization: Bearer <token>'. "
                        + "Errors are RFC 9457 Problem Details (application/problem+json); many carry a stable 'code' extension.",
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "Access token from POST /api/v1/auth/login, /refresh or /external/exchange (valid 1 hour).",
                };
                return Task.CompletedTask;
            });

            options.AddOperationTransformer(async (operation, context, ct) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var requiresUser = !metadata.OfType<IAllowAnonymous>().Any() && metadata.OfType<IAuthorizeData>().Any();
                var needsConfirmedEmail = metadata.OfType<IAuthorizeData>().Any(a => a.Policy == AuthPolicies.EmailConfirmed);
                var path = System.Text.RegularExpressions.Regex.Replace("/" + context.Description.RelativePath?.Trim('/'), @"\{(\w+)(:[^}]*)?\}", "{$1}");
                var method = context.Description.HttpMethod ?? "GET";

                operation.Responses ??= new OpenApiResponses();
                var problem = await context.GetOrCreateSchemaAsync(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), cancellationToken: ct);

                void Problem(string status, string description)
                {
                    if (!operation.Responses.ContainsKey(status))
                    {
                        operation.Responses[status] = new OpenApiResponse
                        {
                            Description = description,
                            Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = problem } },
                        };
                    }
                }

                if (requiresUser)
                {
                    operation.Security =
                    [
                        new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [] },
                    ];
                    Problem("401", "Missing, invalid or expired access token.");
                }

                if (needsConfirmedEmail)
                {
                    Problem("403", "The account's email address is not confirmed yet.");
                }

                if (metadata.OfType<EnableRateLimitingAttribute>().Any() || AuthRateLimiting.IsLimited(path))
                {
                    Problem("429", "Too many requests. Retry after the number of seconds in the Retry-After header.");
                }

                Problem("500", "Unexpected server error. The body never contains exception details.");

                foreach (var (status, response) in operation.Responses)
                {
                    if (string.IsNullOrWhiteSpace(response.Description) && int.TryParse(status, out var code))
                    {
                        response.Description = ReasonPhrases.GetReasonPhrase(code);
                    }
                }

                if (IdentityDocs.TryGetValue((method.ToUpperInvariant(), path), out var docs))
                {
                    operation.Summary ??= docs.Summary;
                    operation.Description ??= docs.Description;
                    operation.Tags = new HashSet<OpenApiTagReference> { new("Authentication", context.Document) };
                }

                if (Examples.TryGetValue((method.ToUpperInvariant(), path), out var example)
                    && operation.RequestBody?.Content is { } content)
                {
                    foreach (var media in content.Values)
                    {
                        media.Example = JsonNode.Parse(example);
                    }
                }
            });
        });

        return services;
    }

    /// <summary>Summaries for the endpoints ASP.NET Core Identity maps itself (they cannot carry our metadata).</summary>
    private static readonly Dictionary<(string Method, string Path), (string Summary, string Description)> IdentityDocs = new()
    {
        [("POST", "/api/v1/auth/register")] = ("Register with email and password",
            "Creates an account and sends a confirmation email. Documents and questions need a confirmed email address. Password rules: at least 6 characters with upper case, lower case, a digit and a symbol."),
        [("POST", "/api/v1/auth/login")] = ("Sign in with email and password",
            "Returns an access token (1 hour) and a refresh token (14 days). 401 for wrong credentials. Locked accounts get 401 as well. Limited to 10 requests per minute per IP address."),
        [("POST", "/api/v1/auth/refresh")] = ("Refresh the access token",
            "Exchanges a refresh token for a new access token and refresh token pair. 401 when the refresh token is invalid or expired."),
        [("GET", "/api/v1/auth/confirmEmail")] = ("Confirm an email address",
            "Opened from the link in the confirmation email (userId, code and optional changedEmail query parameters). Returns 200 on success, 401 when the code is invalid."),
        [("POST", "/api/v1/auth/resendConfirmationEmail")] = ("Resend the confirmation email",
            "Always answers 200, whether or not the address belongs to an account, so it cannot be used to find out who is registered."),
        [("POST", "/api/v1/auth/forgotPassword")] = ("Request a password reset code",
            "Emails a reset code when the address belongs to a confirmed account. Always answers 200."),
        [("POST", "/api/v1/auth/resetPassword")] = ("Reset the password with the emailed code",
            "Sets a new password using the code from the reset email. Removes nothing else from the account. 400 when the code or the new password is invalid."),
        [("POST", "/api/v1/auth/manage/2fa")] = ("Manage two-factor authentication",
            "ASP.NET Core Identity two-factor settings for the signed-in user."),
        [("GET", "/api/v1/auth/manage/info")] = ("Get the account's Identity information",
            "Email address and confirmation state of the signed-in user. GET /api/v1/me returns more."),
        [("POST", "/api/v1/auth/manage/info")] = ("Change email or password",
            "Changing the password needs the old password. Changing the email sends a new confirmation email."),
    };

    private static readonly Dictionary<(string Method, string Path), string> Examples = new()
    {
        [("POST", "/api/v1/auth/register")] = """{ "email": "ada@example.com", "password": "Passw0rd!x" }""",
        [("POST", "/api/v1/auth/login")] = """{ "email": "ada@example.com", "password": "Passw0rd!x" }""",
        [("POST", "/api/v1/auth/refresh")] = """{ "refreshToken": "<refresh token from login>" }""",
        [("POST", "/api/v1/auth/forgotPassword")] = """{ "email": "ada@example.com" }""",
        [("POST", "/api/v1/auth/resetPassword")] = """{ "email": "ada@example.com", "resetCode": "<code from the email>", "newPassword": "N3w!Passw0rd" }""",
        [("POST", "/api/v1/auth/external/exchange")] = """{ "code": "<one-time code from the return URL>" }""",
        [("PUT", "/api/v1/me/llm-key")] = """{ "provider": "openrouter", "model": "openai/gpt-4o-mini", "apiKey": "<your provider API key>" }""",
        [("POST", "/api/v1/ask")] = """{ "question": "How many vacation days do I get?", "documentIds": ["3fa85f64-5717-4562-b3fc-2c963f66afa6"] }""",
        [("POST", "/api/v1/ask/stream")] = """{ "question": "What does the contract say about termination?" }""",
        [("POST", "/api/v1/documents/{id}/summary")] = """{ "focus": "schedule and risks" }""",
        [("POST", "/api/v1/documents/compare")] = """{ "documentIdA": "3fa85f64-5717-4562-b3fc-2c963f66afa6", "documentIdB": "9b2e1c4a-0d57-4f0e-8a31-5d6f7a1b2c3d", "focus": "prices" }""",
    };
}
