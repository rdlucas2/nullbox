# Rule sets

A rule set tells nullbox what to ask Jev about each email and what to do with the answers. It is one JSON document,
checked against [`ruleset.schema.json`](../src/Nullbox.Core/Rules/ruleset.schema.json) (served by the app at
`/schemas/ruleset.schema.json`). The two examples added on first start live in
[`src/Nullbox.Core/Rules/Seeds`](../src/Nullbox.Core/Rules/Seeds).

```json
{
  "name": "promo-cleanup",
  "mode": "live",
  "model": "jev-latest",
  "state": { "fields": ["from", "subject", "snippet", "labels", "age_days", "has_unsubscribe"], "body_max_chars": 2000 },
  "questions": {
    "category": { "type": "choice", "instructions": "What kind of email is this?",
                  "criteria": { "promo": "Marketing", "receipt": "Purchase confirmation", "personal": "From a real person" } }
  },
  "match": "first",
  "decisions": [
    { "name": "Archive confident promos",
      "when": "category.choice == \"promo\" && category.confidence >= 0.85",
      "actions": ["label:nullbox/promo", "mark_read"],
      "remove": "archive" }
  ]
}
```

| Key | Meaning |
|---|---|
| `name` | Identity: lowercase letters, digits, `-`, `_` (up to 64). Saving under a new name duplicates the rule set. |
| `mode` | `live` (default) applies decisions; `dry_run` only records them. The example rule sets start as dry runs. |
| `state.fields` | Email fields sent to Jev, in order: `from`, `subject`, `snippet`, `body`, `labels`, `age_days`, `has_unsubscribe`, `date`. |
| `state.body_max_chars` | The body is cut to this length (only sent if `body` is listed). |
| `questions` | Jev questions in Jev's own request shape (see [jev-api.md](jev-api.md)): `choice`, `score`, `noul`. Names: lowercase letters, digits, `_`. |
| `match` | `first`: the first matching decision wins. `all`: every matching decision applies. |
| `decisions` | Ordered rules: `name`, `when` → `actions`, and the `remove` switch. A rule needs at least one action or `remove`. A decision may set `allow_protected: true` (see Actions). |

## Conditions (`when`)

Comparisons on answers, joined with `&&`, `||`, `!` and parentheses (`&&` binds tighter than `||`).
Text values go in double quotes; numbers are plain.

| Question type | Fields |
|---|---|
| `choice` | `q.choice == "option"` (or `!=`), `q.confidence`, `q.probabilities.<option>` |
| `score` | `q.score` (0 to levels − 1), `q.confidence`, `q.probabilities.<level>` (levels numbered from 0) |
| `noul` | `q.noul` (probability of yes, 0 to 1) |

Saving checks that every condition names an existing question, field and option, and that thresholds are in range.
A comparison on a missing answer is never true (not even under `!`), so a missing output can't trigger an action.

Patterns from the Jev docs work as ordinary conditions:

- **Screen with a noul first**: put `from_a_person.noul >= 0.5` → `none` as the first decision with `match: first`.
- **Companion noul**: ask `fits_category` alongside a choice and require `fits_category.noul >= 0.7`.
- **Riskier actions need higher thresholds**: label at 0.6, archive at 0.85.

## Actions

`label:<name>` (created if needed), `mark_read`, `star`, and `none` (match and stop without changing anything; can't be
combined with other actions or with `remove`).

## Archive first: the `remove` switch

Taking mail out of the inbox is not an action; it is each rule's **Archive | Delete** switch, so a rule can only
delete when you have flipped it on purpose.

| `remove` | What happens to matching mail |
|---|---|
| omitted | Stays in the inbox (labels, mark read and star still apply). |
| `archive` | Leaves the inbox and gets the review label `nullbox/review/<rule-set>/<rule>`. New rules in the editor start here. |
| `delete` | Moves to Trash (Gmail keeps it 30 days). Rules never delete permanently. |

Try a rule on `archive`, look through its review label in Gmail, and flip it to `delete` once you trust it.

The review label is named after the rule (lowercase, `-` between words, accents dropped), or `rule-<n>` for an unnamed
rule, so name your rules to keep the label when you reorder them. Two rules that remove mail can't share a label.

While the app-wide **delete lock** is on (`Nullbox:Gmail:AllowDelete=false`, the default), a `delete` rule archives
with its review label instead, and nothing is moved to Trash.

Rule sets saved before the switch existed used `archive` and `trash` actions. On startup both are converted to
`"remove": "archive"` (trash rules go back to archive-only), with a warning in the log naming each one.

With `match: all`, the actions of every matching decision are combined, each once, and applied in a fixed order:
labels (including review labels), star, mark read, then removal. If any matching decision archives, the message is
archived; it goes to Trash only when a matching decision is set to `delete` and the delete lock is lifted. A
`delete` blocked by the lock is recorded as skipped.

Starred and important mail is protected: `mark_read` and `remove` are held back (and recorded as skipped) unless the
matching decision sets `"allow_protected": true`. Labels and stars only add, so they still apply; a held-back removal
also skips its review label.

Nothing changes in Gmail unless the rule set's `mode` is `live`, the kill switch is off and the hourly cap isn't
reached; see the README's "Message pipeline" section.

## Versions

Every save that changes the document adds a new immutable version; saving identical content does not. The pipeline
uses the **active** version, which only changes when a version is activated. Export is the stored JSON; import runs
the same checks as a save.

## Editing in the UI

`/rules` lists every rule set with its active version, its mode, and how many decisions of that version are set to
Delete. From there you can make a new rule set, edit, duplicate (saved as `<name>-copy`, not activated), export the
JSON, delete a rule set with all its versions (its decision log is kept), or import a JSON file or pasted JSON (saved
as the next version of the rule set it names, not activated).

The editor at `/rules/<name>` (or `/rules/new`) has a tab for each part of the document:

- **State**: which email fields Jev sees, the body length cap, and the state Jev would get for a sample email.
- **Choices, Scores, Nouls**: add, rename, reorder and remove questions, their options or levels, and instructions.
  Instructions and criteria are plain text, or JSON when they start with `{` or `[`.
- **Decisions**: the match mode, then each decision's `when`, actions and `remove` switch. A `when` made of
  comparisons joined by `&&` shows each comparison, with a slider for confidence, probability and noul thresholds; a
  clause builder appends new comparisons. Switching a decision to Delete asks first, says matching mail goes to Trash
  (purged by Gmail after 30 days) and links its review label in Gmail; it also says when the delete lock means it
  will still archive. Delete rules without a confidence threshold, or with one below 0.8, get a warning.
- **JSON**: the document as it would be saved, editable. Applying edited JSON updates the other tabs; JSON that
  fails the checks is not applied. Beside it is the exact request Jev would get for the sample email.
- **Test**: run the draft over real messages; see [Testing a rule set](#testing-a-rule-set).
- **Versions**: every saved version, with a line diff against the one before it, and a button that loads an old
  version back into the editor (save it to make it the newest version).

Validation errors (schema and references, with their JSON paths) are listed under the tabs and block saving. Saving
adds a version but doesn't activate it; **Activate** and **Switch to live / dry run** ask for confirmation and are
disabled while there are unsaved changes. Switching modes saves the active version with the new mode and activates
that. Leaving the page with unsaved changes asks first. New rule sets made in the editor start as dry runs.

## Testing a rule set

The editor's **Test** tab tests the draft as it stands, saved or not. `/test` tests the newest saved version of a
rule set you pick. Either way:

1. Pick the messages: the newest in the inbox, a Gmail search (e.g. `from:shop newer_than:7d`), or a sample of the
   messages in the decision log for a rule set. Up to `Nullbox:Tester:MaxMessages` (25) per run.
2. **Check cost** reads the messages and shows how many Jev calls and input tokens the run would take at most, next
   to today's Jev budget. A run that would go over the day's tokens or the hour's calls is refused before any call.
3. **Run in dry run** asks Jev and shows, for each message, the state sent, Jev's answers, the decisions that
   matched and the actions they would take.

Nothing is changed in Gmail: the tester only has read access to it, and records nothing in the decision log. Jev's
answers are cached like the pipeline's, so running the same draft again over the same messages costs nothing.

With **Compare with the active version** ticked, each message also shows what the active version decides, and rows
where the actions differ are highlighted. The active version's verdict is taken from the decision log when it has
already decided that message; otherwise Jev is asked for it too, which the cost check counts.

## Saved rules

Some mail needs no judgement: a sender you always want gone, or mail to one address that always gets a label. Asking
Jev about it costs tokens and time and can come out wrong, so keep those as **saved rules** at `/saved-rules`.

```json
{
  "$schema": "/schemas/savedrules.schema.json",
  "rules": [
    { "name": "shop-deals", "mode": "live", "from": "@shop.example", "actions": ["label:shop"], "remove": "archive" },
    { "name": "newsletters", "to": "me+news@gmail.com", "subject_contains": "weekly", "actions": ["mark_read"], "stop": false }
  ]
}
```

| Key | Meaning |
|---|---|
| `name` | Identity: lowercase letters, digits, `-`, `_` (up to 64). Its audit key is `saved:<name>`. |
| `enabled` | `false` keeps the rule but never runs it. Default `true`. |
| `mode` | `dry_run` (the default, so new rules start here) records matches; `live` applies them. |
| `from` | The sender: an address (`deals@shop.example`) or a whole domain with its subdomains (`@shop.example`). |
| `to` | A recipient in To or Cc, written the same way. `me@gmail.com` also matches `me+anything@gmail.com`; `me+news@gmail.com` matches only itself. |
| `subject_contains` | Optional text the subject must contain. |
| `actions` | `label:<name>`, `mark_read`, `star`. |
| `remove` | `archive` (with the review label `nullbox/review/saved-rules/<name>`) or `delete` (Trash). |
| `stop` | When the rule is live and matches, skip the later saved rules and the Jev rule sets. Default `true`. |
| `allow_protected` | Allow `mark_read` and `remove` on starred or important mail. |

A rule needs `from`, `to` or both, and at least one action or `remove`. Every part it has must match, ignoring case.
Matching is on addresses only: no wildcards, regex or Gmail search syntax.

Saved rules run for each new inbox message before the rule sets, in the order listed. Each match is applied through
the same plan as a rule-set decision, so the delete lock, protected mail, the kill switch and the hourly cap all
apply, and each is a decision record with no Jev answers and confidence 1 (filter the Audit page by `saved:<name>`).
A dry-run rule never stops anything: the rule sets still run, so you can compare the two before going live.

The page lists each rule with its match, action, mode, on/off switch and hit count (the number of decision records,
linked to them on the Audit page). The editor has a form and a JSON view; switching a rule to Delete, in either,
asks first. Up and down arrows set the order. Export downloads every rule (`/api/saved-rules/export`); import
replaces rules with the same name in place and adds the others last, and stores nothing if any rule is invalid.
The test box shows which rules a sample From, To and Subject would match, in order, and where a stop cuts them off.

### Which tool to use

| Use | When |
|---|---|
| A saved rule | The sender or recipient alone settles it (always block this sender, always label mail to this address), with each match in the audit log. |
| A Jev rule set | Deciding needs judgement about what the mail says (promo or personal, worth reading or not). |
| A cleanup file | A one-off or scheduled sweep of mail that is already in the mailbox, by Gmail search ([cleanup](../README.md#cleanup-rules)). |
| A Gmail filter | Gmail should act the moment mail arrives, even when nullbox is down, and search syntax is enough ([filters](../README.md#filters-and-bulk-jobs)). |
