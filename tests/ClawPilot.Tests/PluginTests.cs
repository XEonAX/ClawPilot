using ClawPilot.AI;
using ClawPilot.AI.Plugins;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Database;
using ClawPilot.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClawPilot.Tests;

public class PluginTests
{
    private static (IServiceScopeFactory scopeFactory, string dbName) CreateScopedDb()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ClawPilotDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IServiceScopeFactory>(), dbName);
    }

    [Fact]
    public async Task MessagingPlugin_SendMessage_CallsTelegram()
    {
        var telegramMock = new Mock<ITelegramChannel>();
        var (scopeFactory, _) = CreateScopedDb();
        var options = new ClawPilotOptions { TelegramBotToken = "test", OpenRouterApiKey = "test" };
        var plugin = new MessagingPlugin(telegramMock.Object, scopeFactory, options);

        var result = await plugin.SendMessageAsync(123, "Hello!", CancellationToken.None);

        Assert.Equal("Message sent successfully.", result);
        telegramMock.Verify(t => t.SendTextAsync(123, "Hello!", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MessagingPlugin_SearchMessages_ReturnsResults()
    {
        var telegramMock = new Mock<ITelegramChannel>();
        var (scopeFactory, _) = CreateScopedDb();

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
            var conv = new Conversation { ChatId = "123" };
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            db.Messages.Add(new Message { ConversationId = conv.Id, Role = "user", Content = "hello world", SenderName = "Test" });
            db.Messages.Add(new Message { ConversationId = conv.Id, Role = "assistant", Content = "goodbye", SenderName = "Bot" });
            await db.SaveChangesAsync();
        }

        var options = new ClawPilotOptions { TelegramBotToken = "test", OpenRouterApiKey = "test" };
        var plugin = new MessagingPlugin(telegramMock.Object, scopeFactory, options);
        var result = await plugin.SearchMessagesAsync("hello", ct: CancellationToken.None);

        Assert.Contains("hello world", result);
        Assert.DoesNotContain("goodbye", result);
    }

    [Fact]
    public async Task SchedulerPlugin_ScheduleTask_PersistsToDb()
    {
        var (scopeFactory, _) = CreateScopedDb();
        var plugin = new SchedulerPlugin(scopeFactory);

        var result = await plugin.ScheduleTaskAsync(123, "Daily reminder", "0 9 * * *", CancellationToken.None);

        Assert.Contains("Daily reminder", result);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
        var task = await db.ScheduledTasks.FirstOrDefaultAsync();
        Assert.NotNull(task);
        Assert.Equal("123", task.ChatId);
        Assert.Equal("0 9 * * *", task.CronExpression);
        Assert.True(task.IsActive);
    }

    [Fact]
    public void UtilityPlugin_GetCurrentDateTime_ReturnsValidFormat()
    {
        var opts = new ClawPilotOptions { TelegramBotToken = "t", OpenRouterApiKey = "k" };
        var memory = new MemoryService(opts, NullLogger<MemoryService>.Instance);
        var plugin = new UtilityPlugin(memory);

        var result = plugin.GetCurrentDateTime();

        Assert.Contains("UTC:", result);
        Assert.Contains("Unix:", result);
    }
}
