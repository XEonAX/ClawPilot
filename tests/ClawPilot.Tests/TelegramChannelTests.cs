using ClawPilot.Channels;

namespace ClawPilot.Tests;

public class TelegramChannelTests
{
    [Fact]
    public void ChunkText_ShortMessage_ReturnsSingleChunk()
    {
        var chunks = TelegramChannel.ChunkText("Hello world", 4096).ToList();
        Assert.Single(chunks);
        Assert.Equal("Hello world", chunks[0]);
    }

    [Fact]
    public void ChunkText_LongMessage_SplitsAtParagraphBoundary()
    {
        var paragraph1 = new string('A', 2000);
        var paragraph2 = new string('B', 2000);
        var paragraph3 = new string('C', 2000);
        var text = $"{paragraph1}\n\n{paragraph2}\n\n{paragraph3}";

        var chunks = TelegramChannel.ChunkText(text, 4096).ToList();
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Length <= 4096));
    }

    [Fact]
    public void ChunkText_EmptyText_ReturnsSingleEmptyChunk()
    {
        var chunks = TelegramChannel.ChunkText("", 4096).ToList();
        Assert.Single(chunks);
        Assert.Equal("", chunks[0]);
    }

    [Fact]
    public void ChunkText_ExactlyMaxLen_ReturnsSingleChunk()
    {
        var text = new string('X', 4096);
        var chunks = TelegramChannel.ChunkText(text, 4096).ToList();
        Assert.Single(chunks);
    }

    [Fact]
    public void IncomingMessage_RecordEquality()
    {
        var ts = DateTimeOffset.UtcNow;
        var msg1 = new IncomingMessage(123, 1, "hi", "Alice", "100", false, null, ts);
        var msg2 = new IncomingMessage(123, 1, "hi", "Alice", "100", false, null, ts);
        Assert.Equal(msg1, msg2);
    }
}
