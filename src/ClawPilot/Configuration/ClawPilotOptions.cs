namespace ClawPilot.Configuration;

public class ClawPilotOptions
{
    public const string SectionName = "ClawPilot";

    public required string TelegramBotToken { get; set; }
    public string BotUsername { get; set; } = "@ClawPilotBot";
    public HashSet<string> AllowedChatIds { get; set; } = [];

    public required string OpenRouterApiKey { get; set; }
    public string Model { get; set; } = "z-ai/glm-5";
    public string EmbeddingModel { get; set; } = "openai/text-embedding-3-small";

    public string? SystemPrompt { get; set; }
    public string DatabasePath { get; set; } = "clawpilot.db";
    public int MaxResponseTokens { get; set; } = 4096;
    public int MaxResponseLength { get; set; } = 4096;
    public int SessionTimeoutMinutes { get; set; } = 60;
}
