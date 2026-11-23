using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Core.Auth;

namespace DocAgent.IntegrationTests;

/// <summary>Plan A3 / D40: a one-time ticket lets the browser post a file without the access token.</summary>
[Collection(PostgresCollection.Name)]
public sealed class UploadTicketTests(PostgresFixture postgres) : IAsyncLifetime
{
    private DocAgentApiFactory _factory = null!;

    public async Task InitializeAsync() =>
        _factory = new DocAgentApiFactory(await TestDatabase.CreateAsync(postgres), new Dictionary<string, string?> { ["Documents:Ingestion:WorkerEnabled"] = "false" });

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static async Task<string> TicketAsync(TestUser user)
    {
        var response = await user.Client.PostAsync("/api/v1/documents/upload-ticket", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(60, body.GetProperty("expiresInSeconds").GetInt32());
        return body.GetProperty("ticket").GetString()!;
    }

    private async Task<HttpResponseMessage> UploadWithTicketAsync(string? ticket, byte[]? bytes = null, string fileName = "a.txt")
    {
        using var anonymous = _factory.CreateClient(); // no Authorization header at all
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = DocumentTestHelpers.FileContent(bytes ?? "ticket upload"u8.ToArray(), fileName) };
        if (ticket is not null)
        {
            request.Headers.Add(UploadTicketService.Header, ticket);
        }

        return await anonymous.SendAsync(request);
    }

    [Fact]
    public async Task A_ticket_uploads_a_document_for_the_user_who_asked_once()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);
        var ticket = await TicketAsync(user);

        var response = await UploadWithTicketAsync(ticket);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var listed = await user.Client.GetFromJsonAsync<JsonElement>($"/api/v1/documents/{id}"); // owned by the ticket's user
        Assert.Equal("a.txt", listed.GetProperty("fileName").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadWithTicketAsync(ticket)).StatusCode); // single use
    }

    [Fact]
    public async Task Missing_unknown_and_foreign_header_use_is_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadWithTicketAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadWithTicketAsync("not-a-ticket")).StatusCode);
    }

    [Fact]
    public async Task A_ticket_is_worth_nothing_on_any_other_route()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);
        var ticket = await TicketAsync(user);
        using var anonymous = _factory.CreateClient();

        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/v1/documents"), (HttpMethod.Get, "/api/v1/me"), (HttpMethod.Post, "/api/v1/ask") })
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add(UploadTicketService.Header, ticket);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(request)).StatusCode);
        }

        // Still unused: those calls did not redeem it.
        Assert.Equal(HttpStatusCode.Accepted, (await UploadWithTicketAsync(ticket)).StatusCode);
    }

    [Fact]
    public async Task The_ticket_endpoint_needs_a_confirmed_signed_in_user()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/documents/upload-ticket", null)).StatusCode);

        var unconfirmed = await DocumentTestHelpers.CreateUserAsync(_factory, confirmEmail: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await unconfirmed.Client.PostAsync("/api/v1/documents/upload-ticket", null)).StatusCode);
    }

    [Fact]
    public async Task The_full_document_limits_apply_to_ticket_uploads()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);
        for (var i = 0; i < 3; i++) // demo limit: 3 documents
        {
            Assert.Equal(HttpStatusCode.Accepted, (await UploadWithTicketAsync(await TicketAsync(user), fileName: $"d{i}.txt")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await UploadWithTicketAsync(await TicketAsync(user), fileName: "d4.txt")).StatusCode);

        var big = new byte[5 * 1024 * 1024 + 10];
        Array.Fill(big, (byte)'a');
        var other = await DocumentTestHelpers.CreateUserAsync(_factory);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await UploadWithTicketAsync(await TicketAsync(other), big)).StatusCode);
    }
}
