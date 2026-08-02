# View Display Modes (SDI / MDI)

## Purpose

A view tells the client *how* it should be presented, not just what to render. Historically that
was a single `display` string describing a single-document presentation — full page, popup,
drawer. Client renderers also support a multi-document interface, where several documents are
open side by side, and that layout needs its own answer: does this view open as a tab, a floating
window, or a split pane?

`display` therefore accepts a per-mode declaration while keeping the original string form working
unchanged.

## Authoring

Both shapes are valid on `attributes.display` of a `sys-views` component.

```jsonc
"display": "popup"                          // legacy - means sdi: "popup"
"display": { "sdi": "popup", "mdi": "tab" } // both modes
"display": { "mdi": "window" }              // MDI only
```

At least one of `sdi` / `mdi` must carry a value; an empty object is rejected by component
validation.

### Vocabulary

| Mode | Values |
| --- | --- |
| `sdi` | `full-page`, `popup`, `bottom-sheet`, `top-sheet`, `drawer`, `inline` |
| `mdi` | `tab`, `window`, `split`, `inline` |

The JSON schema constrains these values. The **runtime** does not: like `renderer`, the values are
a documented vocabulary (`ViewDisplayMode.Sdi.*` / `ViewDisplayMode.Mdi.*`) and any non-blank
string is accepted, so a domain can pilot a new value without a runtime release.

## Runtime model

`View.DisplayModes` (`ViewDisplay`) is the parsed declaration. Two accessors sit on top:

| Member | Meaning |
| --- | --- |
| `View.Display` | The SDI value, or empty string. This is what every pre-existing call site reads. |
| `View.MdiDisplay` | The MDI value, or null. |
| `View.DisplayModes` | Both, or null when no display is declared. |

`ViewDisplayJsonConverter` handles both input shapes, mirroring `ViewDefinitionJsonConverter`'s
approach to the `view` / `views` back-compat pair. Writing mirrors the authored shape: an SDI-only
declaration is written back as a bare string, anything declaring `mdi` as an object — so component
JSON round-trips without churn.

## Response contract

The view response keeps `display` as the SDI string and adds `modes`:

```jsonc
{
  "key": "customer-form",
  "type": "Json",
  "display": "popup",                       // SDI value; empty when only mdi is declared
  "modes": { "sdi": "popup", "mdi": "tab" }, // null when no display is declared
  "renderer": "pseudo-ui",
  "content": { }
}
```

Clients predating MDI support keep reading `display` and are unaffected. Clients that render both
modes read `modes` and pick the value for the interface they are in.

This applies to both resolution paths — local (`IComponentCacheStore`) and remote (another
domain's instance read) — and to the function contract endpoint, which embeds the same view shape.

## Monitoring

The component summary projects `display` from either shape, reading `sdi` out of the object form,
so the existing monitor display filter keeps matching regardless of how a view was authored.

## Change safety

- Adding `mdi` to an existing view is additive: `display` in the response is unchanged.
- Moving a view to `mdi`-only empties the response `display` field — check consumers first.
- Component validation requires at least one mode; it does not check membership in the vocabulary.

## References

- `src/BBT.Workflow.Domain/Definitions/Views/View.cs`
- `src/BBT.Workflow.Domain/Definitions/Views/ViewDisplay.cs`, `ViewDisplayJsonConverter.cs`, `ViewEnums.cs`
- `src/BBT.Workflow.Application/Instances/ViewContentResolutionService.cs`
- `vnext-schema/schemas/view-definition.schema.json`
