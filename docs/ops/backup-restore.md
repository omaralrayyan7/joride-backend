# Backup & Restore (E8.3)

Two independent data stores, two independent backup mechanisms — see `CLAUDE.md`/prior
epics: **Postgres** (money/device-command/telemetry/auth/KYC state, via `PaymentsDbContext`)
and **Firestore** (users/vehicles/trips, the JoRide-specific system of record).

## Postgres — `deploy/backup/backup-postgres.sh`

Runs `pg_dump` inside the running `postgres` container via `docker compose exec` — no
network access to the database is needed beyond what the compose stack already provides,
and it works even though `docker-compose.prod.yml` deliberately does not publish Postgres's
port to the host.

```bash
cd /opt/joride-backend   # wherever docker-compose.prod.yml lives on the VPS
BACKUP_DIR=/opt/joride-backend/backups ./deploy/backup/backup-postgres.sh
```

**Nightly schedule** (add once a real VPS exists — not installed anywhere yet):
```cron
# /etc/cron.d/joride-backup — runs 03:00 UTC daily
0 3 * * * root cd /opt/joride-backend && COMPOSE_FILE=docker-compose.prod.yml BACKUP_DIR=/opt/joride-backend/backups RETENTION_DAYS=14 ./deploy/backup/backup-postgres.sh >> /var/log/joride-backup.log 2>&1
```

**Restore procedure** (tested — see "What was actually verified" below):
```bash
# 1. Stop the app so nothing writes to Postgres mid-restore.
docker compose -f docker-compose.prod.yml stop api

# 2. Restore into the running postgres container. Restoring into a database that already
#    has the same tables will error on CREATE TABLE / fail on constraint conflicts — if
#    this is a genuine disaster-recovery restore (not a test), drop and recreate the
#    database first:
docker compose -f docker-compose.prod.yml exec -T postgres \
  psql -U joride -d postgres -c "DROP DATABASE IF EXISTS joride; CREATE DATABASE joride OWNER joride;"

# 3. Load the dump.
gunzip -c backups/joride-postgres-<TIMESTAMP>.sql.gz | \
  docker compose -f docker-compose.prod.yml exec -T postgres psql -U joride -d joride

# 4. Bring the app back up.
docker compose -f docker-compose.prod.yml start api
```

## Firestore — `deploy/backup/backup-firestore.sh`

Uses the real GCP mechanism, `gcloud firestore export`, which writes an export to a Google
Cloud Storage bucket (not a local file — Firestore has no `pg_dump` equivalent). This
requires one-time setup that has **not** been done yet, since no backup bucket has been
provisioned for this project:

1. Create a GCS bucket in the same GCP project as Firestore (`joride-e049b`), e.g.
   `gs://joride-firestore-backups`, with a lifecycle rule to expire objects after your
   retention window (do this in GCS directly — cheaper and more reliable than a script
   deleting old exports).
2. Grant the service account that will run this script the
   `roles/datastore.importExportAdmin` IAM role on the project.
3. Then:
   ```bash
   FIRESTORE_BACKUP_BUCKET=gs://joride-firestore-backups ./deploy/backup/backup-firestore.sh
   ```

**Nightly schedule** (same caveat — add once the bucket/IAM above exist):
```cron
0 3 * * * root FIRESTORE_BACKUP_BUCKET=gs://joride-firestore-backups /opt/joride-backend/deploy/backup/backup-firestore.sh >> /var/log/joride-firestore-backup.log 2>&1
```

**Restore procedure** (real `gcloud` command, not run against real data — see below):
```bash
gcloud firestore import gs://joride-firestore-backups/<TIMESTAMP> --project=joride-e049b
```
Firestore import merges into the existing database rather than wiping it first — for a
full disaster-recovery restore, delete existing collections first via the Firebase console
or the [Firestore delete-collection CLI recipe](https://firebase.google.com/docs/firestore/manage-data/delete-data#collections)
(there's no single `gcloud` flag for "wipe then import").

## What was actually verified (honest accounting)

- ✅ **`backup-postgres.sh` ran for real** against the local dev Postgres
  (`docker-compose.dev.yml`, real data — 8 `device_commands` rows, 11 `payment_intents`
  rows at the time). Produced a real 12KB gzip dump containing all 11 real tables.
- ✅ **Postgres restore was ran for real** too: the dump was piped into a *fresh, throwaway*
  `postgres:16` container (`docker run`, not part of any compose stack) and the row counts
  matched exactly (8 `device_commands`, 11 `payment_intents`) — proving the dump/restore
  round-trip is correct, not just that the commands don't error.
- ❌ **`backup-firestore.sh` was NOT run.** There is no GCS bucket provisioned and no
  service account with export permissions — running it would just fail on `gcloud`
  authentication/bucket-not-found, which wouldn't prove anything beyond "the command exists
  and takes the arguments described." The `gcloud firestore export`/`import` commands
  themselves are the real, documented GCP mechanism (not invented), but the *script* is
  untested end-to-end pending that one-time GCP setup.
- ❌ **Neither script has been scheduled anywhere** (no cron/systemd timer installed) —
  the cron entries above are what to add once a real VPS exists, not something already running.
