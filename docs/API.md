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
| `rawState` | Raw byte of the power state characteristic, for clients that want to interpret it themselves |
| `busy` | A Bluetooth operation for this station is running |
| `error` | Last error (e.g. `"Not reachable"`), `null` after the next success |
| `updatedAt` | When `state` was last confirmed (read or successfully set) |

`state` is cached. It's read at service start, every `AppSettings:StateRefreshInterval` seconds (default 300, 0 = off),
after commands, and on `refresh`. A station woken by a command reports `booting` until the follow-up read about 10
seconds later.

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
| `PUT` | `/api/automatic` | Body `{"enabled": true\|false}`, returns the status |
| `GET` | `/api/stations` | Array of [stations](#station) |
| `POST` | `/api/stations/wake` | Wake all stations |
| `POST` | `/api/stations/sleep` | Put all stations to sleep |
| `POST` | `/api/stations/refresh` | Read the state of all stations |
| `POST` | `/api/stations/{address}/wake` | Wake one station |
| `POST` | `/api/stations/{address}/sleep` | Put one station to sleep |
| `POST` | `/api/stations/{address}/identify` | Flash the station's LED |
| `POST` | `/api/stations/{address}/refresh` | Read the state of one station |

`identify` makes the LED blink. Note that the base station firmware also **wakes a sleeping station** on identify, and
it stays awake until it gets a sleep command. The service re-reads the state about 5 seconds later, so clients see
the change.

`{address}` accepts `AA:BB:CC:DD:EE:01`, `AA-BB-CC-DD-EE-01` or `AABBCCDDEE01`. Only configured stations can be
used.

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
