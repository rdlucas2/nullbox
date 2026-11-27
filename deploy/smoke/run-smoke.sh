#!/usr/bin/env bash
# End-to-end smoke test in containers: nullbox polls a fake Gmail, asks a fake Jev, records dry-run decisions,
# runs a cleanup dry run, and changes nothing. Also checks that no message content reaches the logs.
# Usage: deploy/smoke/run-smoke.sh   (needs docker compose, curl, jq; cleans up after itself)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
compose=(docker compose -f "$here/docker-compose.smoke.yml")
port="${SMOKE_PORT:-18480}"
fake_port="${SMOKE_FAKE_PORT:-18481}"
base="http://127.0.0.1:$port"
export SMOKE_DATA="$(mktemp -d)"
jar="$(mktemp)"

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then "${compose[@]}" logs --no-color --tail 200 || true; fi
  "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  # The container's entrypoint hands the data folder to the app user; empty it and give it back before deleting.
  docker run --rm -v "$SMOKE_DATA:/d" alpine sh -c "rm -rf /d/* /d/.??*; chown $(id -u):$(id -g) /d" >/dev/null 2>&1 || true
  rm -rf "$SMOKE_DATA" "$jar"
  exit "$status"
}
trap cleanup EXIT

fail() { echo "SMOKE FAILED: $*" >&2; exit 1; }

# Wait up to $2 seconds for the shell condition $1 to hold.
wait_for() {
  local deadline=$((SECONDS + $2))
  until eval "$1"; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 2
  done
}

mkdir -p "$SMOKE_DATA/google" "$SMOKE_DATA/cleanup"
cat > "$SMOKE_DATA/google/token.json" <<'JSON'
{ "token": null, "refresh_token": "fake-refresh-token", "client_id": "fake-client.apps.googleusercontent.com",
  "client_secret": "fake-secret", "scopes": ["https://mail.google.com/"], "expiry": null }
JSON
cat > "$SMOKE_DATA/cleanup/smoke.json" <<'JSON'
{ "label": "cleanup_smoke", "keep": { "senders": ["bank.test"] },
  "rules": [ { "name": "Old promos", "query": "category:promotions older_than:30d" } ] }
JSON

echo "== starting nullbox and the fake upstream"
"${compose[@]}" up --detach --build --quiet-pull
wait_for "curl -fsS $base/healthz >/dev/null 2>&1" 120 || fail "nullbox never became healthy"

echo "== signing in"
token() { curl -fsS -c "$jar" -b "$jar" "$base/login" | sed -n 's/.*name="__RequestVerificationToken" value="\([^"]*\)".*/\1/p' | head -1; }
curl -fsS -o /dev/null -c "$jar" -b "$jar" --data-urlencode "__RequestVerificationToken=$(token)" --data-urlencode "password=smoke-password" "$base/login"

echo "== running the inbox poll"
curl -fsS -o /dev/null -c "$jar" -b "$jar" --data-urlencode "__RequestVerificationToken=$(token)" "$base/inbox/run"
# Two messages, two example rule sets: four verdicts once the inbox queue has drained.
wait_for "[ \"\$(curl -fsS -b $jar '$base/pipeline/decisions?limit=50' | jq '.items | length')\" -ge 4 ]" 180 || fail "no decisions were recorded"
decisions="$(curl -fsS -b "$jar" "$base/pipeline/decisions?limit=50")"
echo "$decisions" | jq -e '.items | length >= 4 and all(.[]; .outcome == "dry_run")' >/dev/null || fail "the example rule sets should only dry-run: $decisions"

echo "== cleanup dry run"
run="$(curl -fsS -c "$jar" -b "$jar" --data-urlencode "__RequestVerificationToken=$(token)" "$base/cleanup/configs/smoke/run?sample=1")"
id="$(echo "$run" | jq -r '.id')"
wait_for "curl -fsS -b $jar $base/cleanup/runs/$id | jq -e '.status == \"Completed\" or .status == \"Failed\"' >/dev/null" 120 || fail "cleanup run $id never finished"
result="$(curl -fsS -b "$jar" "$base/cleanup/runs/$id")"
echo "$result" | jq -e '.status == "Completed" and .matched >= 1 and .changed == 0' >/dev/null || fail "unexpected cleanup result: $result"

echo "== component health"
ready="$(curl -fsS -b "$jar" "$base/healthz/ready")"
echo "$ready" | jq -e '.checks.database.status == "Healthy" and .checks.gmail.status == "Healthy" and .checks.jev.status == "Healthy"' >/dev/null \
  || fail "components not healthy: $ready"

echo "== what reached the fakes"
calls="$(curl -fsS "http://127.0.0.1:$fake_port/_fake/calls")"
echo "$calls" | jq -e 'any(.[]; . == "POST /token") and any(.[]; . == "POST /v1/systemone") and any(.[]; startswith("GET /gmail/v1/users/me/messages/"))' >/dev/null \
  || fail "expected token, Jev and message calls: $calls"
if echo "$calls" | jq -e 'any(.[]; test("batchModify|trash|batchDelete"))' >/dev/null; then fail "a dry run changed Gmail: $calls"; fi

echo "== logs carry no message content"
if "${compose[@]}" logs --no-color nullbox | grep -E "SMOKE-SECRET-BODY|SMOKE-SUBJECT|SMOKE-SNIPPET"; then fail "message content was logged"; fi

echo "SMOKE PASSED"
