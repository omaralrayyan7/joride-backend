using JoRideBackend.Models.Auth;
using JoRideBackend.Models.Payments;
using JoRideBackend.Models.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Data
{
    /// <summary>
    /// Relational store for money, device-command state, telemetry history, and auth/KYC
    /// security state. Separate from Firestore, which remains the store of record for
    /// users/vehicles/trips (including each vehicle's *live* lat/lng — telemetry history
    /// lives here, the current position is mirrored onto the Vehicle document by
    /// TraccarPollingService).
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
        public DbSet<TelemetrySnapshot> TelemetrySnapshots => Set<TelemetrySnapshot>();
        public DbSet<ProcessedPaymentEvent> ProcessedPaymentEvents => Set<ProcessedPaymentEvent>();
        public DbSet<PendingTopUp> PendingTopUps => Set<PendingTopUp>();
        public DbSet<PaymentAdminAudit> PaymentAdminAudits => Set<PaymentAdminAudit>();
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<KycDocument> KycDocuments => Set<KycDocument>();

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

            modelBuilder.Entity<TelemetrySnapshot>(entity =>
            {
                entity.ToTable("telemetry_snapshots");
                entity.HasKey(e => e.Id);
                // Application-level dedup (TraccarService's in-memory cache) already stops
                // duplicate writes; this unique index is the database-level backstop.
                entity.HasIndex(e => new { e.DeviceId, e.DeviceTime }).IsUnique();
                entity.HasIndex(e => e.VehicleId);
            });

            modelBuilder.Entity<ProcessedPaymentEvent>(entity =>
            {
                entity.ToTable("processed_payment_events");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ProviderEventId).HasMaxLength(255).IsRequired();
                entity.HasIndex(e => e.ProviderEventId).IsUnique();
            });

            modelBuilder.Entity<PendingTopUp>(entity =>
            {
                entity.ToTable("pending_topups");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Amount).HasColumnType("numeric(18,2)");
                entity.Property(e => e.PaymentMethod).HasMaxLength(64).IsRequired();
                entity.Property(e => e.Reference).HasMaxLength(255);
                entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.Status);
            });

            modelBuilder.Entity<PaymentAdminAudit>(entity =>
            {
                entity.ToTable("payment_admin_audits");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
                entity.Property(e => e.AdminLabel).HasMaxLength(255).IsRequired();
                entity.Property(e => e.Details).HasMaxLength(1000).IsRequired();
                entity.HasIndex(e => e.PaymentIntentId);
                entity.HasIndex(e => e.PendingTopUpId);
            });

            modelBuilder.Entity<RefreshToken>(entity =>
            {
                entity.ToTable("refresh_tokens");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.TokenHash).HasMaxLength(255).IsRequired();
                entity.Property(e => e.ReplacedByTokenHash).HasMaxLength(255);
                entity.Property(e => e.CreatedByIp).HasMaxLength(64);
                entity.HasIndex(e => e.TokenHash).IsUnique();
                entity.HasIndex(e => e.FamilyId);
                entity.HasIndex(e => e.UserId);
                entity.Ignore(e => e.IsActive); // computed, not a column
            });

            modelBuilder.Entity<KycDocument>(entity =>
            {
                entity.ToTable("kyc_documents");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.DocumentType).HasConversion<string>().HasMaxLength(32);
                entity.Property(e => e.FileName).HasMaxLength(255).IsRequired();
                entity.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
                entity.Property(e => e.StoragePath).HasMaxLength(500).IsRequired();
                entity.HasIndex(e => e.UserId);
            });
        }
    }
}
