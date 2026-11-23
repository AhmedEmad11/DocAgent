using DocAgent.Core.Auth;

namespace DocAgent.UnitTests.Auth;

public sealed class UploadTicketServiceTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_ticket_is_redeemed_once_for_the_user_it_was_issued_to()
    {
        var service = new UploadTicketService(new ManualClock());
        var user = Guid.NewGuid();

        var (ticket, expiresIn) = service.Issue(user);

        Assert.Equal(60, expiresIn);
        Assert.Equal(user, service.Redeem(ticket));
        Assert.Null(service.Redeem(ticket)); // single use
    }

    [Fact]
    public void A_ticket_expires_after_sixty_seconds()
    {
        var clock = new ManualClock();
        var service = new UploadTicketService(clock);
        var (ticket, _) = service.Issue(Guid.NewGuid());

        clock.Now += TimeSpan.FromSeconds(61);

        Assert.Null(service.Redeem(ticket));
    }

    [Fact]
    public void A_ticket_is_still_valid_just_before_it_expires()
    {
        var clock = new ManualClock();
        var service = new UploadTicketService(clock);
        var user = Guid.NewGuid();
        var (ticket, _) = service.Issue(user);

        clock.Now += TimeSpan.FromSeconds(59);

        Assert.Equal(user, service.Redeem(ticket));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-ticket")]
    public void Unknown_or_empty_tickets_are_refused(string? ticket)
    {
        Assert.Null(new UploadTicketService(new ManualClock()).Redeem(ticket));
    }

    [Fact]
    public void An_overlong_value_is_refused_without_hashing_it()
    {
        Assert.Null(new UploadTicketService(new ManualClock()).Redeem(new string('a', 5000)));
    }

    [Fact]
    public void Tickets_are_unique_and_url_safe()
    {
        var service = new UploadTicketService(new ManualClock());
        var tickets = Enumerable.Range(0, 20).Select(_ => service.Issue(Guid.NewGuid()).Ticket).ToList();

        Assert.Equal(20, tickets.Distinct().Count());
        Assert.All(tickets, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
    }

    [Fact]
    public void One_user_keeps_at_most_five_unused_tickets_and_the_oldest_is_dropped()
    {
        var clock = new ManualClock();
        var service = new UploadTicketService(clock);
        var user = Guid.NewGuid();
        var other = service.Issue(Guid.NewGuid()).Ticket;
        var issued = new List<string>();
        for (var i = 0; i < UploadTicketService.MaxOutstandingPerUser + 2; i++)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            issued.Add(service.Issue(user).Ticket);
        }

        Assert.Null(service.Redeem(issued[0]));
        Assert.Null(service.Redeem(issued[1]));
        foreach (var ticket in issued.Skip(2))
        {
            Assert.Equal(user, service.Redeem(ticket));
        }

        Assert.NotNull(service.Redeem(other)); // another user's ticket is untouched
    }
}
