# Voicemeeter Hub WebSocket Protocol (v1)

The hub is a single loopback WebSocket server that owns the one Voicemeeter Remote API login
session (`VBVMR_Login`/`VBVMR_Logout`) and the one state poller for the whole machine. Every
application (Stream Dock plugins, scripts, other tools) connects as a client instead of loading
`VoicemeeterRemote64.dll` itself, which removes the login-session contention that appears when
multiple processes talk to Voicemeeter directly.

## Endpoint and discovery

- URL: `ws://127.0.0.1:<port>/` (loopback only).
- Default port: `50505`. Override with the `VOICEMEETER_HUB_PORT` environment variable or
  `--port <n>` on the server command line.
- While running, the server writes its endpoint to
  `%LOCALAPPDATA%\voicemeeter-hub\endpoint.json`:

  ```json
  { "port": 50505, "pid": 1234, "protocol": 1, "server": "voicemeeter-hub", "startedUtc": "..." }
  ```

  Clients should read this file first and fall back to the default port. The file is deleted on
  clean shutdown.

## Framing

One JSON object per WebSocket text frame. UTF-8. Frames larger than 1 MiB are rejected.

## Client → server

```json
{ "id": "<correlation-id>", "op": "<Operation>", "args": { ... } }
```

- `op` is required. `id` is optional but recommended; it is echoed back on the matching response.
- Control operations: `Subscribe` / `Unsubscribe` (state push, see below) and `Ping` (returns
  `"pong"`).

## Server → client

Every frame carries a `type` discriminator. Null fields are omitted.

```json
{ "type": "hello",    "server": "voicemeeter-hub", "protocol": 1, "version": "0.1.0" }
{ "type": "response", "id": "<id>", "result": { ... } }
{ "type": "response", "id": "<id>", "error": "message" }
{ "type": "event",    "topic": "state", "data": { ...snapshot... } }
```

- `hello` is sent once immediately after connect.
- `response` correlates to a request `id`. Exactly one of `result` / `error` is present.
- `event` is an unsolicited push. The only v1 topic is `state`.

## State subscription

Send `{ "op": "Subscribe" }`. The server replies with a `response`, then immediately pushes the
current `state` event, and thereafter pushes a new `state` event whenever Voicemeeter parameters
change (detected server-side via `VBVMR_IsParametersDirty`, polled ~1s while any client is
subscribed). `{ "op": "Unsubscribe" }` stops the push.

The `state` `data` is a snapshot:

```json
{
  "updatedAt": "2026-09-01T12:00:00+09:00",
  "edition": "Banana",
  "currentStates": {
    "strip:0": { "channelKey": "strip:0", "shortLabel": "HW In 1", "gainDb": -6.0, "muted": false, "error": null },
    "bus:0":   { "channelKey": "bus:0",   "shortLabel": "Out A1",  "gainDb": 0.0,  "muted": true,  "error": null }
  },
  "error": null
}
```

`currentStates` covers `strip:0..7` and `bus:0..7` (the maximum across editions); indices beyond
the installed edition report a per-channel `error`.

## Operations

Same surface as the `IVoicemeeterClient` interface. Arguments are named.

| Operation | Args | Result |
|---|---|---|
| `EnsureConnected` | – | `OperationResult` |
| `SuppressReconnect` | – | `OperationResult` |
| `GetEdition` | – | edition string |
| `GetVersion` | – | version string / null |
| `GetChannelState` | `channelKind`, `index` | `{ gainDb, muted }` |
| `SetGain` | `channelKind`, `index`, `gainDb` | `OperationResult` |
| `SetMute` | `channelKind`, `index`, `muted` | `OperationResult` |
| `GetSolo` | `stripIndex` | bool |
| `SetSolo` | `stripIndex`, `solo` | `OperationResult` |
| `GetMono` | `channelKind`, `index` | bool |
| `SetMono` | `channelKind`, `index`, `mono` | `OperationResult` |
| `SetDevice` | `channelKind`, `index`, `driver`, `deviceName` | `OperationResult` |
| `GetDevice` | `channelKind`, `index`, `driver` | string / null |
| `GetInputDevices` / `GetOutputDevices` | – | device list |
| `PressMacroButton` | `index`, `pressed` | `OperationResult` |
| `GetMacroButtonState` | `index` | bool |
| `IsParametersDirty` / `IsMacroButtonDirty` | – | bool |
| `SetRecorder` | `recording` | `OperationResult` |
| `GetRecorderState` | – | bool |
| `SetEq` | `busIndex`, `on` | `OperationResult` |
| `GetEqState` | `busIndex` | bool |
| `TriggerCommand` | `commandName` | `OperationResult` |
| `BuildDiagnostics` | – | diagnostics object |

`channelKind` is `"strip"` or `"bus"`. `driver` is one of `mme`, `wdm`, `ks`, `asio`.

`OperationResult`:

```json
{ "success": true, "statusCode": 0, "paramName": "Strip[0].Mute", "errorSummary": null }
```

## Lifecycle

- One server instance per machine, guarded by a global mutex; a second launch exits immediately.
- The server exits after 60s with no connected clients, releasing the Remote API session. Clients
  restart it on demand (see the client's launch logic).
- On a Remote API `-2` (disconnect after sleep/reboot/Voicemeeter restart) the server logs out the
  stale session, logs in again, and retries the call once before surfacing an error.
