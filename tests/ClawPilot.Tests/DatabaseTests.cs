using ClawPilot.Database;
using ClawPilot.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClawPilot.Tests;

public class DatabaseTests : IDisposable
{
    private readonly ClawPilotDbContext _db;

    public DatabaseTests()
    {
        var options = new DbContextOptionsBuilder<ClawPilotDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new ClawPilotDbContext(options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task CreateConversation_SetsDefaults()
    {
        var conv = new Conversation { ChatId = "123" };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();

        var saved = await _db.Conversations.FirstAsync();
        Assert.Equal("123", saved.ChatId);
        Assert.False(saved.IsGroup);
        Assert.NotEqual(default, saved.CreatedAt);
    }

    [Fact]
    public async Task Messages_LinkedToConversation()
    {
        var conv = new Conversation { ChatId = "456" };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();

        _db.Messages.Add(new Message
        {
            ConversationId = conv.Id,
            Role = "user",
            Content = "Hello",
        });
        await _db.SaveChangesAsync();

        var messages = await _db.Messages
            .Where(m => m.ConversationId == conv.Id)
            .ToListAsync();
        Assert.Single(messages);
        Assert.Equal("user", messages[0].Role);
    }

    [Fact]
    public async Task ScheduledTask_Persistence()
    {
        _db.ScheduledTasks.Add(new ScheduledTask
        {
            ChatId = "789",
            Description = "Daily summary",
            CronExpression = "0 9 * * *",
        });
        await _db.SaveChangesAsync();

        var task = await _db.ScheduledTasks.FirstAsync();
        Assert.Equal("789", task.ChatId);
        Assert.True(task.IsActive);
        Assert.Null(task.LastRunAt);
    }

    public void Dispose() => _db.Dispose();
}
