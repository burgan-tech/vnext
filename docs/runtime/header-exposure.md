# Header Exposure (`Workflow:HeaderExposure:Deny`)

A transition request carries every header the client and the network in front of the runtime added: credentials
(`Authorization`, `Cookie`, API keys), identity (`sub`, `act_sub`, `user_reference`) and whatever gateways and proxies
stamp. Before this setting the runtime copied that set, unchanged, to two places a domain does not control: the
recorded transition (`InstanceTransitions.Header`, served by `GET …/instances/{id}/transitions`) and every script and
mapping context (`context.Headers`). Issue vnext#1260.

`Workflow:HeaderExposure:Deny` lists the header names a domain keeps out of both.

```json
"Workflow": {
  "HeaderExposure": { "Deny": [ "authorization", "cookie", "x-api-key", "x-actor" ] }
}
```

## Contract

- **Deny only, by design.** There is no allow list: gateways, proxies and meshes add headers a domain cannot enumerate,
  so listing what may pass is not maintainable. A domain lists what is sensitive.
- **Empty (the default) changes nothing.** The runtime ships no default list — which header is sensitive is the
  domain's decision. A domain that sets nothing keeps today's behaviour, headers included.
- Names match **exactly and case-insensitively**; blank entries are ignored. No prefixes or wildcards.

## Where a listed header is removed

| Surface | Effect |
|---|---|
| Transition record (`CreateTransitionRecordStep`) | never written to `InstanceTransitions.Header` |
| Transition history (`GET …/instances/{id}/transitions`) | removed from every row's `header`, including rows recorded **before** the list was configured — stored rows are never rewritten, so this is what protects them |
| Script and mapping contexts (`context.Headers`) | absent for every script the runtime builds through `IScriptContextBuilder`: task and transition mappings, conditions and rules, schedule and resource-lock scripts, SubFlow input/output mappings, functions, extensions, views, events |
| `context.CurrentTransition.Header` | follows from the record (the record does not hold it) |
| SubFlow carried headers (`Workflow:SubFlow:CarriedHeaders`) | not carried from the settling child to the parent's output mapping and resume request, in addition to that policy's own deny floor |

## Where it is deliberately NOT applied

The runtime's own reads of the request keep the headers **as they arrived**, so no list can break authorization or
identity:

- caller-role resolution, `ICurrentUser`, dynamic grants on `$.context.Headers.*`, row-security parameters;
- the instance-call credential (`sub`, `act_sub`, `position`, `client_id`, `role`) the runtime forwards on SubFlow start
  and on trigger / Get* tasks, and the identity it propagates to a remote task;
- parent/root instance ids, trace and correlation headers, the async job that continues the hop.

These read `TransitionExecutionContext.Headers` (never reduced) or, from a script context, the internal transport view
(`ScriptContext.TransportHeaders` via `GetTransportHeadersAsDictionary`), which scripts cannot reach.

## Consequences for a domain

- A mapping that read a listed header — for example to forward `Authorization` to a downstream API — no longer sees it.
  Listing a header is the domain's decision to stop that.
- Body fields are not headers: actor or audit fields a client puts in the **body** are protected with the master schema
  (`x-roles`, `x-masking`, `x-encryption`; [Field masking](../domain/field-masking.md)), and only for fields the master
  schema declares.
- `createdBy` / `createdByBehalfOf` on history rows are runtime audit columns and are returned as recorded.

## Implementation

- `IHeaderExposureDenyList` (Domain, `BBT.Workflow.Headers`) — `IsEmpty`, `IsDenied`, `Filter` (dictionary), `Apply`
  (recorded JSON). Default implementation `HeaderExposureDenyList` (Application), singleton, options validated on start.
- `ScriptContextBuilder` sets `Headers` to the reduced copy and `TransportHeaders` to the request as it arrived; with an
  empty list both are the very dictionary that was passed in. `CreateParallelBranch` keeps the split.
- Runtime readers of a script context's headers (`TaskExecutorBase.CallerHeadersOf`, the trigger-task credential,
  `RemoteInvokerService.CreateTraceContext`, `SubflowStarter`) use `GetTransportHeadersAsDictionary`.
