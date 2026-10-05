# Role Grants — a Worked Example Across Every Surface

One definition, eight callers, every place a role grant is evaluated. Read
[Role Grant Authorization](role-grant-authorization.md) for the rules themselves; this page shows what
they produce. Every verdict below follows from the code on the runtime that introduced `allOf` / `anyOf`
(0.0.99) and is exercised by the vnext-example scenarios `authorization-chain-lab`, `role-matrix-lab`
and `human-task-chain`.

## 0. The rules in one table

| Grant | Matches when |
|---|---|
| `{ "role": "x" }` | the caller holds `x` (or, for a predefined/dynamic value, the caller's identity matches) |
| `allOf: [A, B]` | A **and** B — each child is matched against the caller's **whole** role set and identity, so A may be met by a role and B by the identity |
| `anyOf: [A, B]` | A **or** B |

A grant set decides like this:

1. An empty set allows — except on the human-task list, where an empty set drops the task.
2. A `deny` that comes out **Yes** or **Unknown** refuses.
3. If the set has any `allow`, one of them must come out **Yes**.
4. A deny-only set (blacklist) whose denies did not fire allows.

**Role-less caller:** a role-bound leaf (static role or `$role.`) is **Unknown**; an identity leaf
(`$InstanceStarter`, `$PreviousUser`, `$InstanceBehalfOfStarter`, `$PreviousBehalfOfUser`, `$user.…`,
`$userBehalfOf.…`) is always a definite Yes/No. `allOf` is No as soon as one child is No; `anyOf` is Yes
as soon as one child is Yes; otherwise an Unknown child makes the result Unknown.

## 1. The definition

```jsonc
// Workflow: loan (root)
"queryRoles": [
  { "grant": "allow", "anyOf": [ { "role": "loan.officer" }, { "role": "loan.auditor" } ] },
  { "grant": "deny",  "role": "loan.blocked" }
],
"states": [
  { "key": "draft" },                                  // no queryRoles of its own → the root's apply
  { "key": "review", "subType": 6,                     // Human
    "queryRoles": [
      { "grant": "allow", "role": "loan.officer" },
      { "grant": "allow", "role": "loan.auditor" },
      { "grant": "deny",  "allOf": [ { "role": "loan.officer" }, { "role": "$InstanceStarter" } ] }
    ],
    "transitions": [
      { "key": "approve", "roles": [
          { "grant": "allow", "role": "loan.approver" },
          { "grant": "deny",  "allOf": [ { "role": "loan.approver" }, { "role": "$PreviousUser" } ] } ] },
      { "key": "request-docs", "roles": [
          { "grant": "allow", "anyOf": [ { "role": "loan.officer" }, { "role": "loan.approver" } ] } ] }
    ] },
  { "key": "signature",
    "interaction": { "longPoll": { "terminate": true, "roles": [
      { "grant": "allow", "allOf": [ { "role": "loan.customer" }, { "role": "$InstanceStarter" } ] } ] } } },
  { "key": "kyc", "stateType": 4,
    "subFlow": { "type": "S", "process": { "key": "kyc" },
      "overrides": {
        "states":      { "kyc-review":  { "queryRoles": [ { "grant": "allow", "anyOf": [ { "role": "kyc.analyst" }, { "role": "loan.officer" } ] } ] } },
        "transitions": { "kyc-approve": { "roles":      [ { "grant": "allow", "role": "kyc.senior" } ] } } } } }
],
"cancel": { "key": "cancel",
  "roles":       [ { "grant": "allow", "anyOf": [ { "role": "$InstanceStarter" }, { "role": "loan.officer" } ] } ],
  "availableIn": [ { "state": "review", "roles": [ { "grant": "allow", "role": "$InstanceStarter" } ] } ] }

// Child workflow: kyc → state kyc-review (Human)
//   queryRoles:                 [ allow kyc.analyst ]
//   transition kyc-approve.roles: [ allow kyc.analyst ]

// Master schema
"iban":         { "x-roles": [ { "grant": "allow", "anyOf": [ { "role": "$InstanceStarter" }, { "role": "loan.officer" } ] } ] },
"riskScore":    { "x-roles": [ { "grant": "allow", "role": "loan.approver" },
                               { "grant": "deny",  "allOf": [ { "role": "loan.approver" }, { "role": "$InstanceStarter" } ] } ] },
"internalNote": { "x-roles": [ { "grant": "deny",  "role": "loan.customer" } ] }   // blacklist
```

**Instance:** started by ALİ (`CreatedBy = u-ali`) on behalf of `c-acme` (`CreatedByBehalfOf = c-acme`);
the last completed manual transition was made by MERT (`$PreviousUser = u-mert`).

| Caller | roles | act_sub |
|---|---|---|
| ALİ | loan.customer | u-ali (the starter) |
| AYŞE | loan.officer | u-ayse |
| MERT | loan.approver | u-mert (made the previous transition) |
| EMRE | loan.approver | u-emre |
| DENİZ | loan.auditor | u-deniz |
| BLOKE | loan.officer, loan.blocked | u-bloke |
| KAAN | kyc.analyst | u-kaan |
| ANON | (none) | u-x |

## 2. The `authorize` function

### `?queryRoles=true` — may the caller see the instance?

Grant set at **review**: parent-stamped override, else the state's own `queryRoles`, else the root's.
`review` declares its own, so the root's set — including `deny loan.blocked` — does not apply.

| Caller | allow officer / auditor | deny allOf[officer, Starter] | Verdict |
|---|---|---|---|
| AYŞE | Yes | Yes ∧ No = No | ✅ |
| DENİZ | Yes | No | ✅ |
| BLOKE | Yes | No | ✅ — the root's `deny loan.blocked` is not in this set |
| ALİ | No | No ∧ Yes = No | ❌ no allow matched |
| ANON | Unknown | Unknown ∧ No = No | ❌ allow needs Yes |

At **draft** the root's set applies: BLOKE ❌ (deny Yes), and ANON ❌ too — a role-less caller cannot
prove it is not `loan.blocked`, so that deny is Unknown and refuses.

### `?transitionKey=approve` — four eyes

| Caller | allow approver | deny allOf[approver, PreviousUser] | Verdict |
|---|---|---|---|
| EMRE | Yes | Yes ∧ No = No | ✅ |
| MERT | Yes | Yes ∧ Yes = **Yes** | ❌ the one who made the previous step may not approve |
| AYŞE | No | No | ❌ |

### `?transitionKey=cancel` — transition roles AND `availableIn` roles

| Caller | roles anyOf[Starter, officer] | availableIn[review] = Starter | Verdict |
|---|---|---|---|
| ALİ | Yes | Yes | ✅ |
| AYŞE | Yes | No | ❌ |
| ANON | Unknown | No | ❌ |

## 3. The state function

The state function does **not** refuse on `queryRoles`; the Internal Gateway asks
`authorize?queryRoles=true` first. What the state function does with roles is filter
`availableTransitions`. At **review**:

| Caller | availableTransitions |
|---|---|
| ALİ | `cancel` |
| AYŞE | `request-docs` |
| MERT | `request-docs` (`approve` dropped by four eyes) |
| EMRE | `approve`, `request-docs` |
| DENİZ, ANON | — |

Transition roles are a **discovery** control: `POST …/transitions/{key}` does not evaluate them. The real
boundaries are `queryRoles` (through the gateway), function `roles`, and the state half of `availableIn`.

## 4. Schema `x-roles` — field visibility on reads

| Caller | `iban` anyOf[Starter, officer] | `riskScore` allow approver + deny allOf[approver, Starter] | `internalNote` deny customer |
|---|---|---|---|
| ALİ | ✅ starter | ❌ | ❌ customer |
| AYŞE | ✅ officer | ❌ | ✅ |
| EMRE | ❌ | ✅ | ✅ |
| ANON | ❌ No ∨ Unknown = Unknown | ❌ | ❌ cannot be proven not to be a customer |

Order: `x-roles` hides, then `x-masking` / `x-encryption` transform what is still visible. Their exemption
`roles` lists take **no** combinators — only `{ "role", "grant": "allow" }`; a combinator there is a
publish error.

## 5. The human-task list

`review` is a Human state, so the instance is a candidate. Visibility is the `queryRoles` table of §2:
AYŞE, DENİZ and BLOKE see the task; ALİ and ANON do not. Two differences from `authorize`:

- **Empty set:** a Human state with no `queryRoles` (none on the state, none on the root) is **dropped**,
  not shown to everyone.
- **Identity:** the caller's `act_sub` / `sub` travel in the leaf request body, so `$InstanceStarter` is
  compared with the right person in another domain as well.

## 6. SubFlows — only the deepest active leaf decides

The instance moved to `kyc`; the child waits in `kyc-review`. `authorize?queryRoles=true` asked of the
parent descends; **only the leaf decides**, with the leaf's grants. The root's and every intermediate
level's `queryRoles` are not consulted.

| Caller | leaf's own (allow kyc.analyst) | parent override (anyOf[kyc.analyst, officer]) |
|---|---|---|
| KAAN | ✅ | ✅ |
| AYŞE | ❌ | ✅ |
| BLOKE | ❌ | ✅ — the root's `deny loan.blocked` does not apply |
| DENİZ | ❌ | ❌ — the root would have admitted, but the leaf decides |

- A leaf with no `queryRoles` and no override **allows**. A parent that wants a restriction must stamp
  one with `subFlow.overrides.states.<state>.queryRoles`.
- Transitions the parent owns (`cancel`, `exit`, `updateData`, shared transitions available in the
  current state) are still answered at the parent while the SubFlow runs.
- Predefined roles resolve against the instance whose grant is evaluated: `$InstanceStarter` in the leaf
  reads the **child's** `CreatedBy`. The runtime gives a first-level child the caller's identity
  automatically; from the second level down, the flow's input mapping must forward the `sub` / `act_sub`
  headers — that is the flow author's decision.

## 7. Overrides

| Override | Effect |
|---|---|
| `overrides.states.<s>.queryRoles` | **Replaces** the child's `queryRoles` at that state — never merged (§6) |
| `overrides.transitions.<t>.roles` | Replaces the child's transition roles and skips `availableIn` narrowing — here KAAN no longer sees `kyc-approve`, only `kyc.senior` does |
| `overrides.states.<s>.interaction.longPoll.roles` | Replaces the long-poll roles as a whole list; **an empty list admits every caller** |
| Reach | Only the **direct** parent's override applies; A's override of B never reaches grandchild C |

Overrides are stamped onto the child when it starts and resolved child-side, so a directly addressed
leaf answers the same as one reached through its parent.

## 8. Interaction — long-poll acknowledge

`signature` waits with `terminate: true`. Who may acknowledge (`POST …/longpoll/ack`, which the gateway
pre-flights with `authorize?ack=true`) is decided by `allow allOf[loan.customer, $InstanceStarter]`:

| Caller | Verdict |
|---|---|
| ALİ (customer and starter) | ✅ |
| another customer (u-veli) | ❌ Yes ∧ No |
| AYŞE | ❌ |
| ANON | ❌ Unknown ∧ No = No |

- When nothing is awaiting an acknowledgement, `authorize?ack=true` **allows** — the endpoint answers
  `Ok` idempotently in that case.
- Instead of `roles` a single `rule` (C# script) may be declared; they are mutually exclusive, and the
  gate fails closed.

## Related

- [Role Grant Authorization](role-grant-authorization.md) — the rules, combinators, three-valued evaluation, upgrade notes.
- [The `authorize` Function](authorize-function.md) — the selectors and the leaf-only `queryRoles` rule.
- [SubFlow Overrides](subflow-overrides.md) — every parent override and how it is stamped.
- [Long-Poll Termination](long-poll-termination.md) — the interaction block and the acknowledge gate.
- [Field Masking and Encryption](field-masking.md) — what happens to a visible field after `x-roles`.
