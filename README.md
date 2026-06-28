# What is JoRide Backend Project

## Overview

**JoRide Backend** is an **ASP.NET Core 8 REST API** that powers a car-rental / ride-sharing platform for Jordan. It handles user accounts, JWT authentication, vehicle fleet management, trip lifecycle (book → drive → return), in-app wallet payments, OTP verification, digital car keys, push notifications, and an admin dashboard. Data is persisted in **Firebase Firestore** and vehicle GPS tracking is delegated to a **Traccar** server.

**Tech stack:** C# · ASP.NET Core 8 · Firebase Admin SDK (Firestore) · JWT Bearer auth · Swagger/OpenAPI · Traccar GPS API · SMS/Email services

---

## Key Code Segments

### Dependency Injection & JWT Setup (`Program.cs`)
Registers all services and configures JWT Bearer authentication.

```csharp
builder.Services.AddSingleton<FirestoreService>();
builder.Services.AddHostedService<FirestoreDataLoader>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<TraccarService>();
builder.Services.AddScoped<IOtpService, OtpService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuer = true, ValidateAudience = true,
            ValidateLifetime = true, ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key))
        };
    });
```

### Start Trip (`TripsController.cs`)
Validates the user, checks wallet balance, charges the fare, then creates the trip and issues a digital key.

```csharp
[HttpPost("start")]
public async Task<ActionResult<Trip>> Start(StartTripRequest request)
{
    // Block booking if wallet is negative
    var user = UsersController.GetUser(request.UserId);
    if (user?.WalletBalance < 0)
        return BadRequest("Outstanding balance — please top up your wallet first.");

    if (!VehiclesController.IsAvailable(request.VehicleId))
        return BadRequest("Vehicle is not available.");

    // Charge wallet
    var paid = await WalletController.TryChargeAsync(
        request.UserId, request.TotalFare,
        $"Trip payment for vehicle #{request.VehicleId}",
        request.PaymentMethod);

    if (!paid) return BadRequest("Payment failed or insufficient wallet balance.");

    var trip = new Trip { UserId = request.UserId, VehicleId = request.VehicleId,
                          Status = "InProgress", DigitalKeyEnabled = true, ... };
    trips.Add(trip);
    await _firestore.SaveTripAsync(trip);
    return CreatedAtAction(nameof(Get), new { id = trip.Id }, trip);
}
```

### User Registration (`UsersController.cs`)
Hashes the password, verifies driving license, and returns a JWT on success.

```csharp
[HttpPost("register")]
public async Task<ActionResult<object>> Register(RegisterRequest req)
{
    if (users.Any(u => u.Email == req.Email)) return Conflict("Email already registered.");
    var user = new User { Name = req.Name, Email = req.Email, Phone = req.Phone };
    user.PasswordHash = hasher.HashPassword(user, req.Password);
    var verified = await licenseVerifier.VerifyAsync(req.DrivingLicenseNumber);
    user.IsLicenseVerified = verified;
    users.Add(user);
    await _firestore.SaveUserAsync(user);
    var token = tokens.Generate(user);
    return CreatedAtAction(nameof(Get), new { id = user.Id }, BuildProfileResponse(user, token));
}
```
