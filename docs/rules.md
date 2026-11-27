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

## Dry run, then archive, then delete: the `remove` switch

Taking mail out of the inbox is not an action; it is each rule's **Dry run | Archive | Delete** switch, so a rule can
only delete when you have promoted it on purpose.

| `remove` | What happens to matching mail |
|---|---|
| `dry_run` | Nothing changes, even in a live rule set. The match is recorded, and the rule's actions show as held back in the audit log. New rules in the editor start here. |
| omitted | Stays in the inbox (labels, mark read and star still apply). |
| `archive` | Leaves the inbox and gets the review label `nullbox/review/<rule-set>/<rule>`. |
| `delete` | Moves to Trash (Gmail keeps it 30 days). Rules never delete permanently. |

Start a rule on `dry_run` and watch what it matches in the audit log. Promote it to `archive` and look through its
review label in Gmail, then to `delete` once you trust it. A dry-run rule is recorded but never takes the first
match, so with `match: first` the rules below it still act as before: adding a rule on trial changes nothing. The audit
log shows the dry-run match next to the rule that did act, which is what the new rule will take over once promoted.

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

## Examples

**Archive newsletters, label receipts, never touch people.** First match wins, so the screen goes first:

```json
{
  "name": "inbox-tidy",
  "questions": {
    "from_a_person": { "type": "noul", "instructions": "A real person wrote this email to the recipient." },
    "kind": { "type": "choice", "instructions": "What kind of email is this?",
              "criteria": { "newsletter": "A newsletter or digest the recipient subscribed to",
                            "receipt": "A purchase, payment or shipping confirmation",
                            "other": "Anything else" } }
  },
  "match": "first",
  "decisions": [
    { "name": "Leave people alone", "when": "from_a_person.noul >= 0.5", "actions": ["none"] },
    { "name": "Archive newsletters", "when": "kind.choice == \"newsletter\" && kind.confidence >= 0.85",
      "actions": ["mark_read"], "remove": "dry_run" },
    { "name": "Label receipts", "when": "kind.choice == \"receipt\" && kind.confidence >= 0.7",
      "actions": ["label:Receipts"] }
  ]
}
```

"Archive newsletters" starts as a dry run, like every new rule: once the rule set is active it records what it would
archive and changes nothing (the receipts rule below it still runs). When the audit log shows only newsletters,
promote it to `archive`; matching mail then gets the label `nullbox/review/inbox-tidy/archive-newsletters`. Check that
label in Gmail for a week, then switch it to Delete (and lift the delete lock if you want it to actually trash).

**Flag likely phishing without moving it.** A rule with only a label never leaves the inbox:

```json
{ "name": "Flag phishing", "when": "is_phishing.noul >= 0.9", "actions": ["label:nullbox/suspicious", "star"] }
```

Rules can only test Jev's answers. For plain sender or search rules ("everything from spam.example"), use a cleanup file
(`/cleanup`) or a Gmail filter instead.

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
  clause builder appends new comparisons. The switch offers Dry run (the default for a new rule), Keep in inbox,
Archive and Delete. Switching a decision to Delete asks first, says matching mail goes to Trash
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
that. Leaving the page with unsaved changes asks first. New rule sets made in the editor are live, and each new rule in
them starts on Dry run.

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
