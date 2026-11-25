using Microsoft.Extensions.Options;
using TradeFlow.FixGateway;
using TradeFlow.Messaging;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddTradeFlowSharedSettings(builder.Environment);

builder.Services.AddTradeFlowMessaging(builder.Configuration);
builder.Services.Configure<FixClientOptions>(builder.Configuration.GetSection("Fix"));
builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.AddHttpClient<ExecutionClient>((sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IOptions<GatewayOptions>>().Value.ApiBaseUrl.TrimEnd('/') + "/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<FixGatewayWorker>();

builder.Build().Run();
