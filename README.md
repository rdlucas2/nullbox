# nullbox

Gmail inbox helper: a Linux container running .NET + Hangfire that polls Gmail on a schedule, uses
[TypeSafe Jev](https://docs.typesafe.ai) to classify mail, and acts on it (label / mark read / archive / trash),
with a web UI to build and edit the Jev rules (choices, scores, nouls) on the fly.

See [PLAN.md](PLAN.md) for architecture and milestones, [docs/jev-api.md](docs/jev-api.md) for the Jev contract,
[docs/rules.md](docs/rules.md) for writing rule sets,
and the GitHub issues for the work breakdown. Gmail actions are a port of [emailBlaster](https://github.com/rdlucas2/emailblaster).

## Layout

| Path | What lives there |
|---|---|
| `src/Nullbox.Web` | ASP.NET Core host: endpoints, Hangfire, Blazor + MudBlazor UI (`Components/`) |
| `src/Nullbox.Core` | Domain model, rule model, interfaces (`IGmailService`, `IJevClient`, ...) |
| `src/Nullbox.Data` | EF Core + SQLite (`{data}/nullbox.db`): rule-set versions, migrations applied on start |
| `src/Nullbox.Gmail` | Gmail API implementation |
| `src/Nullbox.Jev` | TypeSafe Jev HTTP client |
| `deploy/` | Server compose file, `backup.sh`/`restore.sh` and their test ([docs/deploy.md](docs/deploy.md)) |
| `tools/Nullbox.Auth` | `nullbox-auth`: one-time Google consent on a PC, writes `token.json` ([docs/gmail-auth.md](docs/gmail-auth.md)) |
| `tests/Nullbox.Tests` | xUnit tests (unit + in-process integration via `WebApplicationFactory`) |

## Build and run

Requires the .NET 10 SDK (pinned in `global.json`, any 10.0 feature band works).

```sh
dotnet build Nullbox.sln
dotnet test Nullbox.sln
dotnet run --project src/Nullbox.Web
```

The app exposes `GET /healthz` (returns `Healthy`, no login needed) and `GET /api/info` (app name and version).

## Web UI

Sign in at `/login` with `ADMIN_PASSWORD`; every UI page redirects there until you do. The shell is Blazor
(interactive server) with MudBlazor: a side nav for Dashboard, Rule sets, Test, Audit, Jobs and Settings, a
light/dark toggle (follows the system setting until you switch it) and a sign-out button.

The dashboard at `/` shows the Gmail token state (scopes, access-token expiry, and a link to `/settings/gmail`
when it needs reauthorizing), the last inbox poll and scheduler heartbeat, Hangfire queue depth, and today's Jev
token budget. Rule sets, Test, Audit and Jobs are placeholders that later milestones fill in; Jobs links to the
Hangfire dashboard meanwhile.

## Configuration

Settings bind from the `Nullbox` section (`appsettings.json`, or env vars such as `Nullbox__Hangfire__WorkerCount=4`).
The container's plain env vars `ADMIN_PASSWORD` and `NULLBOX_DATA_DIR` apply when the `Nullbox:` keys are unset.

| Key | Default | Meaning |
|---|---|---|
| `Nullbox:AdminPassword` / `ADMIN_PASSWORD` | unset | Admin login for `/hangfire` and the UI. Unset means nobody can sign in. |
| `Nullbox:DataDirectory` / `NULLBOX_DATA_DIR` | `data` | SQLite databases, Google tokens and the login key ring in `keys/` (`/data` in the container). |
| `Nullbox:Hangfire:Storage` | `SQLite` | `SQLite` (file `hangfire.db` in the data directory) or `InMemory` (tests). |
| `Nullbox:Hangfire:WorkerCount` | `2` | Hangfire worker threads. |
| `Nullbox:Hangfire:DashboardPath` | `/hangfire` | Dashboard URL, behind the admin login. |
| `Nullbox:ForwardedHeaders:Enabled` / `KnownProxies` | off / none | Trust `X-Forwarded-*` from these proxy IPs, only if a proxy is added later ([docs/deploy.md](docs/deploy.md)). |
| `Nullbox:Jobs:HeartbeatCron` | `*/5 * * * *` | No-op heartbeat job (UTC) that proves the scheduler runs. |
| `Nullbox:Jobs:InboxPollCron` | `*/5 * * * *` | How often the `inbox-poll` job checks Gmail for new inbox mail (UTC). |
| `Nullbox:Jobs:InboxResyncWindow` | `2d` | Gmail `newer_than:` window listed on first run and when history has expired. |
| `Nullbox:Jobs:MessageWorkers` | `2` | Messages processed at once (workers on the `inbox` queue). |
| `Nullbox:Pipeline:KillSwitch` | `false` | `true` stops every Gmail change; live rule sets only record what they would do. |
| `Nullbox:Pipeline:MaxMessagesPerHour` | `100` | Most messages changed in any rolling hour; later live decisions are only recorded. |
| `Nullbox:Pipeline:MaxAttempts` | `4` | Failed attempts before a message is recorded as poison and not retried. |
| `Nullbox:Pipeline:AuthRetryDelay` | `01:00:00` | Wait before retrying when Gmail or Jev needs re-authorizing. |
| `Nullbox:Audit:RetentionDays` | `90` | Decision records older than this are deleted by the daily purge; `0` keeps them forever. |
| `Nullbox:Audit:PurgeCron` | `17 3 * * *` | When the purge runs (UTC). |
| `Nullbox:Audit:StoredBodyChars` | `280` | How much of the body sent to Jev a record keeps; `0` keeps none. Full bodies are never stored. |
| `Nullbox:Gmail:AllowDelete` | `false` | The app-wide **delete lock**. While false, nothing is moved to Trash or deleted, whatever a rule or job says; rules archive instead. The dashboard shows "Deletes locked". |
| `Nullbox:Gmail:AllowPermanentDelete` | `false` | Permanent delete (bulk jobs only, never rules). Also needs `AllowDelete`. |

## Jobs and dashboard

Hangfire runs inside the web app. Sign in at `/login` with `ADMIN_PASSWORD` to open the dashboard at `/hangfire`,
where the `heartbeat` recurring job shows up and can be triggered by hand. Jobs are registered through
`IJobScheduler` (in `Nullbox.Core`), so nothing outside `Nullbox.Web` references Hangfire.

## Inbox polling

The `inbox-poll` recurring job reads Gmail's history since the last stored history id and enqueues one
`ProcessMessageJob` per new inbox message on the `inbox` queue. On first run, and when Gmail no longer has history
that far back, it lists `in:inbox newer_than:{InboxResyncWindow}` instead. Message ids already handed off are kept
in `{data}/nullbox.db`, so a message is enqueued once even when it shows up again. Polls never overlap and are not
retried (the next run carries on). Without a Gmail token the poll records "Gmail not configured" and does nothing.

Signed in as admin: `GET /inbox/status` shows the history id, pause flag and last result; `POST /inbox/pause` and
`POST /inbox/resume` (with an antiforgery token) stop and restart polling.

## Message pipeline

Each new message goes through every active rule set ([docs/rules.md](docs/rules.md)): nullbox fetches the message
(the body only when a rule set's `state` asks for it), asks Jev, picks the matching decisions and records the verdict
in `{data}/nullbox.db`. Gmail is only changed when all of these hold:

- the rule set's `mode` is `live` (the default for new rule sets; the example rule sets are `dry_run`, which only
  records what would happen);
- the kill switch is off (`Nullbox:Pipeline:KillSwitch`, or `POST /pipeline/kill-switch/on|off` at runtime);
- fewer than `Nullbox:Pipeline:MaxMessagesPerHour` messages were changed in the last hour.

A decision takes mail out of the inbox only through its `remove` switch ([docs/rules.md](docs/rules.md)): `archive`
adds the rule's review label `nullbox/review/<rule-set>/<rule>`, and `delete` moves to Trash only when the delete lock
is lifted (`Nullbox:Gmail:AllowDelete`). While it is on, a delete archives with the review label and is recorded as
blocked; when several decisions match, the gentlest removal wins. Starred and important mail is never marked read
or removed unless the decision sets `allow_protected`. The pipeline never deletes permanently.

A Jev budget that runs out, or a Gmail or Jev credential that needs fixing, puts the message off until later. Other failures are retried with Hangfire's growing
delay up to `MaxAttempts`, then the message is recorded as poison and left alone.

Signed in as admin: `GET /pipeline/status`, `GET /pipeline/decisions` and `GET /pipeline/poison`.

### Audit log and replay

Every verdict is a record in the `decisions` table: the message, rule set and version, mode and outcome, Jev's
answers and model, the matched decisions, the actions taken (or that would have been), steps held back, the
message's labels, a SHA-256 of the full state sent to Jev, and that state with the body cut to
`Nullbox:Audit:StoredBodyChars`. `confidence` is Jev's least sure answer (a choice or score's confidence, or
`max(p, 1 - p)` for a noul). A failed Jev call or Gmail change is recorded with outcome `error` and the message is
retried. Records older than `Nullbox:Audit:RetentionDays` are purged daily.

`GET /pipeline/decisions` filters with `messageId`, `ruleSet`, `version`, `action` (a recorded action starting with
it, e.g. `archive` or `label:nullbox/promo`), `mode`, `outcome`, `minConfidence` and `maxConfidence`, newest first.
It returns `{ items, next }`; pass `next` as `before` for the next page (`limit` up to 500).

`POST /pipeline/replay` (antiforgery token in the `RequestVerificationToken` header) re-evaluates past messages against a
draft rule set and returns what would change, without reading or changing Gmail or recording anything:

```json
{ "rule_set": { "name": "promo-cleanup", ... }, "sample": { "rule_set": "promo-cleanup", "version": 3, "message_ids": [], "limit": 50 } }
```

The sample (optional) defaults to the newest verdict per message of the draft's own rule set. When the draft asks
Jev the same thing as the recorded version (same model, state fields and questions), the stored answers are reused
and Jev is not called. Otherwise Jev is asked again with the stored inputs; a message whose record lacks a field the
draft needs is skipped, and a stored body is only the first `StoredBodyChars` characters.

## Cleanup rules

A port of emailBlaster's `cleanup.py`: bulk mark-read and archive of inbox mail by Gmail search, separate from the
Jev pipeline. Rule files live in `{data}/cleanup/<id>.json` (`./data/cleanup/` on the host) in emailBlaster's own
format, so its `cleanup_rules.json`, `cleanup_all_read.json` and `cleanup_all_unread.json` can be copied in as they are:

```json
{
  "label": "cleanup_unread",
  "keep": { "starred": true, "important": true, "senders": ["yourbank.com"] },
  "rules": [
    { "name": "Old promos", "query": "category:promotions older_than:30d", "mark_read": true, "archive": true }
  ]
}
```

Each rule's search becomes `in:inbox is:unread (<query>) -is:starred -is:important -from:(<senders>)`, exactly as
`build_query` builds it (`"unread_only": false` drops `is:unread`; the `keep` flags drop their exclusions). A message
matched by one rule isn't counted again by a later one. Matches are marked read and archived with the file's `label`
(or `cleanup_<yyyyMMdd>`, UTC). **Cleanup never deletes**, so the delete lock doesn't come into it.

Signed in as admin:

- `GET /cleanup/configs`: every file with the exact Gmail search of each rule, or why the file can't be read.
- `POST /cleanup/configs/{id}/run?sample=25` (antiforgery token): a **dry run**, counting matches per rule and the top
  senders of the first `sample` matches. Add `dryRun=false` to apply. Applying is refused (recorded as `Blocked`)
  while the pipeline kill switch is on; dry runs always go ahead.
- `GET /cleanup/runs` and `GET /cleanup/runs/{id}`: queued, running and finished runs with per-rule counts.

Differences from cleanup.py: the "type yes" prompt is the explicit `dryRun=false`; the default label uses the UTC date;
a rule with both `mark_read` and `archive` off makes no Gmail call (cleanup.py sends an empty modify).

## Run in Docker

nullbox is meant to run entirely in a container. The image is a multi-stage build (`sdk:10.0` → `aspnet:10.0`),
runs the app as a non-root user, listens on 8080 inside the container, and has a `HEALTHCHECK`
that calls `/healthz`. All state (SQLite database, Google token files) lives in the `/data` volume.

```sh
cp .env.example .env              # fill in ADMIN_PASSWORD, JEV_API_KEY, ...
docker compose up -d --build
curl http://localhost:8480/healthz   # -> Healthy
docker compose ps                    # STATUS shows (healthy)
```

- No `chown` needed: on start the container creates `./data` if missing, gives anything in it that the app user
  doesn't own to that user, then drops root before the app runs ([docker/entrypoint.sh](docker/entrypoint.sh)).
  The app user is 1654 by default; set `NULLBOX_UID`/`NULLBOX_GID` in `.env` to your `id -u`/`id -g` to keep
  `./data` editable without sudo. If you start it with `--user` or compose `user:`, you own the permissions, and
  the start-up error names the folder and the fix.
- Host port defaults to **8480**; set `NULLBOX_PORT` in `.env` to change it, and `NULLBOX_BIND` to a LAN IP to
  publish on one interface only.
- Put emailBlaster's `credentials.json` and `token.json` in `./data/google/`, or upload the token at
  `/settings/gmail` after signing in. See [docs/gmail-auth.md](docs/gmail-auth.md). `./data` and `.env`
  are gitignored and excluded from the build context.
- On the home server, use `deploy/docker-compose.yml`. It publishes on the LAN IP only, keeps nightly backups and
  caps memory at 512 MB. See [docs/deploy.md](docs/deploy.md) for the checklist, backup/restore, updates and remote access.
- Without Compose: `docker build -t nullbox:local .` then
  `docker run -d -p 8480:8080 -v "$PWD/data:/data" --env-file .env nullbox:local`.

Builds treat warnings as errors and enforce code style; run `dotnet format Nullbox.sln` before committing.
CI (`.github/workflows/ci.yml`) runs restore, format check, build and test on every PR and push to `main`, and a second job validates the
compose file, builds the image (no push) and checks the container comes up healthy as the non-root user.
