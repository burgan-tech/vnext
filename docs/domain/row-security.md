# Row Security (`attributes.rowSecurity`)

Column-level authority exists already (`x-roles`, `x-masking`, `x-encryption`, see
[field-masking](field-masking.md) and [role-grant-authorization](role-grant-authorization.md)). Row
security adds the **row** dimension: a flow declares, per role, a filter that the runtime ANDs into the
instance reads it covers, so a caller sees only the instances a rule opens. Driving case: a branch
employee sees only the applications of their own branch.

**Platform position.** The runtime provides the mechanism. Which request values are trustworthy and how
tight a rule is, is the domain team's responsibility (see
[Gateway precondition](#gateway-precondition) and [Client-controlled sources](#client-controlled-sources)).

## Scope (phase 1) — read this first

Phase 1 filters **two surfaces only**:

- the instance **list** (`GET {domain}/workflows/{workflow}/instances`, including `groupBy` and
  aggregations: counts and totals cover visible rows only), and
- the **single instance GET** (`GET {domain}/workflows/{workflow}/instances/{instance}`),

and, because they go through those two, the `GetInstances` / `GetInstance` trigger tasks (same-domain and
cross-domain).

The GET `404` for an excluded instance is a **consistency check with the list, not a confidentiality
boundary**: row security is not an access boundary for an instance whose id the caller knows.

### Known gap: unfiltered surfaces

These surfaces do **not** apply row security in phase 1. A caller who knows an instance id can read the
instance there even when the list and the GET hide it:

- `functions/state`, `functions/data`, `functions/view`, `functions/schema`, `functions/master`
- transition and data history endpoints
- `internal/*` endpoints
- the `GetInstanceData` task, `context.Related` in scripts, the human-task list, event correlation
  selectors

Consequences to design around:

- **Existence oracle.** For an excluded instance, `GET` answers `404` while `functions/data` answers
  `200`. A caller can therefore confirm an id exists and read its data.
- `authorize?queryRoles=true` may admit a caller whom the GET hides: it decides at the SubFlow leaf and
  has no row term.
- **SubFlow children do not inherit the parent's rules.** A child instance is read through its own flow's
  `rowSecurity` (or none). Publish logs RS-W7 for a flow that declares `rowSecurity` and has a SubFlow
  state.

This gap is a recorded, accepted risk of phase 1 with no date for phase 1b.

### Phase-1b conditions

Any later coverage of the instance-addressed surfaces must: check before every cache read (the state
function and ETag/304 paths otherwise bypass a loader check); put the row inputs (rule indexes and hashed
values, never raw values) in cache keys and ETags; and make an `authorize` row term ignore the `role`
request parameter and answer "excluded" the same as "missing".

## Contract

Workflow-level, inside `attributes`:

```json
"rowSecurity": {
  "rules": [
    { "roles": ["branch-staff", "branch-ops"],
      "filter": { "attributes": { "branchCode": { "eq": "$.context.Headers.x-branch-code" } } } },
    { "roles": ["region-manager"],
      "filter": { "attributes": { "branchCode": { "in": { "$param": "$.context.Headers.x-branches", "split": "," } } } } },
    { "roles": ["hq-auditor"] },
    { "roles": ["customer"],
      "filter": { "createdBy": { "eq": "$InstanceStarter" } } },
    { "roles": ["$InstanceStarter"],
      "filter": { "status": { "eq": "A" } } }
  ]
}
```

**There is no `default`. A flow with `rowSecurity` is deny by default.** A caller no static rule matches
sees nothing beyond the identity rules the author declared. "Role X sees everything" is written as an
explicit unfiltered rule (`{ "roles": ["hq-auditor"] }`). A flow without `rowSecurity` is not filtered at
all, byte for byte as before. Any member other than `rules` is a publish error (RS-E12).

### `rules[].roles`

- **Static roles**, compared like role grants (`OrdinalIgnoreCase`).
- **Identity-bound reserved roles**: `$InstanceStarter` (row predicate `createdBy eq <caller actor>`) and
  `$InstanceBehalfOfStarter` (`createdByBehalfOf eq <caller user name>`). Same pairing as
  [role grants](role-grant-authorization.md): CreatedBy with the actor, BehalfOf with the user name.
- Rejected at publish: `$PreviousUser` / `$PreviousBehalfOfUser` (RS-E01), dynamic grants (`$user.`,
  `$userBehalfOf.`, `$role.`; RS-E02), any other reserved role (RS-E05). An `allOf` / `anyOf` object is
  refused before publish validation: `roles` binds as a list of strings, so the runtime answers
  `400 App:900006` at JSON binding, and `vnext-schema` validation reports it first. RS-E03 is reserved for
  this case and is not emitted by the runtime.
- A static role and a reserved role may not share one rule's `roles` (RS-E04): split into two rules.

### `rules[].filter`

- Optional. Absent on a static-role rule opens every row; absent on a reserved-role rule is exactly the
  identity predicate. Present on a reserved-role rule it is ANDed with the identity predicate.
- Body is the list-engine filter JSON (GraphQL wire form): `eq ne gt ge lt le between like match
  startswith endswith in nin isNull includes`, with `and` / `or` / `not`, on `attributes.*` paths and
  whitelisted instance columns, including `createdBy`, `createdByBehalfOf`, `modifiedBy`,
  `modifiedByBehalfOf`.
- Not part of a rule: `sort`, `orderBy`, `groupBy`, `aggregations` (RS-E13).

### Parameters

A leaf value that is a string starting with `$.context.` (case-sensitive prefix) is a parameter.

| Path | Source |
|---|---|
| `$.context.Headers.<name>` | request header (case-insensitive name) |
| `$.context.QueryParameters.<name>` | query string (**client-controlled**) |
| `$.context.CurrentUser.<ActorUserName\|ActorUserId\|UserName\|Position>` | the authenticated caller (`ICurrentUser`); `sub`, `act_sub` and `client_id` are reachable as `Headers` |

There is no `Route` source and no `Query` source: `$.context.Route.*`, `$.context.Query.*`, an unknown
`CurrentUser` field and any malformed path are RS-E07. A value starting with `$.context.` in a different
letter case (for example `$.Context.`) is treated as a literal and logged as RS-W4. Escaping a literal
that starts with `$.context.` is not supported.

Shorthands, valid only as the value of their own column (RS-E06 elsewhere):
`$InstanceStarter` on `createdBy` = `$.context.CurrentUser.ActorUserName`;
`$InstanceBehalfOfStarter` on `createdByBehalfOf` = `$.context.CurrentUser.UserName`.

Parameters are not allowed on `like` / `match` / `startswith` / `endswith` or inside `includes` (RS-E17):
an unescaped `%` or `_` in a caller-supplied value would change the pattern.

### Arrays (`in`, `nin`, `between`)

- **Form A, element-wise:** a JSON array whose elements are literals or parameters, each resolved alone:
  `"between": ["$.context.QueryParameters.from", "$.context.QueryParameters.to"]`.
- **Form C, explicit split:** `{ "$param": "<path>", "split": "<separator>" }`. Without `split`, a
  multi-valued source (a repeated header or query key) is taken element by element. Each element is
  trimmed. An empty element is dropped only for a narrowing `in` (one outside an odd number of `not`);
  for `nin`, `between`, `ne` and anything under `not`, an empty element drops the whole rule. `split` on an operator that takes no array is RS-E11;
  `between` with anything but exactly two operands is RS-E10.

Values are strings on the wire; each is coerced to the field type from the flow's master schema. A value
that cannot be coerced drops its rule.

On an **instance column** (`createdBy`, `createdByBehalfOf`, `key`, `status`, `createdAt`, …; not
`attributes.*`), an `in`, `nin` or `between` value that contains a comma drops its rule, whether it comes
from a parameter, an identity shorthand or an authored literal. The list engine re-splits these operands
on `,`, so one value would otherwise become several. `eq` / `ne` on an instance column and every operator
on a data path keep the comma as part of the one value.

### Limits

`Workflow:RowSecurity` configuration section:

| Key | Default | Effect |
|---|---|---|
| `MaxElements` | `100` | More elements in one parameter drops the rule |
| `MaxElementLength` | `1000` | A longer element drops the rule |

The rule does not count against the caller's own filter length limits.

## Evaluation model

Inputs: the resolved role set and how it was obtained, the request's header and query values, and the
current user. **The platform never adds an implicit branch**: everything a caller sees comes from a rule
the author wrote.

1. **Role resolution failed** (provider error: status, timeout, transport, unparseable body) means **no
   rows**. A provider answer of "no roles" (`204`, blank body, `roles: []`) and a skipped call (no caller
   identity) are a role-less caller, not a failure.
2. **Base, static-role matching.** The caller holds a role listed in at least one static rule: base = OR
   of those rules' filters (a rule without `filter` = all rows); the caller's other roles add nothing.
   No static rule matches (including a role-less caller): base = **none**.
3. **Identity add-on.** Only rules declared with `roles: ["$InstanceStarter"]` or
   `["$InstanceBehalfOfStarter"]` contribute, and only when the caller's identity value resolves
   (`ActorUserName` / `UserName`): identity predicate AND the rule's filter, ORed onto the base. A caller
   identity with no such rule adds nothing.
4. **Effective query** = the caller's own filter AND (base OR identity add-ons). Base none and no add-on
   means no rows, decided in the application service without SQL: the list answers an empty page.

Parameter failures never widen. A matched static rule whose parameters fail still counts as matched and
contributes nothing:

- a missing or empty parameter: nothing;
- narrowing array (`in`): unresolved elements are dropped, none left means nothing;
- widening operator (`nin`, `ne`) or any failure under `not`: the whole rule contributes nothing;
- `between` without exactly two values: nothing;
- a scalar operator fed a multi-valued source: nothing (ambiguous, there is no "first value");
- an element over `MaxElementLength`, or more than `MaxElements` elements: nothing.

A rule whose filter touches a protected master-schema path (`x-encryption`, `x-masking`, or a
`QueryParameters` value on an `x-roles` path) is dropped at read time as well, in case the publish check
was skipped (RS-W0).

### Outcome tables

Definition A: `branch-staff` opens `branchCode eq $.context.Headers.x-branch-code`, `hq-auditor` has no
filter, no identity rule.

| Request | Result |
|---|---|
| no credential | none |
| identity, no roles | none |
| identity, `[menu.view]` | none |
| `[branch-staff]`, header present | own branch |
| `[branch-staff]`, header missing | none |
| `[branch-staff, menu.view, report.read]` | own branch |
| `[branch-staff, hq-auditor]` | all |
| `[hq-auditor]` | all |
| role resolution failed | none |

Definition B: Definition A plus `{ "roles": ["$InstanceStarter"] }`.

| Request | Result |
|---|---|
| no credential | none |
| identity, no roles | own instances |
| identity, `[menu.view]` | own instances |
| identity, `[branch-staff]`, header present | own branch ∪ own instances |
| identity, `[branch-staff]`, header missing | own instances |
| no identity, `[branch-staff]` | own branch |
| role resolution failed | none |

## Which rule set applies (version)

The rule set is the one in the flow's **default version** (the `sys-version-registry` pointer,
[Flow Default Version](../runtime/flow-default-version.md)) when one is set, otherwise the **latest**
published version. It is read with one lookup, `GetFlowAsync(domain, workflow, null)`, for instances of
every version. The list and the GET use the same lookup and never read `instance.FlowVersion`, so they
always agree. A `set-default` therefore also switches the row rules in force.

### Lookup failure

If that lookup fails for any reason other than "no published definition" (`CacheItemNotFound`) the
request is **denied for every flow, opted in or not**, and the lookup error is returned as is. This
includes `Instance:100052` (invalid pointer, 409), `Instance:100053` (registry unavailable, 503) and a
flow version not activated. No published definition is not a failure: that flow is not configured and
is not filtered.

This is a **behaviour change for every flow**, released as breaking (council decision D5, accepted by the
requester on 2026-10-23): before this release the list ignored such a lookup error and served rows, and
GET did not look the flow up at all. GET now always performs one (cached) flow lookup, and a lookup error
fails both the list and GET of any flow. Successful lookups on a flow without `rowSecurity` leave its
answers unchanged.

## Surfaces and behaviour

- **List.** The predicate joins the query before paging, `groupBy` and aggregations, on both list routes
  (GraphQL and the `groupBy` string route), at the single repository merge point. Never compiled by a
  second evaluator.
- **Single GET.** Probed with the same list compiler as `id = <instance> AND <row predicate>`, before
  extensions and before the ETag comparison (no `304` for an excluded row). An excluded or no-rows instance and a missing
  one answer the same `404`; only a rule-set lookup failure answers its own error. The probe runs against the instance's latest data, also when `?version=` asks
  for an older data version.
- **Runtime-internal lookups** (direct-trigger key resolution, view content resolution) are not caller
  reads and are not filtered; no DTO carries a bypass flag.
- **Trigger tasks** evaluate with the header set they present, which carries only `sub`, `act_sub`,
  `position`, `client_id` and `role`. A rule parameter header is **not** forwarded automatically: the
  domain team sets it in the task's input mapping, otherwise the parametrised rule contributes nothing.
  Query parameters have no task counterpart.

## Gateway precondition

Row security trusts the header set a request arrives with. The gateway **must strip or overwrite, never
append,** the `role` header and every header a rule reads, on every public route to the list and the GET,
writing the values introspected from the token. Under the default caller-role provider the `role` header
**is** the role set; a `split` on a header is only safe under this precondition. Requester attestation
that the gateway does this: 2026-10-23.

## Client-controlled sources

`QueryParameters` is client-controlled even behind a perfect gateway: the caller picks the value. A rule
on it restricts nothing against that caller and is **never a boundary**. Publish rejects it on `x-roles`,
`x-masking` and `x-encryption` paths (RS-E18 or the stricter code) and logs RS-W6q on every other use.

## Cross-domain tasks

A task mapping may set `role`, `sub`, `act_sub` or a rule header, and the target domain trusts the header
set a calling runtime presents. Reading another domain through a trigger task is therefore as strong as
the mapping's author. Domain teams own that mapping.

## Publish validation

Errors block the publish. Warnings are **log-only**: the publish response is unchanged and the flow goes
live (`ComponentValidationWarning` plus the code in the message).

| Code | Severity | Meaning |
|---|---|---|
| RS-E01 | error | `$PreviousUser` / `$PreviousBehalfOfUser` used as a rule role |
| RS-E02 | error | dynamic grant (`$user.`, `$userBehalfOf.`, `$role.`) used as a rule role |
| RS-E03 | (reserved) | `allOf` / `anyOf` used as a rule role — not emitted by the runtime: refused at JSON binding (`400 App:900006`), reported first by `vnext-schema` |
| RS-E04 | error | static and reserved roles mixed in one rule |
| RS-E05 | error | reserved role other than `$InstanceStarter` / `$InstanceBehalfOfStarter` |
| RS-E06 | error | instance-starter shorthand on a column it does not belong to |
| RS-E07 | error | malformed `$.context.` path, unknown source (`Route`, `Query`) or unknown `CurrentUser` field |
| RS-E08 | error | filter on, or above, an `x-encryption: encrypt` path |
| RS-E09 | error | invalid operator or filter JSON shape |
| RS-E10 | error | `between` without an array of exactly two operands |
| RS-E11 | error | `split` on an operator that takes no array |
| RS-E12 | error | `rules` missing or empty, or an unknown member on `rowSecurity` or a rule |
| RS-E13 | error | `sort` / `orderBy` / `groupBy` / `aggregations` inside a rule filter |
| RS-E14 | error | filter on an `x-encryption: hash` path |
| RS-E15 | error | filter on an `x-masking` path |
| RS-E16 | error | `rowSecurity` on a `sys-*` flow |
| RS-E17 | error | parameter on `like` / `match` / `startswith` / `endswith`, or inside `includes` |
| RS-E18 | error | `QueryParameters` value on an `x-roles` path |
| RS-W0 | warning | master schema not resolvable at publish: schema-aware checks skipped; protected-path rules are dropped at read time instead |
| RS-W1 | warning | filtered field not declared in the master schema |
| RS-W2a | warning | `in` / range / pattern operator on a field without `x-indexed` |
| RS-W2b | warning | identity rule or filter on `createdBy` / `createdByBehalfOf`, which have no index |
| RS-W2c | warning | `ne` / `nin` as the only predicates of a rule |
| RS-W4 | warning | `$.context.` in another letter case: treated as a literal |
| RS-W5 | warning | `not` / `nin` on a field that is not `required` also matches rows missing it |
| RS-W6q | warning | `QueryParameters` used: client-controlled, not a boundary |
| RS-W6r | warning | `Headers` value on an `x-roles` path |
| RS-W7 | warning | SubFlow children do not inherit `rowSecurity` |

Messages are English and never echo authored values. With the master schema resolvable the protected-path
checks (RS-E08, E14, E15, E18) are errors; without it they downgrade to RS-W0.

## Observability

Log and span output carries parameter **names**, rule indexes, error codes and coarse kinds, never a
parameter, header or identity value and never exception text.

`WorkflowLogs` EventIds (`205xx`):

| EventId | Level | Meaning |
|---|---|---|
| 20505 | Information | decision of one read: surface, outcome, matched rule indexes |
| 20506 | Information | single GET found the instance outside the predicate (answered `404`) |
| 20507 | Warning | an applicable rule contributed nothing (reason, parameter names) |
| 20508 | Warning | role resolution failed, request returns no rows |
| 20509 | Warning | rule set could not be loaded, request denied |
| 20510 | Warning | publish: schema checks skipped (RS-W0) |
| 20511 | Information | publish: SubFlow children do not inherit (RS-W7) |

**Log volume.** EventId 20507 is a Warning and can fire on **every request** of a caller who lacks a
header a matched rule reads (for example a client that never sends `x-branch-code`). Operators should
expect it to scale with traffic and filter or sample it accordingly.

Span tags on the read span (a flow without `rowSecurity` carries **no** row-security tag):

| Tag | Value |
|---|---|
| `vnext.rowsecurity.applied` | the read was evaluated |
| `vnext.rowsecurity.outcome` | `unrestricted`, `filtered`, `excluded`, `denied-no-match`, `denied-no-contribution`, `denied-resolution-failed` or `denied-flow-load` |
| `vnext.rowsecurity.rules` | matched rule indexes, comma separated |
| `vnext.rowsecurity.missing_params` | names of the parameters that did not resolve |
| `vnext.rowsecurity.flow_version` | the resolved rule-set version |

`excluded` is written by the single GET after its probe found no visible row; the other values come from
the decision itself.

## Deployment / cache shape

The rule set travels inside the cached `Workflow` component, and that cache is shared by every pod. A
runtime that predates row security has no `rowSecurity` member; if it re-writes a flow's cache entry, the
member is dropped, and a row-security runtime reading that entry would treat the flow as not opted in and
serve every row. To prevent this, component cache keys carry a shape segment
(`ComponentCacheShape.Version`, `s2` since row security: `{component}:{domain}:{key}:s2:full:…` and
`…:s2:res:…`). During a rolling upgrade or a rollback, pods of different shape versions therefore use
**disjoint** cache keys and each loads the definition from the database in its own shape; no cache flush
is needed. Generation tokens stay shared, so a publish on either version still invalidates the resolution entries of both (full-version entries are immutable bodies).

This protects the new pods only. A pod running a release below row security still serves every row of an
opted-in flow, from its own entries or straight from the database — see [Rollback](#rollback).

**Do not force re-publish the same version during a rollout.** Full-version entries are immutable bodies
kept for `ComponentCache:FullVersionTtlSeconds` (default 1800 s) and are not invalidated by a generation bump.
While pods of both releases run, force re-publishing an **unchanged version number** (for example to add
`rowSecurity` to it) can leave a stale `s2` full body served until it expires. Publish a new version
instead, or wait for the TTL.

**morph-idm.** With `CallerRoleProvider:Provider = morph-idm`, `CallerRoleProvider:MorphIdm:BaseUrl` and
`CallerRoleProvider:MorphIdm:GetRolesPath` must be configured. A provider that cannot be reached resolves
to a failed role set, and every opted-in flow then returns no rows (`denied-resolution-failed`).

### Request logging (known platform behaviour)

Aether's `HttpBodyLoggingMiddleware` writes **all request headers** into the request log, redacting only
its built-in list (`Authorization`, `Cookie`, …) plus `Telemetry:Logging:Body:AdditionalSensitiveHeaderNames`.
Header values a rule reads (for example `x-branch-code`) therefore appear in request logs unless they are
listed there. Row security itself never logs a parameter value; whether to redact these headers is a
deployment decision.

## Rollback

Removing `rowSecurity` from the flow restores today's behaviour (it **widens** visibility).

A runtime older than the release that introduced row security **ignores `rowSecurity` entirely** and
serves every row of every flow, whatever the definition holds. Nothing in the flow definition protects
anything on such a runtime: removing `rowSecurity` changes nothing, and "closing" the flow with a rule for
a role nobody holds changes nothing either, because the old runtime never reads the rules. The only
protections are:

- do not roll the runtime back below this release while any flow relies on `rowSecurity`; or
- for as long as an older runtime serves those flows, block or authorize the instance list and the single
  GET of those flows at the gateway.

## Related

- [Role grant authorization](role-grant-authorization.md): the role evaluator and the caller-role provider
- [Flow default version](../runtime/flow-default-version.md): which flow version's rules apply
- [Field masking](field-masking.md): the column-level counterpart
