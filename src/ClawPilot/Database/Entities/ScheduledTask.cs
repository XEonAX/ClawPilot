namespace ClawPilot.Database.Entities;

public class ScheduledTask
{
    public int Id { get; set; }
    public required string ChatId { get; set; }
    public required string Description { get; set; }
    public required string CronExpression { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
