using Microsoft.Extensions.Options;
using TradeFlow.SimCorpSimulator;
using TradeFlow.Legacy;
using TradeFlow.Messaging;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddTradeFlowSharedSettings(builder.Environment);

builder.Services.AddLegacyQueue(builder.Configuration);
builder.Services.AddTradeFlowMessaging(builder.Configuration);
builder.Services.Configure<SimCorpOptions>(builder.Configuration.GetSection("SimCorp"));
builder.Services.PostConfigure<SimCorpOptions>(o =>
    o.OrdersViaFix = builder.Configuration.GetValue("Routing:OrdersViaFix", false));
builder.Services.AddHttpClient<BookingClient>((sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IOptions<SimCorpOptions>>().Value.ApiBaseUrl.TrimEnd('/') + "/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<BookingWorker>();
builder.Services.AddHostedService<LegacyFeedWorker>();

builder.Build().Run();
