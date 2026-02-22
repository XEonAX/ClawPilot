namespace ClawPilot.Database.Entities;

public class Message
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public required string Role { get; set; }
    public required string Content { get; set; }
    public long? TelegramMessageId { get; set; }
    public string? SenderName { get; set; }
    public string? SenderId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
