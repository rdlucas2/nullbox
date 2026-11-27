# Security

nullbox holds a Gmail refresh token with full mailbox access, so treat the host and `./data` accordingly.

## Secrets

| Secret | Where it lives | Notes |
|---|---|---|
| Gmail tokens | `data/google/token.json`, `token_filters.json` | Written owner-only (`0600`) through a temp file and rename. Disconnect on `/settings` deletes the file. |
| OAuth client | `data/google/credentials.json` or inside `token.json` | Desktop-app client; a refresh needs no browser. |
| `ADMIN_PASSWORD` | `.env` | The only login. Without it nobody can sign in. |
| `JEV_API_KEY` | `.env` | Sent only to the Jev endpoint, as a bearer token. |
| Login cookie keys | `data/keys/` | ASP.NET Data Protection key ring; delete it to sign everyone out. |
| Saved settings | `data/settings.json` | Written by `/settings` and the Jobs page; overrides `.env`, including the delete settings. |

- Keep `.env` owner-only (`chmod 600 .env`) and out of git (it is gitignored, like `data/`).
- Compose reads `.env` into the container's environment, so the values show in `docker inspect` to anyone who can
  run Docker on the host (who could read `./data` anyway). That is the supported route for a single-user LAN box.
  Docker secrets (`*_FILE` variables) are not wired up; it would take a small entrypoint change.
- Backups (`data/backups`, see [deploy.md](deploy.md#backups)) contain the tokens: store them as carefully as `data/`.
- Nothing logs message bodies, subjects, snippets or token values; the compose smoke test fails if message content
  reaches the logs.

## Network

- Publish the port on the LAN only (`NULLBOX_BIND` in the server compose refuses to start without it) and reach it
  remotely through Tailscale or WireGuard, never a port forward ([deploy.md](deploy.md#remote-access-tailscale-or-wireguard-only)).
- For HTTPS, put Caddy (or Tailscale Serve) in front and turn on forwarded headers for that proxy only:
  `Nullbox__ForwardedHeaders__Enabled=true`, `Nullbox__ForwardedHeaders__KnownProxies__0=<proxy IP>`
  ([deploy.md](deploy.md#optional-https-with-caddy)). Without it nullbox ignores `X-Forwarded-*`.
- The login cookie is `HttpOnly` and `SameSite=Strict`; every state-changing API call needs an antiforgery token.

## Sign-in

- One admin password, compared in constant time. `POST /login` is rate limited per client address
  (`Nullbox:Login:AttemptsPerWindow`, default 10 per `WindowSeconds` 300); more attempts answer 429 and are logged.
- Every UI page, the Hangfire dashboard and every admin API sits behind the login; only `/login`, `/healthz` and static
  assets are public.

## Safety switches

Deleting mail is off unless `Nullbox:Gmail:AllowDelete=true`, permanent delete also needs `AllowPermanentDelete`, and
creating Gmail filters needs `AllowFilterSync`. A signed-in admin can change them on `/settings`: turning one on asks
for confirmation, is written to `data/settings.json` (which overrides `.env` and `appsettings.json`) and only takes
effect after a restart. So the admin password is what stands between a browser and deleting mail; keep it strong and
the app LAN-only. Deleting the keys from `data/settings.json` (or the file) goes back to your configuration. See the
README's safety model.

## Testing-only settings

`Nullbox:Gmail:ApiBaseUri` and `Nullbox:Gmail:TokenEndpoint` send Gmail calls and token refreshes elsewhere (the smoke
test's fakes). Anyone who can set them can read the refresh token, so never set them in production; the app logs a
warning at start when they are set.
