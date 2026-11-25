namespace TradeFlow.Api;

/// <summary>Carries one correlation ID from Calypso through the API, the bus and SimCorp for audit.</summary>
public static class Correlation
{
    public const string Header = "X-Correlation-ID";

    public static string GetCorrelationId(this HttpContext context) =>
        context.Items[Header] as string ?? "";

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var id = context.Request.Headers[Header].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N");
            }
            context.Items[Header] = id;
            context.Response.Headers[Header] = id;

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TradeFlow.Api");
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            {
                await next(context);
            }
        });
}
