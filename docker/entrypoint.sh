#!/bin/sh
# Starts nullbox as an unprivileged user whose files in /data it can always write.
#
# The image starts as root only long enough to:
#   1. pick the run-as user: NULLBOX_UID / NULLBOX_GID (set them to your host `id -u` / `id -g` to keep
#      ./data editable by you), defaulting to the image's built-in "app" user (1654);
#   2. create /data/google and hand anything in /data not owned by that user over to it, so a fresh clone
#      works without a manual chown even when Docker or the host created ./data as root or as you;
#   3. drop root for good (setpriv, no new privileges) and exec the app.
# Started with `--user` / compose `user:`, it skips all of that and runs as given.
set -eu

data="${NULLBOX_DATA_DIR:-/data}"

if [ "$(id -u)" != "0" ]; then
  exec "$@"
fi

uid="${NULLBOX_UID:-${APP_UID:-1654}}"
gid="${NULLBOX_GID:-$uid}"
case "$uid$gid" in
  *[!0-9]*) echo "nullbox: NULLBOX_UID and NULLBOX_GID must be numbers (got '$uid' / '$gid')." >&2; exit 64 ;;
esac
if [ "$uid" = "0" ]; then
  echo "nullbox: refusing to run as root; set NULLBOX_UID to a non-zero id." >&2
  exit 64
fi

mkdir -p "$data/google"
# Only touch what is not already ours, so restarts are cheap and correct trees are left alone.
find "$data" \( \! -user "$uid" -o \! -group "$gid" \) -exec chown "$uid:$gid" {} +
chmod 0700 "$data/google"

# The backup folder (deploy/docker-compose.yml mounts ./backups there), when mounted: same treatment.
backups="${NULLBOX_BACKUP_DIR:-}"
if [ -n "$backups" ] && [ -d "$backups" ]; then
  find "$backups" \( \! -user "$uid" -o \! -group "$gid" \) -exec chown "$uid:$gid" {} +
  chmod 0700 "$backups"
fi

exec setpriv --reuid="$uid" --regid="$gid" --clear-groups --no-new-privs -- "$@"
