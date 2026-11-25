using Microsoft.Extensions.Options;
using TradeFlow.Contracts;
using TradeFlow.Legacy;

namespace TradeFlow.Bridge;

/// <summary>
/// Strangler-fig adapter. Reads Calypso's legacy queue and, per message type, either calls the new
/// Trade API (migrated) or forwards to SimCorp's legacy queue (not yet migrated). The legacy message is
/// only removed after a successful hand-off.
/// </summary>
public sealed class BridgeWorker(
    ILegacyQueue queue,
    LegacyQueueOptions legacy,
    IOptions<BridgeOptions> options,
    TradeApiClient api,
    ILogger<BridgeWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, int> _failures = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => Run(stoppingToken), stoppingToken);

    private void Run(CancellationToken ct)
    {
        var migrated = options.Value.MigratedTypeSet;
        logger.LogInformation("Bridge reading {Queue} ({Transport}); migrated to API: {Types}",
            legacy.CalypsoOutQueue, legacy.Transport, string.Join(", ", migrated));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                queue.TryProcessNext(legacy.CalypsoOutQueue, TimeSpan.FromSeconds(2), m => Route(m, migrated, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Hand-off failed, message returned to {Queue}: {Error}", legacy.CalypsoOutQueue, ex.Message);
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            }
        }
    }

    private void Route(LegacyMessage message, ISet<string> migrated, CancellationToken ct)
    {
        if (!migrated.Contains(message.MessageType))
        {
            queue.Send(legacy.SimCorpInQueue, message);
            logger.LogInformation("{Type} {Id} -> legacy queue {Queue}", message.MessageType, message.MessageId, legacy.SimCorpInQueue);
            return;
        }

        var attempts = _failures.GetValueOrDefault(message.MessageId);
        if (attempts >= options.Value.MaxAttempts)
        {
            queue.Send(legacy.DeadLetterQueue, message);
            _failures.Remove(message.MessageId);
            logger.LogError("{Type} {Id} failed {Attempts} times -> dead-letter queue {Queue}",
                message.MessageType, message.MessageId, attempts, legacy.DeadLetterQueue);
            return;
        }

        try
        {
            var request = Json.Deserialize<TradeOrderRequest>(message.Payload);
            var result = api.SubmitAsync(request, message.CorrelationId, ct).GetAwaiter().GetResult();
            _failures.Remove(message.MessageId);

            if (result.Status == OrderStatus.Rejected)
            {
                logger.LogWarning("{Type} {ClientOrderId} -> API rejected: {Errors}",
                    message.MessageType, result.ClientOrderId, string.Join("; ", result.Errors));
            }
            else
            {
                logger.LogInformation("{Type} {ClientOrderId} -> API {Status} ({OrderId})",
                    message.MessageType, result.ClientOrderId, result.Status, result.OrderId);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // An API outage is not the message's fault: keep retrying without counting it,
            // so nothing is dead-lettered just because the API was down for a while.
            if (!IsTransient(ex))
            {
                _failures[message.MessageId] = attempts + 1;
            }
            throw;
        }
    }

    /// <summary>Connection refused, timeout, 408, 429 or 5xx: worth retrying forever.</summary>
    internal static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || (int)code == 408 || (int)code == 429,
        TaskCanceledException => true,
        _ => false,
    };
}
