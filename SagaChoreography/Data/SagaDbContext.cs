using Microsoft.EntityFrameworkCore;
using SagaChoreography.Domain;

namespace SagaChoreography.Data;

public class SagaDbContext(DbContextOptions<SagaDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<SagaLogEntry> SagaLog => Set<SagaLogEntry>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<InventoryReservation> Reservations => Set<InventoryReservation>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Order>().Property(o => o.Amount).HasPrecision(18, 2);
        b.Entity<Payment>().HasKey(p => p.OrderId);
        b.Entity<Payment>().Property(p => p.Amount).HasPrecision(18, 2);
        b.Entity<InventoryReservation>().HasKey(r => r.OrderId);
        b.Entity<Shipment>().HasKey(s => s.OrderId);
        b.Entity<Product>().HasKey(p => p.Sku);
        b.Entity<Product>().HasData(
            new Product { Sku = "LAPTOP", Name = "Laptop", Stock = 10 },
            new Product { Sku = "PHONE", Name = "Phone", Stock = 5 },
            new Product { Sku = "MOUSE", Name = "Mouse", Stock = 100 },
            new Product { Sku = "KEYBOARD", Name = "Mechanical Keyboard", Stock = 40 },
            new Product { Sku = "MONITOR", Name = "27-inch Monitor", Stock = 15 },
            new Product { Sku = "HEADSET", Name = "Wireless Headset", Stock = 25 },
            new Product { Sku = "WEBCAM", Name = "HD Webcam", Stock = 30 },
            new Product { Sku = "DOCK", Name = "USB-C Docking Station", Stock = 8 },
            new Product { Sku = "CHARGER", Name = "65W Charger", Stock = 60 },
            new Product { Sku = "CABLE", Name = "USB-C Cable", Stock = 200 });
    }
}
