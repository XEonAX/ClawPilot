using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace ClawPilot.AI.Plugins;

public class UtilityPlugin
{
    private readonly MemoryService _memory;

    public UtilityPlugin(MemoryService memory)
    {
        _memory = memory;
    }

    [KernelFunction("get_current_datetime")]
    [Description("Get the current date and time in UTC and as a Unix timestamp.")]
    public string GetCurrentDateTime()
    {
        var now = DateTimeOffset.UtcNow;
        return $"UTC: {now:yyyy-MM-dd HH:mm:ss}, Unix: {now.ToUnixTimeSeconds()}";
    }

    [KernelFunction("recall_memory")]
    [Description("Search conversation memory for relevant past context.")]
    public async Task<string> RecallMemoryAsync(
        [Description("The conversation ID to search within")] string conversationId,
        [Description("The query to search for relevant memories")] string query,
        CancellationToken ct = default)
    {
        var results = await _memory.RecallAsync(conversationId, query, limit: 5, ct);
        return results.Count == 0
            ? "No relevant memories found."
            : string.Join("\n---\n", results);
    }
}
