using Microsoft.Extensions.Options;
using TradeFlow.Bridge;
using TradeFlow.Legacy;
using TradeFlow.Messaging;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddTradeFlowSharedSettings(builder.Environment);

builder.Services.AddLegacyQueue(builder.Configuration);
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.AddHttpClient<TradeApiClient>((sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IOptions<BridgeOptions>>().Value.ApiBaseUrl.TrimEnd('/') + "/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<BridgeWorker>();

builder.Build().Run();
