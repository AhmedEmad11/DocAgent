namespace DocAgent.Api.Security;

/// <summary>
/// Container hosts such as SnapDeploy manage the listening port themselves and pass it in the <c>PORT</c> environment variable.
/// When <c>PORT</c> is set the app listens on it (all interfaces, plain HTTP: TLS ends at the host's edge); otherwise the image default
/// applies (<c>ASPNETCORE_HTTP_PORTS=8080</c>, which Azure Container Apps targets). <c>ASPNETCORE_URLS</c>, if someone sets it, still wins.
/// </summary>
public static class ListenPort
{
    public const string EnvironmentVariable = "PORT";
    public const int Default = 8080;

    /// <summary>The port from <c>PORT</c>, or null when it is not set. A value that is not a port number stops the startup with a clear message.</summary>
    public static int? Resolve(IConfiguration configuration)
    {
        var raw = configuration[EnvironmentVariable];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"PORT must be a port number between 1 and 65535 (got '{raw}'). Unset it to use the default {Default}.");
        }

        return port;
    }

    /// <summary>Maps <c>PORT</c> to the framework's <c>HTTP_PORTS</c> setting (before the host is built). Returns the port used, or null when <c>PORT</c> is not set.</summary>
    public static int? Apply(ConfigurationManager configuration)
    {
        var port = Resolve(configuration);
        if (port is { } p)
        {
            configuration["HTTP_PORTS"] = p.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return port;
    }
}
