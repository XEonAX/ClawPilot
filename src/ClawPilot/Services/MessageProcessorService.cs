using System.Collections.Concurrent;
using System.Diagnostics;
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
using Microsoft.SemanticKernel;
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
            var totalSw = Stopwatch.StartNew();
            var stepSw = Stopwatch.StartNew();

            // Start continuous typing indicator that runs until LLM response completes
            using var typingCts = new CancellationTokenSource();
            var typingTask = Task.Run(() => SendTypingContinuouslyAsync(message.ChatId, typingCts.Token, ct), ct);

            try
            {
                stepSw.Restart();
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
                _logger.LogDebug("[Timing] LoadOrCreateConversation took {Elapsed}ms for chat {ChatId}",
                    stepSw.ElapsedMilliseconds, message.ChatId);

                stepSw.Restart();
                var userMessage = new Message
                {
                    ConversationId = conversation.Id,
                    Role = "user",
                    Content = message.Text,
                    TelegramMessageId = message.MessageId,
                    SenderName = message.SenderName,
                    SenderId = message.SenderId,
                    Status = "processing",
                };
                db.Messages.Add(userMessage);
                await db.SaveChangesAsync(ct);
                _logger.LogDebug("[Timing] SaveUserMessage took {Elapsed}ms for chat {ChatId}",
                    stepSw.ElapsedMilliseconds, message.ChatId);

                stepSw.Restart();
                var systemPrompt = BuildSystemPrompt(conversation, message);
                _logger.LogDebug("[Timing] BuildSystemPrompt took {Elapsed}ms for chat {ChatId}",
                    stepSw.ElapsedMilliseconds, message.ChatId);

                // §2.4: Restore session on first message after process restart
                if (!_orchestrator.HasHistory(chatKey))
                {
                    stepSw.Restart();
                    var recentMessages = await db.Messages
                        .Where(m => m.ConversationId == conversation.Id && m.Status != "processing")
                        .OrderByDescending(m => m.Id)
                        .Take(50)
                        .OrderBy(m => m.Id)
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
                    _logger.LogDebug("[Timing] RestoreSession took {Elapsed}ms for chat {ChatId}",
                        stepSw.ElapsedMilliseconds, message.ChatId);
                }

                stepSw.Restart();
                var response = await _orchestrator.SendMessageAsync(
                    chatKey, message.Text, systemPrompt, ct);
                _logger.LogDebug("[Timing] LLM SendMessage took {Elapsed}ms for chat {ChatId}",
                    stepSw.ElapsedMilliseconds, message.ChatId);

                stepSw.Restart();
                db.Messages.Add(new Message
                {
                    ConversationId = conversation.Id,
                    Role = "assistant",
                    Content = response,
                    Status = "done",
                });

                userMessage.Status = "done";
                conversation.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                _logger.LogDebug("[Timing] SaveAssistantMessage took {Elapsed}ms for chat {ChatId}",
                    stepSw.ElapsedMilliseconds, message.ChatId);

                stepSw.Restart();
                await _telegram.SendTextAsync(message.ChatId, response, message.MessageId, ct);
                _logger.LogDebug("[Timing] SendResponse took {Elapsed}ms for chat {ChatId}",
                    stepSw.ElapsedMilliseconds, message.ChatId);

                totalSw.Stop();
                _logger.LogDebug("[Timing] Total processing took {Elapsed}ms for message {MessageId} in chat {ChatId}",
                    totalSw.ElapsedMilliseconds, message.MessageId, message.ChatId);
                    
            }
            finally
            {
                // Stop the typing indicator loop
                typingCts.Cancel();
                try { await typingTask; } catch (OperationCanceledException) { }
            }

        }
        catch (HttpOperationException skEx)
        {
            _logger.LogError(skEx,
                "LLM request failed for message {MessageId} in chat {ChatId} — Status: {StatusCode}, Response: {ResponseContent}",
                message.MessageId, message.ChatId, skEx.StatusCode, skEx.ResponseContent);
            await TrySendErrorAsync(message.ChatId, skEx, ct);
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx,
                "LLM request failed for message {MessageId} in chat {ChatId} with status {StatusCode}",
                message.MessageId, message.ChatId, httpEx.StatusCode);
            await TrySendErrorAsync(message.ChatId, httpEx, ct);
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
            await TrySendErrorAsync(message.ChatId, dbEx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Message processing cancelled for chat {ChatId}", message.ChatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing message {MessageId} for chat {ChatId}", message.MessageId, message.ChatId);
            await TrySendErrorAsync(message.ChatId, ex, ct);
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

        // Always include the chat ID so the AI can use it for tool calls
        prompt += $"\n\nCurrent chat ID: {message.ChatId}";

        if (message.IsGroup)
        {
            prompt += $"\nYou are in a group chat called \"{message.GroupName ?? "Unknown"}\".";
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

    private async Task SendTypingContinuouslyAsync(long chatId, CancellationToken typingCt, CancellationToken overallCt)
    {
        // Telegram typing indicator lasts ~5 seconds, so we send it every 4 seconds to keep it active
        const int typingIntervalMs = 4000;

        try
        {
            while (!typingCt.IsCancellationRequested && !overallCt.IsCancellationRequested)
            {
                try
                {
                    await _telegram.SendTypingAsync(chatId, overallCt);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to send typing indicator to chat {ChatId}", chatId);
                }

                try
                {
                    await Task.Delay(typingIntervalMs, typingCt);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when processing completes
        }
    }

    private async Task TrySendErrorAsync(long chatId, Exception ex, CancellationToken ct)
    {
        try
        {
            await _telegram.SendTextAsync(chatId, "⚠️ Sorry, something went wrong.", ct: ct);
            await _telegram.SendTextAsync(chatId, ex.ToString(), ct: ct);
        }
        catch (Exception sendEx)
        {
            _logger.LogError(sendEx, "Failed to send error message to chat {ChatId}", chatId);
        }
    }
}
