#!/bin/sh
# Snapshots nullbox's data through the running container into deploy/backups/nullbox-<UTC stamp>/:
# every SQLite database via SQLite's online backup (safe while the app runs), plus data/google and data/keys.
# Files are 0600, folders 0700; only the newest NULLBOX_BACKUP_KEEP snapshots (default 14) are kept.
#
# Nightly from cron (as a user that can run docker):
#   15 3 * * * /srv/nullbox/deploy/backup.sh >> /var/log/nullbox-backup.log 2>&1
# Extra arguments go to the app, e.g. `./backup.sh --keep 30`.
set -eu
cd "$(dirname "$0")"

setting() { [ -f .env ] && sed -n "s/^$1=//p" .env | tail -n 1; }
uid="$(setting NULLBOX_UID)"; uid="${uid:-1654}"
gid="$(setting NULLBOX_GID)"; gid="${gid:-$uid}"

exec docker compose exec -T --user "$uid:$gid" nullbox dotnet Nullbox.Web.dll --backup "$@"
