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

The app exposes `GET /healthz` (returns `Healthy`) and `GET /` (app name and version).

Builds treat warnings as errors and enforce code style; run `dotnet format Nullbox.sln` before committing.
CI (`.github/workflows/ci.yml`) runs restore, format check, build and test on every PR and push to `main`.
