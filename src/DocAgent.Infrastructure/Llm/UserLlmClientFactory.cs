using DocAgent.Core.Abstractions;
using DocAgent.Infrastructure.Agents;
using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// Chooses the route per request (SRS KEY-6): the signed-in user's saved key gives a BYOK route with the
/// <c>Agent:Limits:Byok</c> caps; without a key the <see cref="INoKeyRouteProvider"/> decides (Ollama in development,
/// otherwise a 403 until Phase 6 adds the demo route). Scoped: the saved key is read once per request.
/// </summary>
public sealed class UserLlmClientFactory(
    AppDbContext db,
    ICurrentUser currentUser,
    ILlmKeyProtector protector,
    IProviderChatClientFactory providers,
    INoKeyRouteProvider noKeyRoute,
    IOptions<AgentOptions> agentOptions) : ILlmClientFactory
{
    private Task<LlmRoute>? _route;

    public ValueTask<LlmRoute> GetRouteAsync(CancellationToken ct = default) => new(_route ??= ResolveAsync(ct));

    private async Task<LlmRoute> ResolveAsync(CancellationToken ct)
    {
        // The tenant query filter limits this to the current user's row; no user means no row.
        var credential = currentUser.UserId is { } userId
            ? await db.UserLlmCredentials.AsNoTracking().FirstOrDefaultAsync(ct)
            : null;

        if (credential is null)
        {
            return await noKeyRoute.GetRouteAsync(ct) ?? throw new NoLlmKeyException();
        }

        var apiKey = protector.Unprotect(credential.UserId, credential.EncryptedKey) ?? throw new StoredLlmKeyUnreadableException();
        var client = providers.Create(credential.Provider, credential.Model, apiKey);
        return new LlmRoute(
            client,
            new LlmRouteInfo("byok", credential.Provider.ToString(), credential.Model),
            Options: null,
            agentOptions.Value.Limits.Byok.ToLimits());
    }
}
