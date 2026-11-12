using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using DocAgent.Api.Auth;
using DocAgent.Api.Documents;
using DocAgent.Api.Qa;
using DocAgent.Api.Data;
using DocAgent.Core.Abstractions;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Auth;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Email;
using DocAgent.Infrastructure.Ingestion;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using DocAgent.Infrastructure.Retrieval;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery, HTTP resilience.
builder.AddServiceDefaults();

// PostgreSQL (connection string "docagentdb") through EF Core with pgvector. Registers the
// AppDbContext plus a database health check used by /health/ready.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
// Not AddNpgsqlDbContext: that registers a *pooled* context, which cannot take the scoped ICurrentUser
// the tenant query filters need. AddDbContext + EnrichNpgsqlDbContext is Aspire's documented
// alternative and keeps retries, telemetry and the connection-string wiring.
var connectionString = builder.Configuration.GetConnectionString("docagentdb");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
builder.EnrichNpgsqlDbContext<AppDbContext>(settings => settings.DisableHealthChecks = true); // replaced by DatabaseHealthCheck below
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

// ---- Authentication (Identity API endpoints with bearer tokens) ----
builder.Services
    .AddIdentityApiEndpoints<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = false; // sign-in allowed; document/Q&A endpoints use the EmailConfirmed policy
    })
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
{
    options.BearerTokenExpiration = TimeSpan.FromHours(1);   // SRS D26
    options.RefreshTokenExpiration = TimeSpan.FromDays(14);  // SRS D26
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.EmailConfirmed, policy => policy
        .RequireAuthenticatedUser()
        .AddRequirements(new EmailConfirmedRequirement()));
builder.Services.AddScoped<IAuthorizationHandler, EmailConfirmedHandler>();

builder.Services.Configure<ExternalAuthOptions>(builder.Configuration.GetSection(ExternalAuthOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ExternalAccountResolver>();
builder.Services.AddScoped<ExternalLoginCodeService>();

// External providers are registered only when configured, so the app starts without them (CI, tests).
var authentication = builder.Services.AddAuthentication();

var googleId = builder.Configuration["Authentication:Google:ClientId"];
var googleSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleId) && !string.IsNullOrWhiteSpace(googleSecret))
{
    authentication.AddGoogle("Google", options =>
    {
        options.ClientId = googleId;
        options.ClientSecret = googleSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        // Callback path stays at the default (/signin-google): it is registered with Google.
        options.ClaimActions.MapCustomJson(ExternalIdentityMapper.GoogleEmailVerifiedClaim, json =>
            json.TryGetProperty("email_verified", out var verified) ? verified.ToString() : null);
        options.Events.OnRemoteFailure = ExternalAuthEndpoints.HandleRemoteFailure;
    });
}

var gitHubId = builder.Configuration["Authentication:GitHub:ClientId"];
var gitHubSecret = builder.Configuration["Authentication:GitHub:ClientSecret"];
if (!string.IsNullOrWhiteSpace(gitHubId) && !string.IsNullOrWhiteSpace(gitHubSecret))
{
    authentication.AddGitHub("GitHub", options =>
    {
        options.ClientId = gitHubId;
        options.ClientSecret = gitHubSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.Scope.Add("user:email");
        options.SaveTokens = true;
        // Callback path stays at the default (/signin-github): it is registered with GitHub.
        options.Events.OnRemoteFailure = ExternalAuthEndpoints.HandleRemoteFailure;
        options.Events.OnCreatingTicket = async context =>
        {
            // GitHub's own email claim ignores verification, so ask /user/emails and keep only
            // the primary address that GitHub reports as verified.
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True
                    && entry.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True
                    && entry.TryGetProperty("email", out var email) && email.GetString() is { Length: > 0 } address)
                {
                    context.Identity?.AddClaim(new Claim(ExternalIdentityMapper.GitHubVerifiedEmailClaim, address));
                    break;
                }
            }
        };
    });
}

// ---- Document ingestion: parsing, chunking, ONNX embeddings, background worker ----
builder.Services.AddDocumentIngestion(builder.Configuration);

// ---- Retrieval, LLM access (IChatClient only, SRS MAIN-1) and Q&A ----
builder.Services.AddRetrieval(builder.Configuration);
builder.Services.AddLlm(builder.Configuration);
builder.Services.AddQa(builder.Configuration);

// ---- Email ----
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddTransient<IEmailSender<AppUser>, SmtpEmailSender>();

// ---- Rate limiting (SRS SEC-8, D27) ----
builder.Services.AddAuthRateLimiting(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.ApplyMigrationsIfNeededAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapDevExternalLoginTestPage();
}

app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();

var auth = app.MapGroup("/api/v1/auth");
auth.MapIdentityApi<AppUser>();
auth.MapExternalAuth();

app.MapMe();
app.MapDocuments();
app.MapAsk();
app.MapWorkflows();

app.Run();

// Makes the generated Program class visible to WebApplicationFactory in integration tests.
public partial class Program;
