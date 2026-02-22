namespace ClawPilot.Database.Entities;

public class Conversation
{
    public int Id { get; set; }
    public required string ChatId { get; set; }
    public string? DisplayName { get; set; }
    public bool IsGroup { get; set; }
    public string? SessionId { get; set; }
    public string? SystemPrompt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<Message> Messages { get; set; } = [];
}
