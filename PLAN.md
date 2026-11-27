# nullbox — Gmail inbox helper (Hangfire + Jev)

A Linux container running the latest .NET (10 LTS is the target; confirm at scaffold time) that:

1. runs **Hangfire** as the scheduler,
2. periodically queries a Gmail inbox,
3. uses **TypeSafe Jev** to classify each message (choices, scores, yes/no judgments),
4. performs inbox actions (label, mark read, archive, trash) based on the result,
5. provides a **web UI** to create and edit the Jev rule/schema JSON on the fly.

The action layer is a .NET port of the existing Python tool, [emailBlaster](https://github.com/rdlucas2/emailblaster).

## Jev (verified)

Contract captured from the official docs in [`docs/jev-api.md`](docs/jev-api.md): `POST https://api.typesafe.ai/v1/systemone`,
Bearer auth, model `jev-latest`. One request per email: `state` (the email as JSON) + named **questions** of three kinds —
**choice** (pick one; returns probabilities + confidence), **score** (ordered levels; returns float score + confidence), and
**noul** (yes/no; returns P(yes)). Priced per input token (~$0.042 / Mtok), 64k context. No .NET SDK, so we ship a typed `HttpClient`.
Jev supplies judgments only; **our code owns the decision logic** (thresholds, routing, actions).

Rule model (maps the user's "nouls, scores, choices"): a **RuleSet** = `state` template (which email fields are sent) +
`questions` (choice/score/noul definitions, in Jev's own request shape so the editor builds real Jev JSON) +
`decisions` (conditions over answers, e.g. `category == promo && confidence >= 0.8`, or `is_phishing >= 0.9`) → actions.
Everything Jev-specific sits behind `IJevClient`, so the rest can be built with a fake.

## Source material: emailBlaster capabilities to port

| Python | Capability | .NET home |
|---|---|---|
| `main.py` `authenticate_gmail` | OAuth desktop flow, token.json | `GmailAuth` (web-based OAuth flow, see below) |
| `main.py` `search_messages` | list + get From/Subject | `IGmailService.SearchAsync` |
| `main.py` `batch_delete_messages` | batchDelete, 1000/chunk | `IGmailService.BatchDeleteAsync` |
| `main.py` `mark_as_read` | batchModify remove UNREAD | `IGmailService.ModifyAsync` |
| `main.py` `archive_all_mail`, `get_or_create_label` | remove INBOX, add label | `IGmailService.EnsureLabelAsync` + modify |
| `cleanup.py` | rule file → query → mark read + archive, keep-list, dry run, dedupe across rules | `CleanupRuleRunner` (Gmail-query rules, preserved) |
| `create_filters.py` / `filters.json` | create Gmail server-side filters (needs settings scope) | `GmailFilterSync` |
| `batch_archive.py`, `one_thousand_batch.py` | bulk archive / capped bulk delete | bulk action jobs |

Design principle carried over: **never delete by default, always dry-run first, always label what was archived.**

## Architecture

Single ASP.NET Core app (one container) — keeps ops simple:

- **Hangfire** (server + dashboard at `/hangfire`, auth-protected), storage: SQLite file on a mounted volume
  (Hangfire.Storage.SQLite) — swap to Postgres later if needed.
- **Recurring job `InboxPoll`** (cron configurable in UI): fetch new messages since last history ID → enqueue one
  `ClassifyMessage` job each → apply actions.
- **Gmail layer**: `Google.Apis.Gmail.v1`; incremental sync via `history.list`, fallback to query.
- **Jev layer**: `IJevClient` (HTTP, typed, retry/backoff, cost guard, response cache keyed by message+schema hash).
- **Rules**: JSON documents stored in the DB, versioned, validated against a JSON Schema; each rule set = state template + questions (choices, scores, nouls)
  + decision table mapping Jev outputs → actions (label X, mark read, archive, trash, star, none) with thresholds.
- **UI**: Blazor (server) inside the same app, or React SPA if preferred — **Blazor (interactive server)** — decided.
- **Audit log**: every message decision (inputs hash, Jev output, action taken, dry-run flag) so rules can be tested/replayed.
- **Hosting**: docker compose on the home Ubuntu server (alongside Home Assistant/Jellyfin), LAN-only, remote access via VPN; see M4-19.
- **Container**: multi-stage Dockerfile on `mcr.microsoft.com/dotnet/aspnet` (latest), non-root, healthcheck, volume `/data`,
  docker-compose with secrets for Google client + Jev API key.

### Auth note (fixes emailBlaster's open TODO)
Reuse emailBlaster's existing **desktop OAuth client**: mount `credentials.json` + `token.json` into `/data/google/`.
A refresh token needs no browser, so the container just refreshes it (and writes it back). Initial consent still happens on a
machine with a browser (Google removed the OOB flow; device flow disallows Gmail scopes) — either emailBlaster's existing
tokens or a small `nullbox-auth` loopback helper. No web OAuth flow / HTTPS redirect needed. Caveat: consent screens in
"Testing" expire refresh tokens after 7 days. Existing token scope is `mail.google.com`; filter sync uses a separate
`token_filters.json` (`gmail.settings.basic`), as in emailBlaster.

### Safety defaults
- **Archive first** (decided 2026-11-27, replaces dry-run-by-default): a new rule acts right away, but only archives,
  adding a review label `nullbox/review/<rule-set>/<rule>`. Each rule has an Archive | Delete switch, Archive by
  default; flipping it to Delete is an explicit, confirmed UI action (#36). `dry_run` stays available.
- **App-wide delete lock** `Nullbox:Gmail:AllowDelete`, off by default: Gmail's Trash and permanent delete are refused
  for every caller, and Delete rules archive instead (#35).
- Delete means Trash (recoverable 30 days); permanent delete is never available to rules, and for bulk jobs it is
  opt-in (`AllowPermanentDelete`, plus the lock lifted) and capped per run.
- Per-run action caps and a global kill switch (pause recurring job).
- Never log message bodies; send Jev only the email fields the rule set's state template names, with bodies truncated.

## Milestones

**M0 Foundation** — scaffold, container, CI.
**M1 Gmail + Hangfire** — auth, polling, port of emailBlaster actions, query-based cleanup rules. Useful on its own, no Jev.
**M2 Jev** — spike, client, rule model, evaluator, decision engine, dry-run + audit.
**M3 UI** — rule list/editor (choices/scores/nouls builder), test-against-real-message panel, audit/dry-run viewer, job controls.
**M4 Hardening** — metrics, docs, secrets, backup.

Dependency order is encoded in the issues (each lists "Depends on"). Issues are sized for one autonomous loop iteration (≈ one PR).

## Working agreement for the scheduled loop
- Pick the lowest-numbered open issue whose dependencies are closed; label it `in-progress`.
- One branch + draft PR per issue; tests required; update this plan if a decision changes.
- No live Gmail/Jev calls in tests — use fakes. Real-credential verification is a manual `needs-human` step.
- Issues labelled `needs-human` (credentials, Google Cloud console, Jev account) are skipped by the loop.
