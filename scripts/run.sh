#!/usr/bin/env bash
#
# Starts the whole SocietyHub stack: SQL Server, Redis, RabbitMQ, and all five services.
#
#   scripts/run.sh
#
# Then open the Aspire dashboard at http://localhost:15009 — it shows every service, its
# logs, live traces and metrics, and the login link is printed below.

set -euo pipefail

cd "$(dirname "$0")/.."

# Docker Desktop installs per-user on Windows and does not always land on PATH.
for candidate in \
  "$LOCALAPPDATA/Programs/DockerDesktop/resources/bin" \
  "/c/Users/$USER/AppData/Local/Programs/DockerDesktop/resources/bin" \
  "/c/Program Files/Docker/Docker/resources/bin"
do
  [ -d "$candidate" ] && PATH="$candidate:$PATH"
done
export PATH

if ! docker info >/dev/null 2>&1; then
  echo "Docker is not running. Start Docker Desktop and wait for the whale icon to settle."
  exit 1
fi

# -p:AspireUseCliBundle=false runs the AppHost directly.
#
# With the bundle enabled, `dotnet run` delegates to the Aspire CLI, which tries to install a
# development certificate, opens a Windows security dialog, and then times out after 120s
# waiting for a click. Bypassing it starts the host immediately over plain HTTP, which is all
# local development needs.
export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true
export DOTNET_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://localhost:15009"
export ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL="http://localhost:19117"
export ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL="http://localhost:20102"

echo "Starting SocietyHub. First run pulls SQL Server (~1.5 GB) and may take a few minutes."
echo

exec dotnet run -p:AspireUseCliBundle=false \
  --project src/Aspire/SocietyHub.AppHost/SocietyHub.AppHost.csproj \
  --no-launch-profile
