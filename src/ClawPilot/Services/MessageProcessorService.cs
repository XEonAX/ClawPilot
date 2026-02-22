using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using ClawPilot.AI;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Database;
using ClawPilot.Database.Entities;
using ClawPilot.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;

namespace ClawPilot.Services;

public class MessageProcessorService : BackgroundService
{
    private readonly Channel<IncomingMessage> _channel;
    private readonly AgentOrchestrator _orchestrator;
    private readonly ITelegramChannel _telegram;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ClawPilotOptions _options;
    private readonly ILogger<MessageProcessorService> _logger;
    private readonly SkillLoaderService _skillLoader;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatLocks = new();

    public MessageProcessorService(
        Channel<IncomingMessage> channel,
        AgentOrchestrator orchestrator,
        ITelegramChannel telegram,
        IServiceScopeFactory scopeFactory,
        IOptions<ClawPilotOptions> options,
        SkillLoaderService skillLoader,
        ILogger<MessageProcessorService> logger)
    {
        _channel = channel;
        _orchestrator = orchestrator;
        _telegram = telegram;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _skillLoader = skillLoader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MessageProcessorService started");

        await foreach (var message in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            _ = ProcessMessageAsync(message, stoppingToken);
        }

        _logger.LogInformation("MessageProcessorService stopping");
    }

    internal async Task ProcessMessageAsync(IncomingMessage message, CancellationToken ct)
    {
        var chatKey = message.ChatId.ToString();
        var semaphore = _chatLocks.GetOrAdd(chatKey, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(ct);
        try
        {
            await _telegram.SendTypingAsync(message.ChatId, ct);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();

            var conversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.ChatId == chatKey, ct);

            if (conversation is null)
            {
                conversation = CreateConversation(message);
                db.Conversations.Add(conversation);
                await db.SaveChangesAsync(ct);
            }

            db.Messages.Add(new Message
            {
                ConversationId = conversation.Id,
                Role = "user",
                Content = message.Text,
                TelegramMessageId = message.MessageId,
                SenderName = message.SenderName,
                SenderId = message.SenderId,
                Status = "processing",
            });
            await db.SaveChangesAsync(ct);

            var systemPrompt = BuildSystemPrompt(conversation, message);

            // §2.4: Restore session on first message after process restart
            if (!_orchestrator.HasHistory(chatKey))
            {
                var recentMessages = await db.Messages
                    .Where(m => m.ConversationId == conversation.Id)
                    .OrderByDescending(m => m.CreatedAt)
                    .Take(50)
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new { m.Role, m.Content })
                    .ToListAsync(ct);

                if (recentMessages.Count > 0)
                {
                    var pairs = recentMessages
                        .Select(m => (m.Role, m.Content))
                        .ToList();
                    await _orchestrator.RestoreSessionAsync(chatKey, systemPrompt, pairs);
                    _logger.LogInformation(
                        "Restored {Count} messages for conversation {ChatId}",
                        recentMessages.Count, chatKey);
                }
            }

            var response = await _orchestrator.SendMessageAsync(
                chatKey, message.Text, systemPrompt, ct);

            db.Messages.Add(new Message
            {
                ConversationId = conversation.Id,
                Role = "assistant",
                Content = response,
                Status = "done",
            });

            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await _telegram.SendTextAsync(message.ChatId, response, message.MessageId, ct);

            _logger.LogDebug("Processed message {MessageId} for chat {ChatId}", message.MessageId, message.ChatId);
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx,
                "LLM request failed for message {MessageId} in chat {ChatId} with status {StatusCode}",
                message.MessageId, message.ChatId, httpEx.StatusCode);
            await TrySendErrorAsync(message.ChatId, ct);
        }
        catch (ApiRequestException apiEx)
        {
            _logger.LogError(apiEx,
                "Telegram API error for chat {ChatId}: {ErrorCode} {Description}",
                message.ChatId, apiEx.ErrorCode, apiEx.Message);
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogError(dbEx,
                "Database error processing message {MessageId} for chat {ChatId}",
                message.MessageId, message.ChatId);
            await TrySendErrorAsync(message.ChatId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Message processing cancelled for chat {ChatId}", message.ChatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing message {MessageId} for chat {ChatId}", message.MessageId, message.ChatId);
            await TrySendErrorAsync(message.ChatId, ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    internal string BuildSystemPrompt(Conversation conversation, IncomingMessage message)
    {
        // §2.1: Start from the global config prompt, then append conversation-level override
        var prompt = _options.SystemPrompt ?? "You are a helpful personal assistant.";

        if (conversation.SystemPrompt is not null)
            prompt += $"\n\nAdditional context:\n{conversation.SystemPrompt}";

        prompt = _skillLoader.AppendSkillPrompts(prompt);

        if (message.IsGroup)
        {
            prompt += $"\n\nYou are in a group chat called \"{message.GroupName ?? "Unknown"}\".";
            prompt += $"\nThe current speaker is {message.SenderName}.";
            prompt += "\nOnly respond when directly addressed.";
        }

        return prompt;
    }

    private static Conversation CreateConversation(IncomingMessage message)
    {
        return new Conversation
        {
            ChatId = message.ChatId.ToString(),
            DisplayName = message.IsGroup ? message.GroupName : message.SenderName,
            IsGroup = message.IsGroup,
        };
    }

    private async Task TrySendErrorAsync(long chatId, CancellationToken ct)
    {
        try
        {
            await _telegram.SendTextAsync(chatId, "⚠️ Sorry, something went wrong.", ct: ct);
        }
        catch (Exception sendEx)
        {
            _logger.LogError(sendEx, "Failed to send error message to chat {ChatId}", chatId);
        }
    }
}
