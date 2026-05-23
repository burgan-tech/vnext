# Spec 05: Migration and Deprecation

## Purpose

Move legacy documentation into an archive, make the new structure the default entry point,
and retire old links in a controlled way.

## Migration Rules

- Preserve old files under `docs/archive/` until their useful content is migrated.
- New docs should link to source references, not archived pages, unless the archive still
  contains the only detailed explanation.
- When a legacy page is superseded, add the replacement link to the archive page if the
  page is edited.
- Do not delete archived content until downstream links are checked.

## Deprecation Policy

| Change | Policy |
| --- | --- |
| New architecture page replaces old topic | Keep old page in archive and link from new README. |
| Old page contains outdated behavior | Mark it archived before deleting or rewriting. |
| Consumer-facing contract changed | Add migration note to contracts or specs. |
| Dead link found | Prefer redirect/update over deletion. |

## Acceptance Checklist

- `docs/README.md` is the default entry point.
- `docs/archive/` contains legacy material.
- New docs avoid linking users into stale pages for primary flows.
- A link check is run before merging.

## Source Alignment

Review these paths when the spec changes:

- `docs/README.md`
- `docs/archive/`
- `docs/documentation-rebuild-plan.md`

