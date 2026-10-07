#!/usr/bin/env bash
# Builds and tests Beam on Linux/macOS (development, CI), and cross-publishes the Windows app.
#   ./build.sh                 test + publish win-x64
#   ./build.sh win-arm64       test + publish win-arm64
#   SKIP_TESTS=1 ./build.sh    publish only
# The Windows installer itself is built on Windows with build.ps1 -Installer (Inno Setup).
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

RUNTIME="${1:-win-x64}"

if ! command -v dotnet >/dev/null; then
  echo ".NET SDK 8 or newer is required: https://dotnet.microsoft.com/download" >&2
  exit 1
fi

if [[ "${SKIP_TESTS:-0}" != "1" ]]; then
  echo "==> Running tests"
  dotnet test Beam.sln -c Release
fi

echo "==> Publishing ${RUNTIME}"
dotnet publish src/Beam.App -f net8.0-windows10.0.19041.0 -p:PublishProfile="${RUNTIME}"
ls -lh "artifacts/publish/${RUNTIME}/"
