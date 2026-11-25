using System.Text.Json;

namespace TradeFlow.Legacy;

/// <summary>
/// Folder-based stand-in for MSMQ, for machines without MSMQ and for CI. One folder per queue,
/// one JSON file per message, processed oldest first.
/// </summary>
public sealed class FileLegacyQueue : ILegacyQueue
{
    private readonly string _root;

    public FileLegacyQueue(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    private string QueueDir(string queueName)
    {
        var dir = Path.Combine(_root, queueName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Send(string queueName, LegacyMessage message)
    {
        var dir = QueueDir(queueName);
        var name = $"{DateTime.UtcNow.Ticks:D20}_{message.MessageId}";
        var tmp = Path.Combine(dir, name + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(message));
        File.Move(tmp, Path.Combine(dir, name + ".json"));
    }

    public bool TryProcessNext(string queueName, TimeSpan timeout, Action<LegacyMessage> handler)
    {
        var dir = QueueDir(queueName);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order())
            {
                var locked = file + ".lock";
                try
                {
                    File.Move(file, locked); // claim the message
                }
                catch (IOException)
                {
                    continue; // another consumer took it
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                try
                {
                    var message = JsonSerializer.Deserialize<LegacyMessage>(File.ReadAllText(locked))
                        ?? throw new InvalidOperationException("Empty legacy message.");
                    handler(message);
                    File.Delete(locked);
                    return true;
                }
                catch
                {
                    File.Move(locked, file); // put it back
                    throw;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            Thread.Sleep(200);
        }
    }
}
