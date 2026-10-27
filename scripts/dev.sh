#!/usr/bin/env bash
# Linux dev loop: PostgreSQL, the server on 127.0.0.1:47821 and Vite on http://localhost:5173.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="${DOTNET_ROOT:-$HOME/.dotnet}:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

as_postgres() { if [ "$(id -u)" = 0 ]; then runuser -u postgres -- "$@"; else sudo -u postgres "$@"; fi; }

# The server's default dev login (OfnrOptions.ConnectionString); CREATEDB because the tests make their own databases.
service postgresql start >/dev/null 2>&1 || true
as_postgres psql -q -v ON_ERROR_STOP=1 <<'SQL'
SELECT 'CREATE ROLE ofnr LOGIN CREATEDB PASSWORD ''ofnr_dev''' WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ofnr')\gexec
SELECT 'CREATE DATABASE ofnr_ops OWNER ofnr' WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'ofnr_ops')\gexec
SQL

[ -d web/node_modules ] || npm --prefix web ci

token=$(od -An -tx1 -N32 /dev/urandom | tr -d ' \n')
dotnet build src/Ofnr.Server -v q -nologo
OFNR_TOKEN=$token dotnet src/Ofnr.Server/bin/Debug/net10.0/Ofnr.Server.dll &
server=$!
trap 'kill $server 2>/dev/null || true' EXIT

for _ in $(seq 60); do
  curl -sf -o /dev/null -H "X-Ofnr-Token: $token" http://127.0.0.1:47821/api/health && break
  kill -0 $server 2>/dev/null || { echo "server exited" >&2; exit 1; }
  sleep 1
done

echo
echo "Open http://localhost:5173/?token=$token"
echo
npm --prefix web run dev
