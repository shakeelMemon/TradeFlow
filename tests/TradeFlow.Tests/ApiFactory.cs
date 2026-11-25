using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradeFlow.Messaging;

namespace TradeFlow.Tests;

/// <summary>Runs the real API in memory with its own SQLite file and an in-memory bus.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tradeflow-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:TradeDb", $"Data Source={_dbPath};Pooling=False");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEventPublisher>();
            services.RemoveAll<IEventConsumer>();
            services.AddSingleton<InMemoryBus>();
            services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryBus>());
            services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<InMemoryBus>());
        });
    }

    public InMemoryBus Bus => Services.GetRequiredService<InMemoryBus>();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
