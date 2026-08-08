using JoRideBackend.Models.Payments;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Data
{
    /// <summary>
    /// Relational store for money and device-command state. Separate from Firestore,
    /// which remains the store of record for users/vehicles/trips.
    /// </summary>
    public class PaymentsDbContext : DbContext
    {
        public PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : base(options)
        {
        }

        public DbSet<PaymentIntent> PaymentIntents => Set<PaymentIntent>();
        public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
        public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
        public DbSet<CommandAudit> CommandAudits => Set<CommandAudit>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PaymentIntent>(entity =>
            {
                entity.ToTable("payment_intents");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Amount).HasColumnType("numeric(18,2)");
                entity.Property(e => e.Currency).HasMaxLength(3).IsRequired();
                entity.Property(e => e.State).HasConversion<string>().HasMaxLength(20);
                entity.Property(e => e.ProviderRef).HasMaxLength(255);
                entity.HasIndex(e => e.TripId);
                entity.HasIndex(e => e.UserId);
            });

            modelBuilder.Entity<LedgerEntry>(entity =>
            {
                entity.ToTable("ledger_entries");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Amount).HasColumnType("numeric(18,2)");
                entity.Property(e => e.DebitAccount).HasMaxLength(255).IsRequired();
                entity.Property(e => e.CreditAccount).HasMaxLength(255).IsRequired();
                entity.Property(e => e.Reference).HasMaxLength(255);
                entity.HasOne<PaymentIntent>()
                    .WithMany()
                    .HasForeignKey(e => e.PaymentIntentId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<DeviceCommand>(entity =>
            {
                entity.ToTable("device_commands");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ImeiOrDeviceId).HasMaxLength(64).IsRequired();
                entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(20);
                entity.Property(e => e.State).HasConversion<string>().HasMaxLength(20);
                entity.HasIndex(e => e.VehicleId);
                entity.HasIndex(e => e.ImeiOrDeviceId);
            });

            modelBuilder.Entity<CommandAudit>(entity =>
            {
                entity.ToTable("command_audits");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Result).HasMaxLength(255).IsRequired();
                entity.HasOne<DeviceCommand>()
                    .WithMany()
                    .HasForeignKey(e => e.DeviceCommandId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
