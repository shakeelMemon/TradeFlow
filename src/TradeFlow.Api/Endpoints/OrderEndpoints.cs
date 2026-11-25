using Microsoft.EntityFrameworkCore;
using TradeFlow.Api.Data;
using TradeFlow.Api.Validation;
using TradeFlow.Contracts;

namespace TradeFlow.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/orders").WithTags("Orders");
        orders.MapPost("/", SubmitAsync).WithSummary("Validate and accept an order or trade");
        orders.MapGet("/{id:guid}", GetAsync).WithSummary("Order status");
        orders.MapGet("/{id:guid}/audit", GetAuditAsync).WithSummary("Audit trail for an order");
        orders.MapPost("/{id:guid}/booking", BookAsync).WithSummary("Booking confirmation from SimCorp");
        orders.MapPost("/{id:guid}/executions", ExecutionAsync).WithSummary("FIX ExecutionReport forwarded by the FIX gateway");
        orders.MapGet("/{id:guid}/executions", GetExecutionsAsync).WithSummary("Executions (fills) for an order");

        app.MapGet("/api/reconciliation", ReconcileAsync).WithTags("Controls")
            .WithSummary("Accepted vs published vs booked, with breaks");
    }

    private static async Task<IResult> SubmitAsync(
        TradeOrderRequest request, TradeDb db, OrderValidator validator, TimeProvider clock,
        HttpContext http, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TradeFlow.Orders");

        // Idempotency: the legacy side can redeliver; return the original result.
        var existing = await FindExistingAsync(db, request, ct);
        if (existing is not null)
        {
            logger.LogInformation("Duplicate {ClientOrderId} from {Source}; returning original result",
                request.ClientOrderId, request.SourceSystem);
            return ResultFor(existing, replay: true);
        }

        var now = clock.GetUtcNow();
        var order = OrderEntity.From(request, http.GetCorrelationId(), now);
        var errors = validator.Validate(request);

        if (errors.Count > 0)
        {
            order.Status = OrderStatus.Rejected;
            order.RejectReason = string.Join("; ", errors);
            db.Orders.Add(order);
            db.Audit.Add(AuditEntry.For(order, "Rejected", order.RejectReason, now));
        }
        else
        {
            order.Status = OrderStatus.Accepted;
            db.Orders.Add(order);
            // Same transaction as the order: the event can't be lost if the bus is down.
            db.Outbox.Add(new OutboxMessage
            {
                OrderId = order.Id,
                EventType = EventTypes.OrderAccepted,
                Body = Json.Serialize(order.ToAcceptedEvent()),
                CorrelationId = order.CorrelationId,
                CreatedAt = now,
            });
            db.Audit.Add(AuditEntry.For(order, "Accepted", "Passed pre-trade validation", now));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two copies arrived at the same moment; the unique index let only one in.
            db.ChangeTracker.Clear();
            existing = await FindExistingAsync(db, request, ct);
            if (existing is null) throw;
            return ResultFor(existing, replay: true);
        }

        logger.LogInformation("{ClientOrderId} {Status}", order.ClientOrderId, order.Status);
        return ResultFor(order, replay: false);
    }

    private static Task<OrderEntity?> FindExistingAsync(TradeDb db, TradeOrderRequest request, CancellationToken ct) =>
        db.Orders.AsNoTracking().FirstOrDefaultAsync(
            o => o.SourceSystem == request.SourceSystem && o.ClientOrderId == request.ClientOrderId, ct);

    private static IResult ResultFor(OrderEntity order, bool replay) => order.Status switch
    {
        OrderStatus.Rejected => Results.UnprocessableEntity(order.ToResponse()),
        _ when replay => Results.Ok(order.ToResponse()),
        _ => Results.Accepted($"/api/orders/{order.Id}", order.ToResponse()),
    };

    private static async Task<IResult> GetAsync(Guid id, TradeDb db, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        return order is null ? Results.NotFound() : Results.Ok(order.ToResponse());
    }

    private static async Task<IResult> GetAuditAsync(Guid id, TradeDb db, CancellationToken ct)
    {
        var entries = await db.Audit.AsNoTracking()
            .Where(a => a.OrderId == id)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Action, a.Detail, a.CorrelationId, a.At })
            .ToListAsync(ct);
        return entries.Count == 0 ? Results.NotFound() : Results.Ok(entries);
    }

    private static async Task<IResult> BookAsync(
        Guid id, BookingConfirmation booking, TradeDb db, TimeProvider clock, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return Results.NotFound();

        switch (order.Status)
        {
            case OrderStatus.Booked:
                return Results.Ok(order.ToResponse()); // redelivered confirmation
            case OrderStatus.Rejected:
            case OrderStatus.BrokerRejected:
                return Results.Conflict(order.ToResponse());
        }

        var now = clock.GetUtcNow();
        order.Status = OrderStatus.Booked;
        order.ExternalBookingId = booking.ExternalBookingId;
        order.BookedAt = now;
        db.Audit.Add(AuditEntry.For(order, "Booked", $"{booking.BookedBy} booking {booking.ExternalBookingId}", now));
        await db.SaveChangesAsync(ct);
        return Results.Ok(order.ToResponse());
    }

    private static async Task<IResult> ExecutionAsync(
        Guid id, ExecutionReportDto report, TradeDb db, TimeProvider clock, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return Results.NotFound();

        // Idempotent: the same ExecId is only applied once.
        if (await db.Executions.AnyAsync(e => e.ExecId == report.ExecId, ct))
        {
            return Results.Ok(order.ToResponse());
        }

        var now = clock.GetUtcNow();
        db.Executions.Add(new ExecutionEntity
        {
            OrderId = id,
            ExecId = report.ExecId,
            ExecType = report.ExecType,
            OrdStatus = report.OrdStatus,
            LastQty = report.LastQty,
            LastPx = report.LastPx,
            CumQty = report.CumQty,
            LeavesQty = report.LeavesQty,
            AvgPx = report.AvgPx,
            Text = report.Text,
            ReceivedAt = now,
        });
        if (!string.IsNullOrEmpty(report.BrokerOrderId))
        {
            order.BrokerOrderId = report.BrokerOrderId;
        }

        switch (report.ExecType)
        {
            case "0": // New: broker accepted the order
                if (order.Status is OrderStatus.Accepted or OrderStatus.Published)
                {
                    order.Status = OrderStatus.SentToBroker;
                }
                db.Audit.Add(AuditEntry.For(order, "SentToBroker", $"Broker acknowledged as {report.BrokerOrderId} ({report.ExecId})", now));
                break;

            case "F": // Trade: partial or full fill
                order.FilledQuantity = report.CumQty;
                order.AveragePrice = report.AvgPx;
                if (report.LeavesQty <= 0)
                {
                    order.Status = OrderStatus.Filled;
                    db.Audit.Add(AuditEntry.For(order, "Filled",
                        $"{report.CumQty:N0} @ avg {report.AvgPx:0.####} ({report.ExecId})", now));
                    db.Outbox.Add(new OutboxMessage
                    {
                        OrderId = order.Id,
                        EventType = EventTypes.OrderFilled,
                        Body = Json.Serialize(order.ToFilledEvent()),
                        CorrelationId = order.CorrelationId,
                        CreatedAt = now,
                    });
                }
                else
                {
                    order.Status = OrderStatus.PartiallyFilled;
                    db.Audit.Add(AuditEntry.For(order, "PartiallyFilled",
                        $"{report.LastQty:N0} @ {report.LastPx:0.####}; {report.CumQty:N0} of {order.Quantity:N0} done ({report.ExecId})", now));
                }
                break;

            case "8": // Rejected by the broker
                order.Status = OrderStatus.BrokerRejected;
                order.RejectReason = report.Text ?? "Rejected by broker";
                db.Audit.Add(AuditEntry.For(order, "BrokerRejected", order.RejectReason, now));
                break;

            default:
                db.Audit.Add(AuditEntry.For(order, "Execution", $"ExecType {report.ExecType} ({report.ExecId})", now));
                break;
        }

        await db.SaveChangesAsync(ct);
        loggerFactory.CreateLogger("TradeFlow.Executions")
            .LogInformation("{ClientOrderId} execution {ExecType} -> {Status}", order.ClientOrderId, report.ExecType, order.Status);
        return Results.Ok(order.ToResponse());
    }

    private static async Task<IResult> GetExecutionsAsync(Guid id, TradeDb db, CancellationToken ct)
    {
        var rows = await db.Executions.AsNoTracking()
            .Where(e => e.OrderId == id)
            .OrderBy(e => e.Id)
            .Select(e => new { e.ExecId, e.ExecType, e.OrdStatus, e.LastQty, e.LastPx, e.CumQty, e.LeavesQty, e.AvgPx, e.Text, e.ReceivedAt })
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> ReconcileAsync(TradeDb db, TimeProvider clock, int? breakAfterSeconds, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking()
            .Select(o => new { o.Id, o.ClientOrderId, o.MessageType, o.Status, o.ReceivedAt })
            .ToListAsync(ct);
        var pendingOutbox = await db.Outbox.CountAsync(m => m.PublishedAt == null, ct);

        var finished = new[] { OrderStatus.Booked, OrderStatus.Rejected, OrderStatus.BrokerRejected };
        var cutoff = clock.GetUtcNow().AddSeconds(-(breakAfterSeconds ?? 60));
        var breaks = orders
            .Where(o => !finished.Contains(o.Status) && o.ReceivedAt < cutoff)
            .Select(o => new { o.Id, o.ClientOrderId, o.MessageType, o.Status, o.ReceivedAt })
            .ToList();

        return Results.Ok(new
        {
            Total = orders.Count,
            Rejected = orders.Count(o => o.Status == OrderStatus.Rejected),
            AwaitingPublish = orders.Count(o => o.Status == OrderStatus.Accepted),
            AtBroker = orders.Count(o => (o.Status is OrderStatus.Published or OrderStatus.SentToBroker or OrderStatus.PartiallyFilled)
                                         && o.MessageType == "Order"),
            AwaitingBooking = orders.Count(o => o.Status == OrderStatus.Filled
                                                || (o.Status == OrderStatus.Published && o.MessageType != "Order")),
            BrokerRejected = orders.Count(o => o.Status == OrderStatus.BrokerRejected),
            Booked = orders.Count(o => o.Status == OrderStatus.Booked),
            ByStatus = orders.GroupBy(o => o.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            PendingOutboxMessages = pendingOutbox,
            Breaks = breaks,
        });
    }
}
