#!/usr/bin/env bash
#
# Regenerates typed API clients from the running services' OpenAPI documents.
#
# Why this is a script you run rather than an MSBuild step: generation needs the six services
# running, which means a database, a broker and Redis. Wiring that into `dotnet build` would
# make an offline build impossible and a CI build dependent on a working SQL Server — so the
# repository ships hand-written clients in SocietyHub.Client.Shared/Api, and this exists to
# check them against what the services actually publish.
#
# Run it after changing an endpoint. If the diff is empty, the hand-written client is still
# accurate. If it is not, the hand-written client is wrong and that is exactly what you want
# to find out.
#
#   ./scripts/generate-clients.sh            # fetch documents, report drift
#   ./scripts/generate-clients.sh --write    # also emit generated clients under artifacts/
#
set -euo pipefail

cd "$(dirname "$0")/.."

OUT="artifacts/openapi"
WRITE="${1:-}"

mkdir -p "$OUT"

# The ports Aspire assigns are dynamic, so they are discovered rather than assumed. Same
# approach as scripts/smoke-test.sh.
discover_port() {
  local service="$1"
  local port

  port=$(dotnet aspire exec --resource "$service" -- printenv ASPNETCORE_HTTP_PORTS 2>/dev/null \
    | tr -d '\r' | head -1 || true)

  echo "$port"
}

SERVICES=(identity society gate helpdesk notification notice)
FAILED=0

for service in "${SERVICES[@]}"; do
  echo "→ ${service}"

  port=$(discover_port "${service}-api")

  if [[ -z "$port" ]]; then
    echo "  could not resolve a port. Is the stack running? (./scripts/run.sh)"
    FAILED=1
    continue
  fi

  url="http://localhost:${port}/openapi/v1.json"

  if ! curl -fsS "$url" -o "${OUT}/${service}.json"; then
    echo "  ${url} did not respond."
    FAILED=1
    continue
  fi

  echo "  saved ${OUT}/${service}.json"

  if [[ "$WRITE" == "--write" ]]; then
    # NSwag is a global tool here; see docs/RUNNING.md for installation.
    nswag openapi2csclient \
      /input:"${OUT}/${service}.json" \
      /classname:"${service^}Client" \
      /namespace:"SocietyHub.Client.Generated" \
      /output:"artifacts/generated/${service^}Client.cs" \
      /generateOptionalParameters:true \
      /generateClientInterfaces:true >/dev/null

    echo "  generated artifacts/generated/${service^}Client.cs"
  fi
done

if [[ "$FAILED" -ne 0 ]]; then
  echo
  echo "Some services did not respond. Start the stack with ./scripts/run.sh and retry."
  exit 1
fi

echo
echo "OpenAPI documents are in ${OUT}."
echo "Compare them against src/Clients/SocietyHub.Client.Shared/Api/SocietyHubApiClient.cs;"
echo "a mismatch there is a client that will fail at runtime, not a cosmetic difference."
