---
product: vnext-docs
milestone: v0.0.104
status: pending
---

# Human-task `dueBy` and `priority` — Handoff for vnext-docs

## Context

- vNext PR: the human-task dueBy/priority PR of milestone v0.0.104
- Milestone: v0.0.104
- Issue: none

## What changed in the runtime

Each row of `GET /api/v1/{domain}/functions/human-task` now carries two optional fields beside
`title` and `description`, read from the same place: the `humanTask` object at the root of the
**leaf** instance's latest data. Source of truth when this page and the engine disagree:
`HumanTaskLeafResolver.ReadHumanTask` (`src/BBT.Workflow.Application/Instances/HumanTask/`) and the
"Response body" section of `docs/runtime/human-task-function.md`.

- `humanTask.dueBy` — an ISO 8601 date (`2026-11-10`) or date-time (`2026-11-10T09:30:00+03:00`)
  string. Served as `dueBy`, a date-time with offset.
- `humanTask.priority` — a JSON number or a numeric string (`"1.5"`). Served as `priority`, a number.
- Both are optional. Absent, or of the wrong shape (`"next week"`, an object), they are served as
  `null`; the row is still listed.
- The body is still a bare JSON array; the change is additive.

## What this product must do

- `docs/how-to/human-task-approval.md` § 3 (`humanTask` data block): add `dueBy` and `priority` to the
  example block and the field table (type, optional), and the rule that a malformed value reads as
  `null` without dropping the task.
- Same page, response section: add `dueBy` and `priority` to the response example and the field table.

## Contract and examples

Instance data:

```json
{
  "humanTask": {
    "title": "50.000 TL EFT onayı",
    "description": "Ahmet Yılmaz — TR33 0006 ...",
    "dueBy": "2026-11-10T17:00:00+03:00",
    "priority": 1
  }
}
```

Response row:

```json
{
  "instanceId": "eft-2026-000123",
  "id": "3f2c9a1e-5b7d-4c1a-9e2f-0a1b2c3d4e5f",
  "workflow": "payment-order",
  "title": "50.000 TL EFT onayı",
  "description": "Ahmet Yılmaz — TR33 0006 ...",
  "dueBy": "2026-11-10T17:00:00+03:00",
  "priority": 1,
  "createdAt": "2026-09-25T09:12:31Z",
  "vNext": true
}
```

## Acceptance

- Both tables of the page list `dueBy` and `priority` as optional, with their accepted shapes.
- Both JSON examples show the two fields.

## Out of scope

- No ordering by `dueBy` or `priority`: the list is still sorted by `createdAt`, newest first.
