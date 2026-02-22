using ClawPilot.AI;
using ClawPilot.Channels;
using ClawPilot.Database;
using Microsoft.EntityFrameworkCore;

namespace ClawPilot.Services;

public class TaskSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentOrchestrator _orchestrator;
    private readonly ITelegramChannel _telegram;
    private readonly ILogger<TaskSchedulerService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public TaskSchedulerService(
        IServiceScopeFactory scopeFactory,
        AgentOrchestrator orchestrator,
        ITelegramChannel telegram,
        ILogger<TaskSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _orchestrator = orchestrator;
        _telegram = telegram;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TaskSchedulerService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndExecuteTasksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in task scheduler loop");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    internal async Task CheckAndExecuteTasksAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();

        var tasks = await db.ScheduledTasks
            .Where(t => t.IsActive)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;

        foreach (var task in tasks)
        {
            if (!IsDue(task.CronExpression, task.LastRunAt, now))
                continue;

            _logger.LogInformation("Executing scheduled task {TaskId}: {Description}", task.Id, task.Description);

            try
            {
                var chatId = long.Parse(task.ChatId);
                var prompt = $"You have a scheduled task: \"{task.Description}\". Please execute or respond to this task now.";
                var response = await _orchestrator.SendMessageAsync(
                    task.ChatId, prompt, "You are a helpful assistant executing a scheduled task.", ct);

                await _telegram.SendTextAsync(chatId, response, ct: ct);

                task.LastRunAt = now;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute scheduled task {TaskId}", task.Id);
            }
        }
    }

    internal static bool IsDue(string cronExpression, DateTimeOffset? lastRunAt, DateTimeOffset now)
    {
        var parts = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return false;

        if (!MatchesCronField(parts[0], now.Minute)) return false;
        if (!MatchesCronField(parts[1], now.Hour)) return false;
        if (!MatchesCronField(parts[2], now.Day)) return false;
        if (!MatchesCronField(parts[3], now.Month)) return false;
        if (!MatchesCronField(parts[4], (int)now.DayOfWeek)) return false;

        if (lastRunAt.HasValue)
        {
            var elapsed = now - lastRunAt.Value;
            if (elapsed.TotalSeconds < 59)
                return false;
        }

        return true;
    }

    internal static bool MatchesCronField(string field, int value)
    {
        if (field == "*")
            return true;

        if (field.Contains('/'))
        {
            var stepParts = field.Split('/');
            if (stepParts.Length == 2
                && int.TryParse(stepParts[1], out var step)
                && step > 0)
            {
                var basePart = stepParts[0];
                int start = basePart == "*" ? 0 : int.TryParse(basePart, out var s) ? s : 0;
                return (value - start) % step == 0 && value >= start;
            }
            return false;
        }

        if (field.Contains(','))
        {
            return field.Split(',').Any(v => int.TryParse(v.Trim(), out var n) && n == value);
        }

        if (field.Contains('-'))
        {
            var rangeParts = field.Split('-');
            if (rangeParts.Length == 2
                && int.TryParse(rangeParts[0], out var low)
                && int.TryParse(rangeParts[1], out var high))
            {
                return value >= low && value <= high;
            }
            return false;
        }

        return int.TryParse(field, out var exact) && exact == value;
    }
}
