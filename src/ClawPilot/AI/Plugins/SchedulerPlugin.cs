using System.ComponentModel;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClawPilot.Database;
using ClawPilot.Database.Entities;
using Microsoft.SemanticKernel;
using Microsoft.EntityFrameworkCore;

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

    [KernelFunction("list_tasks")]
    [Description("List all active scheduled tasks for the current chat.")]
    public async Task<string> ListTasksAsync(
        [Description("The numeric Telegram chat ID from the current conversation context")] long chatId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
        var tasks = await db.ScheduledTasks
            .Where(t => t.IsActive && t.ChatId == chatId.ToString())
            .ToListAsync(ct);

        if (tasks.Count == 0)
        {
            return "No active scheduled tasks found.";
        }

        var result = "Active scheduled tasks:\n";
        foreach (var task in tasks)
        {
            result += $"ID: {task.Id} - {task.Description} (Cron: {task.CronExpression})\n";
        }
        // current timestamp to indicate when the list was generated
        result += $"Current Timestamp:  {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        return result;
    }


    [KernelFunction("pause_task")]
    [Description("Pause a scheduled task for the current chat.")]
    public async Task<string> PauseTaskAsync(
        [Description("The numeric Telegram chat ID from the current conversation context")] long chatId,
        [Description("Task Id to pause")] int taskId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
        var task = await db.ScheduledTasks
            .Where(t => t.IsActive && t.ChatId == chatId.ToString() && t.Id == taskId)
            .FirstOrDefaultAsync(ct);
        if (task == null)
        {
            return "No active scheduled tasks found.";
        }

        task.IsActive = false;
        await db.SaveChangesAsync(ct);
        return $"Paused scheduled task with ID: {taskId}. for chat ID: {chatId}.";
    }

    [KernelFunction("resume_task")]
    [Description("Resume a paused scheduled task for the current chat.")]
    public async Task<string> ResumeTaskAsync(
        [Description("The numeric Telegram chat ID from the current conversation context")] long chatId,
        [Description("Task Id to resume")] int taskId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
        var task = await db.ScheduledTasks
            .Where(t => !t.IsActive && t.ChatId == chatId.ToString() && t.Id == taskId)
            .FirstOrDefaultAsync(ct);
        if (task == null)
        {
            return "No paused scheduled tasks found.";
        }

        task.IsActive = true;
        await db.SaveChangesAsync(ct);
        return $"Resumed scheduled task with ID: {taskId}. for chat ID: {chatId}.";
    }

    //cancel_task
    [KernelFunction("cancel_task")]
    [Description("Cancel a scheduled task for the current chat.")]
    public async Task<string> CancelTaskAsync(
        [Description("The numeric Telegram chat ID from the current conversation context")] long chatId,
        [Description("Task Id to cancel")] int taskId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
        var task = await db.ScheduledTasks
            .Where(t => t.ChatId == chatId.ToString() && t.Id == taskId)
            .FirstOrDefaultAsync(ct);
        if (task == null)
        {
            return "No scheduled tasks found with the specified ID.";
        }

        db.ScheduledTasks.Remove(task);
        await db.SaveChangesAsync(ct);
        return $"Cancelled scheduled task with ID: {taskId}. for chat ID: {chatId}.";
    }
}
