namespace DocAgent.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public SmtpOptions Smtp { get; set; } = new();
    public string FromAddress { get; set; } = "noreply@docagent.local";
    public string FromName { get; set; } = "DocAgent";
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 25;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseStartTls { get; set; }
}
