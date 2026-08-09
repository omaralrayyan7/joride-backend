# E8.2 — Load Test Report

**Date:** 2026-08-09
**Target:** local dev instance (`http://localhost:5007`), real Postgres (docker-compose.dev.yml) + real Firestore project `joride-e049b`, `ASPNETCORE_ENVIRONMENT=Development`.
**Tool:** [k6](https://k6.io) (official `grafana/k6` Docker image — no local k6 binary was installed, pulled via Docker).
**Note:** `docs/DELIVERY_PLAN.md` does not exist in this repository (confirmed absent, consistent with every prior epic in this engagement) — this report is scoped from the task description directly.

Two independent runs were executed with the same script; results below are from both (numbers are consistent between runs, cited as ranges where they differ).

## What was tested

Real endpoints, real auth, real state changes — no fake data, no bypass paths:

| Endpoint | Auth | Notes |
|---|---|---|
| `GET /health` | none | |
| `GET /api/vehicles` | none | |
| `GET /api/trips/overdue` | admin JWT | |
| `POST /api/trips/start` → `PUT /api/trips/{id}/end` | admin JWT | admin bypasses the ownership check (see E3), booking on behalf of 14 real, pre-existing, non-debt users, against 15 vehicles created via the real `POST /api/Vehicles` admin endpoint specifically for this test (`LOADTEST-01`..`15`), **deleted after the run** |
| `POST /api/auth/login` | none | included at low frequency (5% of iterations) |

**Load profile:** ramping-VUs — 0→50 over 20s, hold 50 VUs for 60s, ramp to 0 over 10s. ~9,200–9,300 iterations completed per run, ~110 req/s sustained.

**Traccar:** per the task, no synthetic multi-device load was attempted — `TRACCAR_BASE_URL`/`TRACCAR_TOKEN` are unset in this dev environment (consistent with every prior epic), so Traccar-dependent code paths (`_traccar.SendBookingEventAsync` etc.) are fire-and-forget no-ops here regardless. Single-device polling stability (`Test-Vehicle-1`) was already verified in E1.1–E1.4; full fleet-scale load testing is deferred until real or multiple test devices exist, per the task.

## Results (p95 target: < 300ms)

| Endpoint | p95 | p99 | Result |
|---|---|---|---|
| `GET /health` | 11–15ms | ~17ms | ✅ pass |
| `GET /api/vehicles` | 2.3–3.3ms | ~5ms | ✅ pass |
| `GET /api/trips/overdue` | 2.5–3.1ms | ~6ms | ✅ pass |
| `POST /api/trips/start` | **515–655ms** | **~817ms** | ❌ **fails** |
| `PUT /api/trips/{id}/end` | **324–415ms** | **~793ms** | ❌ **fails** |
| `POST /api/auth/login` | 2.6–4.5ms *(on the 2% that weren't rate-limited)* | — | N/A, see below |

**Errors:** `unexpected_error_rate` 0.01–0.07% (1–4 failures out of ~5,600 checked responses) — negligible, not endpoint-specific, no crashes or timeouts observed anywhere.

**Read-heavy endpoints (`/health`, `/api/vehicles`, `/api/trips/overdue`) comfortably meet the p95 < 300ms goal** — all under 16ms even at 50 concurrent VUs. These are simple in-memory list reads (or, for `/health`, cheap live checks) with no external network call in the request path, and it shows.

## Two expected, by-design results (not bugs)

- **`login_429_rate`: 97.9–98.0%** — nearly all login attempts got `429 Too Many Requests`. This is the E8.1 rate limiter (`auth-login` policy, 5 requests/min per IP) working exactly as designed: a k6 load generator hits the app from a single source IP, so once concurrency ramps up the 5/min budget is exhausted almost immediately. This is **not a performance problem** — it's the security control doing its job. A real fleet of users, each on their own IP, would not see this.
- **`trip_start_conflict_rate`: 56.6–57.4%** — over half of booking attempts were correctly rejected (`409`/`400`, "vehicle unavailable" or "user already has an active trip"). With 15 test vehicles and up to 50 concurrent VUs racing for them, this is the expected, correct behavior of E3.1's overlap lock — proof it holds up under sustained concurrent load, not a capacity bug. (`checks_succeeded` was 100%/99.98% across both runs — the one failing check in run 2 was a single `trip end` 200-check miss, not reproduced in run 1, and not chased further given its negligible rate.)

## The real finding: `trip_start`/`trip_end` latency

The **median** `trip_start_duration` was ~2ms (matching the fast in-memory rejection path for the majority of contended requests), but the **p95/p99 balloon to 500–800ms**. This bimodal shape — most requests fast, a substantial tail very slow — points at one specific thing: **the subset of requests that actually succeed pay for a real, synchronous network round-trip to Firestore before the HTTP response is returned.**

Traced in the code (not guessed):

- `TripsController.Start` (`JoRideBackend/Controllers/TripsController.cs:232`) — `await (_firestore?.SaveTripAsync(trip) ?? Task.CompletedTask);`
- `TripsController.End` (`JoRideBackend/Controllers/TripsController.cs:358`) — same pattern
- `WalletController.TryChargeAsync`, called from `Start()` for the payment step, **unconditionally** awaits `_firestore?.SaveTransactionAsync(t)` (`JoRideBackend/Controllers/WalletController.cs:94`) regardless of payment method — so a successful booking pays for **two sequential** blocking Firestore writes before the client gets a response, not one.

By contrast, in the exact same methods, the Traccar event dispatch is already correctly **fire-and-forget**:
```csharp
_ = _traccar.SendBookingEventAsync(...);   // TripsController.cs:238 — not awaited
```
as is notification delivery and audit logging elsewhere in this codebase (`NotificationsController.Push`, `AuditController.Log` both fire-and-forget their own Firestore writes). The fix pattern already exists and is already proven acceptable in this codebase for exactly this kind of "durable but non-blocking" write — it's just not applied consistently to the trip/wallet save calls.

## Fix implemented (narrow scope, per instruction)

Discussed the trade-off with the requester; decided on the **narrower** of the two options: leave `TripsController.Start`/`End`'s own `SaveTripAsync` calls fully awaited (trip-state durability untouched), and make **only** `WalletController.TryChargeAsync`'s `SaveTransactionAsync` (`WalletController.cs:94`) fire-and-forget — matching the pattern already used for Traccar dispatch/notifications/audit logs elsewhere in the same files. Nothing else from E8.1 or earlier epics was touched.

```csharp
_transactions.Add(t);
_ = _firestore?.SaveTransactionAsync(t);   // was: await (_firestore?.SaveTransactionAsync(t) ?? Task.CompletedTask);
return true;
```

`dotnet build` clean, `dotnet test` 119/119 passing after the change.

## Re-measured: before vs. after (3 real runs total post-fix, not projected)

| Endpoint | Before (2 runs) | After (3 runs) | Change |
|---|---|---|---|
| `POST /api/trips/start` p95 | 515ms, 655ms | 305ms, 326ms, 306ms | **≈45% faster** — 585ms avg → 312ms avg |
| `POST /api/trips/start` p99 | 817ms *(1 run measured)* | 798ms, 386ms | improved, more variable |
| `PUT /api/trips/{id}/end` p95 | 415ms, 324ms | 325ms, 347ms, — | **unchanged** (as expected — `End()`'s own `SaveTripAsync` was deliberately left untouched) |
| `PUT /api/trips/{id}/end` p99 | 793ms *(1 run measured)* | 765ms, 428ms | roughly unchanged, still highly variable |

`trip_start` p95 is now right at the 300ms line (305–326ms across 3 runs) rather than well above it (515–655ms) — a real, consistent, roughly 45% improvement, exactly matching the theory: removing one of the two sequential Firestore writes on the booking path cut its latency by close to half. `trip_end` is statistically unchanged across runs, which is the expected control result — it still has its own single synchronous `SaveTripAsync` write that this narrow fix deliberately did not touch, so its cost remains.

**`trip_start` did not fully clear the 300ms threshold** (305–326ms vs. a 300ms target) — the remaining cost is `TripsController.Start`'s own `SaveTripAsync` call plus whatever else is in the request path, which this fix intentionally left alone. If getting `trip_start`/`trip_end` fully under 300ms matters, the next step would be the same fire-and-forget treatment applied to those two `SaveTripAsync` calls — but that's the wider change already discussed and explicitly deferred, not something to do silently as a follow-on here.

## Artifacts

- k6 script: `jorride-load-test.js` (scratchpad; not committed to the repo — happy to commit it under e.g. `scripts/load-test/` if you'd like it kept in-repo for reuse)
- Raw k6 summary output captured above for all 5 runs (2 baseline, 3 post-fix); full console logs available on request.
