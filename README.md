# nullbox

Gmail inbox helper: a Linux container running .NET + Hangfire that polls Gmail on a schedule, uses
[TypeSafe Jev](https://docs.typesafe.ai) to classify mail, and acts on it (label / mark read / archive / trash),
with a web UI to build and edit the Jev rules (choices, scores, nouls) on the fly.

See [PLAN.md](PLAN.md) for architecture and milestones, [docs/jev-api.md](docs/jev-api.md) for the Jev contract,
and the GitHub issues for the work breakdown. Gmail actions are a port of [emailBlaster](https://github.com/rdlucas2/emailblaster).
