using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Auth;
using DocAgent.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ExternalAccountResolverTests(PostgresFixture postgres)
{
    private async Task<ServiceProvider> CreateServicesAsync()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs, n => n.UseVector()));
        services.AddIdentityCore<AppUser>(o => o.User.RequireUniqueEmail = true).AddEntityFrameworkStores<AppDbContext>();
        services.AddScoped<ExternalAccountResolver>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Existing_external_login_returns_that_user()
    {
        await using var sp = await CreateServicesAsync();
        await using var scope = sp.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ExternalAccountResolver>();

        var first = await resolver.ResolveAsync(new("Google", "g-1", "a@example.com", true));
        // Same login now reports a different (even unverified) email: still the same user.
        var second = await resolver.ResolveAsync(new("Google", "g-1", "other@example.com", false));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(first.User!.Id, second.User!.Id);
    }

    [Fact]
    public async Task Verified_email_matching_existing_user_links_and_confirms_email()
    {
        await using var sp = await CreateServicesAsync();
        await using var scope = sp.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var resolver = scope.ServiceProvider.GetRequiredService<ExternalAccountResolver>();
        var local = new AppUser { UserName = "a@example.com", Email = "a@example.com", EmailConfirmed = false };
        Assert.True((await users.CreateAsync(local, AuthTestHelpers.Password)).Succeeded);

        var result = await resolver.ResolveAsync(new("GitHub", "gh-1", "A@Example.com", true));

        Assert.True(result.Succeeded);
        Assert.Equal(local.Id, result.User!.Id);
        var reloaded = (await users.FindByIdAsync(local.Id.ToString()))!;
        Assert.True(reloaded.EmailConfirmed);
        Assert.Contains(await users.GetLoginsAsync(reloaded), l => l.LoginProvider == "GitHub" && l.ProviderKey == "gh-1");
        // The unproven password was dropped so a pre-registrant cannot keep access.
        Assert.False(await users.HasPasswordAsync(reloaded));
    }

    [Fact]
    public async Task Linking_keeps_the_password_of_an_already_confirmed_account()
    {
        await using var sp = await CreateServicesAsync();
        await using var scope = sp.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var resolver = scope.ServiceProvider.GetRequiredService<ExternalAccountResolver>();
        var local = new AppUser { UserName = "a@example.com", Email = "a@example.com", EmailConfirmed = true };
        await users.CreateAsync(local, AuthTestHelpers.Password);

        var result = await resolver.ResolveAsync(new("Google", "g-2", "a@example.com", true));

        Assert.True(result.Succeeded);
        Assert.True(await users.HasPasswordAsync(local));
    }

    [Fact]
    public async Task Unverified_email_matching_existing_user_is_not_linked()
    {
        await using var sp = await CreateServicesAsync();
        await using var scope = sp.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var resolver = scope.ServiceProvider.GetRequiredService<ExternalAccountResolver>();
        var local = new AppUser { UserName = "a@example.com", Email = "a@example.com", EmailConfirmed = true };
        await users.CreateAsync(local, AuthTestHelpers.Password);

        var result = await resolver.ResolveAsync(new("Google", "g-3", "a@example.com", false));

        Assert.False(result.Succeeded);
        Assert.Equal(ExternalAccountError.EmailNotVerified, result.Error);
        Assert.Empty(await users.GetLoginsAsync(local));
    }

    [Fact]
    public async Task Unknown_verified_email_creates_confirmed_user_with_login()
    {
        await using var sp = await CreateServicesAsync();
        await using var scope = sp.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var resolver = scope.ServiceProvider.GetRequiredService<ExternalAccountResolver>();

        var result = await resolver.ResolveAsync(new("Google", "g-4", "new@example.com", true));

        Assert.True(result.Succeeded);
        var user = (await users.FindByEmailAsync("new@example.com"))!;
        Assert.Equal(result.User!.Id, user.Id);
        Assert.Equal("new@example.com", user.UserName);
        Assert.True(user.EmailConfirmed);
        Assert.Single(await users.GetLoginsAsync(user));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("new@example.com", false)] // present but unverified, nobody to link to
    public async Task No_verified_email_creates_nothing(string? email, bool verified)
    {
        await using var sp = await CreateServicesAsync();
        await using var scope = sp.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var resolver = scope.ServiceProvider.GetRequiredService<ExternalAccountResolver>();

        var result = await resolver.ResolveAsync(new("GitHub", "gh-5", email, verified));

        Assert.False(result.Succeeded);
        Assert.Equal(ExternalAccountError.NoVerifiedEmail, result.Error);
        Assert.Empty(users.Users);
    }
}
