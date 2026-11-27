# Gmail sign-in for the container

nullbox runs headless, so it never opens a browser. It reuses the **desktop-client credentials** that emailBlaster
already has: a refresh token needs no browser or redirect, only the first consent does.

## Files

All under `./data/google/` on the host (`/data/google/` in the container):

| File | What it is | Needed |
|---|---|---|
| `token.json` | Authorized-user token from google-auth (`token`, `refresh_token`, `client_id`, `client_secret`, `scopes`, `expiry`) | yes |
| `credentials.json` | The OAuth "Desktop app" client (`{"installed": {...}}`) | only if `token.json` has no `client_id`/`client_secret` |
| `token_filters.json` | Second token with `gmail.settings.basic`, same format as emailBlaster's | only for Gmail filter jobs (M1-07) |

The folder and these names are gitignored and excluded from the Docker build context. nullbox writes token files
owner-only (`0600`) through a temp file and rename.

## Option A: reuse emailBlaster's tokens

```sh
mkdir -p data/google
cp ../emailblaster/volume/credentials.json ../emailblaster/volume/token.json data/google/
cp ../emailblaster/volume/token_filters.json data/google/   # if you use filters
sudo chown -R 1654:1654 data && chmod 600 data/google/*.json
docker compose up -d
```

Sign in at `http://<host>:8480/login`, then open `/settings/gmail`. It should say **Connected** and list the
token's scopes. When the access token expires nullbox refreshes it and writes the new one back to `token.json`; the
file stays readable by emailBlaster.

## Option B: make a token with nullbox-auth

On a PC with a browser and the .NET 10 SDK, with `credentials.json` in `data/google/`:

```sh
dotnet run --project tools/Nullbox.Auth                       # mail token: gmail.modify
dotnet run --project tools/Nullbox.Auth -- --filters          # filters token: gmail.settings.basic
dotnet run --project tools/Nullbox.Auth -- --scopes full      # https://mail.google.com/ (needed only for permanent delete)
```

It opens Google's consent page and listens on a `http://localhost:<port>` loopback redirect, like google-auth's
`run_local_server`. Copy the resulting file to the server's `data/google/`, or upload it at `/settings/gmail`.
Options: `--credentials PATH`, `--out PATH`, `--scopes modify,readonly,full,settings` (or full scope URLs).

## Uploading or replacing a token

`/settings/gmail` (admin login required) takes a file or pasted JSON. nullbox checks the shape, calls
`users.getProfile` with it, and only saves it if that works. The page shows the account it saved, never the token.

## When it stops working

If Google refuses the refresh token (`invalid_grant`: revoked, password change, or expired), the page shows
**Needs re-authorization** and Gmail jobs stop with a clear error instead of retrying. Upload a new token; nullbox
picks it up without a restart.

**Testing-mode consent screens expire refresh tokens after 7 days.** In Google Cloud Console, set the OAuth consent
screen's publishing status to **In production**. An unverified app is fine for personal use; Google shows a warning
during consent that you can click through.

## Not in scope

A browser-based web OAuth flow inside nullbox. It needs an HTTPS redirect URI on the server and can be a later issue.

## Manual check with a real account

1. Copy emailBlaster's `token.json` to `data/google/` and start the container.
2. `/settings/gmail` shows Connected, the right scopes and an expiry.
3. Set `expiry` in a copy of `token.json` to a past time and upload it at `/settings/gmail`: the test call refreshes
   it, and the saved `token.json` has a new `token` and `expiry`, the same `refresh_token`, and mode `0600`.
4. Revoke the app at https://myaccount.google.com/permissions, then upload the old token again: it is refused with
   `invalid_grant`. (A running job that hits the same refusal sets the page to Needs re-authorization.)
5. Run `nullbox-auth` on a PC, upload the new token: Connected again without a restart.
