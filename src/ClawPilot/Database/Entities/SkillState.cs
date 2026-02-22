namespace ClawPilot.Database.Entities;

/// <summary>
/// Persists skill enabled/disabled state across restarts (§3.8).
/// </summary>
public class SkillState
{
    public int Id { get; set; }
    public required string SkillName { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
