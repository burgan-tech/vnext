# Checks

Each check names the scanner line it reads, why it costs, and what to recommend. Order is rough
impact order, not a required sequence. A check that finds nothing is not reported.

Verify every candidate by opening the file. The scanner sees structure; intent lives in the code.

---

## 1. Reference data with no cache

**Scanner:** `## extensions` lines marked `NETWORK`; `!! ... okuma basina N agli cagri`;
`## functions` rows whose task type is `Http` or `DaprService`; `hic kullanilmayan yetenek: 18 CacheAside`.

Lookup data — cities, districts, countries, occupation codes, cancellation reasons, product
catalogues — fetched from a backend on every read. Extensions are the worst case: they run on
read surfaces, and the runtime executes a state's extension set **fail-fast**, so one slow
lookup keeps the whole screen from rendering.

The `okuma basina N agli cagri` line is the number that matters. N is per read, and it multiplies
by the state's traffic.

**Recommend:** `CacheAsideTask` (type 18) wrapping the source task, with a TTL matched to how
often the data really changes. `StateStoreTask` (17) when the domain wants to manage the cache
itself. Mention that both have been available for a long time and cost nothing to adopt.

## 2. No retry anywhere

**Scanner:** `errorBoundary: ...` — the action histogram, already labelled with the member names
read from `ErrorAction`, plus how many workflows carry no `onError` at all.

A domain whose network
calls sit behind Abort turns a transient 5xx into a faulted instance and a manual recovery.

Note two things in the finding. Retry lives on the error boundary, not on the task — `HttpTask`
has no retry surface. And retry is unsafe to switch on blindly for calls that create records;
tell them to confirm the backend's behaviour on a repeated request first.

**Recommend:** `action: 1` on lookup and idempotent calls. The runtime defaults (3 attempts,
1 s initial delay, exponential, jitter) are reasonable; do not invent numbers.

## 3. Flows that block each other

**Scanner:** `subflow kenari N tip={...}`; per-workflow `cumTimeout`, `net`, `chain`.

A `S` subflow blocks its parent for the child's whole activation; runtime-generated subflow
starts are always `sync=true`. Deep `S` nesting plus a long serial chain means one request holds
the whole tree.

`cumTimeout` is the worst case for that workflow alone — the sum of the timeouts of every network
task it can reach. It is an upper bound, not a prediction; say so.

A domain with `tip={}` has no subflows and this check does not apply. A domain with no `P` at all
is worth a sentence on its own: something in there probably does not need to be waited for.

**Recommend:** `P` for children the parent gains nothing by awaiting. Delete wrapper subflows
whose body is one call. Review the long timeouts on the worst chain.

## 4. Decision-only states

**Scanner:** per-workflow `decision=N`, against `states=`.

States with no view, no hook and only automatic outgoing transitions. Each one is a full pipeline
pass that exists to evaluate a condition. A quarter of a workflow's states being decision-only is
worth raising; a handful is not.

Also grep the rules folder for an always-true rule (`DefaultTrueRule` and friends) — those
transitions have no decision in them at all.

**Recommend:** fold the condition into the previous state's transition and delete the state.

## 5. Independent tasks running serially

**Scanner:** `seri hook <hook>: [(order, task), ...]` — printed when a hook has two or more tasks
with distinct `order` values.

The runtime runs same-`order` tasks in a hook in parallel and different-`order` tasks one at a
time. A hook whose tasks do not feed each other is paying for ordering it does not need.

**This check produces false positives by design.** A token fetch followed by the call that uses
the token is correctly serial. Read the mappings before recommending anything: the question is
whether the later task consumes the earlier one's output.

Two things worth calling out separately when you see them: the same task listed twice in one hook,
and an order sequence that disagrees with the array order.

**Recommend:** give the genuinely independent tasks the same `order`.

## 6. The domain calling itself over the network

**Scanner:** `!! kendi domainine cagri (N): ...`; `hic kullanilmayan yetenek: 15 GetInstances`;
`InstanceQuery(fluent)=0` next to a non-zero `JsonSerializer.Serialize`.

Read that last signal against the `(log icinde=M)` figure beside it: `Serialize` calls inside a
log statement are check 19, not a hand-built filter. Only the remainder points here, and it still
has to be confirmed by opening the mapping.

A `DaprService` task whose `methodName` addresses the domain's own API — an instance query or a
data read going out to the network and back into the same runtime. Usually paired with a filter
hand-built as a `Dictionary` and serialized to JSON in a mapping.

`GetInstancesTask` (type 15) with `SetFilterSpec` runs in-process for the same domain and takes a
fluent filter. Also check those queries for paging: a self-call query with no `pageSize` is a full
scan that gets slower as the table grows.

**Verify:** the check matches on the domain name appearing in the URL, so an external gateway path
that happens to contain the domain name is a false positive. Open the task and look at the host.

**Recommend:** `GetInstancesTask` + `SetFilterSpec`; `CacheAsideTask` for the self data reads;
restore paging where it was removed.

## 7. Oversized scripts on a hot path

**Scanner:** `## scriptler` largest list, and whether the largest file backs an extension.

A large mapping is only a problem where it runs often. An extension mapping is the hot case: it
runs on every read of every state it is attached to. Cross the largest scripts against the
extension table before writing anything.

What to look for once inside: a long `if / else if` chain on `currentState` (every branch is
evaluated to select one), a per-element log in an output handler, a nested scan over a list that
grows with instance data, repeated parsing of the same value inside a comparator.

**Recommend:** split per-state logic out of the single file, or at minimum turn the chain into a
lookup. Remove per-element logging. Name the line numbers.

## 8. Schemas defined but not bound

**Scanner:** `schema baglama: bagli=N null=M`, and `schemasOrphans`.

`"schema": null` on every transition while schema components sit in the folder means request
payloads are not validated. Bad input then fails somewhere downstream, far from its cause.

**Recommend:** bind the schemas to the transitions they were written for. Cheap, and it moves
failures to where they can be understood.

## 9. Dead weight and broken references

**Scanner:** `viewsOrphans`, `schemasOrphans`, `tasksOrphans`;
`!! tanimi olmayan subflow`; `!! task tanimi bulunamadi`; `!! ikiz task tanimi`.

Split these two ways when reporting. **Broken references come first** — a subflow target with no
workflow behind it, an extension pointing at a task that does not exist. Those are defects, and
they may be live.

Unreferenced components are only clutter, but quantify them; "half the views are unused" lands
where a list of filenames does not. Look at the names before recommending deletion: a whole set
sharing a prefix is usually a superseded generation, while one lonely file may be work in
progress.

**Twin definitions are the case orphan-scanning misses.** `!! ikiz task tanimi` groups task
components whose `type` **and** `config` are byte-identical. Both are referenced, so neither is an
orphan, yet a change to one has to be made to the other. Script tasks are excluded (their code
lives in `mapping`, so an empty `config` would make every one of them a twin).

**Verify before recommending deletion.** A hook supplies its own `mapping` on the task reference,
so two components with the same config can still do different work — `dapr-task-check-has-active-application`
and `dapr-task-get-active-application` both point at the same instances endpoint and differ only in
what their callers map. Say "consolidate these" only where the callers also agree; otherwise report
it as duplication to be aware of.

## 10. Timeouts

**Scanner:** per-task `!! timeout YOK`; per-workflow `timeout=var|yok`.

Two different things. A network task with no `timeoutSeconds` can hold the pipeline indefinitely.
A workflow with no `timeout` has no upper bound on how long an instance may sit unfinished — fine
for a short flow, worth asking about for one a human abandons.

Also check for a `reset: OnEntry` workflow timeout together with a frequently invoked `$self`
transition: every call re-arms the timer. Say whether that looks deliberate rather than asserting
it is a bug.

## 11. Embedded base64 weight

**Scanner:** `gomulu base64: X KB / Y KB JSON (%Z), N blob, tekrar ...`

Every mapping is stored twice — the `.csx` on disk and a base64 copy inside the component JSON.
At a high share this dominates file size, merge conflicts and validation cost.

**It is not a compile cost.** The runtime's script cache is keyed by source hash, so a blob
repeated across files compiles once. Say that explicitly, or the finding reads as bigger than it
is. Report it as build and review friction, and keep it low in the list.

## 12. Runtime and schema version pin

**Scanner:** the header line `runtime: X schema: Y`.

Compare against this repo's `common.props` and `vnext-meta/version-manifest.json`. A stale pin
does not change what the deployed runtime does — environments run what they run — but validation
and meta checks read it, so it silently drifts from reality.

Treat it as a one-line note in the report intro, not a numbered finding.

## Tooling (optional)

If the user asks about build or validation speed, the template tooling at the domain root is
usually worth a look: whether `validate.js` reads and parses every component more than once, how
it locates the line number for an error, whether `build.js` shells out to a second Node process,
and whether the file walker in `index.js` descends into subfolders. These are real but they cost
developer seconds, not production latency — keep them out of the main list unless asked.


## 13. Copy-pasted workflows and state templates

**Scanner:** `!! ayni state kumesine sahip workflow: ...`; `tekrarlayan state kalibi xN: ...`.

The first line means two or more workflows have an identical set of state keys — the same flow
authored once per data type. The second counts structurally identical states across the domain:
same state type, same sub type, same transition count, same hook tasks.

A repeated terminal or failure state is normal and not worth reporting on its own. What is worth
reporting is a **whole flow** duplicated, or a template repeated enough that a change has to be
made in twenty places to be made once.

**Recommend:** for duplicated flows, one flow whose behaviour comes from data. For repeated
failure states, a shared transition with `availableIn` instead of one state per failure.

## 14. No event transitions, and hand-rolled waiting

**Scanner:** `event transition (triggerType 3): 0` together with a non-zero `$self hedefi`.

Zero event transitions in a domain that clearly waits for an external system means the waiting is
built by hand: a state with an automatic transition guarded by a data check, plus a `$self`
`updateData` the other system calls to push data in and re-activate the state.

It works. It is also a mechanism the domain now owns and must debug. Report it as a design
observation, not a defect, and check whether the events surface would actually fit before
recommending it — some callers genuinely cannot publish.

**Recommend:** a `triggerType: 3` transition with an `event.mapping` where the upstream can
publish. Where it cannot, say so and leave the pattern alone.

## 15. Secrets and tokens refetched per execution

**Scanner:** `GetSecret=N`, and any auth-token task that appears in several hooks.

`GetSecret` runs per task execution with no memo. A token task called from four places fetches
four tokens for one flow. Both are cheap individually and add up on a hot path.

Cross-check the task inventory for a token or auth task and count the hooks that reference it —
the scanner's `seri hook` lines usually show it sitting in front of the call that consumes it.

**Recommend:** cache the token with `CacheAsideTask` at a TTL below its lifetime. Treat secret
fetching as a smaller, separate note.

## 16. Filtered attribute paths with no index

**Scanner:** `filtrelenen attributes yolu (N): ...`.

The JSON paths the domain filters instance queries on. By default these are unindexed, so every
query walks the jsonb. Deep paths cost more, and the cost grows with the table.

**Verify** before recommending: `x-indexed` is opt-in on Master-schema **scalar** fields, the
runtime prepares the projection, and `AttributeIndexes:Enabled` is off by default. Read
`docs/runtime/manual-attribute-index-maintenance.md` in this repo and confirm the current rules
rather than asserting them.

**Recommend:** index the paths that actually appear in a filter — usually an identity number or a
status — not every path the scanner lists.

## 17. View payload weight and loadData density

**Scanner:** `## en agir viewlar`; `loadData:true: N`.

A view component is served and cached per read; a 100 KB view is 100 KB on every render. `loadData`
adds the instance's data to that response.

Look at the heaviest views for per-channel twins — the same screen re-authored for web, mobile and
backoffice. That is a maintenance cost first and a payload cost second, so keep it low in the
list unless the sizes are extreme.

**Recommend:** share the common structure between channel variants; drop `loadData` where the view
does not read instance data.

## 18. Lookup data read through the instance-scoped address

**Scanner:** `!! instance scope (N): ...` under `## functions`; `!! instance adresinden okuma (N): ...`
under `## tasks`.

A function can be invoked through two addresses:

```
/{domain}/functions/{fn}
/{domain}/workflows/{wf}/instances/{id}/functions/{fn}
```

The second one loads the application instance **before** the function runs. That is not a single
row: the instance's data, its data-version list and its open correlations make it several SQL
round trips. For a function returning cities, districts, countries or cancellation reasons — data
that has nothing to do with the instance — the whole load is waste.

**`scope` does not trigger the read. It decides which URL the client must use.** With `scope: "I"`
the client has no choice but the instance address, and the cost follows from there. Keep that
distinction in the finding; stating it as "scope causes an instance load" sends the domain team
looking in the wrong place. Scope codes are letters, not numbers — `D` Domain, `F` Flow,
`I` Instance (`src/BBT.Workflow.Domain/Definitions/TaskScope.cs`).

**The same cost appears without a function component.** A `DaprService`/`Http` task whose URL is
`.../instances/{id}/functions/data` is reading a reference table through the instance address —
vnext-onboarding-ekyc reads its settings singleton exactly that way. The scanner flags both shapes;
report whichever is present.

**Verify, do not assert.** The runtime has a `DataFunctionCache`
(`src/BBT.Workflow.Application/Instances/Caching/DataFunctionCache.cs`) — distributed cache, TTL
from the flow definition's `functionCache.ttlSeconds`, freshness by fingerprint ETag. So a
`functions/data` read is not automatically the same cost as an uncached instance load. Check
whether `functionCache` is defined for that flow before treating the two as equivalent, and do not
put a number on either without measuring.

**Recommend:** move functions that do not depend on the instance to `scope: "D"`. Two things must
be said with it, or the recommendation is unsafe:

- **It changes the client's URL.** Mobile and web teams have to move with it; this is coordinated
  work, not a domain-side edit.
- **`context.Instance` is null under `"D"`.** Any mapping carrying
  `response.Data = context.Instance?.Data;` needs its reason established first — that line is the
  usual reason a lookup function was left at `"I"`.

Suggest starting with the one function that visibly does not read instance data, as a pilot.

## 19. Payloads serialized into log calls

**Scanner:** `JsonSerializer.Serialize=N (log icinde=M)` on the `## sorgu / gizli bilgi` line.

`M` counts `JsonSerializer.Serialize(...)` appearing inside a `Log*` call. Each one is a serialize
plus a log write on every execution of that mapping. On a hot path that is real, though rarely the
biggest number in the report.

What makes it worth a finding is usually what is in the body. In vnext-onboarding-ekyc both hits
were request bodies carrying an identity number, birth date, parents' names, e-mail and phone —
logged at `Warning`, which no environment filters out.

Report it in one paragraph where you found it, name the file and line, and move on. This is a
performance audit, not a data-protection review; flag it because it would otherwise be lost, and
do not let the report turn into one.

**Recommend:** drop the body log, or lower it to `Debug` **with the fields masked** — lowering the
level alone changes who sees it, not what it contains.
