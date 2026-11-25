using TradeFlow.Legacy;

namespace TradeFlow.Tests;

public class FileLegacyQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradeflow-queue-" + Guid.NewGuid().ToString("N"));
    private readonly FileLegacyQueue _queue;

    public FileLegacyQueueTests() => _queue = new FileLegacyQueue(_root);

    private static LegacyMessage Message(string id) => new(id, "Order", "corr", "{}", DateTimeOffset.UtcNow);

    [Fact]
    public void Messages_are_received_in_order()
    {
        _queue.Send("q", Message("a"));
        _queue.Send("q", Message("b"));

        var received = new List<string>();
        while (_queue.TryProcessNext("q", TimeSpan.Zero, m => received.Add(m.MessageId))) { }

        Assert.Equal(new[] { "a", "b" }, received);
    }

    [Fact]
    public void Failed_handler_leaves_message_on_queue()
    {
        _queue.Send("q", Message("x"));

        Assert.Throws<InvalidOperationException>(() =>
            _queue.TryProcessNext("q", TimeSpan.Zero, _ => throw new InvalidOperationException("API down")));

        string? redelivered = null;
        Assert.True(_queue.TryProcessNext("q", TimeSpan.Zero, m => redelivered = m.MessageId));
        Assert.Equal("x", redelivered);
    }

    [Fact]
    public void Empty_queue_returns_false() =>
        Assert.False(_queue.TryProcessNext("empty", TimeSpan.Zero, _ => { }));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
