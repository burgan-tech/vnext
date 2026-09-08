# Agent Council — Decision Log

Chronological index of every council session. Session folders live under `ai-docs/agent-council/sessions/<task-id>/` (git-ignored, local to the machine that ran the council); this index is the only committed record. One row per session,
appended when `DECISION.md` is recorded (and updated if the verdict later changes). This is the
history: read it first to find out whether a question has already been decided, then open the
session folder for the evidence, dissent and conditions.

Session folders are NOT committed (team decision 2026-09-07). Keep the verdict, selected approach and follow-up in this row rich enough to stand alone;
the folder link below resolves only on the machine that ran the session.

| Date | Session | Topic | Risk | Verdict | Selected approach | Follow-up |
|------|---------|-------|------|---------|-------------------|-----------|
| 2026-09-07 | [2026-09-07-sse-push-channel](../../../ai-docs/agent-council/sessions/2026-09-07-sse-push-channel/DECISION.md) | Add SSE as a push channel for instance state changes? | High | `EXPERIMENT_REQUIRED` (Awaiting Chair) | No SSE now; poll never retired; body-over-stream and broker-ACL export rejected; transport-independent `instance-changed` pointer contract approved in principle; experiments E1–E3 + bounded ownership clarification C2 before a transport session | — |
<!-- newest at the bottom; keep one line per session -->

Verdicts: `APPROVED` · `APPROVE_WITH_CONDITIONS` · `EXPERIMENT_REQUIRED` · `CLARIFICATION_REQUIRED` · `BLOCKED` · `REJECTED`.
`Follow-up` links the implementation PR/issue, the superseding session, or `—`.
