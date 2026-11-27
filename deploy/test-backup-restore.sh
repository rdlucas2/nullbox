#!/bin/sh
# Scripted backup/restore check against a temp directory (no Docker, no real data). CI runs it after the tests.
#   deploy/test-backup-restore.sh [path/to/Nullbox.Web.dll]
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
dll="${1:-$root/src/Nullbox.Web/bin/Release/net10.0/Nullbox.Web.dll}"
[ -f "$dll" ] || { echo "build first: $dll not found" >&2; exit 66; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
data="$tmp/data"; backups="$tmp/backups"
mkdir -p "$data/google" "$data/keys"
echo '{"refresh_token":"original"}' > "$data/google/token.json"
echo '<key/>' > "$data/keys/key-1.xml"

sql() { python3 -I -c 'import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); r=c.execute(sys.argv[2]).fetchall(); c.commit(); print("|".join(str(v[0]) for v in r))' "$@"; }
sql "$data/nullbox.db" "CREATE TABLE t (v TEXT)" >/dev/null
sql "$data/nullbox.db" "INSERT INTO t VALUES ('original')" >/dev/null
sql "$data/hangfire.db" "CREATE TABLE j (v TEXT)" >/dev/null

backup() { NULLBOX_DATA_DIR="$data" NULLBOX_BACKUP_DIR="$backups" dotnet "$dll" --backup "$@"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

backup --keep 2
first="$(ls -1 "$backups" | head -n 1)"
sleep 1; backup --keep 2
sleep 1; backup --keep 2
[ "$(ls -1 "$backups" | wc -l)" -eq 2 ] || fail "expected 2 snapshots kept, got: $(ls "$backups")"
ls "$backups" | grep -q "$first" && fail "oldest snapshot $first should have been pruned"
snap="$backups/$(ls -1 "$backups" | tail -n 1)"

[ "$(stat -c %a "$snap")" = "700" ] || fail "snapshot folder should be 0700"
[ "$(stat -c %a "$snap/google/token.json")" = "600" ] || fail "token copy should be 0600"
[ "$(stat -c %a "$snap/nullbox.db")" = "600" ] || fail "database copy should be 0600"
[ "$(sql "$snap/nullbox.db" 'SELECT v FROM t')" = "original" ] || fail "snapshot database content"

# Change everything, then restore.
sql "$data/nullbox.db" "UPDATE t SET v = 'changed'" >/dev/null
echo '{"refresh_token":"changed"}' > "$data/google/token.json"
NULLBOX_DATA_DIR="$data" dotnet "$dll" --backup --restore "$snap"
[ "$(sql "$data/nullbox.db" 'SELECT v FROM t')" = "original" ] || fail "restored database content"
grep -q original "$data/google/token.json" || fail "restored token.json"

echo "backup/restore OK"
