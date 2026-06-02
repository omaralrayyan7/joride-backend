# JoRide Backend Merged Version

This backend was merged from `joride-backend-FINAL.zip` and `joride-backend-master(2).zip`.

## Kept / merged features

- Existing vehicle, pricing, trip, wallet, notification, digital-key, and location APIs.
- Launch-ready trip flow from the master backend:
  - paid booking required before issuing the digital key;
  - scheduled trip end time;
  - duration, duration type, base fare, booking fee, tax, total fare, payment method, payment status, paid time;
  - Firestore trip persistence for booking and payment data.
- OTP features from the FINAL backend:
  - SMS OTP endpoint;
  - Email OTP endpoint;
  - OTP verification endpoints;
  - email verification flag on users.
- License verification service from the FINAL backend, made configurable so development registration is not blocked by mock-only license data.
- Swagger support in development mode.
- JWT role claim and AdminOnly authorization policy.
- Firestore user persistence updated to include admin, license verification, and email verification flags.

## Security cleanup

- Real Twilio credentials, Gmail SMTP credentials, and generated JWT secrets were removed from `appsettings.json`.
- `Jwt:Key` is set to a placeholder. Replace it before deployment with a long random secret.
- Firebase service-account path is still a placeholder and must be changed locally.
- Swagger only runs in Development mode.

## Required local configuration

Edit `JoRideBackend/appsettings.json` or use environment variables/user-secrets for:

- `Jwt:Key`
- `Firebase:ProjectId`
- `Firebase:CredentialsPath`
- `Twilio:AccountSid`
- `Twilio:AuthToken`
- `Twilio:FromPhoneNumber`
- `Smtp:User`
- `Smtp:Password`
- `Cors:AllowedOrigins`

## Run

```powershell
cd JoRideBackend
dotnet restore
dotnet run --launch-profile http
```

Expected development API URL: `http://localhost:5007`

Swagger in development: `http://localhost:5007/swagger`
