using TradeFlow.BrokerSimulator;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<BrokerOptions>(builder.Configuration.GetSection("Broker"));
builder.Services.AddHostedService<BrokerWorker>();
builder.Build().Run();
