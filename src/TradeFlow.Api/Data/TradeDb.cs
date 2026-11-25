using Microsoft.EntityFrameworkCore;

namespace TradeFlow.Api.Data;

public sealed class TradeDb(DbContextOptions<TradeDb> options) : DbContext(options)
{
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<ExecutionEntity> Executions => Set<ExecutionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderEntity>(e =>
        {
            e.HasKey(o => o.Id);
            // Idempotency: one order per source system + client order ID.
            e.HasIndex(o => new { o.SourceSystem, o.ClientOrderId }).IsUnique();
            e.Property(o => o.Status).HasConversion<string>();
            e.Property(o => o.Side).HasConversion<string>();
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => m.PublishedAt);
        });

        modelBuilder.Entity<ExecutionEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExecId).IsUnique();
            e.HasIndex(x => x.OrderId);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.OrderId);
        });
    }
}
