# Codebase Navigation & Architecture Rule (Graphify First)

Applies whenever `graphify-out/graph.json` exists in the repo (see the `graphify` skill,
`.graphifyignore`). Governs how to answer architecture, dependency, and "how does X relate to Y"
questions — before falling back to manual exploration.

1. **Never grep blindly.** Do not scan the whole codebase with `grep`/`find`, and do not read
   multiple files sequentially, just to understand architecture or dependencies. If a knowledge
   graph already exists, query it first — it is cheaper and more precise than re-deriving structure
   from raw text search.
2. **Consult graphify first.** If `graphify-out/graph.json` exists:
   - Relationship questions ("How does X connect to Y?", "What depends on Y?"): run
     `graphify path "X" "Y"`.
   - Component understanding ("What does X do?", "What touches X?"): run `graphify explain "X"`.
   - Open-ended architecture discovery ("How does the subflow lifecycle work end to end?"): run
     `graphify query "<question>"`.
   - Full command reference and the query/path/explain flow: `.claude/skills/graphify/SKILL.md`.
3. **Read files only after pruning.** Use the graph's answer to identify the exact source files or
   symbols involved, then open only those with `Read`/`Grep` for the actual implementation detail.
   The graph tells you *where* to look; it is not a substitute for reading the code you are about
   to change.
4. **No graph yet, or it's stale:** if `graphify-out/graph.json` is missing, or the question is
   about files added/changed since the last build, say so and offer to run `/graphify` (or
   `/graphify --update` for an incremental refresh) before falling back to manual search.
