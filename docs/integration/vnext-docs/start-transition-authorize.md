---
product: vnext-docs
milestone: v0.0.105
status: pending
---

# authorize?start=true — may this caller start an instance? — Handoff for vnext-docs

## Context

- vNext PR: the start-transition authorize PR of milestone v0.0.105
- Milestone: v0.0.105
- Issue: https://github.com/burgan-tech/vnext/issues/1203

## What changed in the runtime

A new, **workflow-scoped** route of the `authorize` system function:

`GET /api/v1/{domain}/workflows/{workflow}/functions/authorize?start=true[&version=x][&role=r]`

It answers whether the caller may START an instance of the flow, before any instance exists, from the
flow's own `startTransition.roles`. Until now `authorize` existed only on the instance route, so the
gateway had no way to ask this question and `startTransition.roles` was applied nowhere.

- `start=true` is the only target; without it the call is `400` (`AuthorizeRequiresExactlyOneTarget`).
  The gateway does not need to know the start transition's key — the route resolves it.
- `version` pins the definition; the gateway should forward the start request's own `version`.
- Response contract is unchanged: `200 {"allowed": true}` / `403 {"allowed": false}`; unknown flow or
  version → `404`.
- Caller roles are resolved exactly as on the instance route (configured provider, `role` parameter).
- Grant evaluation: static roles and `$Everyone` compare as usual; **instance-bound grants**
  (`$InstanceStarter`, `$PreviousUser`, `$InstanceBehalfOfStarter`, `$PreviousBehalfOfUser`, `$user.` /
  `$userBehalfOf.` / `$role.` context references) **never match** before a start — they neither admit
  nor refuse. A start transition guarded only by `allow $InstanceStarter` is closed to everyone.
  No grants → allowed.
- The runtime itself still does not check `startTransition.roles` at `POST .../instances/start`; the
  gateway decides, as for every other transition.

Source of truth when this page and the runtime disagree: `AuthorizeAppService.GetStartAuthorizeResultAsync`
(`src/BBT.Workflow.Application/Authorization/`) and
[docs/domain/authorize-function.md](../../domain/authorize-function.md) § Starting an instance.

## What this product must do

- `docs/concepts/authorization` / the `authorize` function reference: add the workflow-scoped route,
  its single target `start=true`, and the "instance-bound grants never match at start" rule with the
  `allow $InstanceStarter`-only example.
- Gateway guidance: a second check-authorize rule for `POST .../workflows/{workflow}/instances/start`
  that calls this route (host is environment-specific), forwarding `version` when the start request
  carries it:
  `Utils.CallApiGet("http://<orchestrator-host>/api/v1/"+path.var1+"/workflows/"+path.var2+"/functions/authorize?start=true", header, allHeader).IsSuccessStatusCode == true`
- Workflow authoring guide: `startTransition.roles` is now meaningful (enforced through the gateway).
