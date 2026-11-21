using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.Infrastructure.Data;

/// <summary>
/// Where the Data Protection key ring lives (it encrypts the users' stored LLM keys, SEC-1) and how it is protected.
/// <list type="bullet">
/// <item><b>Production</b> (anything but Development/Testing): the ring is stored in the database (<c>DataProtectionKeys</c> table) and the keys are
/// encrypted at rest with an X.509 certificate from <c>DataProtection:CertificatePfxBase64</c> + <c>DataProtection:CertificatePassword</c>
/// (Container Apps secrets). A container filesystem is ephemeral, and a database dump alone must not reveal the keys. Missing or unreadable certificate: startup fails.</item>
/// <item><b>Development/Testing</b>: the ASP.NET default (a key file ring in the user profile) so local runs need no certificate and no extra table.
/// <c>DataProtection:Store=Database</c> opts in to the database ring (certificate optional), which is how the tests exercise the production path.</item>
/// </list>
/// The application name stays "DocAgent" so every instance and environment shares one key purpose space.
/// </summary>
public static class DataProtectionSetup
{
    public const string ApplicationName = "DocAgent";
    public const string DatabaseStore = "Database";
    public const string DefaultStore = "Default";

    public static IServiceCollection AddDocAgentDataProtection(this IServiceCollection services, IConfiguration configuration, bool developmentLike)
    {
        var store = configuration["DataProtection:Store"];
        if (string.IsNullOrWhiteSpace(store))
        {
            store = developmentLike ? DefaultStore : DatabaseStore;
        }

        if (store is not (DatabaseStore or DefaultStore))
        {
            throw new InvalidOperationException($"Unsupported DataProtection:Store '{store}'. Supported: {DatabaseStore}, {DefaultStore}.");
        }

        var certificate = LoadCertificate(configuration);
        if (!developmentLike)
        {
            if (store != DatabaseStore)
            {
                throw new InvalidOperationException("In production the Data Protection key ring must be stored in the database (DataProtection:Store=Database): the container file system is not persistent.");
            }

            if (certificate is null)
            {
                throw new InvalidOperationException(
                    "In production the Data Protection keys must be encrypted with a certificate. Set 'DataProtection:CertificatePfxBase64' (base64 of a .pfx) and "
                    + "'DataProtection:CertificatePassword' (see docs/DEPLOYMENT.md, 'Data Protection certificate').");
            }
        }

        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        if (store == DatabaseStore)
        {
            builder.PersistKeysToDbContext<AppDbContext>();
        }

        if (certificate is not null)
        {
            builder.ProtectKeysWithCertificate(certificate);
        }

        return services;
    }

    private static X509Certificate2? LoadCertificate(IConfiguration configuration)
    {
        var base64 = configuration["DataProtection:CertificatePfxBase64"];
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            // Ephemeral: the private key stays in memory, so nothing is written to the (read-only, non-root) container's user profile.
            var certificate = X509CertificateLoader.LoadPkcs12(bytes, configuration["DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException("The certificate has no private key.");
            }

            return certificate;
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            // The message is deliberately generic: never echo the password or the key material.
            throw new InvalidOperationException(
                "'DataProtection:CertificatePfxBase64' is not a usable base64 PKCS#12 (.pfx) certificate with a private key, or 'DataProtection:CertificatePassword' is wrong.", ex);
        }
    }
}
