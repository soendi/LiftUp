#!/usr/bin/env bash
# Startet einen Wegwerf-PostgreSQL-Cluster im Benutzerverzeichnis und lässt den
# Abgleich zweier Clients dagegen laufen. Danach wird der Server wieder gestoppt.
#
#   tools/postgres-sync-test.sh              # einrichten, testen, aufräumen
#   tools/postgres-sync-test.sh --reset      # vorhandene Testdaten vorher löschen
#
# Es wird nichts systemweit installiert: Die PostgreSQL-Binaries liegen als
# entpackte Ubuntu-Pakete unter $HOME/.local/pg16, die Daten unter /tmp.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PGBASE="${LIFTUP_PG_HOME:-$HOME/.local/pg16}"
PGCACHE="${LIFTUP_PG_CACHE:-/tmp/pgdebs}"
PGDATA="${LIFTUP_PG_DATA:-/tmp/liftup-pg/data}"
SOCKETDIR="${LIFTUP_PG_SOCKET:-/tmp/liftup-pg/socket}"
PGPORT="${LIFTUP_PG_PORT:-55432}"
PGVERSION="16"
BASEURL="https://archive.ubuntu.com/ubuntu/pool/main/p/postgresql-16"
PACKAGES=(
  "libpq5_16.2-1ubuntu4_amd64.deb"
  "postgresql-16_16.2-1ubuntu4_amd64.deb"
  "postgresql-client-16_16.2-1ubuntu4_amd64.deb"
)

BIN="$PGBASE/usr/lib/postgresql/$PGVERSION/bin"
export LD_LIBRARY_PATH="$PGBASE/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

if [ "${1:-}" = "--reset" ]; then
  echo "== Testdaten löschen: $PGDATA"
  rm -rf "$PGDATA"
fi

if [ ! -x "$BIN/initdb" ]; then
  echo "== PostgreSQL $PGVERSION holen und nach $PGBASE entpacken"
  mkdir -p "$PGBASE" "$PGCACHE"
  for package in "${PACKAGES[@]}"; do
    if [ ! -f "$PGCACHE/$package" ]; then
      curl -fsSL --max-time 180 -o "$PGCACHE/$package" "$BASEURL/$package"
    fi
    dpkg-deb -x "$PGCACHE/$package" "$PGBASE"
  done
fi

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  echo "== Cluster in $PGDATA anlegen (Vertrauensstellung nur lokal)"
  mkdir -p "$PGDATA"
  "$BIN/initdb" -D "$PGDATA" -U postgres --auth-local=trust --auth-host=trust \
    --encoding=UTF8 --locale=C.UTF-8 > /tmp/liftup-pg-initdb.log 2>&1 \
    || "$BIN/initdb" -D "$PGDATA" -U postgres --auth-local=trust --auth-host=trust \
         --encoding=UTF8 --no-locale > /tmp/liftup-pg-initdb.log 2>&1
  cat /tmp/liftup-pg-initdb.log | tail -3
fi

cleanup() {
  if "$BIN/pg_ctl" -D "$PGDATA" status > /dev/null 2>&1; then
    echo "== Server stoppen"
    "$BIN/pg_ctl" -D "$PGDATA" -m fast stop > /dev/null || true
  fi
}
trap cleanup EXIT

if ! "$BIN/pg_ctl" -D "$PGDATA" status > /dev/null 2>&1; then
  echo "== Server starten auf Port $PGPORT"
  mkdir -p "$SOCKETDIR"
  "$BIN/pg_ctl" -D "$PGDATA" -l /tmp/liftup-pg-server.log \
    -o "-p $PGPORT -k $SOCKETDIR -c listen_addresses=127.0.0.1 -c fsync=off" -w start
fi

export LIFTUP_TEST_POSTGRES="Host=127.0.0.1;Port=$PGPORT;Database=postgres;Username=postgres"

echo "== Abgleich mit zwei Clients gegen PostgreSQL testen"
set +e
dotnet test "$ROOT_DIR/tests/LiftUp.Core.Tests/LiftUp.Core.Tests.csproj" -c Release \
  --filter "FullyQualifiedName~PostgresSyncEndToEndTests" \
  --logger "console;verbosity=normal"
status=$?
set -e

if [ "$status" -ne 0 ]; then
  echo "== Fehlgeschlagen. Serverprotokoll:"
  tail -30 /tmp/liftup-pg-server.log
fi

exit "$status"
