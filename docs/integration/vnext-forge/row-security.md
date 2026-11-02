---
product: vnext-forge
milestone: v0.0.102
status: pending
---

# Row security (`attributes.rowSecurity`) — Handoff for vnext-forge

## Context

- vNext PR: the row-security PR of milestone v0.0.102 (burgan-tech/vnext#1107 is the issue)
- Milestone: v0.0.102
- Issue: <https://github.com/burgan-tech/vnext/issues/1107>

## What changed in the runtime

A workflow can declare `attributes.rowSecurity.rules`: role-keyed row filters that the runtime ANDs into
the instance list, the single instance GET and the GetInstances / GetInstance tasks. A flow with
`rowSecurity` is deny by default; a flow without it is not filtered. Source of truth:
[docs/domain/row-security.md](../../domain/row-security.md) and the `row-security` fragment in
`vnext-meta/unreleased/row-security.json`. The JSON shape is in vnext-schema
(`workflow.attributes.rowSecurity`).

## What this product must do

- Offer a `rowSecurity` editor on the workflow attributes: a list of rules, each with `roles`
  (non-empty string array; static roles, `$InstanceStarter`, `$InstanceBehalfOfStarter`) and an optional
  `filter` (list-engine filter JSON). A rule without `filter` means "these roles see every row".
- Offer parameter references in filter values: `$.context.Headers.<name>`,
  `$.context.QueryParameters.<name>`, `$.context.CurrentUser.<field>`, and the
  `{ "$param": "...", "split": "," }` form for `in` / `nin` / `between`.
- Show the publish result: errors `RS-E*` reject the publish; warnings `RS-W*` are log-only today, so a
  designer-side hint for the common ones (`RS-W2c` only `ne`/`nin`, `RS-W7` SubFlow state under a
  filtered flow) is useful.
- State in the editor that there is no `default` rule: an unmatched role sees nothing.

## Contract and examples

```json
"rowSecurity": {
  "rules": [
    { "roles": ["branch-staff"],
      "filter": { "attributes": { "branchCode": { "eq": "$.context.Headers.x-branch-code" } } } },
    { "roles": ["hq-auditor"] },
    { "roles": ["customer"], "filter": { "createdBy": { "eq": "$InstanceStarter" } } }
  ]
}
```

Publish codes and their meaning: [row-security § Publish validation](../../domain/row-security.md#publish-validation).

## Acceptance

- The example above round-trips through the designer unchanged and publishes without `RS-E*`.
- A rule with an empty `roles` array or a member other than `rules` is flagged before publish.

## Out of scope

- Previewing which rows a role would see (needs a live runtime and the caller's headers).
