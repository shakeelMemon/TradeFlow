using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using TradeFlow.Fix;

namespace TradeFlow.BrokerSimulator;

public sealed class BrokerOptions
{
    public int Port { get; set; } = 9878;
    public string SenderCompId { get; set; } = "BROKER";
    public string TargetCompId { get; set; } = "TRADEFLOW";
    public int HeartBtInt { get; set; } = 30;

    /// <summary>Share of orders the broker rejects ("insufficient liquidity").</summary>
    public double RejectRate { get; set; } = 0.05;

    /// <summary>An accepted order is filled in 1 to MaxFills executions.</summary>
    public int MaxFills { get; set; } = 3;
    public int MinFillDelayMs { get; set; } = 300;
    public int MaxFillDelayMs { get; set; } = 1200;
}

/// <summary>
/// Plays an executing broker: a FIX 4.4 acceptor that answers each NewOrderSingle with an
/// ExecutionReport "New", then one or more fills (partial, then full), or a reject.
/// </summary>
public sealed class BrokerWorker(IOptions<BrokerOptions> options, ILogger<BrokerWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, byte> _seenClOrdIds = new();
    private int _orderSeq;
    private int _execSeq;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var listener = new TcpListener(IPAddress.Loopback, o.Port);
        listener.Start();
        logger.LogInformation("Broker FIX acceptor listening on 127.0.0.1:{Port} as {Sender}", o.Port, o.SenderCompId);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var o = options.Value;
        await using var session = new FixSession(client, o.SenderCompId, o.TargetCompId, logger);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? heartbeats = null;
        try
        {
            while (true)
            {
                var message = await session.ReceiveAsync(ct);
                if (message is null)
                {
                    break;
                }

                switch (message.MsgType)
                {
                    case MsgTypes.Logon:
                        await session.SendAsync(MsgTypes.Logon, FixSession.LogonBody(o.HeartBtInt), ct);
                        session.IsLoggedOn = true;
                        heartbeats ??= session.RunHeartbeatsAsync(o.HeartBtInt, heartbeatCts.Token);
                        logger.LogInformation("{Client} logged on", message.Get(Tags.SenderCompID));
                        break;
                    case MsgTypes.TestRequest:
                        await session.SendAsync(MsgTypes.Heartbeat,
                            new FixMessage().Set(Tags.TestReqID, message.Get(Tags.TestReqID) ?? ""), ct);
                        break;
                    case MsgTypes.Logout:
                        await session.SendAsync(MsgTypes.Logout, new FixMessage(), ct);
                        return;
                    case MsgTypes.NewOrderSingle:
                        await HandleOrderAsync(session, message, ct);
                        break;
                }
            }
            logger.LogInformation("Client disconnected");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogInformation("Client connection closed: {Reason}", ex.Message);
        }
        finally
        {
            heartbeatCts.Cancel();
            if (heartbeats is not null)
            {
                try
                {
                    await heartbeats;
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private async Task HandleOrderAsync(FixSession session, FixMessage order, CancellationToken ct)
    {
        var o = options.Value;
        var clOrdId = order.Get(Tags.ClOrdID) ?? "";
        var quantity = order.GetDecimal(Tags.OrderQty);
        var brokerOrderId = $"BRK-{Interlocked.Increment(ref _orderSeq):D6}";

        if (!_seenClOrdIds.TryAdd(clOrdId, 0))
        {
            await SendExecutionAsync(session, order, brokerOrderId, ExecTypes.Rejected, OrdStatuses.Rejected,
                0, 0, 0, quantity, 0, "Duplicate ClOrdID", ct);
            return;
        }

        if (Random.Shared.NextDouble() < o.RejectRate)
        {
            await SendExecutionAsync(session, order, brokerOrderId, ExecTypes.Rejected, OrdStatuses.Rejected,
                0, 0, 0, quantity, 0, "Insufficient liquidity", ct);
            return;
        }

        await SendExecutionAsync(session, order, brokerOrderId, ExecTypes.New, OrdStatuses.New,
            0, 0, 0, quantity, 0, null, ct);
        _ = Task.Run(() => FillAsync(session, order, brokerOrderId, ct), ct);
    }

    private async Task FillAsync(FixSession session, FixMessage order, string brokerOrderId, CancellationToken ct)
    {
        var o = options.Value;
        var quantity = order.GetDecimal(Tags.OrderQty);
        var limit = order.GetDecimal(Tags.Price);
        var isBuy = order.Get(Tags.Side) == "1";
        var fills = Random.Shared.Next(1, Math.Max(1, o.MaxFills) + 1);
        decimal cumQty = 0, notional = 0;

        try
        {
            for (var i = 1; i <= fills; i++)
            {
                await Task.Delay(Random.Shared.Next(o.MinFillDelayMs, o.MaxFillDelayMs), ct);

                var lastQty = i == fills ? quantity - cumQty : Math.Floor(quantity / fills);
                // Fill at the limit or up to 2 cents better: lower for a buy, higher for a sell.
                var improvement = Random.Shared.Next(0, 3) / 100m;
                var lastPx = isBuy ? limit - improvement : limit + improvement;

                cumQty += lastQty;
                notional += lastQty * lastPx;
                var avgPx = Math.Round(notional / cumQty, 6);
                var leaves = quantity - cumQty;

                await SendExecutionAsync(session, order, brokerOrderId, ExecTypes.Trade,
                    leaves == 0 ? OrdStatuses.Filled : OrdStatuses.PartiallyFilled,
                    lastQty, lastPx, cumQty, leaves, avgPx, null, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogWarning("Could not finish filling {BrokerOrderId}: {Reason}", brokerOrderId, ex.Message);
        }
    }

    private Task SendExecutionAsync(FixSession session, FixMessage order, string brokerOrderId,
        string execType, string ordStatus, decimal lastQty, decimal lastPx, decimal cumQty, decimal leavesQty,
        decimal avgPx, string? text, CancellationToken ct)
    {
        var er = new FixMessage()
            .Set(Tags.OrderID, brokerOrderId)
            .Set(Tags.ClOrdID, order.Get(Tags.ClOrdID) ?? "")
            .Set(Tags.SecondaryClOrdID, order.Get(Tags.SecondaryClOrdID) ?? "")
            .Set(Tags.ExecID, $"EX-{Interlocked.Increment(ref _execSeq):D7}")
            .Set(Tags.ExecType, execType)
            .Set(Tags.OrdStatus, ordStatus)
            .Set(Tags.Account, order.Get(Tags.Account) ?? "")
            .Set(Tags.Symbol, order.Get(Tags.Symbol) ?? "")
            .Set(Tags.Side, order.Get(Tags.Side) ?? "")
            .Set(Tags.OrderQty, order.GetDecimal(Tags.OrderQty))
            .Set(Tags.Price, order.GetDecimal(Tags.Price))
            .Set(Tags.LastQty, lastQty)
            .Set(Tags.LastPx, lastPx)
            .Set(Tags.CumQty, cumQty)
            .Set(Tags.LeavesQty, leavesQty)
            .Set(Tags.AvgPx, avgPx)
            .Set(Tags.TransactTime, FixCodec.FormatTime(DateTime.UtcNow));
        if (text is not null)
        {
            er.Set(Tags.Text, text);
        }
        return session.SendAsync(MsgTypes.ExecutionReport, er, ct);
    }
}
