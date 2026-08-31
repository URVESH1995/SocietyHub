#!/usr/bin/env bash
#
# SocietyHub smoke test — walks the whole platform end to end against a running stack.
#
#   1. start the stack:  scripts/run.sh
#   2. run this:         scripts/smoke-test.sh
#
# Aspire assigns service ports dynamically, so this discovers them from the running
# processes rather than hard-coding anything.

set -uo pipefail

pass=0
fail=0

ok()   { echo "  PASS  $1"; pass=$((pass + 1)); }
bad()  { echo "  FAIL  $1"; fail=$((fail + 1)); }
head() { echo; echo "=== $1 ==="; }

# --- discover ports -------------------------------------------------------

port_for() {
  local exe="$1"
  powershell.exe -NoProfile -Command "
    \$p = Get-CimInstance Win32_Process -Filter \"Name='$exe'\" | Select-Object -First 1 -Expand ProcessId
    if (\$p) { Get-NetTCPConnection -State Listen -OwningProcess \$p -EA SilentlyContinue |
               Where-Object { \$_.LocalPort -gt 1024 } |
               Select-Object -First 1 -Expand LocalPort }
  " 2>/dev/null | tr -d '\r '
}

IDENTITY=$(port_for "SocietyHub.Identity.Api.exe")
SOCIETY=$(port_for "SocietyHub.Society.Api.exe")
GATE=$(port_for "SocietyHub.Gate.Api.exe")
HELPDESK=$(port_for "SocietyHub.Helpdesk.Api.exe")
GATEWAY=$(port_for "SocietyHub.ApiGateway.exe")

head "Discovered services"
for pair in "identity:$IDENTITY" "society:$SOCIETY" "gate:$GATE" "helpdesk:$HELPDESK" "gateway:$GATEWAY"; do
  name="${pair%%:*}"; p="${pair##*:}"
  if [ -n "$p" ]; then echo "  $name -> http://localhost:$p"; else echo "  $name -> NOT RUNNING"; fi
done

if [ -z "$IDENTITY" ]; then
  echo
  echo "Identity is not running. Start the stack first with scripts/run.sh"
  exit 1
fi

# --- health ---------------------------------------------------------------

head "Health — each service checks its own SQL, Redis and RabbitMQ"
for pair in "identity:$IDENTITY" "society:$SOCIETY" "gate:$GATE" "helpdesk:$HELPDESK"; do
  name="${pair%%:*}"; p="${pair##*:}"
  [ -z "$p" ] && continue
  if curl -s -m 10 "http://localhost:$p/health" | grep -q '"status":"Healthy"'; then
    ok "$name healthy"
  else
    bad "$name unhealthy"
  fi
done

# --- sign in --------------------------------------------------------------
#
# The seeded demo society. In Development the OTP comes back in the response so the
# flow works without an SMS provider; in Production that field is always null.

head "Sign in as the resident (phone OTP)"
PHONE="+919000000002"

OTP_JSON=$(curl -s -m 10 -X POST "http://localhost:$IDENTITY/api/auth/otp/request" \
  -H 'Content-Type: application/json' -d "{\"phoneNumber\":\"$PHONE\"}")

CODE=$(echo "$OTP_JSON" | grep -oE '"developmentCode":"[0-9]+"' | grep -oE '[0-9]+')

if [ -n "$CODE" ]; then ok "OTP issued ($CODE)"; else bad "no OTP returned: $OTP_JSON"; exit 1; fi

TOKEN_JSON=$(curl -s -m 10 -X POST "http://localhost:$IDENTITY/api/auth/otp/verify" \
  -H 'Content-Type: application/json' -d "{\"phoneNumber\":\"$PHONE\",\"code\":\"$CODE\"}")

TOKEN=$(echo "$TOKEN_JSON" | grep -oE '"accessToken":"[^"]+"' | cut -d'"' -f4)
REFRESH=$(echo "$TOKEN_JSON" | grep -oE '"refreshToken":"[^"]+"' | cut -d'"' -f4)

if [ -n "$TOKEN" ]; then ok "access token issued"; else bad "no token: $TOKEN_JSON"; exit 1; fi

AUTH="Authorization: Bearer $TOKEN"

# --- the token carries exactly one society --------------------------------

head "Token contents"
ME=$(curl -s -m 10 "http://localhost:$IDENTITY/api/auth/me" -H "$AUTH")
echo "$ME" | grep -q '"societyId"' && ok "token carries a society" || bad "no society claim: $ME"
echo "$ME" | grep -qi 'Resident' && ok "token carries the Resident role" || bad "no role: $ME"

# --- auth is actually enforced --------------------------------------------

head "Authorisation is enforced, not decorative"
code=$(curl -s -o /dev/null -w '%{http_code}' -m 10 "http://localhost:$GATE/api/passes/expected")
[ "$code" = "401" ] && ok "no token -> 401" || bad "no token -> $code (expected 401)"

# A resident token must NOT satisfy the guard-only policy.
code=$(curl -s -o /dev/null -w '%{http_code}' -m 10 "http://localhost:$GATE/api/passes/expected" -H "$AUTH")
[ "$code" = "403" ] && ok "resident token on a guard endpoint -> 403" \
                    || bad "resident on guard endpoint -> $code (expected 403)"

# --- gate: pre-approve a visitor ------------------------------------------

head "Gate — pre-approve a visitor"
FLAT="33333333-3333-3333-3333-333333333333"
FROM=$(date -u +%Y-%m-%dT%H:%M:%SZ)
UNTIL=$(date -u -d '+4 hours' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v+4H +%Y-%m-%dT%H:%M:%SZ)

PASS_JSON=$(curl -s -m 10 -X POST "http://localhost:$GATE/api/passes" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d "{\"flatId\":\"$FLAT\",\"visitorName\":\"Ramesh Kumar\",\"visitorPhone\":\"+919812345678\",
       \"visitorType\":0,\"validFromUtc\":\"$FROM\",\"validUntilUtc\":\"$UNTIL\",
       \"expectedPersonCount\":1,\"purpose\":\"Dinner guest\"}")

PASS_CODE=$(echo "$PASS_JSON" | grep -oE '"code":"[0-9]+"' | grep -oE '[0-9]+')
PASS_ID=$(echo "$PASS_JSON" | grep -oE '"passId":"[^"]+"' | cut -d'"' -f4)

if [ -n "$PASS_CODE" ]; then ok "pass issued, gate code $PASS_CODE"; else bad "no pass: $PASS_JSON"; fi

# --- helpdesk: raise a complaint ------------------------------------------

head "Helpdesk — raise a complaint and check the SLA clock"
CMP_JSON=$(curl -s -m 10 -X POST "http://localhost:$HELPDESK/api/complaints" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d "{\"flatId\":\"$FLAT\",\"category\":0,\"priority\":1,
       \"title\":\"Kitchen tap leaking\",
       \"description\":\"The mixer tap drips constantly and the cabinet below is damp.\"}")

TICKET=$(echo "$CMP_JSON" | grep -oE '"ticketNumber":"[^"]+"' | cut -d'"' -f4)
DUE=$(echo "$CMP_JSON" | grep -oE '"slaDueAtUtc":"[^"]+"' | cut -d'"' -f4)

if [ -n "$TICKET" ]; then ok "complaint $TICKET raised, SLA due $DUE"; else bad "no complaint: $CMP_JSON"; fi

# A stuck lift reported as Normal must be escalated to High automatically.
LIFT_JSON=$(curl -s -m 10 -X POST "http://localhost:$HELPDESK/api/complaints" -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -d "{\"flatId\":\"$FLAT\",\"category\":2,\"priority\":1,
       \"title\":\"Lift stuck between floors\",
       \"description\":\"The lift in Tower A has stopped between the third and fourth floors.\"}")

echo "$LIFT_JSON" | grep -q '"priority":"High"' \
  && ok "lift complaint auto-escalated Normal -> High" \
  || bad "lift not escalated: $LIFT_JSON"

# --- refresh rotation ------------------------------------------------------

head "Refresh rotation and reuse detection"
R1=$(curl -s -m 10 -X POST "http://localhost:$IDENTITY/api/auth/refresh" \
  -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$REFRESH\"}")

NEW_REFRESH=$(echo "$R1" | grep -oE '"refreshToken":"[^"]+"' | cut -d'"' -f4)
[ -n "$NEW_REFRESH" ] && [ "$NEW_REFRESH" != "$REFRESH" ] \
  && ok "refresh rotated to a new token" || bad "rotation failed: $R1"

# Replaying the old one must kill the whole family.
R2=$(curl -s -m 10 -X POST "http://localhost:$IDENTITY/api/auth/refresh" \
  -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$REFRESH\"}")

echo "$R2" | grep -q 'TokenReuseDetected' \
  && ok "replaying a used token detected as reuse" || bad "reuse not detected: $R2"

# And the rotated token is dead too, because the family was revoked.
R3=$(curl -s -m 10 -X POST "http://localhost:$IDENTITY/api/auth/refresh" \
  -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$NEW_REFRESH\"}")

echo "$R3" | grep -qE 'InvalidRefreshToken|SessionExpired|TokenReuseDetected' \
  && ok "the whole token family was revoked" || bad "family survived: $R3"

# --- gateway routing -------------------------------------------------------

if [ -n "$GATEWAY" ]; then
  head "Gateway routing (YARP + service discovery)"
  for svc in identity society gate helpdesk; do
    code=$(curl -s -o /dev/null -w '%{http_code}' -m 10 "http://localhost:$GATEWAY/api/$svc/info")
    [ "$code" = "200" ] && ok "/api/$svc/info -> 200" || bad "/api/$svc/info -> $code"
  done
fi

# --- summary ---------------------------------------------------------------

echo
echo "==============================="
echo "  passed: $pass    failed: $fail"
echo "==============================="
[ "$fail" -eq 0 ] || exit 1
