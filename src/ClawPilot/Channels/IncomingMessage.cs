namespace ClawPilot.Channels;

public record IncomingMessage(
    long ChatId,
    long MessageId,
    string Text,
    string SenderName,
    string SenderId,
    bool IsGroup,
    string? GroupName,
    DateTimeOffset Timestamp
);
