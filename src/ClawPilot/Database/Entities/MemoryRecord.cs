using Microsoft.Extensions.VectorData;

namespace ClawPilot.Database.Entities;

public sealed class MemoryRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    [VectorStoreData]
    public string Text { get; set; } = string.Empty;

    [VectorStoreData]
    public string ConversationId { get; set; } = string.Empty;

    [VectorStoreVector(1536)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
