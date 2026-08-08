# JoRide — Device Install & Traccar Onboarding Runbook

For: hardware install team (Ahmad). Purpose: repeatable checklist for pairing a
physical FMC130 unit to a JoRide vehicle so the backend controls it exactly
like it controls the local test device today.

---

## 1. Wiring profile (per FMC130 unit)

Reference: QMFMC130 datasheet, JoRide_Hardware summary, wiring scheme.

| Pin/Output | Connects to | Function |
|---|---|---|
| VCC (pin 1) / GND (pin 7) | Vehicle battery, 10–30V DC | Power |
| DIN1 | Ignition line | Ignition detection (safety gate input) |
| DOUT1 | Automotive Relay (starter/fuel-pump cutoff) | Immobilize/Mobilize |
| DOUT2 | CAN-CONTROL trigger | Lock/Unlock |
| INPUT5/6 (LV-CAN RX/TX) | CAN-CONTROL module | Decoded vehicle data (speed, doors, fuel, RPM) |

**Do not wire DOUT directly to starter/fuel pump current** — always through the
Automotive Relay (DOUT max 0.5A open-collector).

## 2. Teltonika Configurator checklist (per unit, before install)

1. Connect via USB or Bluetooth (default BT PIN **5555**).
2. **Change the Bluetooth PIN** to a unit-specific value — do not leave default (STRIDE finding V9).
3. Set APN for the SIM carrier.
4. Set server: `Server IP` = your Traccar host, `Port` = **5027** (Teltonika/Codec8E port), protocol = Codec 8 Extended.
5. Set reporting interval (recommend 10–30s moving, longer stationary — balance data cost vs freshness).
6. Configure DOUT1/DOUT2 per the wiring profile above.
7. Set DIN1 as ignition input.
8. Save config, disconnect, power-cycle the unit, confirm it connects (see §3).

## 3. Pairing a new device with Traccar

1. In Traccar (production instance, not the local dev one) → **Devices → +**.
2. **Name**: matches the vehicle (e.g. plate number).
3. **Identifier**: the unit's real IMEI (found on the FMC130 label or via Configurator).
4. Save. Within one reporting interval, status should show **Online**.

## 4. Pairing the Traccar device to a JoRide vehicle record

Use the existing admin vehicle update flow to set the vehicle's mapping field
(license plate ↔ Traccar `uniqueId`) — same mechanism used for the local test
device (`Test-Vehicle-1` → Vehicle #1) during E1.1–E1.4 verification. No new
endpoint needed; this is already wired.

## 5. Post-install verification (must pass before vehicle goes live)

- [ ] Position updates appear in Traccar within one reporting interval.
- [ ] `telemetry_snapshots` rows accumulate in Postgres for the real IMEI (per E1.4).
- [ ] Vehicle's live lat/lng updates in Firestore (per E1.4).
- [ ] Unlock command (admin, `/api/vehicles/{id}/commands/unlock`) actuates the real lock.
- [ ] Immobilize is **SafetyBlocked** while the vehicle is moving (drive it, attempt immobilize, confirm 409).
- [ ] Immobilize succeeds once stationary; Mobilize restores.
- [ ] Every attempt above appears in the command audit table.
- [ ] Bluetooth PIN changed from default (§2 step 2).

## 6. Known gap — swapping local dev device for real hardware

Nothing in application code needs to change. The backend talks to Traccar by
device ID/uniqueId, not by protocol — the local `Test-Vehicle-1` (OsmAnd
protocol, port 5055) and a real FMC130 (Codec8E, port 5027) are both just
"a device in Traccar" to our code. When the first real unit is installed:
retire (or keep as a permanent staging fixture) `Test-Vehicle-1`, and switch
which Traccar device is mapped to a given vehicle per §4.

One real gap remains for later hardware-in-the-loop work (E8.4): command
**confirmation** currently trusts Traccar's immediate API response (E1.3 TODO).
With real hardware, confirmation should instead watch for the expected
telemetry change (e.g. actual speed/ignition state after an Immobilize) before
marking a command Confirmed. This is intentionally deferred until real
hardware exists to test against.

## 7. Production network exposure — Traccar must NOT be public (E8.1)

In dev, `TRACCAR_BASE_URL=http://localhost:8083` (see `.env.example`) points at
a Traccar instance running unauthenticated-by-network on localhost — fine on a
developer machine, **not fine in production**. Before any real deployment:

- **The Traccar web UI/API port (8082 web UI / 8083 in this project's dev
  config, and the device-ingest ports 5027/5055/etc.) must sit on a private
  network — a VPC subnet, an internal-only security group, or behind a VPN —
  never bound to a public IP or exposed through a public load balancer/ingress.**
  Traccar has its own login, but that login is not a substitute for network
  isolation: it's an additional layer, not the perimeter. A public Traccar
  instance is a direct path to live vehicle position data and (if reachable)
  device command dispatch, entirely outside this backend's own auth/rate
  limiting/audit trail.
- This backend (`JoRideBackend`) is the only thing that should be able to
  reach Traccar's REST API (`TRACCAR_BASE_URL`) — from inside the same private
  network/VPC, not over the public internet. `TRACCAR_TOKEN` should be scoped
  to what the REST calls this app actually makes (device/position reads,
  command dispatch), not a full-admin Traccar account, per least privilege.
- The **inbound** direction — Traccar calling back into this app
  (`POST /api/payments/webhooks/hyperpay` is a *different* webhook; the
  Traccar-side one is `POST /api/traccar/events`, see
  `traccar-event-forwarding.md`) — is the only Traccar-related surface that
  legitimately needs to be internet-reachable, and it already has its own
  HMAC-signature verification (`TRACCAR_WEBHOOK_SECRET`) independent of
  network placement.
- Device ingest ports (5027 Codec8E, 5055 OsmAnd, etc.) need to be reachable
  by the physical FMC130 units in the field over their cellular APN — that's
  a narrower, deliberate exception to "private network only," and should be
  restricted at the firewall to the ingest ports specifically, not used as
  justification for exposing the web UI/REST API port too.

Verification checklist for a production cutover:
- [ ] `curl`/`nmap` the deployment's public IP(s) and confirm the Traccar web
      UI/API port does **not** respond from outside the private network/VPN.
- [ ] Confirm `TRACCAR_BASE_URL` used by this backend resolves to a private
      (internal) address, not a public DNS name/IP.
- [ ] Confirm `TRACCAR_TOKEN` is a scoped credential, not a Traccar admin
      account's own login token.
