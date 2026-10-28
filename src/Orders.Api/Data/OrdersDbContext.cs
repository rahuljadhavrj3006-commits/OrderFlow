using Microsoft.EntityFrameworkCore;

namespace Orders.Api.Data;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderEntity>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.CustomerId).HasMaxLength(100).IsRequired();
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);

            // SQLite can't sort DateTimeOffset, so store it as UTC ticks (a long).
            e.Property(o => o.CreatedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));

            e.Property(o => o.Version).IsConcurrencyToken();
            e.HasIndex(o => o.CreatedAt);

            e.HasMany(o => o.Items)
                .WithOne()
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItemEntity>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Sku).HasMaxLength(50).IsRequired();
        });
    }
}