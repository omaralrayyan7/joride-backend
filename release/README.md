# JoRide Backend — Grader/Demo Bundle

Run the whole backend with one command — no .NET SDK, no Postgres install, no manual
`.env` file needed.

```bash
docker-compose -f docker-compose.release.yml up --build
```

Then wait ~30 seconds for services to start. Once running:

- API: http://localhost:9000 (e.g. http://localhost:9000/api/vehicles/available, http://localhost:9000/swagger)
- Admin dashboard: http://localhost:9000/Dashboard
- Traccar (GPS backend) web UI: http://localhost:8083 (default login `admin` / `admin`)

**Note on `/health`:** it will report `503 Unhealthy` out of the box — that's expected, not
a broken container. It aggregates a Postgres check (passes) and a Traccar check (fails,
because no `TRACCAR_TOKEN` is wired in by default — see "Known simplifications" below). The
app itself is fully up; confirm with `http://localhost:9000/api/vehicles/available` instead,
which returns the seeded vehicle list with a `200`.

To stop: `Ctrl+C`, then `docker-compose -f docker-compose.release.yml down` (add `-v` to also
wipe the Postgres volume and start fully fresh next time).

## What's included

- **API** — built from `../Dockerfile`, the same image used for a real deploy.
- **PostgreSQL 16** — same image as `../docker-compose.dev.yml`, with EF Core migrations
  applied automatically on container startup (no manual `dotnet ef database update` step).
- **Traccar** — the GPS/device-command backend, bundled and running.

All credentials in `docker-compose.release.yml` are baked-in **dev-only placeholder values**,
not real secrets — safe to commit, not meant for any real deployment. For an actual production
deploy, use `../docker-compose.prod.yml` with a real `.env` (see `../.env.example`).

## Known simplifications (by design, for a zero-setup demo)

- **No Firebase/Firestore project is configured.** Users, vehicles, and trips run entirely
  in-memory instead of syncing to a real Firestore project (the grader has no GCP
  credentials for `joride-e049b`). Functionally identical for a demo session; nothing
  persists across a container restart. Postgres data (payments, ledger, device commands)
  *does* persist in a Docker volume across restarts.
- **Traccar has no API token wired in.** The Traccar container is running and reachable, but
  the backend needs a `TRACCAR_TOKEN` to actually poll it, and a token can only be generated
  from a running Traccar instance (can't be baked in ahead of time). GPS/device-command
  endpoints run as documented no-ops without one. To wire up live GPS for a demo:
  1. Open http://localhost:8083, log in as `admin`/`admin`, generate a token under your user's
     account settings (or `POST /api/session` then `POST /api/token`).
  2. Add `TRACCAR_TOKEN: "<token>"` under the `api` service's `environment:` in
     `docker-compose.release.yml`, then `docker-compose -f docker-compose.release.yml up -d --build`.
  3. Optionally pair a phone as a test GPS device using an OsmAnd-compatible tracker app
     pointed at `<host>:5055`.
- **No real payment gateway (HyperPay) credentials.** Trip payments go through the app's
  in-app wallet simulation path, which is what the flow uses end-to-end already — no HyperPay
  credentials are required for the booking/payment demo to work.
- **OTP/email codes print to the container log** instead of sending real SMS/email (no Twilio
  or SMTP credentials configured). Run `docker-compose -f docker-compose.release.yml logs api`
  and look for the "TWILIO NOT CONFIGURED" / "SMTP NOT CONFIGURED" blocks to read them.

## Troubleshooting

- **`api` restarts immediately**: check `docker-compose -f docker-compose.release.yml logs api`
  — most likely Postgres wasn't ready yet; the `depends_on: condition: service_healthy` should
  prevent this, but if it happens, `docker-compose -f docker-compose.release.yml up -d` again.
- **Port already in use**: something else on the host is using 9000, 8083, or 5055. Stop it,
  or edit the left-hand side of the relevant `ports:` mapping in `docker-compose.release.yml`.
