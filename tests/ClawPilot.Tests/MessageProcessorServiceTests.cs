using System.Threading.Channels;
using ClawPilot.AI;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Database;
using ClawPilot.Database.Entities;
using ClawPilot.Services;
using ClawPilot.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Moq;
using Xunit;

namespace ClawPilot.Tests;

public class MessageProcessorServiceTests
{
    private static ClawPilotDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<ClawPilotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ClawPilotDbContext(opts);
    }

    private static IncomingMessage CreateMessage(long chatId = 123, string text = "Hello", bool isGroup = false)
    {
        return new IncomingMessage(chatId, 1, text, "TestUser", "user1", isGroup, isGroup ? "TestGroup" : null, DateTimeOffset.UtcNow);
    }

    private static MessageProcessorService CreateProcessor(
        Mock<ITelegramChannel>? telegramMock = null,
        Mock<AgentOrchestrator>? orchestratorMock = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        var mockOptions = Options.Create(new ClawPilotOptions
        {
            TelegramBotToken = "test",
            OpenRouterApiKey = "test",
        });

        telegramMock ??= new Mock<ITelegramChannel>();

        if (orchestratorMock is null)
        {
            var kernel = Kernel.CreateBuilder().Build();
            var memory = new MemoryService(mockOptions.Value, NullLogger<MemoryService>.Instance);
            orchestratorMock = new Mock<AgentOrchestrator>(
                MockBehavior.Loose, kernel, memory, mockOptions, NullLogger<AgentOrchestrator>.Instance);
        }

        if (scopeFactory is null)
        {
            var services = new ServiceCollection();
            services.AddDbContext<ClawPilotDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        var skillLoader = new SkillLoaderService(
            Path.Combine(Path.GetTempPath(), "clawpilot-test-skills-" + Guid.NewGuid()),
            NullLogger<SkillLoaderService>.Instance);

        var channel = Channel.CreateUnbounded<IncomingMessage>();

        return new MessageProcessorService(
            channel, orchestratorMock.Object, telegramMock.Object,
            scopeFactory, mockOptions, skillLoader,
            NullLogger<MessageProcessorService>.Instance);
    }

    [Fact]
    public void BuildSystemPrompt_ReturnsDefault_WhenNoOverride()
    {
        var processor = CreateProcessor();
        var conversation = new Conversation { ChatId = "123" };
        var message = CreateMessage();

        var prompt = processor.BuildSystemPrompt(conversation, message);

        Assert.Equal("You are a helpful personal assistant.", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_UsesOverride_WhenSet()
    {
        var processor = CreateProcessor();
        var conversation = new Conversation { ChatId = "123", SystemPrompt = "Be terse." };
        var message = CreateMessage();

        var prompt = processor.BuildSystemPrompt(conversation, message);

        Assert.StartsWith("Be terse.", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_AppendsGroupContext_WhenGroupChat()
    {
        var processor = CreateProcessor();
        var conversation = new Conversation { ChatId = "123" };
        var message = CreateMessage(isGroup: true);

        var prompt = processor.BuildSystemPrompt(conversation, message);

        Assert.Contains("group chat", prompt);
        Assert.Contains("TestGroup", prompt);
        Assert.Contains("TestUser", prompt);
        Assert.Contains("Only respond when directly addressed", prompt);
    }

    [Fact]
    public async Task ProcessMessageAsync_PersistsMessagesAndSendsResponse()
    {
        var telegramMock = new Mock<ITelegramChannel>();

        var mockOptions = Options.Create(new ClawPilotOptions
        {
            TelegramBotToken = "test",
            OpenRouterApiKey = "test",
        });

        var kernelBuilder = Kernel.CreateBuilder();
        var kernel = kernelBuilder.Build();
        var memory = new MemoryService(mockOptions.Value, NullLogger<MemoryService>.Instance);

        var orchestratorMock = new Mock<AgentOrchestrator>(
            MockBehavior.Loose,
            kernel, memory, mockOptions, NullLogger<AgentOrchestrator>.Instance);

        orchestratorMock
            .Setup(o => o.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Bot response");

        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ClawPilotDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var processor = CreateProcessor(telegramMock, orchestratorMock, scopeFactory);

        var message = CreateMessage(text: "What's up?");
        await processor.ProcessMessageAsync(message, CancellationToken.None);

        using var scope = scopeFactory.CreateScope();
        var verifyDb = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
        var conv = await verifyDb.Conversations.FirstOrDefaultAsync();
        Assert.NotNull(conv);
        Assert.Equal("123", conv.ChatId);

        var messages = await verifyDb.Messages.ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Role == "user" && m.Content == "What's up?");
        Assert.Contains(messages, m => m.Role == "assistant" && m.Content == "Bot response");

        telegramMock.Verify(t => t.SendTextAsync(123, "Bot response", 1L, It.IsAny<CancellationToken>()), Times.Once);
        telegramMock.Verify(t => t.SendTypingAsync(123, It.IsAny<CancellationToken>()), Times.Once);
    }
}
