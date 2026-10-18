# Release Lines — Parallel Patch Lines and Backports

How a fix reaches an older minor line (`release-vX.Y`) without moving the `latest` channels
backwards. This page is the single home of the process; the workflows and scripts implement it:
[`backport.yml`](../../.github/workflows/backport.yml),
[`build-and-publish-images.yml`](../../.github/workflows/build-and-publish-images.yml),
[`meta-sync.mjs`](../../scripts/governance/meta-sync.mjs). Rule ids (`PR-RELEASE-LINE`,
`PR-E2E-RUN`) are defined in [reviewer: evidence §4](../code-review/reviewers/evidence.md).

## Supported lines

Two lines are supported at a time: the latest minor line (N) and the one before it (N-1).
Everything older gets no fixes. `master` is where the next minor is built; each released minor has a
branch `release-vX.Y`.

## When a line becomes backport-only

A line is backport-only once the first PR of the next minor merges to `master`. From then on the
next line `release-vX.(Y+1)` is cut from `master` for its first release, and the previous line
accepts only backports (and emergency fixes, below).

## Normal fix flow (upstream-first)

1. Merge the fix to `master` as usual (full contract, E2E evidence, fragment).
2. Add the label `backport/release-vX.Y` to the merged PR, before or after the merge. The
   [`Backport` workflow](../../.github/workflows/backport.yml) cherry-picks it onto a
   `backport-<n>-to-release-vX.Y` branch and opens a PR whose body starts with
   `Backport of #<n>`, with the original title and milestone.
3. If the cherry-pick conflicts, the action comments on the original PR instead; a person resolves
   it by cherry-picking manually onto the backport branch and opening the PR with the same first
   body line.
4. Review and merge the backport PR into `release-vX.Y`. `PR-RELEASE-LINE` accepts it because of
   the `Backport of #<n>` line; the E2E evidence comes from the original PR, so a SHA that is not in
   the backport's commits is only a warning.

## Emergency fix (release-first)

When the fix cannot wait for `master`: open the PR against `release-vX.Y` and add the label
`needs-forward-port` (this satisfies `PR-RELEASE-LINE`). Open the forward-port PR to `master` the
same day. Remove the label when the forward-port PR is merged.

## vnext-meta on parallel lines

- **Latest line:** the existing release back-merge into `master` applies unchanged.
- **Older lines:** the release workflow's `sync-meta-to-master` job opens or updates the PR
  `meta-sync/vX.Y.Z` (`chore(meta): sync vnext-meta for vX.Y.Z`) to `master`, produced by
  `meta-sync.mjs`. Merge it promptly: until then `master` does not know the patch release and the
  next stamp can misorder history.
- Fragments consumed on both sides become leftovers that the next stamp deletes
  ([format and leftovers](../../vnext-meta/unreleased/README.md)).
- Limitation: an item or feature key that `master` later deletes comes back on the next sync from an
  older line that still has it. Delete it on that line too, or fix upstream-first.

The job uses `master`'s copy of the scripts, so it works only for releases made after this change
is on `master`.

## Channels

| Channel | Latest line | Older line |
|---|---|---|
| Docker | `latest` and the line tag `release-vX.Y` | only the line tag `release-vX.Y` |
| npm dist-tag | `latest` | `release-vX.Y` |
| GitHub Release | marked "latest" | not marked |
| Helm | triggered with the version | **skipped** |

A release is "latest" when its version is at or above the highest existing stable tag `vX.Y.Z`
(re-publishing the highest version stays latest). The chart is not triggered for older lines because
it would roll its `appVersion` back; the payload carries `is_latest` and `release_line` so the chart
can opt in later. Until then patch-line deployments set the image tag through values. Handoff:
[release-line-charts](../integration/vnext-helm-charts/release-line-charts.md).

## Milestones

Each patch release has its own milestone `vX.Y.Z` (for example `v0.0.101` next to `v0.1.0`). The
backport PR copies the milestone of the original PR; if the patch ships under a different version,
change it on the backport PR before merging.

## Labels a repo admin creates

- `backport/release-vX.Y` — one per supported line (create the next one when a line is cut, delete
  the oldest when it leaves support).
- `needs-forward-port` — marks an emergency fix on a release line.

## Release-owner checklist

- [ ] The backport PR carries `Backport of #<n>` (or `needs-forward-port` for an emergency fix) and
      the right milestone.
- [ ] The release is published from the `release-vX.Y` branch; the run shows the expected channels
      (table above).
- [ ] Older line: the `meta-sync/vX.Y.Z` PR is open, green and merged promptly.
- [ ] Emergency fix: the forward-port PR to `master` is open the same day, then the label is removed.
- [ ] Any `appVersion` or chart change for an older line is a deliberate manual step.
