using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DocAgent.IntegrationTests;

public sealed record Tokens(string AccessToken, string RefreshToken);

public static class AuthTestHelpers
{
    public const string Password = "Passw0rd!x";

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    public static async Task<HttpResponseMessage> RegisterAsync(this HttpClient client, string email, string password = Password) =>
        await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password });

    public static async Task<Tokens> LoginAsync(this HttpClient client, string email, string password = Password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Tokens>(JsonOptions))!;
    }

    public static HttpClient WithBearer(this HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
