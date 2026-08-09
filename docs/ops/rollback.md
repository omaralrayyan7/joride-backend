# Rollback (E8.3)

No automation — deliberately manual, since a bad deploy is exactly the moment you want a
human looking at each step rather than trusting another script. Real, testable steps;
verified below against the actual mechanism (`docker compose pull` + `up -d` against a
tagged image), not the SSH/VPS part specifically (no VPS exists yet — see
"What was actually verified").

## When to roll back

- The new container fails to start, or `docker compose -f docker-compose.prod.yml ps api`
  doesn't show it as `Up` a minute or two after deploy.
- `GET /health` returns something worse than the pre-deploy baseline (e.g. `postgres`
  check newly failing, not just the already-expected `traccar` one — see
  `docs/ops/traccar-runbook.md`).
- Anything else that makes the new version clearly worse than what was running before.

## Procedure

Every image `deploy.yml` builds is tagged and pushed to `ghcr.io/<owner>/<repo>` — both
`:latest` and either `:<git-tag>` (for a tagged release) or `:master-<commit-sha>` (for a
plain merge to `master`). Rolling back means simply telling Compose to run a **previous**
tag instead of pulling the new one:

```bash
# 1. Find the tag that was running before the bad deploy. deploy.yml's SSH step writes
#    the successfully-deployed tag to .last-deployed-tag on every successful deploy — the
#    PREVIOUS good tag is one line up in that file's history if you keep it in git, or
#    check `docker images ghcr.io/<owner>/<repo>` for what's still cached locally, or
#    check GitHub's Packages page for the repo for the full tag history.
cat .last-deployed-tag   # currently-recorded tag — the one you're rolling BACK FROM, not to

# 2. Point IMAGE_TAG at the known-good previous tag and redeploy.
export IMAGE_TAG=master-<previous-good-sha>
docker compose -f docker-compose.prod.yml pull api
docker compose -f docker-compose.prod.yml up -d --no-deps api

# 3. Confirm.
docker compose -f docker-compose.prod.yml ps api
curl -sf https://<domain>/health

# 4. Record it, so the next rollback (if any) has a correct baseline.
echo "$IMAGE_TAG" > .last-deployed-tag
```

No database migration rollback is included here on purpose: `PaymentsDbContext` migrations
(EF Core) are additive in this codebase's history so far (new tables/columns, never a drop),
so rolling back the API image does not require rolling back the schema too. **If a future
migration ever drops or renames a column the previous image's code still reads, this
procedure is not sufficient on its own** — that would need a matching down-migration
applied before rolling the image back, which isn't automated here and should be handled
case-by-case when it happens.

## What was actually verified

- ✅ **The core mechanism — redeploying a specific, older image tag via
  `docker compose pull` + `up -d --no-deps`** — was verified locally: built the image
  twice with two different `IMAGE_TAG` values against `docker-compose.prod.yml`, confirmed
  `docker images` shows both tags distinctly, confirmed `up -d --no-deps api` with
  `IMAGE_TAG` set to the older tag brings up a container running that specific image (checked
  via `docker inspect --format '{{.Config.Image}}'`).
- ❌ **The full flow — deploy.yml pushing to GHCR, a real VPS pulling from GHCR, `.last-deployed-tag`
  actually being written by a real SSH deploy** — was not tested, since no VPS/GHCR-push
  credentials exist yet (see `.github/workflows/deploy.yml`'s header comment). The rollback
  *mechanism* (swap the tag, redeploy) is proven; the specific script wiring around it
  (finding the previous tag from `.last-deployed-tag`, GHCR auth) is written but unexercised
  end-to-end.
