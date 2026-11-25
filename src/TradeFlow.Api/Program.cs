using Microsoft.EntityFrameworkCore;
using TradeFlow.Api;
using TradeFlow.Api.Data;
using TradeFlow.Api.Endpoints;
using TradeFlow.Api.Outbox;
using TradeFlow.Api.Validation;
using TradeFlow.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddTradeFlowSharedSettings(builder.Environment);

builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<TradeDb>((sp, options) =>
    options.UseSqlite(sp.GetRequiredService<IConfiguration>().GetConnectionString("TradeDb") ?? "Data Source=tradeflow-v2.db"));
builder.Services.AddSingleton<ReferenceData>();
builder.Services.AddSingleton<OrderValidator>();
builder.Services.AddTradeFlowMessaging(builder.Configuration);
builder.Services.AddHostedService<OutboxPublisher>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<TradeDb>().Database.EnsureCreated();
}

app.UseCorrelationId();
app.MapOpenApi();
app.MapGet("/", () => Results.Redirect("/openapi/v1.json")).ExcludeFromDescription();
app.MapOrderEndpoints();

app.Run();

public partial class Program;
