using ClawPilot.AI;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Database;
using ClawPilot.Database.Entities;
using ClawPilot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Moq;
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

    private static (TaskSchedulerService scheduler, IServiceScopeFactory scopeFactory, Mock<AgentOrchestrator> orchestratorMock, Mock<ITelegramChannel> telegramMock) CreateScheduler()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ClawPilotDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var telegramMock = new Mock<ITelegramChannel>();
        var mockOptions = Options.Create(new ClawPilotOptions
        {
            TelegramBotToken = "test",
            OpenRouterApiKey = "test",
        });
        var kernel = Kernel.CreateBuilder().Build();
        var memory = new MemoryService(mockOptions.Value, NullLogger<MemoryService>.Instance);
        var orchestratorMock = new Mock<AgentOrchestrator>(
            MockBehavior.Loose, kernel, memory, mockOptions, NullLogger<AgentOrchestrator>.Instance);
        orchestratorMock
            .Setup(o => o.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Scheduled response");

        var scheduler = new TaskSchedulerService(
            scopeFactory, orchestratorMock.Object, telegramMock.Object,
            NullLogger<TaskSchedulerService>.Instance);

        return (scheduler, scopeFactory, orchestratorMock, telegramMock);
    }

    [Fact]
    public async Task TaskSchedulerService_ExecutesDueTasks()
    {
        // §5: Full end-to-end — create a task that's due now, verify it executes
        var (scheduler, scopeFactory, orchestratorMock, telegramMock) = CreateScheduler();
        var now = DateTimeOffset.UtcNow;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
            db.ScheduledTasks.Add(new ScheduledTask
            {
                ChatId = "123",
                Description = "Test task",
                CronExpression = $"{now.Minute} {now.Hour} * * *",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        await scheduler.CheckAndExecuteTasksAsync(CancellationToken.None);

        orchestratorMock.Verify(
            o => o.SendMessageAsync("123", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        telegramMock.Verify(
            t => t.SendTextAsync(123, "Scheduled response", null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TaskSchedulerService_SkipsInactiveTasks()
    {
        // §5: Inactive tasks should not execute
        var (scheduler, scopeFactory, orchestratorMock, _) = CreateScheduler();
        var now = DateTimeOffset.UtcNow;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
            db.ScheduledTasks.Add(new ScheduledTask
            {
                ChatId = "123",
                Description = "Inactive task",
                CronExpression = $"{now.Minute} {now.Hour} * * *",
                IsActive = false,
            });
            await db.SaveChangesAsync();
        }

        await scheduler.CheckAndExecuteTasksAsync(CancellationToken.None);

        orchestratorMock.Verify(
            o => o.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TaskSchedulerService_UpdatesLastRunAt()
    {
        // §5: After execution, LastRunAt should be updated
        var (scheduler, scopeFactory, _, _) = CreateScheduler();
        var now = DateTimeOffset.UtcNow;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
            db.ScheduledTasks.Add(new ScheduledTask
            {
                ChatId = "456",
                Description = "Updatable task",
                CronExpression = $"{now.Minute} {now.Hour} * * *",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        await scheduler.CheckAndExecuteTasksAsync(CancellationToken.None);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
            var task = await db.ScheduledTasks.FirstAsync();
            Assert.NotNull(task.LastRunAt);
        }
    }
}
