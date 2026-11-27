# Rule sets

A rule set tells nullbox what to ask Jev about each email and what to do with the answers. It is one JSON document,
checked against [`ruleset.schema.json`](../src/Nullbox.Core/Rules/ruleset.schema.json) (served by the app at
`/schemas/ruleset.schema.json`). The two examples added on first start live in
[`src/Nullbox.Core/Rules/Seeds`](../src/Nullbox.Core/Rules/Seeds).

```json
{
  "name": "promo-cleanup",
  "mode": "dry_run",
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
      "actions": ["label:nullbox/promo", "mark_read", "archive"] }
  ]
}
```

| Key | Meaning |
|---|---|
| `name` | Identity: lowercase letters, digits, `-`, `_` (up to 64). Saving under a new name duplicates the rule set. |
| `mode` | `dry_run` (default) records decisions only; `live` applies them. |
| `state.fields` | Email fields sent to Jev, in order: `from`, `subject`, `snippet`, `body`, `labels`, `age_days`, `has_unsubscribe`, `date`. |
| `state.body_max_chars` | The body is cut to this length (only sent if `body` is listed). |
| `questions` | Jev questions in Jev's own request shape (see [jev-api.md](jev-api.md)): `choice`, `score`, `noul`. Names: lowercase letters, digits, `_`. |
| `match` | `first`: the first matching decision wins. `all`: every matching decision applies. |
| `decisions` | Ordered `when` → `actions` rules. A decision may set `allow_protected: true` (see Actions). |

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

`label:<name>` (created if needed), `mark_read`, `archive`, `trash` (recoverable, never a permanent delete), `star`,
and `none` (match and stop without changing anything; can't be combined).

With `match: all`, the actions of every matching decision are combined, each once, and applied in a fixed order:
labels, star, mark read, archive, then trash. While the app-wide delete lock is on (`Nullbox:Gmail:AllowDelete`
is false, the default), `trash` archives instead and the blocked delete is recorded as skipped.

Starred and important mail is protected: `mark_read`, `archive` and `trash` are held back (and recorded as skipped)
unless the matching decision sets `"allow_protected": true`. Labels and stars only add, so they still apply.

Nothing changes in Gmail unless the rule set's `mode` is `live`, the kill switch is off and the hourly cap isn't
reached; see the README's "Message pipeline" section.

## Versions

Every save that changes the document adds a new immutable version; saving identical content does not. The pipeline
uses the **active** version, which only changes when a version is activated. Export is the stored JSON; import runs
the same checks as a save.
