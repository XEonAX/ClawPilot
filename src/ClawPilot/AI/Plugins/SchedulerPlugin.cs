using System.ComponentModel;
using ClawPilot.Database;
using ClawPilot.Database.Entities;
using Microsoft.SemanticKernel;

namespace ClawPilot.AI.Plugins;

public class SchedulerPlugin
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SchedulerPlugin(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [KernelFunction("schedule_task")]
    [Description("Schedule a recurring task with a cron expression for a specific chat. Use the chat ID from the current conversation context.")]
    public async Task<string> ScheduleTaskAsync(
        [Description("The numeric Telegram chat ID from the current conversation context")] long chatId,
        [Description("A human-readable description of the task")] string description,
        [Description("A cron expression (e.g. '0 9 * * *' for daily at 9:00 UTC)")] string cronExpression,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();

        db.ScheduledTasks.Add(new ScheduledTask
        {
            ChatId = chatId.ToString(),
            Description = description,
            CronExpression = cronExpression,
            IsActive = true,
        });

        await db.SaveChangesAsync(ct);
        return $"Task scheduled: \"{description}\" with cron \"{cronExpression}\".";
    }
}
