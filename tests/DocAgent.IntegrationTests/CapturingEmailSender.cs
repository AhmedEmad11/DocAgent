using System.Collections.Concurrent;
using System.Net;
using DocAgent.Core.Entities;
using Microsoft.AspNetCore.Identity;

namespace DocAgent.IntegrationTests;

public sealed record CapturedEmail(string Kind, string To, string Value);

/// <summary>Test double for the SMTP sender: records what would have been emailed.</summary>
public sealed class CapturingEmailSender : IEmailSender<AppUser>
{
    private readonly ConcurrentQueue<CapturedEmail> _messages = new();

    public IReadOnlyCollection<CapturedEmail> Messages => _messages.ToArray();

    public Task SendConfirmationLinkAsync(AppUser user, string email, string confirmationLink)
    {
        // Identity HTML-encodes the link it hands to the sender.
        _messages.Enqueue(new("confirm", email, WebUtility.HtmlDecode(confirmationLink)));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(AppUser user, string email, string resetCode)
    {
        _messages.Enqueue(new("resetCode", email, resetCode));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(AppUser user, string email, string resetLink)
    {
        _messages.Enqueue(new("resetLink", email, WebUtility.HtmlDecode(resetLink)));
        return Task.CompletedTask;
    }

    public CapturedEmail Last(string kind, string to) =>
        _messages.Last(m => m.Kind == kind && string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase));
}
