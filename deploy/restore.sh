#!/bin/sh
# Restores a snapshot made by backup.sh: stops nullbox, copies the snapshot's databases and token/key files
# back into deploy/data (other files are left alone), then starts it again.
#
#   ./restore.sh                               # lists snapshots
#   ./restore.sh nullbox-20261127T031500Z      # restores that one
set -eu
cd "$(dirname "$0")"

if [ $# -eq 0 ]; then
  echo "Snapshots in $(pwd)/backups (newest last):"
  ls -1 backups 2>/dev/null | grep '^nullbox-' || echo "  none"
  echo "Usage: $0 <snapshot name>"
  exit 64
fi

name="$(basename "$1")"
case "$name" in
  nullbox-*) ;;
  *) echo "restore: '$1' is not a snapshot name (nullbox-<stamp>)." >&2; exit 64 ;;
esac
if [ ! -d "backups/$name" ]; then
  echo "restore: backups/$name not found." >&2
  exit 66
fi

echo "Stopping nullbox and restoring $name ..."
docker compose stop nullbox
# The entrypoint fixes ownership, drops root, then runs the restore with the app's own code.
docker compose run --rm --no-deps -T nullbox dotnet Nullbox.Web.dll --backup --restore "/backups/$name"
docker compose start nullbox
echo "Restored $name; nullbox is starting again (\`docker compose ps\` shows when it is healthy)."
