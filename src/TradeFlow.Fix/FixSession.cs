using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TradeFlow.Fix;

/// <summary>
/// One FIX session over one TCP connection: sequence numbers, checksum validation, heartbeats and
/// message logging. Both the gateway (initiator) and the broker simulator (acceptor) use it.
/// Simplification for the demo: sequence numbers reset at every logon (141=Y) and gaps are logged,
/// not resent. A production gateway would use a full engine such as QuickFIX/n with a message store.
/// </summary>
public sealed class FixSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly FixReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger _logger;
    private int _nextOutSeq = 1;
    private int _expectedInSeq = 1;

    public FixSession(TcpClient client, string senderCompId, string targetCompId, ILogger logger)
    {
        _client = client;
        _stream = client.GetStream();
        _reader = new FixReader(_stream);
        _logger = logger;
        SenderCompId = senderCompId;
        TargetCompId = targetCompId;
    }

    public string SenderCompId { get; }
    public string TargetCompId { get; }
    public bool IsLoggedOn { get; set; }
    public DateTime LastSentUtc { get; private set; } = DateTime.UtcNow;
    public DateTime LastReceivedUtc { get; private set; } = DateTime.UtcNow;

    public static FixMessage LogonBody(int heartBtIntSeconds) => new FixMessage()
        .Set(Tags.EncryptMethod, "0")
        .Set(Tags.HeartBtInt, heartBtIntSeconds)
        .Set(Tags.ResetSeqNumFlag, "Y");

    public async Task SendAsync(string msgType, FixMessage body, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var raw = FixCodec.Encode(msgType, body, SenderCompId, TargetCompId, _nextOutSeq++, DateTime.UtcNow);
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(raw), cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            LastSentUtc = DateTime.UtcNow;
            Log("OUT", msgType, raw);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<FixMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var raw = await _reader.ReadMessageAsync(cancellationToken);
        if (raw is null)
        {
            return null;
        }
        LastReceivedUtc = DateTime.UtcNow;

        if (!FixCodec.HasValidChecksum(raw))
        {
            _logger.LogWarning("FIX message with bad checksum: {Message}", FixCodec.Pretty(raw));
        }

        var message = FixMessage.Parse(raw);
        if (message.MsgType == MsgTypes.Logon && message.Get(Tags.ResetSeqNumFlag) == "Y")
        {
            _expectedInSeq = 1;
        }
        var seq = message.GetInt(Tags.MsgSeqNum);
        if (seq != _expectedInSeq)
        {
            _logger.LogWarning("FIX sequence gap: expected {Expected}, received {Actual}", _expectedInSeq, seq);
        }
        _expectedInSeq = seq + 1;

        Log("IN ", message.MsgType, raw);
        return message;
    }

    /// <summary>Sends a Heartbeat whenever nothing has been sent for the agreed interval.</summary>
    public async Task RunHeartbeatsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (IsLoggedOn && DateTime.UtcNow - LastSentUtc >= TimeSpan.FromSeconds(intervalSeconds))
            {
                await SendAsync(MsgTypes.Heartbeat, new FixMessage(), cancellationToken);
            }
        }
    }

    private void Log(string direction, string msgType, string raw)
    {
        if (msgType == MsgTypes.Heartbeat)
        {
            _logger.LogDebug("FIX {Direction} {Type} {Message}", direction, MsgTypes.Name(msgType), FixCodec.Pretty(raw));
        }
        else
        {
            _logger.LogInformation("FIX {Direction} {Type} {Message}", direction, MsgTypes.Name(msgType), FixCodec.Pretty(raw));
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsLoggedOn = false;
        try
        {
            await _stream.DisposeAsync();
        }
        catch (IOException)
        {
        }
        _client.Dispose();
    }
}
