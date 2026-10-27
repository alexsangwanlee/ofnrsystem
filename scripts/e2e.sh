#!/usr/bin/env bash
# End-to-end check (plan Task 13) on Linux: the built web on the real server with --seed-demo, a fresh database and data folder.
# 1. Screenshots of every page, light and dark, 1440x900 and 1024x768 (web/e2e/screens.cjs, global Playwright).
# 2. Codex health. 3. When Claude Code is logged in: one real brief job and the engine check (web/e2e/live.cjs).
# E2E_LIVE=0 skips the real job. E2E_KEEP=1 keeps the server, database and data folder for a look afterwards.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="${DOTNET_ROOT:-$HOME/.dotnet}:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

service postgresql start >/dev/null 2>&1 || true
dotnet build src/Ofnr.Server -v q -nologo
npm --prefix web run build >/dev/null

port=${E2E_PORT:-47831}
token=$(od -An -tx1 -N32 /dev/urandom | tr -d ' \n')
db=ofnr_e2e_$(od -An -tx1 -N4 /dev/urandom | tr -d ' \n')
data=$(mktemp -d /tmp/ofnr-e2e-XXXXXX)
log=$data/server.log
export E2E_URL=http://127.0.0.1:$port E2E_TOKEN=$token NODE_PATH=$(npm root -g)

OFNR_DATA=$data OFNR_PORT=$port OFNR_TOKEN=$token OFNR_WEB_ROOT=web/dist \
  OFNR_DB="Host=127.0.0.1;Database=$db;Username=ofnr;Password=ofnr_dev" \
  dotnet src/Ofnr.Server/bin/Debug/net10.0/Ofnr.Server.dll --seed-demo >"$log" 2>&1 &
server=$!
cleanup() {
  status=$?
  [ $status -ne 0 ] && tail -n 40 "$log" >&2
  if [ "${E2E_KEEP:-}" = 1 ]; then echo "kept: $E2E_URL/?token=$token  data $data  database $db"; return; fi
  kill $server 2>/dev/null && wait $server 2>/dev/null || true
  PGPASSWORD=ofnr_dev psql -q -h 127.0.0.1 -U ofnr -d postgres -c "DROP DATABASE IF EXISTS $db WITH (FORCE)" || true
  rm -rf "$data"
}
trap cleanup EXIT

for _ in $(seq 90); do
  curl -sf -o /dev/null -H "X-Ofnr-Token: $token" "$E2E_URL/api/health" && break
  kill -0 $server 2>/dev/null || { echo "server exited" >&2; exit 1; }
  sleep 1
done

node web/e2e/screens.cjs
if [ "${E2E_LIVE:-1}" = 1 ] && claude auth status >/dev/null 2>&1; then E2E_LIVE=1 node web/e2e/live.cjs
else echo "live Claude job skipped (E2E_LIVE=0 or not logged in)"; E2E_LIVE=0 node web/e2e/live.cjs; fi
echo "e2e: PASS"
