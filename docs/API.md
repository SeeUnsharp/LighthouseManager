# LighthouseManagerService API

LighthouseManagerService is the only process that talks to the base stations over Bluetooth. Every UI and
integration (the bundled tray app, scripts, desktop widgets, home automation, …) uses this local HTTP API instead.
That way all clients see the same state and never fight over Bluetooth connections.

- **Base URL:** `http://127.0.0.1:47800` (setting `AppSettings:ApiUrl`, empty disables the API)
- **Format:** JSON, camelCase property names, enum values as camelCase strings
- **Authentication:** none. Keep the API on loopback.

## Security

The API is only meant for clients on the same machine:

- Requests whose `Host` header isn't a loopback name (`localhost`, `127.0.0.1`, `[::1]`) get `403`. This protects
  against DNS rebinding.
- Requests with an `Origin` header (that is, sent by a web page in a browser) get `403` unless the origin is listed
  in `AppSettings:AllowedOrigins` (comma separated, e.g. `http://localhost:5173`). This stops websites from
  controlling your base stations. Allowed origins also get CORS headers, so browser-based UIs work.
- Native clients (curl, apps, scripts) don't send `Origin` and aren't affected.

## Data model

### Station

```json
{
  "address": "AA:BB:CC:DD:EE:01",
  "name": "LHB-1A2B3C4D",
  "state": "awake",
  "rawState": 11,
  "busy": false,
  "error": null,
  "updatedAt": "2026-10-15T10:01:29.512+00:00"
}
```

| Field | Description |
|---|---|
| `address` | MAC address, also the station's id in URLs |
| `name` | Bluetooth name, `null` until the station was read once |
| `state` | `unknown`, `sleeping`, `standby`, `booting` (spinning up after a wake), `awake` |
| `rawState` | Raw byte of the power state characteristic, for clients that want to interpret it themselves; `null` after a command until the next read. Seen: `0x00` sleeping, `0x02` standby, `0x01`/`0x08` booting, `0x09`/`0x0B` awake |
| `busy` | A Bluetooth operation for this station is running |
| `error` | Last error (e.g. `"Not reachable"`), `null` after the next success |
| `updatedAt` | When `state` was last confirmed (read or successfully set) |

`state` is cached. It's read at service start, every `AppSettings:StateRefreshInterval` seconds (default 300, 0 = off),
after commands, and on `refresh`. A station woken by a command reports `booting` until a read shows it awake: after a wake it's read
again after about 10 seconds and then every 15 seconds while it reports `booting`. A station that is still `booting`
after 2 minutes gets `error` "Doesn't finish starting up…" (observed: raw `0x01` for hours; unplugging the station
for a few seconds fixed it).

### Status

```json
{
  "automaticMode": true,
  "triggerActive": true,
  "triggerProcess": "vrserver",
  "triggerProcesses": ["vrserver"],
  "stations": [ /* Station objects */ ],
  "settingsFile": "/home/user/.config/lighthousemanager/appsettings.json",
  "version": "2.0.0"
}
```

| Field | Description |
|---|---|
| `automaticMode` | Whether trigger processes wake/sleep the stations |
| `triggerActive` | A trigger process is running |
| `triggerProcess` | Name of the running trigger process, or `null` |
| `triggerProcesses` | Configured trigger process names (`AppSettings:TriggerProcesses`) |
| `stations` | All configured stations, in configuration order (empty until base stations are configured) |
| `settingsFile` | The user settings file to add base stations to (restart the service afterwards) |
| `version` | Version of the service |

## Endpoints

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/status` | Current [status](#status) |
| `GET` | `/api/events` | Live status as Server-Sent Events, see [Events](#events) |
| `PUT` | `/api/automatic` | Body `{"enabled": true\|false}`, returns the status; saved to the user settings file |
| `GET` | `/api/triggers` | Trigger processes: `{"processes": ["vrserver"]}` |
| `PUT` | `/api/triggers` | Body `{"processes": ["vrserver", "monado-service"]}`: replace the trigger processes (trimmed, without `.exe`, at least one; otherwise `400`), returns the status; takes effect immediately and is saved |
| `GET` | `/api/processes` | Names of the running applications, `{"processes": [...]}`, to offer as trigger processes |
| `GET` | `/api/stations` | Array of [stations](#station) |
| `POST` | `/api/stations/wake` | Wake all stations |
| `POST` | `/api/stations/sleep` | Put all stations to sleep |
| `POST` | `/api/stations/refresh` | Read the state of all stations |
| `POST` | `/api/stations/{address}/wake` | Wake one station |
| `POST` | `/api/stations/{address}/sleep` | Put one station to sleep |
| `POST` | `/api/stations/{address}/identify` | Flash the station's LED |
| `POST` | `/api/stations/{address}/refresh` | Read the state of one station |
| `POST` | `/api/discovery?seconds=10` | Search for base stations nearby (3–60 s, default 10), see [Discovery](#discovery) |
| `POST` | `/api/stations` | Body `{"addresses": ["AA:BB:CC:DD:EE:01", …]}`: add base stations, returns the status |
| `DELETE` | `/api/stations/{address}` | Remove a base station, returns the status (`404` if not configured) |
| `GET` | `/api/mqtt` | [Home Assistant / MQTT](#home-assistant-mqtt) settings and connection status (never the password) |
| `PUT` | `/api/mqtt` | Change them, see [Home Assistant / MQTT](#home-assistant-mqtt); `400` with `{"error": "…"}` if invalid |

`identify` makes the LED blink. Note that the base station firmware also **wakes a sleeping station** on identify, and
it stays awake until it gets a sleep command. The service re-reads the state about 5 seconds later, so clients see
the change.

`{address}` accepts `AA:BB:CC:DD:EE:01`, `AA-BB-CC-DD-EE-01` or `AABBCCDDEE01`. Only configured stations can be
used.

### Discovery

`POST /api/discovery` scans for the given time and returns every Lighthouse base station found (Bluetooth name
starting with `LHB-`). It doesn't change anything; add stations with `POST /api/stations`.

```json
{
  "stations": [
    { "address": "AA:BB:CC:DD:EE:01", "name": "LHB-1A2B3C4D", "configured": true },
    { "address": "AA:BB:CC:DD:EE:02", "name": "LHB-5E6F7A8B", "configured": false }
  ]
}
```

Added and removed stations take effect immediately and are saved to the user settings file (`settingsFile` in the
status), so they survive a restart.

### Command responses

`POST` commands wait until the operation finished (usually 1–3 seconds; up to about a minute when a station is out
of reach and retries run) and return:

```json
{ "success": true, "status": { /* Status */ } }
```

| Status code | Meaning |
|---|---|
| `200` | Done |
| `502` | A base station couldn't be reached or didn't accept the command; the body still contains the status |
| `400` | Invalid address format |
| `404` | Address isn't a configured station |
| `403` | Rejected by the [security checks](#security) |

Bluetooth operations run one at a time; commands wait for their turn.

### Manual commands and automatic mode

A manual `wake` or `sleep` always wins. It cancels a running automatic wake/sleep, and the trigger watcher won't
undo it: automatic handling resumes the next time a trigger process starts or the last one exits. The same applies
after turning automatic mode back on. Nothing happens until the trigger state changes. `identify` and `refresh` don't
affect automatic handling.

## Home Assistant (MQTT)

The service can connect to an MQTT broker (e.g. Home Assistant's Mosquitto add-on) and announces this computer and
its base stations through [MQTT discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery). It
connects outward, so the HTTP API stays on loopback. Off by default.

`GET /api/mqtt`:

```json
{
  "enabled": true, "host": "homeassistant.local", "port": 1883, "username": "lighthouse", "hasPassword": true,
  "useTls": false, "discoveryPrefix": "homeassistant", "baseTopic": "lighthousemanager", "nodeId": "",
  "defaultNodeId": "gamingpc", "connected": true, "error": null
}
```

`PUT /api/mqtt` takes the same fields (without `hasPassword`, `defaultNodeId`, `connected`, `error`) plus
`password`: `null` keeps the stored password, `""` removes it. `discoveryPrefix`, `baseTopic` and `nodeId` may be
`null` to keep them. The settings are saved to `mqtt.json` next to the user settings file (owner-only permissions on
Linux) and take effect immediately (reconnect). An empty `nodeId` uses the computer name (`defaultNodeId`).

Topics (`{base}` = `{baseTopic}/{nodeId}`, `{mac}` = address as 12 lowercase hex digits). All states are retained:

| Topic | Payload |
|---|---|
| `{base}/availability` | `online` / `offline` (also the last will) |
| `{base}/automatic` | `ON` / `OFF` |
| `{base}/trigger` | `ON` / `OFF`; `{base}/trigger/attributes`: `{"process": "vrserver"}` |
| `{base}/station/{mac}/power` | `ON` (awake or booting) / `OFF` (sleeping or standby) / `None` (unknown) |
| `{base}/station/{mac}/state` | `unknown`, `sleeping`, `standby`, `booting`, `awake`; attributes in `…/attributes` |

Commands (same effect as the tray: manual commands win over automatic mode). Publish them without the retain flag:
retained commands are ignored, otherwise they would run again on every connect.

| Topic | Payload |
|---|---|
| `{base}/cmd/automatic` | `ON` / `OFF` |
| `{base}/cmd/wake_all`, `{base}/cmd/sleep_all`, `{base}/cmd/refresh` | anything |
| `{base}/cmd/station/{mac}/power` | `ON` / `OFF` |
| `{base}/cmd/station/{mac}/identify` | anything |

Home Assistant gets one device for the computer (automatic mode, *VR app running*, *Turn all on*, *Sleep all*,
*Refresh*) and one per base station (*Power*, *State*, *Identify*). When the integration is turned off or the topics
change, the service removes its entities and retained topics again.

## Events

`GET /api/events` is a [Server-Sent Events](https://html.spec.whatwg.org/multipage/server-sent-events.html) stream.
It sends the current status immediately and then a new one after every change (state, busy, error, trigger,
automatic mode):

```
event: status
data: {"automaticMode":true,"triggerActive":false,"triggerProcess":null,...}
```

Each event carries the full status, so clients don't need to merge partial updates. A client that falls behind only
gets the latest status. If the connection drops (for example on a service restart), reconnect and you'll receive the
current status again.

## Examples

```sh
curl http://127.0.0.1:47800/api/status
curl -X POST http://127.0.0.1:47800/api/stations/wake
curl -X POST http://127.0.0.1:47800/api/stations/AA:BB:CC:DD:EE:01/identify
curl -X PUT -H 'Content-Type: application/json' -d '{"enabled":false}' http://127.0.0.1:47800/api/automatic
curl -N http://127.0.0.1:47800/api/events
```
