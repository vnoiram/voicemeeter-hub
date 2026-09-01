#!/usr/bin/env bash
# Build both target frameworks and run the platform-neutral test suite inside a Linux .NET SDK
# container. The net8.0-windows exe cannot RUN on Linux, but it compiles here (EnableWindowsTargeting),
# and the tests exercise the protocol, dispatch, and WebSocket server via a faked Remote API client.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$script_dir/.." && pwd)"
image="${VOICEMEETER_HUB_BUILD_IMAGE:-voicemeeter-hub-build:local}"

docker build -f "$root/Dockerfile.build.linux" -t "$image" "$root"
docker run --rm -v "$root":/work -w /work "$image" bash -c '
  set -euo pipefail
  dotnet build src/VoicemeeterHub/VoicemeeterHub.csproj -p:EnableWindowsTargeting=true -c Release
  dotnet test tests/VoicemeeterHub.Tests/VoicemeeterHub.Tests.csproj -p:EnableWindowsTargeting=true -c Release
'
