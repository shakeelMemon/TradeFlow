using Microsoft.EntityFrameworkCore;
using TradeFlow.Api.Data;
using TradeFlow.Contracts;
using TradeFlow.Messaging;

namespace TradeFlow.Api.Outbox;

/// <summary>
/// Publishes outbox rows to the bus (at-least-once). Consumers are idempotent, so a repeat
/// after a crash between publish and save is harmless.
/// </summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopes, IEventPublisher publisher, TimeProvider clock, ILogger<OutboxPublisher> logger)
    : BackgroundService
{
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(stoppingToken);
                if (published == 0)
                {
                    await Task.Delay(500, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox publishing failed");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();

        var batch = await db.Outbox
            .Where(m => m.PublishedAt == null && m.Attempts < MaxAttempts)
            .OrderBy(m => m.Id)
            .Take(50)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(
                    new BusMessage(message.EventType, message.Body, message.MessageId, message.CorrelationId), ct);

                var now = clock.GetUtcNow();
                message.PublishedAt = now;
                db.Audit.Add(new AuditEntry
                {
                    OrderId = message.OrderId,
                    Action = "Published",
                    Detail = $"{message.EventType} sent to message bus",
                    CorrelationId = message.CorrelationId,
                    At = now,
                });
                await db.SaveChangesAsync(ct);

                // Only move Accepted -> Published, never overwrite a faster Booked status.
                await db.Orders
                    .Where(o => o.Id == message.OrderId && o.Status == OrderStatus.Accepted)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Published), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message;
                await db.SaveChangesAsync(ct);
                logger.LogWarning(ex, "Publish failed for order {OrderId} (attempt {Attempt})", message.OrderId, message.Attempts);
            }
        }

        return batch.Count;
    }
}
