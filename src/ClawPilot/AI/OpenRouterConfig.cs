namespace ClawPilot.AI;

/// <summary>
/// Centralizes OpenRouter-specific configuration constants and helpers.
/// </summary>
public static class OpenRouterConfig
{
    public const string BaseUrl = "https://openrouter.ai/api/v1";
    public const string ModelsEndpoint = "https://openrouter.ai/api/v1/models";

    /// <summary>
    /// Returns the OpenRouter API base URI.
    /// </summary>
    public static Uri Endpoint => new(BaseUrl);
}
