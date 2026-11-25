using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using TradeFlow.Contracts;
using TradeFlow.Fix;
using TradeFlow.Messaging;

namespace TradeFlow.FixGateway;

public sealed class FixClientOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9878;
    public string SenderCompId { get; set; } = "TRADEFLOW";
    public string TargetCompId { get; set; } = "BROKER";
    public int HeartBtInt { get; set; } = 30;
}

public sealed class GatewayOptions
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5080";
}

/// <summary>Forwards broker ExecutionReports to the Trade API.</summary>
public sealed class ExecutionClient(HttpClient http)
{
    public async Task SendAsync(Guid orderId, ExecutionReportDto report, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"api/orders/{orderId}/executions", report, Json.Options, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// FIX initiator. Keeps a FIX 4.4 session open to the broker, turns every accepted <b>order</b> from the
/// message bus into a NewOrderSingle (35=D), and forwards each ExecutionReport (35=8) to the Trade API.
/// ClOrdID (11) is the API's order ID, so execution reports map back without any extra state.
/// </summary>
public sealed class FixGatewayWorker(
    IEventConsumer consumer,
    ExecutionClient api,
    IOptions<FixClientOptions> fixOptions,
    MessagingOptions messaging,
    ILogger<FixGatewayWorker> logger) : BackgroundService
{
    private volatile FixSession? _session;
    private readonly ConcurrentDictionary<string, byte> _sentClOrdIds = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FIX gateway: bus {Provider} (subscription {Subscription}) -> broker {Host}:{Port}",
            messaging.Provider, messaging.Subscription, fixOptions.Value.Host, fixOptions.Value.Port);
        return Task.WhenAll(ConnectionLoopAsync(stoppingToken), consumer.RunAsync(HandleEventAsync, stoppingToken));
    }

    // ---------- broker connection ----------

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        var o = fixOptions.Value;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(o.Host, o.Port, ct);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                await using var session = new FixSession(client, o.SenderCompId, o.TargetCompId, logger);
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeats = session.RunHeartbeatsAsync(o.HeartBtInt, heartbeatCts.Token);
                try
                {
                    await session.SendAsync(MsgTypes.Logon, FixSession.LogonBody(o.HeartBtInt), ct);
                    await ReadLoopAsync(session, ct);
                }
                finally
                {
                    _session = null;
                    session.IsLoggedOn = false;
                    heartbeatCts.Cancel();
                    try
                    {
                        await heartbeats;
                    }
                    catch (Exception)
                    {
                        // heartbeat loop ends with cancellation or a closed socket
                    }
                }
                logger.LogWarning("FIX session with {Host}:{Port} ended", o.Host, o.Port);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Cannot reach broker at {Host}:{Port} ({Error}). Retrying in 5 s", o.Host, o.Port, ex.Message);
            }

            try
            {
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReadLoopAsync(FixSession session, CancellationToken ct)
    {
        while (true)
        {
            var message = await session.ReceiveAsync(ct);
            if (message is null)
            {
                return; // broker closed the connection
            }

            switch (message.MsgType)
            {
                case MsgTypes.Logon:
                    session.IsLoggedOn = true;
                    _session = session;
                    logger.LogInformation("FIX session logged on: {Sender} -> {Target}", session.SenderCompId, session.TargetCompId);
                    break;
                case MsgTypes.TestRequest:
                    await session.SendAsync(MsgTypes.Heartbeat,
                        new FixMessage().Set(Tags.TestReqID, message.Get(Tags.TestReqID) ?? ""), ct);
                    break;
                case MsgTypes.Logout:
                    return;
                case MsgTypes.Reject:
                    logger.LogWarning("Broker rejected a FIX message: {Text}", message.Get(Tags.Text));
                    break;
                case MsgTypes.ExecutionReport:
                    await ForwardExecutionAsync(message, ct);
                    break;
            }
        }
    }

    private async Task ForwardExecutionAsync(FixMessage er, CancellationToken ct)
    {
        var clOrdId = er.Get(Tags.ClOrdID) ?? "";
        if (!Guid.TryParseExact(clOrdId, "N", out var orderId))
        {
            logger.LogWarning("ExecutionReport with unknown ClOrdID {ClOrdId}", clOrdId);
            return;
        }

        var report = new ExecutionReportDto(
            ExecId: er.Get(Tags.ExecID) ?? "",
            BrokerOrderId: er.Get(Tags.OrderID) ?? "",
            ExecType: er.Get(Tags.ExecType) ?? "",
            OrdStatus: er.Get(Tags.OrdStatus) ?? "",
            LastQty: er.GetDecimal(Tags.LastQty),
            LastPx: er.GetDecimal(Tags.LastPx),
            CumQty: er.GetDecimal(Tags.CumQty),
            LeavesQty: er.GetDecimal(Tags.LeavesQty),
            AvgPx: er.GetDecimal(Tags.AvgPx),
            Text: er.Get(Tags.Text));

        // The API is the system of record: keep retrying while it is unavailable.
        while (true)
        {
            try
            {
                await api.SendAsync(orderId, report, ct);
                logger.LogInformation("{ClientOrderId}: execution {ExecType}/{OrdStatus} {LastQty} @ {LastPx} sent to API",
                    er.Get(Tags.SecondaryClOrdID), report.ExecType, report.OrdStatus, report.LastQty, report.LastPx);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                logger.LogWarning("API unavailable, retrying execution {ExecId} in 2 s: {Error}", report.ExecId, ex.Message);
                await Task.Delay(2000, ct);
            }
        }
    }

    // ---------- orders from the bus ----------

    private async Task HandleEventAsync(BusMessage message, CancellationToken ct)
    {
        if (message.EventType != EventTypes.OrderAccepted)
        {
            return; // e.g. OrderFilled, which is for SimCorp
        }

        var order = Json.Deserialize<OrderAcceptedEvent>(message.Body);
        if (!string.Equals(order.MessageType, LegacyMessageTypes.Order, StringComparison.OrdinalIgnoreCase))
        {
            return; // trades are already executed; SimCorp books them directly
        }

        var clOrdId = order.OrderId.ToString("N");
        if (!_sentClOrdIds.TryAdd(clOrdId, 0))
        {
            logger.LogInformation("{ClientOrderId} already sent to broker; ignoring redelivered event", order.ClientOrderId);
            return;
        }

        try
        {
            var session = await WaitForSessionAsync(ct);
            var nos = new FixMessage()
                .Set(Tags.ClOrdID, clOrdId)
                .Set(Tags.SecondaryClOrdID, order.ClientOrderId)
                .Set(Tags.Account, order.Portfolio)
                .Set(Tags.Symbol, order.InstrumentId)
                .Set(Tags.Side, order.Side == Side.Buy ? "1" : "2")
                .Set(Tags.OrderQty, order.Quantity)
                .Set(Tags.OrdType, "2") // limit
                .Set(Tags.Price, order.Price)
                .Set(Tags.TimeInForce, "0") // day
                .Set(Tags.Currency, order.Currency)
                .Set(Tags.TransactTime, FixCodec.FormatTime(DateTime.UtcNow));
            await session.SendAsync(MsgTypes.NewOrderSingle, nos, ct);
            logger.LogInformation("{ClientOrderId}: NewOrderSingle sent to broker ({Side} {Qty} {Symbol} @ {Price})",
                order.ClientOrderId, order.Side, order.Quantity, order.InstrumentId, order.Price);
        }
        catch
        {
            _sentClOrdIds.TryRemove(clOrdId, out _);
            throw;
        }
    }

    /// <summary>Waits (without failing the message) until the FIX session is logged on.</summary>
    private async Task<FixSession> WaitForSessionAsync(CancellationToken ct)
    {
        var waited = 0;
        while (true)
        {
            var session = _session;
            if (session is { IsLoggedOn: true })
            {
                return session;
            }
            if (waited % 10 == 0)
            {
                logger.LogWarning("Waiting for FIX session with broker before sending orders...");
            }
            waited++;
            await Task.Delay(1000, ct);
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || (int)code == 408 || (int)code == 429,
        TaskCanceledException => true,
        _ => false,
    };
}
