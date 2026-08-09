#!/usr/bin/env bash
# E8.3 — nightly Postgres backup for the prod stack (docker-compose.prod.yml).
#
# Runs pg_dump INSIDE the running `postgres` container (via `docker compose exec`), so it
# needs no network access to the database beyond what the compose stack already has, and
# works even though docker-compose.prod.yml deliberately does not publish Postgres's port
# to the host (see that file's comments).
#
# NOT yet scheduled anywhere real — see docs/ops/backup-restore.md for the documented cron
# entry to add once a real VPS exists. Tested locally against docker-compose.dev.yml's
# Postgres only (see this epic's done-report); never run against a real prod database,
# since none exists yet.
#
# Usage:
#   BACKUP_DIR=/opt/joride-backend/backups ./backup-postgres.sh
#   (run from the directory containing docker-compose.prod.yml, or set COMPOSE_FILE)

set -euo pipefail

COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.prod.yml}"
BACKUP_DIR="${BACKUP_DIR:-./backups}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT_FILE="${BACKUP_DIR}/joride-postgres-${TIMESTAMP}.sql.gz"

mkdir -p "$BACKUP_DIR"

echo "[backup-postgres] Dumping via 'docker compose -f ${COMPOSE_FILE} exec postgres pg_dump' -> ${OUT_FILE}"
docker compose -f "$COMPOSE_FILE" exec -T postgres \
  pg_dump -U joride -d joride --format=plain --no-owner --no-privileges \
  | gzip > "$OUT_FILE"

# Sanity check: a truncated/failed dump would still leave a (tiny, invalid) gzip file —
# fail loudly rather than silently keeping a useless backup.
if ! gzip -t "$OUT_FILE"; then
  echo "[backup-postgres] ERROR: ${OUT_FILE} failed gzip integrity check — deleting and exiting non-zero." >&2
  rm -f "$OUT_FILE"
  exit 1
fi

echo "[backup-postgres] OK: $(du -h "$OUT_FILE" | cut -f1) written."

echo "[backup-postgres] Pruning backups older than ${RETENTION_DAYS} days in ${BACKUP_DIR}"
find "$BACKUP_DIR" -name 'joride-postgres-*.sql.gz' -mtime "+${RETENTION_DAYS}" -print -delete

# Offsite copy (S3/GCS/etc.) is deliberately NOT included here — there is no object storage
# bucket provisioned for this project yet, and writing a step that references one would be
# exactly the "fake automation" this epic said not to produce. When one exists, add a step
# here (e.g. `aws s3 cp "$OUT_FILE" s3://<bucket>/postgres/` or `gsutil cp ... gs://<bucket>/...`)
# and document the required credentials in docs/ops/backup-restore.md.
