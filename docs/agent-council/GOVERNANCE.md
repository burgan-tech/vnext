# Agent Council Governance

## Risk Policy

| Risk | Typical scope | Council | Chair |
|---|---|---|---|
| Low | Local refactor or isolated test | Selection rules | Inform |
| Medium | API, cache or concurrency behavior | Selection rules | Policy exceptions |
| High | Cross-service contract, data model or authorization | Selection rules | Required |
| Critical | Production migration, security boundary or high blast radius | All applicable roles plus human expert | Required double check |

`COUNCIL-SELECTION.md` is the source of truth for council size.

## Decision Scoring

Hard constraints must pass before scoring. Score each criterion from 0 to 5 and normalize using `Total = sum(score / 5 * weight)`. Use the highest valid total; break ties with reversibility, then simplicity. Escalate unresolved ties.

## Separation Of Duties

The proposal author cannot be the sole decision maker. The implementer cannot be the final reviewer. Security-sensitive work requires Security Guardian. Chair gates cannot be skipped. State transitions and approvals are recorded with actor, UTC time, authority, and evidence.

## Access And Audit

Task records must define allowed tools, repository paths, environments, data classes, read/write scope, credential source, redaction rules, egress restrictions, grantor, and expiry. Missing or expired access blocks the session. Audit events include event ID, actor/authority, action, target, environment, result, before/after state, approval/evidence reference, and retention class.
