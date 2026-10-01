#!/usr/bin/env bash
# Checks the AI guidance files (AGENTS.md, CLAUDE.md, .claude/rules, .claude/skills, .claude/agents,
# .cursor/rules) for the mistakes that have already happened: unparseable frontmatter, overlong
# descriptions, broken relative links, a rule without its Cursor pointer, a heading written twice,
# an always-loaded set that has quietly grown past its budget, and a skill or agent missing from
# the capability catalog (docs/ai-capabilities.md).
#
# Usage: scripts/check-ai-guidance.sh            (exit 1 on errors, warnings never fail)
# Budgets (bytes) can be overridden: ALWAYS_BUDGET=45000 SCOPED_BUDGET=40000 scripts/check-ai-guidance.sh
# Rules: docs/agent-onboarding.md § Editing the AI guidance.

set -euo pipefail
cd "$(dirname "$0")/.."

ALWAYS_BUDGET="${ALWAYS_BUDGET:-45000}" SCOPED_BUDGET="${SCOPED_BUDGET:-40000}" python3 - <<'PY'
import os, re, sys, glob

try:
    import yaml
except ImportError:
    sys.exit("check-ai-guidance: python3 'yaml' module missing (pip install pyyaml)")

errors, warnings = [], []
DESC_LIMIT = 350

def frontmatter(path):
    text = open(path, encoding="utf-8").read()
    if not text.startswith("---\n"):
        return None, text
    end = text.find("\n---", 4)
    if end < 0:
        errors.append(f"{path}: frontmatter not closed")
        return None, text
    try:
        return yaml.safe_load(text[4:end]) or {}, text[end + 4:]
    except yaml.YAMLError as e:
        errors.append(f"{path}: frontmatter is not valid YAML ({str(e).splitlines()[0]})")
        return None, text

# 1. Skill / agent frontmatter and description size.
desc_bytes = 0
for path in sorted(glob.glob(".claude/skills/*/SKILL.md") + glob.glob(".claude/agents/*.md")):
    fm, _ = frontmatter(path)
    if fm is None:
        if path not in " ".join(errors):
            errors.append(f"{path}: missing frontmatter")
        continue
    desc = str(fm.get("description", ""))
    if not desc:
        errors.append(f"{path}: empty description")
    desc_bytes += len(desc.encode())
    if len(desc) > DESC_LIMIT:
        warnings.append(f"{path}: description is {len(desc)} chars (> {DESC_LIMIT})")

# 2. Rules: always-on vs path-scoped, Cursor pointer present and in the same mode.
always_files = ["AGENTS.md", "CLAUDE.md"]
scoped_files = []
for path in sorted(glob.glob(".claude/rules/*.md")):
    fm, _ = frontmatter(path)
    scoped = bool(fm and fm.get("paths"))
    (scoped_files if scoped else always_files).append(path)
    name = os.path.splitext(os.path.basename(path))[0]
    mdc = f".cursor/rules/{name}.mdc"
    if not os.path.exists(mdc):
        errors.append(f"{path}: no Cursor pointer {mdc}")
        continue
    mtext = open(mdc, encoding="utf-8").read()
    if f"@{path}" not in mtext:
        errors.append(f"{mdc}: does not @-include {path}")
    cursor_always = re.search(r"^alwaysApply:\s*true", mtext, re.M) is not None
    if cursor_always == scoped:
        errors.append(f"{mdc}: alwaysApply={cursor_always} but the rule is {'path-scoped' if scoped else 'always-on'}")
for mdc in glob.glob(".cursor/rules/*.mdc"):
    target = re.search(r"^@(\S+)", open(mdc, encoding="utf-8").read(), re.M)
    if not target or not os.path.exists(target.group(1)):
        errors.append(f"{mdc}: pointer target missing")

# 3. Relative links and duplicate headings in every guidance file.
guidance = always_files + scoped_files + ["docs/agent-onboarding.md"] \
    + glob.glob(".claude/skills/**/*.md", recursive=True) + glob.glob(".claude/agents/*.md") \
    + glob.glob("docs/code-review/**/*.md", recursive=True)
link_re = re.compile(r"\]\(([^)\s]+)\)")
for path in sorted(set(guidance)):
    text = open(path, encoding="utf-8").read()
    text_no_code = re.sub(r"`[^`\n]*`", "", re.sub(r"```.*?```", "", text, flags=re.S))
    for target in link_re.findall(text_no_code):
        if re.match(r"^(https?:|mailto:|#)", target) or "<" in target:
            continue
        file_part = target.split("#", 1)[0]
        if not file_part:
            continue
        resolved = os.path.normpath(os.path.join(os.path.dirname(path), file_part))
        if not os.path.exists(resolved) and not resolved.startswith(("ai-docs", "../")) and "ai-docs/" not in resolved:
            errors.append(f"{path}: broken link -> {target}")
    if path not in always_files + scoped_files:
        continue  # skill reference files repeat per-check sub-headings on purpose
    seen = {}
    for heading in re.findall(r"^(#{2,4} .+)$", text_no_code, re.M):
        seen[heading] = seen.get(heading, 0) + 1
    for heading, n in seen.items():
        if n > 1:
            errors.append(f"{path}: heading repeated {n}x: {heading}")

# 4. Catalog coverage: every skill and agent is named in docs/ai-capabilities.md.
CATALOG = "docs/ai-capabilities.md"
if not os.path.exists(CATALOG):
    errors.append(f"{CATALOG}: missing")
else:
    catalog = open(CATALOG, encoding="utf-8").read()
    names = [os.path.basename(os.path.dirname(p)) for p in glob.glob(".claude/skills/*/SKILL.md")] \
        + [os.path.splitext(os.path.basename(p))[0] for p in glob.glob(".claude/agents/*.md")]
    for name in sorted(names):
        if f"`{name}`" not in catalog:
            errors.append(f"{CATALOG}: `{name}` is not in the catalog — add its row")

# 5. Budgets.
size = lambda files: sum(os.path.getsize(f) for f in files)
always_total = size(always_files) + desc_bytes
scoped_total = size(scoped_files)
local = os.path.getsize("CLAUDE.local.md") if os.path.exists("CLAUDE.local.md") else 0
always_budget, scoped_budget = int(os.environ["ALWAYS_BUDGET"]), int(os.environ["SCOPED_BUDGET"])
print(f"always-loaded : {always_total:>6} bytes (budget {always_budget}) "
      f"= {size(always_files)} files + {desc_bytes} skill/agent descriptions")
print(f"path-scoped   : {scoped_total:>6} bytes (budget {scoped_budget}) {', '.join(os.path.basename(f) for f in scoped_files)}")
if local:
    print(f"CLAUDE.local.md (machine-local, also loaded): {local} bytes")
if always_total > always_budget:
    warnings.append(f"always-loaded set {always_total} bytes exceeds budget {always_budget}")
if scoped_total > scoped_budget:
    warnings.append(f"path-scoped rules {scoped_total} bytes exceed budget {scoped_budget}")

for w in warnings:
    print(f"WARN  {w}")
for e in errors:
    print(f"ERROR {e}")
print(f"{len(errors)} error(s), {len(warnings)} warning(s)")
sys.exit(1 if errors else 0)
PY
