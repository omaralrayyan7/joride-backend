# Traccar event forwarding → `/api/traccar/events`

Traccar can push events (ignition on/off, motion, online/offline, geofence,
etc.) to an external URL as they happen, instead of us having to poll for
them. This backend exposes `POST /api/traccar/events` to receive those pushes.

## Configuring Traccar

Traccar's event forwarder is configured with `event.forward.url`, either in
`traccar.xml` or via **Settings → Server → Attributes** in the web UI:

```xml
<entry key='event.forward.url'>http://<backend-host>:<port>/api/traccar/events</entry>
```

Restart Traccar (or the server attribute takes effect without restart,
depending on version) and it will `POST` a JSON body shaped like this for
every event:

```json
{
  "event": {
    "id": 500,
    "type": "ignitionOff",
    "eventTime": "2026-08-08T14:35:00.000+00:00",
    "deviceId": 1,
    "positionId": 12346,
    "geofenceId": 0,
    "maintenanceId": 0,
    "attributes": {}
  },
  "position": { "...": "the device's position at event time, if any" },
  "device": { "...": "the device record, if any" }
}
```

## Authentication: why this isn't real per-request HMAC from Traccar

`/api/traccar/events` requires a signed request and returns `401` if the
signature is missing or wrong. The *primary* mechanism it checks for is an
`X-Traccar-Signature` header carrying the HMAC-SHA256 hex digest of the raw
request body, keyed with `TRACCAR_WEBHOOK_SECRET`. This is the correct way to
authenticate a webhook, and it's what our own tests (and any custom relay we
might put in front of Traccar later) use.

**Stock Traccar cannot produce that header.** Its forwarder only supports
`event.forward.header`, a single **static** string set once in config —
Traccar has no way to compute a signature over a body it's about to send,
because the header value is fixed at config time, not computed per request:

```xml
<entry key='event.forward.header'>X-Traccar-Signature: your-shared-secret</entry>
```

So a real Traccar instance can only ever send the same fixed string on every
request — never an HMAC of that request's body. Because of that, the endpoint
also accepts two alternatives that stock Traccar genuinely can do, both
checked as an exact (constant-time) match against `TRACCAR_WEBHOOK_SECRET`:

1. **`X-Traccar-Signature` header set to the raw secret** (not a hash) via
   `event.forward.header` as shown above. This is the recommended option if
   your Traccar version supports `event.forward.header` (most recent
   versions do).
2. **A `secret` query parameter on the forwarding URL itself** — this works
   on every Traccar version because it doesn't depend on header support at
   all, since `event.forward.url` is just a URL and Traccar POSTs to exactly
   what's configured:

   ```xml
   <entry key='event.forward.url'>http://<backend-host>:<port>/api/traccar/events?secret=your-shared-secret</entry>
   ```

   This is the option we recommend if you're not sure whether your Traccar
   version honors `event.forward.header`.

Either way, treat `TRACCAR_WEBHOOK_SECRET` as a real secret — it's a bearer
credential to this endpoint, not a checksum.

## What the endpoint does

- Verifies the signature/secret against the raw body bytes **before**
  parsing anything; on failure it returns `401` and never touches the body.
- Parses the JSON into `TraccarEventWebhookPayload` (`JoRideBackend/Services/TraccarModels.cs`).
- Logs a clear line per event type it recognizes (`deviceOnline`,
  `deviceOffline`, `deviceMoving`, `deviceStopped`, `ignitionOn`,
  `ignitionOff`); unrecognized types are logged too, just less specifically.
- Log-only for now — no persistence. Writing events to storage is E1.4.
