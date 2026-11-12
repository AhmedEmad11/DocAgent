using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.IntegrationTests;

/// <summary>A registered, email-confirmed user with an authenticated client.</summary>
public sealed class TestUser(HttpClient client, Guid id, string email)
{
    public HttpClient Client { get; } = client;
    public Guid Id { get; } = id;
    public string Email { get; } = email;
}

public static class DocumentTestHelpers
{
    public static async Task<TestUser> CreateUserAsync(DocAgentApiFactory factory, bool confirmEmail = true)
    {
        using var anonymous = factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.RegisterAsync(email)).StatusCode);
        if (confirmEmail)
        {
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(factory.Emails.Last("confirm", email).Value)).StatusCode);
        }

        var tokens = await anonymous.LoginAsync(email);
        var client = factory.CreateClient().WithBearer(tokens.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        return new TestUser(client, me.GetProperty("id").GetGuid(), email);
    }

    public static MultipartFormDataContent FileContent(byte[] bytes, string fileName, string fieldName = "file")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, fieldName, fileName);
        return form;
    }

    public static async Task<HttpResponseMessage> UploadAsync(this TestUser user, byte[] bytes, string fileName)
    {
        using var form = FileContent(bytes, fileName);
        return await user.Client.PostAsync("/api/v1/documents", form);
    }

    public static async Task<Guid> UploadOkAsync(this TestUser user, byte[] bytes, string fileName)
    {
        var response = await user.UploadAsync(bytes, fileName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> WaitForFinalStatusAsync(this TestUser user, Guid id, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        while (true)
        {
            var doc = await user.Client.GetFromJsonAsync<JsonElement>($"/api/v1/documents/{id}");
            var status = doc.GetProperty("status").GetString();
            if (status is "Ready" or "Failed")
            {
                return doc;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Document {id} still '{status}' after the timeout.");
            }

            await Task.Delay(200);
        }
    }

    /// <summary>A context that sees all tenants; for assertions on stored data only.</summary>
    public static AppDbContext OpenDb(string connectionString) => TestDatabase.CreateContext(connectionString);

    public static async Task AddCredentialAsync(string connectionString, Guid userId)
    {
        await using var db = TestDatabase.CreateContext(connectionString, userId);
        db.UserLlmCredentials.Add(new UserLlmCredential
        {
            UserId = userId, Provider = LlmProvider.OpenRouter, Model = "m", EncryptedKey = "enc",
            KeyLast4 = "abcd", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public static async Task RemoveCredentialAsync(string connectionString, Guid userId)
    {
        await using var db = TestDatabase.CreateContext(connectionString, userId);
        await db.UserLlmCredentials.ExecuteDeleteAsync();
    }
}
