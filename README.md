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
| `Nullbox:DataDirectory` / `NULLBOX_DATA_DIR` | `data` | SQLite databases and Google tokens (`/data` in the container). |
| `Nullbox:Hangfire:Storage` | `SQLite` | `SQLite` (file `hangfire.db` in the data directory) or `InMemory` (tests). |
| `Nullbox:Hangfire:WorkerCount` | `2` | Hangfire worker threads. |
| `Nullbox:Hangfire:DashboardPath` | `/hangfire` | Dashboard URL, behind the admin login. |
| `Nullbox:Jobs:HeartbeatCron` | `*/5 * * * *` | No-op heartbeat job (UTC) that proves the scheduler runs. |

## Jobs and dashboard

Hangfire runs inside the web app. Sign in at `/login` with `ADMIN_PASSWORD` to open the dashboard at `/hangfire`,
where the `heartbeat` recurring job shows up and can be triggered by hand. Jobs are registered through
`IJobScheduler` (in `Nullbox.Core`), so nothing outside `Nullbox.Web` references Hangfire.

Builds treat warnings as errors and enforce code style; run `dotnet format Nullbox.sln` before committing.
CI (`.github/workflows/ci.yml`) runs restore, format check, build and test on every PR and push to `main`.
