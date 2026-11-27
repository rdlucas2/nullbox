# Jev (TypeSafe System One) API — verified contract

Source: official docs, https://docs.typesafe.ai (index: `/llms.txt`; OpenAPI: https://api.typesafe.ai/openapi.json). Captured 2026-11-27.
Re-check the OpenAPI document when implementing; models/pricing change.

## Endpoint
`POST https://api.typesafe.ai/v1/systemone` — `Authorization: Bearer $TYPESAFE_API_KEY`, `Content-Type: application/json`.
`GET /v1/models` lists model names/aliases. Optional gzip/zstd request bodies. Max body 32 MiB; JSON nesting <= 64 levels.

## Vocabulary (note: "noul", not "noun")
A request = one **state** (string | object | array; here: the email) + named **questions**. Question names are ours; answers come back under the same names.
Three question primitives matter for email (a fourth, `box`, is image-only and out of scope):

| type | asks | answer fields |
|---|---|---|
| `choice` | pick one option | `choice`, `probabilities{option:p}`, `confidence` |
| `score` | rate on ordered levels | `score` (float, e.g. 1.7), `probabilities{"0":p,...}`, `legend{"0":text,...}`, `confidence` |
| `noul` | yes/no statement | `noul` (0–1 probability of yes; no confidence) |

All questions in a request are evaluated in parallel against the same state; adding questions barely changes latency or (per docs) cost much, so **one request per email, many questions**.
`instructions` and every `criteria` value may be a string, object, array or null (structured JSON allowed).

## Request
```json
{
  "model": "jev-latest",
  "state": { "from": "...", "subject": "...", "snippet": "...", "body": "...", "labels": ["INBOX"], "age_days": 2 },
  "questions": {
    "category": { "type": "choice", "instructions": "What kind of email is this?",
                  "criteria": { "receipt": "Purchase or payment confirmation", "promo": "Marketing", "personal": "From a real person" } },
    "urgency":  { "type": "score", "instructions": "How urgent is this?",
                  "criteria": ["Can wait", "Needs attention this week", "Needs attention today"] },
    "is_phishing": { "type": "noul", "instructions": "This message looks like phishing",
                     "criteria": { "true": "...", "false": "..." } }
  }
}
```
`noul.criteria` is optional `{true, false}`. Choice `criteria` is `{option: description}`. Score `criteria` is an ordered array of level descriptions.

## Response
```json
{ "model": "jev-1.14.0",
  "answers": {
    "category": { "type": "choice", "choice": "promo", "confidence": 0.7, "probabilities": { "receipt": 0.1, "promo": 0.8, "personal": 0.1 } },
    "urgency":  { "type": "score", "score": 1.7, "confidence": 0.55, "legend": { "0": "Can wait", "1": "...", "2": "..." }, "probabilities": { "0": 0.1, "1": 0.1, "2": 0.8 } },
    "is_phishing": { "type": "noul", "noul": 0.02 } },
  "usage": { "input_tokens": 120, "output_tokens": 12 } }
```
Log `model` (alias `jev-latest` -> versioned id, e.g. `jev-1.14.0`) per decision: answers can shift when the alias moves, so thresholds should be tied to a version.

## Limits, errors, cost
- Context: 64k tokens per request / 32k per question (header `TypeSafe-Danger-Zone-Long-Context: true` raises to 262k; slower/less accurate). **Truncate email bodies.**
- Price (jev-1.14.0): $0.042 per million input tokens; output free. Billing is on input tokens — use `usage.input_tokens` for the budget guard.
- Errors: 400 (rules / `max_tokens_exceeded`), 401, 413, 415, 422 (validation, details in `detail[]`), 429 (rate limit; honor `retry-after`), 529 (overloaded). Retry 429/529 with exponential backoff.
- Rate limits are per organization (console.typesafe.ai).
- Not trained on customer data; ZDR for enterprise (see /legal).

## Patterns worth using (docs `/patterns`)
- **Routing by confidence / probability**: riskier actions (trash) need higher thresholds than reversible ones (label). Choice/score also return `confidence`.
- **Companion noul**: pair a choice with a noul "does any option fit?" so we can detect "none of these".
- **Screen with a noul first**: cheap yes/no gate before other questions.
- **Composite scoring**: several atomic scores combined with weights in our code.
- Keep questions atomic, gut-check sized; decision logic lives in our code, not in the model.

## Official SDKs
Python and JS/TS only; **no .NET SDK** -> we write a small typed `HttpClient` (this contract is small).
