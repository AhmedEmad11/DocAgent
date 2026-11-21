using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DocAgent.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.IntegrationTests;

/// <summary>Phase 8: the Data Protection key ring lives in the database, encrypted with a certificate (SEC-1, docs/DEPLOYMENT.md).</summary>
[Collection(PostgresCollection.Name)]
public sealed class DataProtectionTests(PostgresFixture postgres)
{
    public const string Password = "example-only-not-a-secret";

    /// <summary>A throwaway self-signed certificate; returns the base64 .pfx the configuration expects.</summary>
    public static string NewPfxBase64()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=DocAgent test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, Password));
    }

    private static ServiceProvider Instance(string connectionString, bool developmentLike, string? pfxBase64, string? store = "Database")
    {
        var settings = new Dictionary<string, string?>
        {
            ["DataProtection:Store"] = store,
            ["DataProtection:CertificatePfxBase64"] = pfxBase64,
            ["DataProtection:CertificatePassword"] = Password,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString, n => n.UseVector()));
        services.AddDocAgentDataProtection(configuration, developmentLike);
        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(ServiceProvider instance) => instance.GetRequiredService<IDataProtectionProvider>().CreateProtector("DocAgent.Test", "user-1");

    private async Task<string> MigratedDatabaseAsync() => await TestDatabase.CreateMigratedAsync(postgres);

    [Fact]
    public async Task A_key_protected_by_one_instance_is_unprotected_by_another_instance_on_the_same_database()
    {
        var cs = await MigratedDatabaseAsync();
        var pfx = NewPfxBase64();

        string payload;
        await using (var a = Instance(cs, developmentLike: false, pfx))
        {
            payload = Protector(a).Protect("sk-or-user-secret");
        }

        // A new process (new service provider, empty in-memory key cache) reads the ring back from the database.
        await using var b = Instance(cs, developmentLike: false, pfx);
        Assert.Equal("sk-or-user-secret", Protector(b).Unprotect(payload));

        await using var db = TestDatabase.CreateContext(cs);
        Assert.Equal(1, await db.DataProtectionKeys.CountAsync()); // B reused A's key instead of creating its own
    }

    [Fact]
    public async Task The_stored_ring_is_encrypted_and_cannot_be_read_without_the_certificate()
    {
        var cs = await MigratedDatabaseAsync();
        var pfx = NewPfxBase64();

        string payload;
        await using (var a = Instance(cs, developmentLike: false, pfx))
        {
            payload = Protector(a).Protect("sk-or-user-secret");
        }

        await using (var db = TestDatabase.CreateContext(cs))
        {
            var xml = (await db.DataProtectionKeys.SingleAsync()).Xml!;
            Assert.Contains("xmlenc#", xml);              // XML-Encryption envelope
            Assert.DoesNotContain("masterKey", xml);      // the plaintext key element is not stored
        }

        // Same database, no certificate: the key cannot be decrypted, so the payload is unreadable.
        await using var withoutCertificate = Instance(cs, developmentLike: true, pfxBase64: null);
        Assert.ThrowsAny<CryptographicException>(() => Protector(withoutCertificate).Unprotect(payload));

        // Another certificate does not help either.
        await using var wrongCertificate = Instance(cs, developmentLike: true, NewPfxBase64());
        Assert.ThrowsAny<CryptographicException>(() => Protector(wrongCertificate).Unprotect(payload));
    }

    [Fact]
    public async Task Without_a_certificate_the_database_ring_is_stored_unencrypted_in_development()
    {
        var cs = await MigratedDatabaseAsync();
        await using (var a = Instance(cs, developmentLike: true, pfxBase64: null))
        {
            Protector(a).Protect("x");
        }

        await using var db = TestDatabase.CreateContext(cs);
        Assert.Contains("masterKey", (await db.DataProtectionKeys.SingleAsync()).Xml!);
    }

    [Fact]
    public void Production_needs_the_database_store_and_a_usable_certificate()
    {
        var anyConnection = "Host=localhost;Database=unused";

        var noCertificate = Assert.Throws<InvalidOperationException>(() => Instance(anyConnection, developmentLike: false, pfxBase64: null, store: null));
        Assert.Contains("DataProtection:CertificatePfxBase64", noCertificate.Message);

        var fileRing = Assert.Throws<InvalidOperationException>(() => Instance(anyConnection, developmentLike: false, NewPfxBase64(), store: "Default"));
        Assert.Contains("database", fileRing.Message);

        var notAPfx = Assert.Throws<InvalidOperationException>(() => Instance(anyConnection, developmentLike: false, Convert.ToBase64String("not a pfx"u8.ToArray())));
        Assert.DoesNotContain(Password, notAPfx.Message);

        Assert.Throws<InvalidOperationException>(() => Instance(anyConnection, developmentLike: false, "%%%not base64%%%"));

        using var ok = Instance(anyConnection, developmentLike: false, NewPfxBase64(), store: null); // defaults to the database store
    }

    [Fact]
    public void Development_defaults_to_the_file_ring_and_needs_no_certificate()
    {
        using var instance = Instance("Host=localhost;Database=unused", developmentLike: true, pfxBase64: null, store: null);
        Assert.NotNull(instance.GetRequiredService<IDataProtectionProvider>());
    }
}
