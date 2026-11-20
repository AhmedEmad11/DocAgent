namespace DocAgent.Core.Entities;

/// <summary>Demo questions a user has used on one UTC calendar day (SRS DEMO-2). One row per user and day; deleted with the user.</summary>
public class DemoUsage
{
    public Guid UserId { get; set; }

    /// <summary>The UTC calendar day.</summary>
    public DateOnly Day { get; set; }

    public int Count { get; set; }
}
