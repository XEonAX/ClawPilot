using ClawPilot.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace ClawPilot.AI;

public class MemoryService : IAsyncDisposable
{
    private readonly ILogger<MemoryService> _logger;
    private readonly SqliteVectorStore? _store;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingService;
    private VectorStoreCollection<string, MemoryRecord>? _collection;
    private bool _initialized;
    private const string CollectionName = "conversations";

    public MemoryService(ClawPilotOptions options, ILogger<MemoryService> logger, IEmbeddingGenerator<string, Embedding<float>>? embeddingService = null)
    {
        _logger = logger;
        _embeddingService = embeddingService;

        try
        {
            _store = new SqliteVectorStore($"Data Source={options.DatabasePath}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize SqliteVectorStore, memory features disabled");
            _store = null;
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized || _store is null || _embeddingService is null)
            return;

        _collection = _store.GetCollection<string, MemoryRecord>(CollectionName);
        await _collection.EnsureCollectionExistsAsync(ct);
        _initialized = true;
    }

    public async Task SaveAsync(
        string conversationId, string userMessage, string assistantResponse,
        CancellationToken ct = default)
    {
        if (_embeddingService is null || _store is null)
            return;

        try
        {
            await EnsureInitializedAsync(ct);
            if (_collection is null) return;

            var text = $"User: {userMessage}\nAssistant: {assistantResponse}";
            var embeddingResult = await _embeddingService.GenerateAsync(text, cancellationToken: ct);
            var embedding = embeddingResult.Vector;

            var record = new MemoryRecord
            {
                Key = $"{conversationId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                Text = text,
                ConversationId = conversationId,
                Embedding = embedding,
            };

            await _collection.UpsertAsync(record, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save memory for {ConversationId}", conversationId);
        }
    }

    public async Task<List<string>> RecallAsync(
        string conversationId, string query, int limit = 5,
        CancellationToken ct = default)
    {
        if (_embeddingService is null || _store is null || _collection is null || !_initialized)
            return [];

        try
        {
            var embeddingResult = await _embeddingService.GenerateAsync(query, cancellationToken: ct);
            var queryEmbedding = embeddingResult.Vector;

            var results = await _collection.SearchAsync(queryEmbedding, limit, cancellationToken: ct).ToListAsync(ct);

            return results
                .Where(r => r.Score >= 0.7)
                .Select(r => r.Record.Text)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to recall memory for {ConversationId}", conversationId);
            return [];
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

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
