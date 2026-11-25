using System.Net;
using System.Net.Http.Json;
using TradeFlow.Contracts;

namespace TradeFlow.Bridge;

public sealed class TradeApiClient(HttpClient http)
{
    /// <summary>
    /// Submits an order. Business rejections (422) are a final answer, not a failure.
    /// Anything else non-2xx throws so the message stays on the legacy queue for retry.
    /// </summary>
    public async Task<OrderResponse> SubmitAsync(TradeOrderRequest request, string correlationId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/orders")
        {
            Content = JsonContent.Create(request, options: Json.Options),
        };
        message.Headers.Add("X-Correlation-ID", correlationId);

        using var response = await http.SendAsync(message, ct);
        if (response.StatusCode != HttpStatusCode.UnprocessableEntity)
        {
            response.EnsureSuccessStatusCode();
        }
        return await response.Content.ReadFromJsonAsync<OrderResponse>(Json.Options, ct)
            ?? throw new InvalidOperationException("Empty response from Trade API.");
    }
}
