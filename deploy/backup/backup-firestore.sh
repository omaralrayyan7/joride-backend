#!/usr/bin/env bash
# E8.3 — nightly Firestore export, via the real, standard GCP mechanism:
# `gcloud firestore export`. This is NOT a pg_dump-style local file — Firestore's managed
# export writes directly to a Google Cloud Storage bucket you specify, using whatever
# identity `gcloud` is currently authenticated as (a service account with the
# `datastore.exportAdmin` — or "Cloud Datastore Import Export Admin" — IAM role on the
# joride-e049b project).
#
# NOT yet run against the real project: this requires (a) a GCS bucket to export into,
# which doesn't exist yet, and (b) a service account with export permissions, which also
# hasn't been provisioned. Both are one-time GCP setup steps documented in
# docs/ops/backup-restore.md, not something this script can create for you.
#
# Usage (once the bucket + IAM are set up):
#   FIRESTORE_BACKUP_BUCKET=gs://joride-firestore-backups ./backup-firestore.sh

set -euo pipefail

: "${FIRESTORE_BACKUP_BUCKET:?Set FIRESTORE_BACKUP_BUCKET to a gs:// bucket URI, e.g. gs://joride-firestore-backups}"
PROJECT_ID="${FIREBASE_PROJECT_ID:-joride-e049b}"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DEST="${FIRESTORE_BACKUP_BUCKET%/}/${TIMESTAMP}"

echo "[backup-firestore] Exporting project '${PROJECT_ID}' to ${DEST}"
gcloud firestore export "$DEST" --project="$PROJECT_ID"

echo "[backup-firestore] Export started (gcloud firestore export is asynchronous — check status with:"
echo "  gcloud firestore operations list --project=${PROJECT_ID}"
echo "Bucket lifecycle rules (set once, in GCS, not here) should handle retention/pruning of old exports)."
