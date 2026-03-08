# Master Schema Field-Level Visibility (x-roles)

## Overview

The workflow engine supports **field-level visibility** for instance data based on the caller's roles. When a workflow uses a **master schema** that defines a `roles` vocabulary on properties, the Data and instance response endpoints return only the fields the caller is allowed to see.

This feature implements [GitHub #402](https://github.com/burgan-tech/vnext/issues/402).

## Business Rules

- **No `roles` on a property:** The field is visible to everyone.
- **`roles` on a property:** Only callers whose role is allowed (by the same DENY/ALLOW semantics as transition authorization) can see the field.
- **DENY overrides ALLOW:** If any of the caller's roles match a `deny` grant for that field, the field is hidden even if another role has `allow`.
- **Scope:** Applies only to **function response** and **instance query** endpoints that return instance data (Data function, GetInstance, GetInstanceList). The existing `authorize` and `permissions` endpoints are unchanged.

## Role Source: Request Header, CurrentUser, and Predefined Instance Roles

- **Static roles:** Caller roles are taken from **`ICurrentUser.Roles`**. `Roles` is populated from the request header **`role`**: the header value is split by **space or comma** (e.g. `role: maker, approver` or `role: maker approver`), trimmed, and stored as an array. This is done via `CurrentUserHeaderExtensions.ParseRolesFromHeader` and when building the current user from headers (e.g. `ChangeFromHeaders`).
- **Effective caller roles for instance data:** When returning instance data (GetInstance, GetInstanceData, list Attributes), the list of roles used for field visibility is **expanded** to include predefined instance roles when the current user matches:
  - **`$InstanceStarter`** is added when `ICurrentUser.ActorUserName` equals the instance’s **CreatedBy** (the user who started the instance).
  - **`$PreviousUser`** is added when `ICurrentUser.ActorUserName` equals the **CreatedBy** of the last completed **manual** transition for that instance.
- So schema properties that declare `roles: [{ "role": "$InstanceStarter", "grant": "allow" }]` or `$PreviousUser` will show or hide correctly based on whether the caller is the instance starter or the previous actor. This resolution is done by `ITransitionAuthorizationManager.GetEffectiveCallerRolesForFieldVisibilityAsync` and is consistent with transition/function authorize semantics.
- Authorize endpoints also use `ICurrentUser.Roles` (and, when an instance is present, the same predefined-role resolution for transitions); if any of the caller's roles is allowed for the requested transition/function/query roles, the result is allowed.

## Schema Format: `roles` Vocabulary

In the master schema (JSON Schema), any property can optionally define a `roles` array. Each entry has `role` and `grant` (`allow` or `deny`), consistent with `RoleGrant`:

```json
{
  "properties": {
    "amount": {
      "type": "number",
      "roles": [
        { "role": "morph-idm.maker", "grant": "allow" },
        { "role": "morph-idm.approver", "grant": "allow" },
        { "role": "morph-idm.viewer", "grant": "allow" }
      ]
    },
    "internalNotes": {
      "type": "string",
      "roles": [
        { "role": "morph-idm.approver", "grant": "allow" },
        { "role": "morph-idm.manager", "grant": "allow" }
      ]
    },
    "publicStatus": {
      "type": "string"
    }
  }
}
```

- **Path format:** Dot-separated (e.g. `amount`, `internalNotes`, `nested.field`). Nested objects are supported; each property path is evaluated independently.
- **Parsing:** Handled by `SchemaRolesParser.ParsePropertyRoles(schemaRoot)`. Schema is loaded from cache (`IComponentCacheStore.GetSchemaAsync`); no change to existing schema caching.

## Where Filtering Is Applied

- **GetInstanceDataAsync** (single instance and list): `GetInstanceDataOutput.Data` is filtered after load; if the workflow has a schema with `roles`, only visible paths are kept.
- **BuildInstanceOutputAsync** (GetInstance, GetInstanceList, history): `GetInstanceOutput.Attributes` is filtered the same way when the workflow has a schema with `roles`.

If the workflow has no schema or the schema has no `roles` on any property, data is returned unchanged.

## ETag

Current ETag behaviour is unchanged. Role-based ETag variation may be addressed in a later change.

## Components

| Component | Location | Purpose |
|-----------|----------|---------|
| Schema roles parser | `BBT.Workflow.Domain/Definitions/Schemas/SchemaRolesParser.cs` | Parses schema `properties` and `roles` into path → RoleGrant[] |
| Field visibility | `BBT.Workflow.Application/Authorization/SchemaFieldVisibilityService.cs` | Computes visible paths from caller roles and path role grants (DENY/ALLOW) |
| JSON filter | `BBT.Workflow.Application/Authorization/InstanceDataRoleFilter.cs` | Filters a JsonElement to only include visible paths |
| Integration | `InstanceQueryAppService` | Applies filter in GetInstanceDataAsync and BuildInstanceOutputAsync using effective caller roles (static + $InstanceStarter / $PreviousUser when instance present) |
| Effective roles | `ITransitionAuthorizationManager.GetEffectiveCallerRolesForFieldVisibilityAsync` | Builds static roles plus $InstanceStarter / $PreviousUser when CurrentUser.ActorUserName matches instance.CreatedBy or last manual transition CreatedBy |
| CurrentUser roles | `BBT.Workflow.Domain/CurrentUser/CurrentUserHeaderExtensions.cs` | `ParseRolesFromHeader`: splits header `role` by space or comma → array for Roles |

## Out of Scope

- **Editability** is not part of this feature; it is handled at transition level.
- **Extensions** script output is not filtered by schema roles in this implementation.
- **Authorize / authorization matrix** endpoint behaviour is unchanged except that they now evaluate **all** `ICurrentUser.Roles` (any role allowed → allowed).
