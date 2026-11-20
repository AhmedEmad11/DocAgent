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

    /// <summary>Connect and per-command timeout. The send happens inside the registration / reset request, so a hung mail server must not hold it for MailKit's default two minutes.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Attempts per email when the failure looks transient (connection dropped or refused, temporary 4xx reply). A timeout is not retried: it already used the whole wait.</summary>
    public int MaxAttempts { get; set; } = 2;

    public int RetryDelayMilliseconds { get; set; } = 1000;
}
