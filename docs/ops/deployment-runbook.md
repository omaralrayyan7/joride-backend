# Production Deployment Runbook (E8.3)

Ties together `Dockerfile`, `docker-compose.prod.yml`, `deploy/nginx/`,
`.github/workflows/deploy.yml`, `docs/ops/backup-restore.md`, and `docs/ops/rollback.md` into
one process. **No real VPS exists yet** — see "What was actually verified" at the bottom of
each linked doc and this one. This runbook is what to *follow*, not a record of something
already done.

## Prerequisites (one-time, before the first deploy)

1. A VPS (or any Docker-capable host) with a public IP, Docker + Docker Compose v2 installed.
2. A domain name with an `A`/`AAAA` record pointing at that IP.
3. A dedicated, least-privilege deploy user on the VPS, in the `docker` group, **not** root.
4. A dedicated SSH keypair for CI to use as that user (don't reuse a personal key). Add the
   public half to that user's `~/.ssh/authorized_keys`.
5. Repo secrets (Settings → Secrets and variables → Actions) — see
   `.github/workflows/deploy.yml`'s header comment for the full list
   (`SSH_HOST`, `SSH_USER`, `SSH_PRIVATE_KEY`, `SSH_PORT`, `DEPLOY_PATH`), and set the
   `DEPLOY_ENABLED` repo/environment **variable** to `true` once those secrets exist —
   the deploy job is skipped until then, on purpose.
6. On the VPS, at `DEPLOY_PATH`: clone the repo (or just copy `docker-compose.prod.yml`,
   `deploy/`, and a real `.env` built from `.env.example` — see that file's header comment
   about the `Jwt__Key`-style double-underscore names, they're not optional cosmetics).
7. The real Firebase service-account JSON, placed somewhere on the VPS **outside** the repo
   checkout, with `FIREBASE_CREDENTIALS_HOST_PATH` in `.env` pointing at it — this file must
   never be committed (see `.gitignore`'s `*firebase-adminsdk*.json` rule) or baked into the
   image (see `Dockerfile`'s header comment).
8. Traccar running separately, on a private network reachable from the VPS but not from the
   public internet — see `docs/ops/traccar-runbook.md` §7. It is intentionally not a
   service in `docker-compose.prod.yml`.

## First deploy — the TLS chicken-and-egg step

`nginx`'s config (`deploy/nginx/default.conf.template`) unconditionally reads a certificate
from `/etc/letsencrypt/live/${DOMAIN_NAME}/...` — nginx **will not start** without one (this
was directly observed while building this runbook: `nginx: [emerg] cannot load certificate
... No such file or directory`, a real crash-loop, not a hypothetical). Certbot's webroot
plugin, in turn, needs nginx running on port 80 to serve the ACME challenge. Breaking that
cycle is a one-time manual step:

```bash
cd $DEPLOY_PATH

# 1. Build and start everything except nginx.
docker compose -f docker-compose.prod.yml up -d --no-deps api postgres certbot

# 2. Get a temporary self-signed cert so nginx can start at all.
mkdir -p ./letsencrypt-bootstrap
openssl req -x509 -nodes -newkey rsa:2048 -days 1 \
  -keyout ./letsencrypt-bootstrap/privkey.pem \
  -out ./letsencrypt-bootstrap/fullchain.pem \
  -subj "/CN=${DOMAIN_NAME}"
docker run --rm -v joride-backend_certbot_certs:/etc/letsencrypt \
  -v "$(pwd)/letsencrypt-bootstrap:/bootstrap" alpine \
  sh -c "mkdir -p /etc/letsencrypt/live/${DOMAIN_NAME} && cp /bootstrap/* /etc/letsencrypt/live/${DOMAIN_NAME}/"

# 3. Now nginx can start (with the fake cert, over real HTTPS syntax).
docker compose -f docker-compose.prod.yml up -d nginx

# 4. Request the REAL certificate via the now-running nginx's ACME challenge path.
docker compose -f docker-compose.prod.yml run --rm certbot \
  certonly --webroot -w /var/www/certbot -d "${DOMAIN_NAME}" \
  --email you@example.com --agree-tos --no-eff-email

# 5. Reload nginx to pick up the real cert that just replaced the bootstrap one.
docker compose -f docker-compose.prod.yml exec nginx nginx -s reload
```

After this, the `certbot` service's renewal loop (already running from step 1) keeps the
real certificate current — no further manual cert steps needed unless the domain changes.

## Subsequent deploys

Once the prerequisites above are done, deploys are just: push to `master` (or push a
`v*` tag) → `deploy.yml` builds and pushes the image to GHCR → (if `DEPLOY_ENABLED`) SSHes
in and runs `docker compose pull api && docker compose up -d --no-deps api`. See
`.github/workflows/deploy.yml` for the exact steps.

Manually, from the VPS:
```bash
cd $DEPLOY_PATH
export IMAGE_TAG=master-<commit-sha>   # or a version tag
docker compose -f docker-compose.prod.yml pull api
docker compose -f docker-compose.prod.yml up -d --no-deps api
curl -sf https://${DOMAIN_NAME}/health
```

If anything looks wrong after a deploy: **`docs/ops/rollback.md`**.

## Backups

Nightly Postgres + Firestore backups, with a tested restore procedure:
**`docs/ops/backup-restore.md`**.

## What was actually verified (honest accounting, across this whole epic)

- ✅ `docker build` — the multi-stage `Dockerfile` builds successfully, runs as a non-root
  `app` user (checked via `docker run --entrypoint whoami`/`id`).
- ✅ `docker compose -f docker-compose.prod.yml build` — succeeds with **zero setup**, no
  `.env` needed (the exact command in this epic's "Done when" bar, run on a clean checkout).
  This required a fix mid-epic: the `api` service's Firebase-credentials volume mount used
  `${FIREBASE_CREDENTIALS_HOST_PATH}` with no default, which is invalid bind-mount syntax
  when unset and made `build`/`config` hard-fail without a `.env` present — given a default
  fallback (`:-/dev/null`) so building/validating the compose file never requires secrets
  that only matter at `up` time.
- ✅ `docker compose -f docker-compose.prod.yml config` — succeeds both with zero setup and
  with a populated `.env` (tested both ways).
- ✅ `docker compose -f docker-compose.prod.yml up` — all four services (api, postgres,
  nginx, certbot) start; **`nginx` correctly crash-loops without a real cert**, which is the
  documented, expected chicken-and-egg problem above, not a bug; **`api` boots successfully
  in `Production` mode**, which also proved the `.env.example` hierarchical-key fix
  (`Jwt__Key` etc.) actually works — using the old flat `JWT_KEY` name would have made the
  app throw `Jwt:Key must be configured...` at startup, and it didn't.
- ✅ Postgres backup + restore — real dump against real dev data, restored into a fresh
  throwaway container, row counts matched exactly. Details in `backup-restore.md`.
- ✅ Rollback's core mechanism — building two distinctly-tagged images and confirming
  `docker inspect` shows the exact right one running after each tag switch.
- ❌ **Not tested, because no real VPS/domain/GHCR-push-with-real-secrets exists**: the
  actual TLS bootstrap dance above (written from documented, standard certbot+nginx
  practice, not verified against a real Let's Encrypt issuance), the GitHub Actions SSH
  deploy step, and the Firestore backup script. All are flagged as such in their own docs
  rather than silently presented as done.
