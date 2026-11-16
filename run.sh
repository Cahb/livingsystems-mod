#!/usr/bin/env bash
# Run a command inside the .NET 8 SDK container. Nothing is installed on the host.
#   ./run.sh dotnet test
#
# Runs as root inside the container: .NET's first-run named mutex fails for
# non-root users on this Docker/kernel. Ownership of everything under the
# project is handed back to the host user afterwards.
set -uo pipefail
cd "$(dirname "$0")"
REFS="${EMPYRION_REFS:?set EMPYRION_REFS to the game ..._Data/Managed folder (ModApi.dll, Mif.dll, UnityEngine.CoreModule.dll)}"
mkdir -p .cache/nuget .cache/home
TTY=(); [ -t 1 ] && TTY=(-t)

docker run --rm "${TTY[@]}" \
  -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
  -e HOME=/src/.cache/home -e NUGET_PACKAGES=/src/.cache/nuget \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  --tmpfs /tmp:exec,mode=1777 \
  -v "$PWD:/src" -v "$REFS:/refs:ro" -w /src \
  --entrypoint bash mcr.microsoft.com/dotnet/sdk:8.0 \
  -c '"$@"; rc=$?; chown -R "$HOST_UID:$HOST_GID" /src; exit $rc' run.sh "$@"
