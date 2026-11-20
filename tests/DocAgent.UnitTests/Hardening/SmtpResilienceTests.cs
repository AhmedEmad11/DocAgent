using System.Net;
using System.Net.Sockets;
using System.Text;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocAgent.UnitTests.Hardening;

/// <summary>REL-2 for email: a dropped connection is retried, a hung or refusing server is not, and nothing ever throws into the request.</summary>
public sealed class SmtpResilienceTests
{
    private enum Mode { Normal, DropFirstConnection, Hang, RejectRecipient }

    /// <summary>A minimal SMTP server: enough of the protocol for MailKit to deliver one plain-text message.</summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Mode _mode;
        private int _connections;

        public FakeSmtpServer(Mode mode)
        {
            _mode = mode;
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref _connections);

        public int Delivered { get; private set; }

        private async Task AcceptLoop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var number = Interlocked.Increment(ref _connections);
                    _ = Task.Run(() => Serve(client, number));
                }
            }
            catch (Exception)
            {
                // stopped
            }
        }

        private async Task Serve(TcpClient client, int number)
        {
            using var _ = client;
            try
            {
                if (_mode == Mode.DropFirstConnection && number == 1)
                {
                    return; // closed without a greeting
                }

                if (_mode == Mode.Hang)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token); // accepts the connection, never speaks
                }

                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                async Task Say(string line) => await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), _stop.Token);

                await Say("220 fake ESMTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var command = line.ToUpperInvariant();
                    if (command.StartsWith("EHLO") || command.StartsWith("HELO"))
                    {
                        await Say("250 fake");
                    }
                    else if (command.StartsWith("RCPT") && _mode == Mode.RejectRecipient)
                    {
                        await Say("550 no such user");
                    }
                    else if (command.StartsWith("MAIL") || command.StartsWith("RCPT") || command.StartsWith("RSET"))
                    {
                        await Say("250 ok");
                    }
                    else if (command.StartsWith("DATA"))
                    {
                        await Say("354 go ahead");
                        while (await reader.ReadLineAsync(_stop.Token) is { } data && data != ".")
                        {
                        }

                        Delivered++;
                        await Say("250 queued");
                    }
                    else if (command.StartsWith("QUIT"))
                    {
                        await Say("221 bye");
                        return;
                    }
                    else
                    {
                        await Say("250 ok");
                    }
                }
            }
            catch (Exception)
            {
                // client went away
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static SmtpEmailSender Sender(FakeSmtpServer server, int timeoutSeconds = 5, int attempts = 2) =>
        new(Options.Create(new EmailOptions
        {
            Smtp = new SmtpOptions { Host = "127.0.0.1", Port = server.Port, TimeoutSeconds = timeoutSeconds, MaxAttempts = attempts, RetryDelayMilliseconds = 50 },
        }), NullLogger<SmtpEmailSender>.Instance);

    private static Task Send(SmtpEmailSender sender) =>
        sender.SendConfirmationLinkAsync(new AppUser(), "ada@example.com", "https://example.test/confirm?code=abc");

    [Fact]
    public async Task A_dropped_connection_is_retried_and_the_email_is_delivered_once()
    {
        using var server = new FakeSmtpServer(Mode.DropFirstConnection);

        await Send(Sender(server));

        Assert.Equal(2, server.Connections);
        Assert.Equal(1, server.Delivered);
    }

    [Fact]
    public async Task Without_a_retry_budget_the_dropped_connection_is_logged_and_swallowed()
    {
        using var server = new FakeSmtpServer(Mode.DropFirstConnection);

        await Send(Sender(server, attempts: 1)); // must not throw (D29)

        Assert.Equal(1, server.Connections);
        Assert.Equal(0, server.Delivered);
    }

    [Fact]
    public async Task A_hung_server_is_given_up_after_the_timeout_without_a_second_wait()
    {
        using var server = new FakeSmtpServer(Mode.Hang);
        var started = DateTime.UtcNow;

        await Send(Sender(server, timeoutSeconds: 1));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "the send must not wait for MailKit's two-minute default");
        Assert.Equal(1, server.Connections); // a timeout already used the whole wait: not retried
        Assert.Equal(0, server.Delivered);
    }

    [Fact]
    public async Task A_permanent_refusal_is_not_retried()
    {
        using var server = new FakeSmtpServer(Mode.RejectRecipient);

        await Send(Sender(server));

        Assert.Equal(1, server.Connections);
        Assert.Equal(0, server.Delivered);
    }

    [Fact]
    public async Task Defaults_are_a_ten_second_timeout_and_two_attempts()
    {
        var smtp = new SmtpOptions();
        Assert.Equal(10, smtp.TimeoutSeconds);
        Assert.Equal(2, smtp.MaxAttempts);
        await Task.CompletedTask;
    }
}
