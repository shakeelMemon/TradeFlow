using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TradeFlow.Contracts;

namespace TradeFlow.Tests;

public class OrdersApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string NewId() => "T-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<(HttpStatusCode Status, OrderResponse Body)> SubmitAsync(TradeOrderRequest order)
    {
        var response = await _client.PostAsJsonAsync("/api/orders", order, Json.Options);
        var body = await response.Content.ReadFromJsonAsync<OrderResponse>(Json.Options);
        return (response.StatusCode, body!);
    }

    private async Task<OrderResponse> GetAsync(Guid id) =>
        (await _client.GetFromJsonAsync<OrderResponse>($"/api/orders/{id}", Json.Options))!;

    private async Task<OrderResponse> WaitForStatusAsync(Guid id, OrderStatus status)
    {
        for (var i = 0; i < 50; i++)
        {
            var order = await GetAsync(id);
            if (order.Status == status) return order;
            await Task.Delay(100);
        }
        return await GetAsync(id);
    }

    [Fact]
    public async Task Valid_order_is_accepted_and_published_to_the_bus()
    {
        var id = NewId();
        var (status, body) = await SubmitAsync(OrderValidatorTests.ValidBondOrder(id));

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(OrderStatus.Accepted, body.Status);

        var published = await WaitForStatusAsync(body.OrderId, OrderStatus.Published);
        Assert.Equal(OrderStatus.Published, published.Status);
        Assert.Contains(factory.Bus.Published, m => m.EventType == EventTypes.OrderAccepted && m.Body.Contains(id));
    }

    [Fact]
    public async Task Duplicate_order_returns_the_original_result()
    {
        var order = OrderValidatorTests.ValidBondOrder(NewId());
        var first = await SubmitAsync(order);
        var second = await SubmitAsync(order);

        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(first.Body.OrderId, second.Body.OrderId);
    }

    [Fact]
    public async Task Invalid_order_is_rejected_with_reasons_and_audited()
    {
        var (status, body) = await SubmitAsync(OrderValidatorTests.ValidBondOrder(NewId()) with { InstrumentId = "XYZ-UNKNOWN" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(OrderStatus.Rejected, body.Status);
        Assert.Contains(body.Errors, e => e.Contains("Unknown instrument"));

        var audit = await _client.GetFromJsonAsync<JsonElement>($"/api/orders/{body.OrderId}/audit");
        Assert.Equal("Rejected", audit[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Booking_confirmation_completes_the_order_and_is_idempotent()
    {
        var (_, body) = await SubmitAsync(OrderValidatorTests.ValidBondOrder(NewId()));
        var booking = new BookingConfirmation("SCD-12345678", "SimCorp");

        var first = await _client.PostAsJsonAsync($"/api/orders/{body.OrderId}/booking", booking, Json.Options);
        var second = await _client.PostAsJsonAsync($"/api/orders/{body.OrderId}/booking", booking, Json.Options);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var order = await GetAsync(body.OrderId);
        Assert.Equal(OrderStatus.Booked, order.Status);
        Assert.Equal("SCD-12345678", order.ExternalBookingId);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_and_stored()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(OrderValidatorTests.ValidBondOrder(NewId()), options: Json.Options),
        };
        request.Headers.Add("X-Correlation-ID", "corr-test-42");
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<OrderResponse>(Json.Options);

        Assert.Equal("corr-test-42", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal("corr-test-42", body!.CorrelationId);
    }

    [Fact]
    public async Task Reconciliation_reports_counts()
    {
        await SubmitAsync(OrderValidatorTests.ValidBondOrder(NewId()));
        var recon = await _client.GetFromJsonAsync<JsonElement>("/api/reconciliation");
        Assert.True(recon.GetProperty("total").GetInt32() >= 1);
    }

    [Fact]
    public async Task Broker_executions_move_order_to_filled_and_publish_order_filled()
    {
        var (_, body) = await SubmitAsync(OrderValidatorTests.ValidBondOrder(NewId()));
        var id = body.OrderId;

        async Task<OrderResponse> Exec(string execId, string execType, string ordStatus, decimal last, decimal cum, decimal leaves)
        {
            var report = new ExecutionReportDto(execId, "BRK-000001", execType, ordStatus, last, 97.45m, cum, leaves, 97.45m, null);
            var response = await _client.PostAsJsonAsync($"/api/orders/{id}/executions", report, Json.Options);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<OrderResponse>(Json.Options))!;
        }

        var execPrefix = Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(OrderStatus.SentToBroker, (await Exec(execPrefix + "-1", "0", "0", 0, 0, 5_000_000m)).Status);
        Assert.Equal(OrderStatus.PartiallyFilled, (await Exec(execPrefix + "-2", "F", "1", 2_000_000m, 2_000_000m, 3_000_000m)).Status);
        var filled = await Exec(execPrefix + "-3", "F", "2", 3_000_000m, 5_000_000m, 0);
        Assert.Equal(OrderStatus.Filled, filled.Status);
        Assert.Equal(5_000_000m, filled.FilledQuantity);
        Assert.Equal("BRK-000001", filled.BrokerOrderId);

        // Same ExecId again is ignored.
        Assert.Equal(OrderStatus.Filled, (await Exec(execPrefix + "-3", "F", "2", 3_000_000m, 5_000_000m, 0)).Status);

        // The fill is published for SimCorp.
        for (var i = 0; i < 50 && !factory.Bus.Published.Any(m => m.EventType == EventTypes.OrderFilled && m.Body.Contains(id.ToString())); i++)
        {
            await Task.Delay(100);
        }
        Assert.Contains(factory.Bus.Published, m => m.EventType == EventTypes.OrderFilled && m.Body.Contains(id.ToString()));

        var executions = await _client.GetFromJsonAsync<JsonElement>($"/api/orders/{id}/executions");
        Assert.Equal(3, executions.GetArrayLength());
    }
}
