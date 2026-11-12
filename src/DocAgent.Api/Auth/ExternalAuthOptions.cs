namespace DocAgent.Api.Auth;

public sealed class ExternalAuthOptions
{
    public const string SectionName = "Authentication:External";

    /// <summary>Absolute URL prefixes the client may be redirected to after external sign-in.</summary>
    public string[] AllowedReturnUrls { get; set; } = [];
}
