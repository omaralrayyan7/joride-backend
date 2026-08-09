# E8.3 — production image for JoRideBackend.
#
# Multi-stage: the SDK image (large, has the compiler/msbuild) only exists to produce a
# publish output; the final image is the much smaller ASP.NET runtime image plus that
# output — the SDK, source code, and NuGet cache never ship in the image that actually runs.
#
# No secrets are baked in anywhere in this file. Every credential (JWT signing key, Firebase
# service account path, Postgres connection string, Twilio/SMTP/HyperPay/Traccar/KYC
# secrets) is supplied at container run time via environment variables or a mounted file —
# see docker-compose.prod.yml and .env.example. appsettings.json ships with placeholder
# values only (already the case in this repo — see appsettings.json's "REPLACE_WITH_..."
# entries), and appsettings.Development.json / *firebase-adminsdk*.json are both gitignored
# and therefore never present in the build context that gets sent to `docker build` either.

# ---- build stage --------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, on just the project files, so `docker build` can cache the (slow) restore
# layer across rebuilds that only change source, not dependencies.
COPY JoRideBackend/JoRideBackend.csproj JoRideBackend/
RUN dotnet restore JoRideBackend/JoRideBackend.csproj

COPY JoRideBackend/ JoRideBackend/
RUN dotnet publish JoRideBackend/JoRideBackend.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# ---- runtime stage -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root: the base image ships a pre-created, unprivileged "app" user/group (uid/gid 64198
# on Debian-based .NET 8 images) specifically for this purpose — use it rather than rolling
# a custom one.
COPY --from=build --chown=app:app /app/publish .

USER app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "JoRideBackend.dll"]
