using Google.Cloud.Firestore;
using JoRideBackend.Models;

namespace JoRideBackend.Services
{
    public class FirestoreService
    {
        private readonly FirestoreDb? _db;
        private readonly ILogger<FirestoreService> _logger;
        public bool IsConnected { get; }

        private const string ColUsers         = "users";
        private const string ColTrips         = "trips";
        private const string ColNotifications = "notifications";
        private const string ColTransactions  = "wallet_transactions";
        private const string ColVehicles      = "vehicles";
        private const string ColPricings      = "pricings";
        private const string ColAuditLogs     = "audit_logs";

        public FirestoreService(IConfiguration config, ILogger<FirestoreService> logger)
        {
            _logger = logger;
            var projectId = config["Firebase:ProjectId"];
            if (string.IsNullOrEmpty(projectId))
            {
                _logger.LogInformation("Firebase:ProjectId not set — using in-memory storage only.");
                return;
            }
            try
            {
                var credPath = config["Firebase:CredentialsPath"];
                var builder  = new FirestoreDbBuilder { ProjectId = projectId };
                if (!string.IsNullOrEmpty(credPath))
                    builder.CredentialsPath = credPath;
                _db          = builder.Build();
                IsConnected  = true;
                _logger.LogInformation("Firestore connected: project={P}", projectId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to Firestore");
            }
        }

        // ── Users ────────────────────────────────────────────────────────────────
        public async Task<List<User>> LoadUsersAsync()
        {
            if (!IsConnected) return [];
            try
            {
                var snap = await _db!.Collection(ColUsers).GetSnapshotAsync();
                return [.. snap.Documents.Select(DocToUser)];
            }
            catch (Exception ex) { _logger.LogError(ex, "LoadUsersAsync failed"); return []; }
        }

        public async Task SaveUserAsync(User u)
        {
            if (!IsConnected) return;
            try
            {
                var dict = new Dictionary<string, object?>
                {
                    ["id"]                   = (long)u.Id,
                    ["name"]                 = u.Name,
                    ["email"]                = u.Email,
                    ["passwordHash"]         = u.PasswordHash,
                    ["phone"]                = u.Phone,
                    ["idNumber"]             = u.IdNumber,
                    ["drivingLicenseNumber"] = u.DrivingLicenseNumber,
                    ["isActive"]             = u.IsActive,
                    ["isAdmin"]              = u.IsAdmin,
                    ["isLicenseVerified"]    = u.IsLicenseVerified,
                    ["isEmailVerified"]      = u.IsEmailVerified,
                    ["isPhoneVerified"]      = u.IsPhoneVerified,
                    ["createdAt"]            = Timestamp.FromDateTime(u.CreatedAt.ToUniversalTime()),
                    ["walletBalance"]        = (double)u.WalletBalance,
                    ["profileImageUrl"]      = u.ProfileImageUrl,
                    ["failedLoginAttempts"]  = (long)u.FailedLoginAttempts,
                };
                if (u.LockoutEndUtc.HasValue)
                    dict["lockoutEndUtc"] = Timestamp.FromDateTime(u.LockoutEndUtc.Value.ToUniversalTime());
                await _db!.Collection(ColUsers).Document(u.Id.ToString()).SetAsync(dict);
            }
            catch (Exception ex) { _logger.LogError(ex, "SaveUserAsync id={Id}", u.Id); }
        }

        public async Task DeleteUserAsync(int id)
        {
            if (!IsConnected) return;
            try { await _db!.Collection(ColUsers).Document(id.ToString()).DeleteAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "DeleteUserAsync id={Id}", id); }
        }

        // ── Trips ────────────────────────────────────────────────────────────────
        public async Task<List<Trip>> LoadTripsAsync()
        {
            if (!IsConnected) return [];
            try
            {
                var snap = await _db!.Collection(ColTrips).GetSnapshotAsync();
                return [.. snap.Documents.Select(DocToTrip)];
            }
            catch (Exception ex) { _logger.LogError(ex, "LoadTripsAsync failed"); return []; }
        }

        public async Task SaveTripAsync(Trip t)
        {
            if (!IsConnected) return;
            try
            {
                var dict = new Dictionary<string, object?>
                {
                    ["id"]                = (long)t.Id,
                    ["userId"]            = (long)t.UserId,
                    ["vehicleId"]         = (long)t.VehicleId,
                    ["startTime"]         = Timestamp.FromDateTime(t.StartTime.ToUniversalTime()),
                    ["duration"]          = (long)t.Duration,
                    ["durationType"]      = t.DurationType,
                    ["baseFare"]          = (double)t.BaseFare,
                    ["bookingFee"]        = (double)t.BookingFee,
                    ["tax"]               = (double)t.Tax,
                    ["totalFare"]         = (double)t.TotalFare,
                    ["overtimeMinutes"]   = (long)t.OvertimeMinutes,
                    ["overtimeFare"]      = (double)t.OvertimeFare,
                    ["overtimePaymentStatus"] = t.OvertimePaymentStatus,
                    ["paymentMethod"]     = t.PaymentMethod,
                    ["paymentStatus"]     = t.PaymentStatus,
                    ["digitalKeyEnabled"] = t.DigitalKeyEnabled,
                    ["status"]            = t.Status,
                };
                if (t.ScheduledEndTime.HasValue)
                    dict["scheduledEndTime"] = Timestamp.FromDateTime(t.ScheduledEndTime.Value.ToUniversalTime());
                if (t.EndTime.HasValue)
                    dict["endTime"] = Timestamp.FromDateTime(t.EndTime.Value.ToUniversalTime());
                if (t.PaidAt.HasValue)
                    dict["paidAt"] = Timestamp.FromDateTime(t.PaidAt.Value.ToUniversalTime());
                await _db!.Collection(ColTrips).Document(t.Id.ToString()).SetAsync(dict);
            }
            catch (Exception ex) { _logger.LogError(ex, "SaveTripAsync id={Id}", t.Id); }
        }

        // ── Notifications ────────────────────────────────────────────────────────
        public async Task<List<Notification>> LoadNotificationsAsync()
        {
            if (!IsConnected) return [];
            try
            {
                var snap = await _db!.Collection(ColNotifications).GetSnapshotAsync();
                return [.. snap.Documents.Select(DocToNotification)];
            }
            catch (Exception ex) { _logger.LogError(ex, "LoadNotificationsAsync failed"); return []; }
        }

        public async Task SaveNotificationAsync(Notification n)
        {
            if (!IsConnected) return;
            try
            {
                await _db!.Collection(ColNotifications).Document(n.Id.ToString()).SetAsync(new Dictionary<string, object?>
                {
                    ["id"]        = (long)n.Id,
                    ["userId"]    = (long)n.UserId,
                    ["title"]     = n.Title,
                    ["body"]      = n.Body,
                    ["type"]      = n.Type,
                    ["isRead"]    = n.IsRead,
                    ["createdAt"] = Timestamp.FromDateTime(n.CreatedAt.ToUniversalTime()),
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "SaveNotificationAsync id={Id}", n.Id); }
        }

        // ── Wallet Transactions ──────────────────────────────────────────────────
        public async Task<List<WalletTransaction>> LoadTransactionsAsync()
        {
            if (!IsConnected) return [];
            try
            {
                var snap = await _db!.Collection(ColTransactions).GetSnapshotAsync();
                return [.. snap.Documents.Select(DocToTransaction)];
            }
            catch (Exception ex) { _logger.LogError(ex, "LoadTransactionsAsync failed"); return []; }
        }

        public async Task SaveTransactionAsync(WalletTransaction t)
        {
            if (!IsConnected) return;
            try
            {
                await _db!.Collection(ColTransactions).Document(t.Id.ToString()).SetAsync(new Dictionary<string, object?>
                {
                    ["id"]          = (long)t.Id,
                    ["userId"]      = (long)t.UserId,
                    ["type"]        = t.Type,
                    ["amount"]      = (double)t.Amount,
                    ["description"] = t.Description,
                    ["createdAt"]   = Timestamp.FromDateTime(t.CreatedAt.ToUniversalTime()),
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "SaveTransactionAsync id={Id}", t.Id); }
        }

        // ── Vehicles ─────────────────────────────────────────────────────────────
        public async Task<List<Vehicle>> LoadVehiclesAsync()
        {
            if (!IsConnected) return [];
            try
            {
                var snap = await _db!.Collection(ColVehicles).GetSnapshotAsync();
                return [.. snap.Documents.Select(DocToVehicle)];
            }
            catch (Exception ex) { _logger.LogError(ex, "LoadVehiclesAsync failed"); return []; }
        }

        public async Task SaveVehicleAsync(Vehicle v)
        {
            if (!IsConnected) return;
            try
            {
                await _db!.Collection(ColVehicles).Document(v.Id.ToString()).SetAsync(new Dictionary<string, object?>
                {
                    ["id"]           = (long)v.Id,
                    ["licensePlate"] = v.LicensePlate,
                    ["model"]        = v.Model,
                    ["category"]     = v.Category,
                    ["color"]        = v.Color,
                    ["status"]       = v.Status,
                    ["latitude"]     = v.Latitude,
                    ["longitude"]    = v.Longitude,
                    ["fuelLevel"]    = (long)v.FuelLevel,
                    ["imageUrl"]     = v.ImageUrl,
                    ["brandLogoUrl"] = v.BrandLogoUrl,
                    ["isVisible"]    = v.IsVisible,
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "SaveVehicleAsync id={Id}", v.Id); }
        }

        public async Task DeleteVehicleAsync(int id)
        {
            if (!IsConnected) return;
            try { await _db!.Collection(ColVehicles).Document(id.ToString()).DeleteAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "DeleteVehicleAsync id={Id}", id); }
        }

        // ── Pricings ──────────────────────────────────────────────────────────────
        public async Task<List<Pricing>> LoadPricingsAsync()
        {
            if (!IsConnected) return [];
            try
            {
                var snap = await _db!.Collection(ColPricings).GetSnapshotAsync();
                return [.. snap.Documents.Select(DocToPricing)];
            }
            catch (Exception ex) { _logger.LogError(ex, "LoadPricingsAsync failed"); return []; }
        }

        public async Task SavePricingAsync(Pricing p)
        {
            if (!IsConnected) return;
            try
            {
                await _db!.Collection(ColPricings).Document(p.Id.ToString()).SetAsync(new Dictionary<string, object?>
                {
                    ["id"]         = (long)p.Id,
                    ["category"]   = p.Category,
                    ["minuteRate"] = (double)p.MinuteRate,
                    ["hourlyRate"] = (double)p.HourlyRate,
                    ["dailyRate"]  = (double)p.DailyRate,
                    ["isActive"]   = p.IsActive,
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "SavePricingAsync id={Id}", p.Id); }
        }

        public async Task DeletePricingAsync(int id)
        {
            if (!IsConnected) return;
            try { await _db!.Collection(ColPricings).Document(id.ToString()).DeleteAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "DeletePricingAsync id={Id}", id); }
        }

        // ── Audit Logs ─────────────────────────────────────────────────────────────
        public async Task<List<AuditLog>> LoadAuditLogsAsync()
        {
            if (!IsConnected) return [];
            try
            {
                var snap = await _db!.Collection(ColAuditLogs).GetSnapshotAsync();
                return [.. snap.Documents.Select(DocToAuditLog)];
            }
            catch (Exception ex) { _logger.LogError(ex, "LoadAuditLogsAsync failed"); return []; }
        }

        public async Task SaveAuditLogAsync(AuditLog a)
        {
            if (!IsConnected) return;
            try
            {
                await _db!.Collection(ColAuditLogs).Document(a.Id.ToString()).SetAsync(new Dictionary<string, object?>
                {
                    ["id"]         = (long)a.Id,
                    ["timestamp"]  = Timestamp.FromDateTime(a.Timestamp.ToUniversalTime()),
                    ["action"]     = a.Action,
                    ["entityType"] = a.EntityType,
                    ["entityId"]   = (long)a.EntityId,
                    ["actor"]      = a.Actor,
                    ["actorRole"]  = a.ActorRole,
                    ["details"]    = a.Details,
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "SaveAuditLogAsync id={Id}", a.Id); }
        }

        // ── Converters ───────────────────────────────────────────────────────────
        private static User DocToUser(DocumentSnapshot d) => new()
        {
            Id                   = (int)d.GetValue<long>("id"),
            Name                 = Str(d, "name"),
            Email                = Str(d, "email"),
            PasswordHash         = Str(d, "passwordHash"),
            Phone                = Str(d, "phone"),
            IdNumber             = Str(d, "idNumber"),
            DrivingLicenseNumber = Str(d, "drivingLicenseNumber"),
            IsActive             = d.ContainsField("isActive") && d.GetValue<bool>("isActive"),
            IsAdmin              = d.ContainsField("isAdmin") && d.GetValue<bool>("isAdmin"),
            IsLicenseVerified    = d.ContainsField("isLicenseVerified") && d.GetValue<bool>("isLicenseVerified"),
            IsEmailVerified      = d.ContainsField("isEmailVerified") && d.GetValue<bool>("isEmailVerified"),
            IsPhoneVerified      = d.ContainsField("isPhoneVerified") && d.GetValue<bool>("isPhoneVerified"),
            CreatedAt            = d.GetValue<Timestamp>("createdAt").ToDateTime(),
            WalletBalance        = (decimal)d.GetValue<double>("walletBalance"),
            ProfileImageUrl      = Str(d, "profileImageUrl"),
            FailedLoginAttempts  = d.ContainsField("failedLoginAttempts") ? (int)d.GetValue<long>("failedLoginAttempts") : 0,
            LockoutEndUtc        = d.ContainsField("lockoutEndUtc") ? d.GetValue<Timestamp>("lockoutEndUtc").ToDateTime() : null,
        };

        private static Trip DocToTrip(DocumentSnapshot d) => new()
        {
            Id                = (int)d.GetValue<long>("id"),
            UserId            = (int)d.GetValue<long>("userId"),
            VehicleId         = (int)d.GetValue<long>("vehicleId"),
            StartTime         = d.GetValue<Timestamp>("startTime").ToDateTime(),
            ScheduledEndTime  = d.ContainsField("scheduledEndTime") ? d.GetValue<Timestamp>("scheduledEndTime").ToDateTime() : null,
            EndTime           = d.ContainsField("endTime") ? d.GetValue<Timestamp>("endTime").ToDateTime() : null,
            Duration          = d.ContainsField("duration") ? (int)d.GetValue<long>("duration") : 0,
            DurationType      = Str(d, "durationType"),
            BaseFare          = d.ContainsField("baseFare") ? (decimal)d.GetValue<double>("baseFare") : 0m,
            BookingFee        = d.ContainsField("bookingFee") ? (decimal)d.GetValue<double>("bookingFee") : 0m,
            Tax               = d.ContainsField("tax") ? (decimal)d.GetValue<double>("tax") : 0m,
            TotalFare         = d.ContainsField("totalFare") ? (decimal)d.GetValue<double>("totalFare") : 0m,
            OvertimeMinutes   = d.ContainsField("overtimeMinutes") ? (int)d.GetValue<long>("overtimeMinutes") : 0,
            OvertimeFare      = d.ContainsField("overtimeFare") ? (decimal)d.GetValue<double>("overtimeFare") : 0m,
            OvertimePaymentStatus = Str(d, "overtimePaymentStatus"),
            PaymentMethod     = Str(d, "paymentMethod"),
            PaymentStatus     = Str(d, "paymentStatus"),
            PaidAt            = d.ContainsField("paidAt") ? d.GetValue<Timestamp>("paidAt").ToDateTime() : null,
            DigitalKeyEnabled = d.ContainsField("digitalKeyEnabled") && d.GetValue<bool>("digitalKeyEnabled"),
            Status            = Str(d, "status"),
        };

        private static Notification DocToNotification(DocumentSnapshot d) => new()
        {
            Id        = (int)d.GetValue<long>("id"),
            UserId    = (int)d.GetValue<long>("userId"),
            Title     = Str(d, "title"),
            Body      = Str(d, "body"),
            Type      = Str(d, "type"),
            IsRead    = d.GetValue<bool>("isRead"),
            CreatedAt = d.GetValue<Timestamp>("createdAt").ToDateTime(),
        };

        private static WalletTransaction DocToTransaction(DocumentSnapshot d) => new()
        {
            Id          = (int)d.GetValue<long>("id"),
            UserId      = (int)d.GetValue<long>("userId"),
            Type        = Str(d, "type"),
            Amount      = (decimal)d.GetValue<double>("amount"),
            Description = Str(d, "description"),
            CreatedAt   = d.GetValue<Timestamp>("createdAt").ToDateTime(),
        };

        private static Vehicle DocToVehicle(DocumentSnapshot d) => new()
        {
            Id           = (int)d.GetValue<long>("id"),
            LicensePlate = Str(d, "licensePlate"),
            Model        = Str(d, "model"),
            Category     = Str(d, "category"),
            Color        = Str(d, "color"),
            Status       = Str(d, "status"),
            Latitude     = d.ContainsField("latitude") ? d.GetValue<double>("latitude") : 0,
            Longitude    = d.ContainsField("longitude") ? d.GetValue<double>("longitude") : 0,
            FuelLevel    = d.ContainsField("fuelLevel") ? (int)d.GetValue<long>("fuelLevel") : 100,
            ImageUrl     = Str(d, "imageUrl"),
            BrandLogoUrl = Str(d, "brandLogoUrl"),
            IsVisible    = !d.ContainsField("isVisible") || d.GetValue<bool>("isVisible"),
        };

        private static Pricing DocToPricing(DocumentSnapshot d) => new()
        {
            Id         = (int)d.GetValue<long>("id"),
            Category   = Str(d, "category"),
            MinuteRate = d.ContainsField("minuteRate") ? (decimal)d.GetValue<double>("minuteRate") : 0m,
            HourlyRate = d.ContainsField("hourlyRate") ? (decimal)d.GetValue<double>("hourlyRate") : 0m,
            DailyRate  = d.ContainsField("dailyRate") ? (decimal)d.GetValue<double>("dailyRate") : 0m,
            IsActive   = d.ContainsField("isActive") && d.GetValue<bool>("isActive"),
        };

        private static AuditLog DocToAuditLog(DocumentSnapshot d) => new()
        {
            Id         = (int)d.GetValue<long>("id"),
            Timestamp  = d.GetValue<Timestamp>("timestamp").ToDateTime(),
            Action     = Str(d, "action"),
            EntityType = Str(d, "entityType"),
            EntityId   = d.ContainsField("entityId") ? (int)d.GetValue<long>("entityId") : 0,
            Actor      = Str(d, "actor"),
            ActorRole  = Str(d, "actorRole"),
            Details    = Str(d, "details"),
        };

        private static string? Str(DocumentSnapshot d, string field)
            => d.ContainsField(field) ? d.GetValue<string>(field) : null;
    }
}
