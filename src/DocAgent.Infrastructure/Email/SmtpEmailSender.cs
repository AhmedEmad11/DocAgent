using System.Net;
using DocAgent.Core.Entities;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DocAgent.Infrastructure.Email;

/// <summary>
/// Sends Identity emails (confirmation link, password-reset code/link) over SMTP with MailKit.
/// Delivery failures are logged (never the message body, link, code or credentials) and swallowed
/// so a mail outage does not turn a registration into a 500; users can request another email.
/// </summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    : IEmailSender<AppUser>
{
    public Task SendConfirmationLinkAsync(AppUser user, string email, string confirmationLink)
    {
        // Identity passes the link HTML-encoded; decode it for the plain-text part. With Email:ConfirmationUrlBase set the link opens the UI (plan A5).
        var plainLink = ConfirmationLink.Rewrite(confirmationLink, options.Value.ConfirmationUrlBase);
        var htmlLink = WebUtility.HtmlEncode(plainLink);
        return SendAsync(email, "Confirm your DocAgent email",
            $"Welcome to DocAgent.\n\nConfirm your email address by opening this link:\n{plainLink}\n\nIf you did not create an account, ignore this message.",
            $"<p>Welcome to DocAgent.</p><p><a href=\"{htmlLink}\">Confirm your email address</a></p><p>If you did not create an account, ignore this message.</p>");
    }

    public Task SendPasswordResetCodeAsync(AppUser user, string email, string resetCode) =>
        SendAsync(email, "Your DocAgent password reset code",
            $"Use this code to reset your DocAgent password:\n\n{resetCode}\n\nIf you did not request this, ignore this message.",
            $"<p>Use this code to reset your DocAgent password:</p><p><code>{WebUtility.HtmlEncode(resetCode)}</code></p><p>If you did not request this, ignore this message.</p>");

    public Task SendPasswordResetLinkAsync(AppUser user, string email, string resetLink)
    {
        var plainLink = WebUtility.HtmlDecode(resetLink);
        return SendAsync(email, "Reset your DocAgent password",
            $"Reset your DocAgent password by opening this link:\n{plainLink}\n\nIf you did not request this, ignore this message.",
            $"<p><a href=\"{resetLink}\">Reset your DocAgent password</a></p><p>If you did not request this, ignore this message.</p>");
    }

    private async Task SendAsync(string to, string subject, string text, string html)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Smtp.Host))
        {
            logger.LogWarning("Email not sent: Email:Smtp:Host is not configured.");
            return;
        }

        MimeMessage message;
        try
        {
            message = new MimeMessage();
            message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to build email ({ExceptionType}).", ex.GetType().Name);
            return;
        }

        var attempts = Math.Max(1, o.Smtp.MaxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var client = new SmtpClient { Timeout = Math.Max(1, o.Smtp.TimeoutSeconds) * 1000 };
                await client.ConnectAsync(
                    o.Smtp.Host, o.Smtp.Port,
                    o.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None);
                if (!string.IsNullOrEmpty(o.Smtp.Username))
                {
                    await client.AuthenticateAsync(o.Smtp.Username, o.Smtp.Password ?? string.Empty);
                }

                await client.SendAsync(message);
                await client.DisconnectAsync(true);
                return;
            }
            catch (Exception ex)
            {
                if (attempt < attempts && IsTransient(ex))
                {
                    logger.LogWarning("Sending email failed ({ExceptionType}); retrying (attempt {Attempt} of {Attempts}).", ex.GetType().Name, attempt, attempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, o.Smtp.RetryDelayMilliseconds) * Math.Pow(2, attempt - 1)));
                    continue;
                }

                // Log the exception type only: messages may echo server responses or addresses.
                logger.LogError("Failed to send email ({ExceptionType}).", ex.GetType().Name);
                return;
            }
        }
    }

    /// <summary>Connection-level failures and temporary (4xx) SMTP replies; not timeouts, authentication or permanent (5xx) errors.</summary>
    internal static bool IsTransient(Exception ex) => ex switch
    {
        SmtpCommandException command => (int)command.StatusCode / 100 == 4,
        AuthenticationException or SslHandshakeException => false,
        ServiceNotConnectedException or SmtpProtocolException or System.IO.IOException or System.Net.Sockets.SocketException => true,
        _ => false,
    };
}
