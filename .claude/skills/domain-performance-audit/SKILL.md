---
name: domain-performance-audit
description: Performance audit of a vNext domain package (a vnext-template repo such as vnext-onboarding or vnext-onboarding-ekyc) against the current runtime. Scans workflows, tasks, extensions, functions, views and scripts, then writes a short prioritised report. Use when the user says "performans incele", "domain performans", "domain audit", "bu domaini incele", "performance review", "iyileştirme noktası var mı", or points at a domain package and asks what to improve. For a diff of THIS runtime repo use workflow-code-review or pr-review instead.
---

# Domain Performance Audit

Reviews a **domain package** — someone else's repo built on this runtime — and answers one
question: what is costing time or reliability, and what should they do about it.

Not a code-quality review. Naming, formatting, try/catch hygiene and clean-code concerns are out
of scope; domain teams have their own review for that. Stay on runtime cost and runtime capability.

## Scope check first

The target must be a vNext domain package: a repo with `vnext.config.json` at its root and the
component folders it names. If there is no `vnext.config.json`, stop and say so — this skill has
nothing to measure.

If the user did not name a path, ask for it. Do not guess between sibling domain repos.

## Procedure

1. **Scan.** `python3 <skill-dir>/scan.py <domain-root>` — read-only, stdlib only, writes nothing
   to the target. `--json` gives the same data machine-readable.
   Task, trigger, extension and error-action enums are parsed out of this repo's own source at run
   time (`TaskEnums.cs`, `TransitionEnums.cs`, `ExtensionEnums.cs`, `ErrorAction.cs`), so the
   scanner cannot drift from the runtime. A `!! ENUM:` line in the output means it could not read
   one — the numbers are then raw and a finding that depends on a type name must not be written
   until that is fixed.
2. **Interpret** the output against [CHECKS.md](CHECKS.md). Every check there names what the
   scanner prints, why it costs, and what to recommend.
3. **Verify before claiming.** The scanner measures structure, not intent. Several checks are
   explicitly "candidate, confirm by reading". Open the file before you write a finding.
4. **Confirm runtime behaviour against this repo**, never from memory. `.claude/rules/` and
   `/docs` describe the runtime; when they disagree with the code, the code wins. A finding that
   says "the runtime does X" and is wrong costs the domain team real work.
5. **Ask which items to keep.** A raw scan produces more findings than anyone will act on. Offer
   the list, let the user split it into must / nice-to-have, and write only what they kept.
6. **Write the report** in the style below.

Read-only with one exception: the report file. Never edit the domain package's components, never
commit, never push.

## The report

Ask where it goes. Match the target repo's existing convention — `docs/code-reviews/DDMMYY-*.md`
in vnext-onboarding, numbered `docs/NN-*.md` in vnext-onboarding-ekyc. If the folder has an index,
offer to add the row; do not edit the index without being asked.

Turkish, and written the way a PO or an architect writes, not the way a template generates:

- Each item is a short heading that reads as a sentence, then a few sentences: what is happening,
  where it lives, what it costs, what to do. Finish with a bold `**Çözüm:**` line.
- No repeating `**Dosya:** / **Sorun:** / **Yapılacak:**` scaffolding, no effort/priority table,
  no "Beklenen sonuç" code block.
- Numbers carry the argument. "16 HTTP çağrısı, timeout toplamı 205 sn" beats "çok fazla çağrı".
- Two sections, `## Yapılmalı` and `## Yapılsa iyi olur`, then a one-line order and a short
  measurement paragraph.
- Name the runtime capability that solves it (`CacheAsideTask` tip 18, `GetInstancesTask` tip 15,
  aynı `order` ile paralel çalıştırma, error boundary `action: 1`) — that is the value this audit
  adds over the domain team reading their own code.

## Measurement

The audit is static. It says where the cost is, not how much it is. Close with how to measure:
a local run of the domain, then `Instance.Activation`, `Step.*`, `Task.Execute` and
`Script.Execute` span durations from OpenObserve, before and after. Never state a latency
improvement that was not measured — see `.claude/rules/mcp-observability-verification.md`.
