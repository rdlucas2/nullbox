# nullbox

Gmail inbox helper: a Linux container running .NET + Hangfire that polls Gmail on a schedule, uses
[TypeSafe Jev](https://docs.typesafe.ai) to classify mail, and acts on it (label / mark read / archive / trash),
with a web UI to build and edit the Jev rules (choices, scores, nouls) on the fly.

See [PLAN.md](PLAN.md) for architecture and milestones, [docs/jev-api.md](docs/jev-api.md) for the Jev contract,
and the GitHub issues for the work breakdown. Gmail actions are a port of [emailBlaster](https://github.com/rdlucas2/emailblaster).

## Layout

| Path | What lives there |
|---|---|
| `src/Nullbox.Web` | ASP.NET Core host: endpoints, Hangfire, UI (later milestones) |
| `src/Nullbox.Core` | Domain model, rule model, interfaces (`IGmailService`, `IJevClient`, ...) |
| `src/Nullbox.Gmail` | Gmail API implementation |
| `src/Nullbox.Jev` | TypeSafe Jev HTTP client |
| `tools/Nullbox.Auth` | `nullbox-auth`: one-time Google consent on a PC, writes `token.json` ([docs/gmail-auth.md](docs/gmail-auth.md)) |
| `tests/Nullbox.Tests` | xUnit tests (unit + in-process integration via `WebApplicationFactory`) |

## Build and run

Requires the .NET 10 SDK (pinned in `global.json`, any 10.0 feature band works).

```sh
dotnet build Nullbox.sln
dotnet test Nullbox.sln
dotnet run --project src/Nullbox.Web
```

The app exposes `GET /healthz` (returns `Healthy`, no login needed) and `GET /` (app name and version).

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
| `Nullbox:Jobs:HeartbeatCron` | `*/5 * * * *` | No-op heartbeat job (UTC) that proves the scheduler runs. |

## Jobs and dashboard

Hangfire runs inside the web app. Sign in at `/login` with `ADMIN_PASSWORD` to open the dashboard at `/hangfire`,
where the `heartbeat` recurring job shows up and can be triggered by hand. Jobs are registered through
`IJobScheduler` (in `Nullbox.Core`), so nothing outside `Nullbox.Web` references Hangfire.

## Run in Docker

nullbox is meant to run entirely in a container. The image is a multi-stage build (`sdk:10.0` → `aspnet:10.0`),
runs as the non-root `app` user (UID 1654), listens on 8080 inside the container, and has a `HEALTHCHECK`
that calls `/healthz`. All state (SQLite database, Google token files) lives in the `/data` volume.

```sh
cp .env.example .env              # fill in ADMIN_PASSWORD, JEV_API_KEY, ...
mkdir -p data/google
sudo chown -R 1654:1654 data      # the container user must own the bind-mounted ./data
docker compose up -d --build
curl http://localhost:8480/healthz   # -> Healthy
docker compose ps                    # STATUS shows (healthy)
```

- Host port defaults to **8480**; set `NULLBOX_PORT` in `.env` to change it, and `NULLBOX_BIND` to a LAN IP to
  publish on one interface only.
- Put emailBlaster's `credentials.json` and `token.json` in `./data/google/`, or upload the token at
  `/settings/gmail` after signing in. See [docs/gmail-auth.md](docs/gmail-auth.md). `./data` and `.env`
  are gitignored and excluded from the build context.
- Without Compose: `docker build -t nullbox:local .` then
  `docker run -d -p 8480:8080 -v "$PWD/data:/data" --env-file .env nullbox:local`.

Builds treat warnings as errors and enforce code style; run `dotnet format Nullbox.sln` before committing.
CI (`.github/workflows/ci.yml`) runs restore, format check, build and test on every PR and push to `main`, and a second job validates the
compose file, builds the image (no push) and checks the container comes up healthy as the non-root user.
