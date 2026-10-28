using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Data;

public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItemEntity> StockItems => Set<StockItemEntity>();
    public DbSet<ReservationEntity> Reservations => Set<ReservationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StockItemEntity>(e =>
        {
            e.HasKey(s => s.Sku);
            e.Property(s => s.Sku).HasMaxLength(50);
            e.Property(s => s.Name).HasMaxLength(200).IsRequired();

            // Last line of defence: even a buggy query can never store negative stock.
            e.ToTable(t => t.HasCheckConstraint("CK_StockItems_Available_NonNegative", "Available >= 0"));

            e.HasData(
                new StockItemEntity { Sku = "SKU-1001", Name = "Mechanical Keyboard", Available = 100 },
                new StockItemEntity { Sku = "SKU-1002", Name = "27\" Monitor", Available = 50 },
                new StockItemEntity { Sku = "SKU-1003", Name = "USB-C Dock", Available = 10 },
                new StockItemEntity { Sku = "SKU-1004", Name = "Limited Edition Mug", Available = 0 });
        });

        modelBuilder.Entity<ReservationEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Sku).HasMaxLength(50).IsRequired();
            e.Property(r => r.ReservedAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));

            // One reservation per (order, SKU): the database itself makes duplicates impossible.
            e.HasIndex(r => new { r.OrderId, r.Sku }).IsUnique();
        });
    }
}