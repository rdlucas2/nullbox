# Deploying to the home server

nullbox runs as one container on the Ubuntu box that already hosts Home Assistant (8123) and Jellyfin (8096).
It listens on plain HTTP on the LAN, on port **8480** by default, and has no reverse proxy. Everything it keeps
lives in `deploy/data`. Nightly snapshots go to `deploy/backups`.

## First deploy (checklist)

1. Install Docker Engine with the compose plugin (`docker compose version` should print v2.20 or newer).
2. Clone the repo, for example to `/srv/nullbox`, then `cd /srv/nullbox/deploy`.
3. `cp .env.example .env` and fill it in:
   - `NULLBOX_BIND`: the server's LAN IP (`ip -4 addr`, e.g. `192.168.1.20`). Compose refuses to start
     without it, so the UI is never published on every interface by accident.
   - `ADMIN_PASSWORD` and `JEV_API_KEY`.
   - Optionally `NULLBOX_UID`/`NULLBOX_GID` (your `id -u`/`id -g`) to read `data/` and `backups/` without sudo.
4. Put emailBlaster's `credentials.json` and `token.json` in `deploy/data/google/` (or upload the token later at
   `/settings/gmail`). See [gmail-auth.md](gmail-auth.md).
5. `docker compose up -d --build`, then `docker compose ps` until it shows `(healthy)`.
6. From another machine on the LAN, open `http://<server-ip>:8480`, sign in, and check the dashboard.
7. Add the nightly backup to cron (below) and run `./backup.sh` once by hand to see it work.

## What the compose file sets

| Setting | Value |
|---|---|
| Restart | `unless-stopped` |
| Port | `${NULLBOX_BIND}:${NULLBOX_PORT:-8480}` → 8080 in the container |
| Volumes | `./data:/data` (databases, tokens, login keys) and `./backups:/backups` |
| Health | the image's `HEALTHCHECK` calls `/healthz` every 30 s |
| Logs | `json-file`, 10 MB × 3 files (`docker compose logs -f`) |
| Memory | `mem_limit: 512m` (`NULLBOX_MEM_LIMIT`) with workstation GC; it idles around 40 MB |

The app never redirects to HTTPS and its login cookie works over plain HTTP. Blazor's websocket connects directly.

## Remote access: Tailscale or WireGuard only

Never forward a router port to nullbox. `/login` and `/hangfire` must not be reachable from the internet.

To reach it away from home, install Tailscale (or WireGuard) on the server and use its tailnet address. With
Tailscale you can either:

- publish on the tailnet instead of the LAN, by setting `NULLBOX_BIND` to the server's tailnet IP
  (`tailscale ip -4`), or
- keep `NULLBOX_BIND=127.0.0.1` and run `tailscale serve --bg 8480`, which gives you HTTPS on
  `https://<server>.<tailnet>.ts.net`. Then set `Nullbox__ForwardedHeaders__Enabled=true` and
  `Nullbox__ForwardedHeaders__KnownProxies__0=127.0.0.1` in `.env` so the app sees the original scheme.

Docker's published ports bypass `ufw`, so the bind address is what keeps nullbox off other interfaces.

## Optional HTTPS with Caddy

`deploy/docker-compose.yml` has a commented-out `caddy` service. To use it, uncomment it and write a
`Caddyfile`. Then turn on forwarded headers as above, listing Caddy's container IP in `KnownProxies`. It is
still LAN- or tailnet-only. None of this is needed for the OAuth flow, because Gmail auth reuses emailBlaster's
desktop credentials.

## Backups

`deploy/backup.sh` asks the running container to snapshot `/data` into `deploy/backups/nullbox-<UTC stamp>/`:

- every SQLite database (`hangfire.db`, `nullbox.db`, `jev.db`), through SQLite's online backup, so it is
  consistent while jobs run;
- `google/` (token and client files) and `keys/` (the login key ring);
- folders `0700`, files `0600`, owned by the app user;
- the newest `NULLBOX_BACKUP_KEEP` snapshots are kept (default 14), older ones are deleted.

Nightly at 03:15, from the crontab of a user that can run docker (`crontab -e`):

```cron
15 3 * * * /srv/nullbox/deploy/backup.sh >> /var/log/nullbox-backup.log 2>&1
```

Snapshots sit on the same disk as the data. Copy `deploy/backups` somewhere else as well, such as a NAS or
another machine, if you want to survive a dead disk.

## Restore

```sh
cd /srv/nullbox/deploy
./restore.sh                               # lists snapshots
./restore.sh nullbox-20261127T031500Z      # stops nullbox, restores, starts it again
```

Restore copies the snapshot's databases and `google/` and `keys/` files back over `data/`, removes stale SQLite
WAL files, and leaves anything else in `data/` alone. The same logic is tested in CI by
`deploy/test-backup-restore.sh` against a temp directory.

## Updating

```sh
cd /srv/nullbox
./deploy/backup.sh                         # snapshot first
git pull
cd deploy && docker compose up -d --build
docker compose ps                          # wait for (healthy)
```

Database migrations run on start. If you publish images later, set `NULLBOX_IMAGE` in `.env` and use
`docker compose pull && docker compose up -d` instead of `--build`. Publishing to GHCR on tags isn't set up yet.

## Rolling back

Migrations only go forward. Roll back the code **and** the data together:

```sh
cd /srv/nullbox
git checkout <previous commit or tag>
cd deploy && docker compose up -d --build
./restore.sh <snapshot taken before the update>
```

## Troubleshooting

- `required variable NULLBOX_BIND is missing a value`: set it in `deploy/.env`.
- The container restarts with a permissions error: the message names the folder. By default the entrypoint
  fixes `data/` and `backups/` ownership itself, unless you started it with `user:`.
- `docker compose logs nullbox` shows the app log. Bodies of mail are never logged.
