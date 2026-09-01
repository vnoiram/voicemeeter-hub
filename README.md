# voicemeeter-hub

日本語版: [README.ja.md](README.ja.md)

A single local **WebSocket service** that owns the one VB-Audio Voicemeeter Remote API session for
the whole machine, so multiple applications can read and control Voicemeeter without fighting over
`VoicemeeterRemote64.dll`.

## Why

The Voicemeeter Remote API is effectively a single-login-session resource. When several processes
each load `VoicemeeterRemote64.dll` and call `VBVMR_Login`/`VBVMR_Logout`, they contend for that
session and each runs its own state-polling loop. `voicemeeter-hub` centralizes this:

- **One** process loads the DLL and holds **one** login session.
- **One** server-side poller detects parameter changes and **pushes** state to every subscriber, so
  clients stop polling independently.
- Clients speak a small, language-agnostic **WebSocket + JSON** protocol over loopback instead of
  linking the native DLL.

The Stream Dock Voicemeeter plugin is the first client; scripts, other plugins, or any tool that
speaks WebSocket can connect too.

## Protocol

See [`docs/protocol.md`](docs/protocol.md). In short:

- `ws://127.0.0.1:50505/` (default port; override with `VOICEMEETER_HUB_PORT` or `--port`).
- Request/response: `{ "id", "op", "args" }` → `{ "type": "response", "id", "result" | "error" }`.
- State push: send `{ "op": "Subscribe" }`, then receive `{ "type": "event", "topic": "state", "data": { ...snapshot... } }`.
- Discovery: the running server writes `%LOCALAPPDATA%\voicemeeter-hub\endpoint.json` with the live port.

## Layout

- `src/VoicemeeterHub/` — the server executable.
  - `VoicemeeterClient.cs` — the P/Invoke wrapper and login-session management (the only code that
    touches the native DLL).
  - `HubServer.cs` / `HubConnection.cs` / `WebSocketHandshake.cs` — the loopback WebSocket server.
  - `HubStateService.cs` — the single server-side state poller and broadcaster.
  - `VoicemeeterOperations.cs` / `HubProtocol.cs` — operation dispatch and the wire contract.
- `tests/VoicemeeterHub.Tests/` — protocol, dispatch, handshake, and an end-to-end WebSocket test
  driven with a faked Remote API client (runs on any OS).
- `docs/protocol.md` — the protocol specification.

## Runtime targets

The exe targets `net8.0-windows` (it is a Windows daemon). The project also multi-targets plain
`net8.0` purely so the platform-neutral parts and tests build and run on any OS; the DLL and
registry paths are Windows-only at runtime and are faked in tests.

## Build and test

`.NET` builds run in Docker in this workspace, not on the host.

```bash
# Compile both targets and run the test suite in a Linux .NET SDK container.
bash scripts/test-in-linux-docker.sh
```

Publish a self-contained Windows executable locally:

```powershell
pwsh scripts/publish.ps1
# -> dist/hub/VoicemeeterHub.exe
```

Build the per-user installer locally (needs Inno Setup / `iscc` on PATH):

```powershell
pwsh scripts/build-installer.ps1 -Version 0.1.0
# -> installer/Output/voicemeeter-hub-0.1.0-setup.exe
```

## Installing

The recommended install is the per-user installer (`voicemeeter-hub-<ver>-setup.exe` from a
release, or built locally). It:

- installs into `%LOCALAPPDATA%\voicemeeter-hub\` — **no administrator rights required**,
- needs **no environment variable**: that path is exactly where the Stream Dock plugin already
  looks, so the plugin auto-discovers and launches the hub,
- supports silent install (`voicemeeter-hub-<ver>-setup.exe /VERYSILENT`) and offers an optional
  "start at sign-in" task (off by default, since clients start the hub on demand).

Other applications can find the running hub through `%LOCALAPPDATA%\voicemeeter-hub\endpoint.json`,
or set `VOICEMEETER_HUB_EXE` to the installed path to have them launch it too.

## CI and releases

- `.github/workflows/ci.yml` runs the test suite and builds the `net8.0-windows` executable on
  every push to `main` and every pull request (Ubuntu runner, `EnableWindowsTargeting`).
- `.github/workflows/release.yml` runs on a `v*` tag in three jobs: (1) test and cross-publish the
  self-contained single-file `win-x64` `VoicemeeterHub.exe` and a zip on Ubuntu, (2) build the
  per-user Inno Setup installer on Windows from that payload, (3) attach both the zip and the
  installer to a generated GitHub Release. The tag (minus the leading `v`) becomes the assembly and
  installer version.

```bash
git tag v0.1.0
git push origin v0.1.0   # -> builds and publishes the release
```

## Running

Launch `VoicemeeterHub.exe`. It:

- refuses to start a second instance (a global mutex; the first instance keeps serving),
- listens on loopback and writes the endpoint file,
- exits automatically after 60 seconds with no connected clients, releasing the Remote API session.

Clients are expected to auto-start it on demand and connect.
