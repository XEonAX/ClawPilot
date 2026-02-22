using ClawPilot.Services;
using Xunit;

namespace ClawPilot.Tests;

public class TaskSchedulerTests
{
    [Fact]
    public void IsDue_MatchesExactTime()
    {
        var now = new DateTimeOffset(2026, 2, 22, 9, 0, 0, TimeSpan.Zero);
        Assert.True(TaskSchedulerService.IsDue("0 9 * * *", null, now));
    }

    [Fact]
    public void IsDue_DoesNotMatchWrongTime()
    {
        var now = new DateTimeOffset(2026, 2, 22, 10, 30, 0, TimeSpan.Zero);
        Assert.False(TaskSchedulerService.IsDue("0 9 * * *", null, now));
    }

    [Fact]
    public void IsDue_SkipsIfRecentlyRun()
    {
        var now = new DateTimeOffset(2026, 2, 22, 9, 0, 0, TimeSpan.Zero);
        var lastRun = now.AddSeconds(-30);
        Assert.False(TaskSchedulerService.IsDue("0 9 * * *", lastRun, now));
    }

    [Fact]
    public void IsDue_RunsIfLastRunOldEnough()
    {
        var now = new DateTimeOffset(2026, 2, 22, 9, 0, 0, TimeSpan.Zero);
        var lastRun = now.AddMinutes(-2);
        Assert.True(TaskSchedulerService.IsDue("0 9 * * *", lastRun, now));
    }

    [Fact]
    public void IsDue_WildcardMatchesAll()
    {
        var now = new DateTimeOffset(2026, 2, 22, 14, 37, 0, TimeSpan.Zero);
        Assert.True(TaskSchedulerService.IsDue("* * * * *", null, now));
    }

    [Fact]
    public void IsDue_StepExpression()
    {
        var at0 = new DateTimeOffset(2026, 2, 22, 9, 0, 0, TimeSpan.Zero);
        var at15 = new DateTimeOffset(2026, 2, 22, 9, 15, 0, TimeSpan.Zero);
        var at7 = new DateTimeOffset(2026, 2, 22, 9, 7, 0, TimeSpan.Zero);
        Assert.True(TaskSchedulerService.IsDue("*/15 * * * *", null, at0));
        Assert.True(TaskSchedulerService.IsDue("*/15 * * * *", null, at15));
        Assert.False(TaskSchedulerService.IsDue("*/15 * * * *", null, at7));
    }

    [Fact]
    public void IsDue_CommaList()
    {
        var at9 = new DateTimeOffset(2026, 2, 22, 9, 0, 0, TimeSpan.Zero);
        var at17 = new DateTimeOffset(2026, 2, 22, 17, 0, 0, TimeSpan.Zero);
        var at12 = new DateTimeOffset(2026, 2, 22, 12, 0, 0, TimeSpan.Zero);
        Assert.True(TaskSchedulerService.IsDue("0 9,17 * * *", null, at9));
        Assert.True(TaskSchedulerService.IsDue("0 9,17 * * *", null, at17));
        Assert.False(TaskSchedulerService.IsDue("0 9,17 * * *", null, at12));
    }

    [Fact]
    public void IsDue_RangeExpression()
    {
        var monday = new DateTimeOffset(2026, 2, 23, 9, 0, 0, TimeSpan.Zero);
        var saturday = new DateTimeOffset(2026, 2, 28, 9, 0, 0, TimeSpan.Zero);
        Assert.True(TaskSchedulerService.IsDue("0 9 * * 1-5", null, monday));
        Assert.False(TaskSchedulerService.IsDue("0 9 * * 1-5", null, saturday));
    }

    [Fact]
    public void IsDue_InvalidCronReturnsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(TaskSchedulerService.IsDue("bad", null, now));
        Assert.False(TaskSchedulerService.IsDue("1 2", null, now));
    }

    [Fact]
    public void MatchesCronField_AllPatterns()
    {
        Assert.True(TaskSchedulerService.MatchesCronField("*", 5));
        Assert.True(TaskSchedulerService.MatchesCronField("5", 5));
        Assert.False(TaskSchedulerService.MatchesCronField("3", 5));
        Assert.True(TaskSchedulerService.MatchesCronField("3,5,7", 5));
        Assert.False(TaskSchedulerService.MatchesCronField("3,7", 5));
        Assert.True(TaskSchedulerService.MatchesCronField("1-10", 5));
        Assert.False(TaskSchedulerService.MatchesCronField("6-10", 5));
        Assert.True(TaskSchedulerService.MatchesCronField("*/5", 10));
        Assert.False(TaskSchedulerService.MatchesCronField("*/5", 7));
    }
}
