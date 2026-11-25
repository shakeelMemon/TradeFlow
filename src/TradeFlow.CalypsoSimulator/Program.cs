using TradeFlow.Legacy;
using TradeFlow.Messaging;
using TradeFlow.CalypsoSimulator;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddTradeFlowSharedSettings(builder.Environment);

builder.Services.AddLegacyQueue(builder.Configuration);
builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.AddHostedService<CalypsoPublisher>();

builder.Build().Run();
