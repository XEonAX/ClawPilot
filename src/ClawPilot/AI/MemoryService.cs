using System.ClientModel;
using ClawPilot.Configuration;
using ClawPilot.Database.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OpenAI;

namespace ClawPilot.AI;

public class MemoryService : IAsyncDisposable
{
    private readonly ILogger<MemoryService> _logger;
    private readonly SqliteVectorStore? _store;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingService;
    private VectorStoreCollection<string, MemoryRecord>? _collection;
    private bool _initialized;
    private const string CollectionName = "conversations";

    public MemoryService(IOptions<ClawPilotOptions> options, ILogger<MemoryService> logger)
    {
        _logger = logger;
        var opts = options.Value;

        try
        {
            _store = new SqliteVectorStore($"Data Source={opts.DatabasePath}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize SqliteVectorStore, memory features disabled");
            _store = null;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(opts.OpenRouterApiKey) && !string.IsNullOrWhiteSpace(opts.EmbeddingModel))
            {
                var openAiClient = new OpenAIClient(
                    new ApiKeyCredential(opts.OpenRouterApiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(OpenRouterConfig.BaseUrl) });

                var embKernelBuilder = Kernel.CreateBuilder();
#pragma warning disable SKEXP0010 // Experimental embedding generator API
                embKernelBuilder.AddOpenAIEmbeddingGenerator(
                    modelId: opts.EmbeddingModel,
                    openAIClient: openAiClient);
#pragma warning restore SKEXP0010
                var embKernel = embKernelBuilder.Build();
                _embeddingService = embKernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            }
            else
            {
                _logger.LogWarning("OpenRouter API key or embedding model not configured, memory features disabled");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize embedding service, memory features disabled");
            _embeddingService = null;
        }
    }

    /// <summary>
    /// Internal constructor for tests — allows running without a real embedding service.
    /// </summary>
    internal MemoryService(ClawPilotOptions options, ILogger<MemoryService> logger)
        : this(Options.Create(options), logger)
    {
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
