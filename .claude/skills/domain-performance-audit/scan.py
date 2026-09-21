#!/usr/bin/env python3
"""Static scanner for a vNext domain package.

Reads vnext.config.json, walks the component folders and prints the measurements the
domain-performance-audit skill interprets. Read-only: never writes to the target repo.

Usage:  python3 scan.py <domain-package-root> [--json]
"""
import json, sys, os, glob, re, collections

# ---------------------------------------------------------------------------
# Enums are parsed from the runtime source, never copied. This file lives at
# <runtime>/.claude/skills/domain-performance-audit/, so the source tree is four
# levels up. If it is not reachable the scan still runs and prints raw numbers.
# ---------------------------------------------------------------------------
RUNTIME_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ENUM_SOURCES = {
    "TaskType": "src/BBT.Workflow.Domain/Definitions/Tasks/TaskEnums.cs",
    "TriggerType": "src/BBT.Workflow.Domain/Definitions/Transitions/TransitionEnums.cs",
    "ErrorAction": "src/BBT.Workflow.Domain/Definitions/ErrorBoundary/ErrorAction.cs",
    "ExtensionType": "src/BBT.Workflow.Domain/Definitions/Extensions/ExtensionEnums.cs",
    "ExtensionScope": "src/BBT.Workflow.Domain/Definitions/Extensions/ExtensionEnums.cs",
}
ENUM_WARNINGS = []


def parse_enum(name):
    """{value: MemberName} for one C# enum, read from the runtime source."""
    path = os.path.join(RUNTIME_ROOT, ENUM_SOURCES[name])
    try:
        src = open(path, encoding="utf-8").read()
    except OSError:
        ENUM_WARNINGS.append(f"{name}: kaynak okunamadi ({ENUM_SOURCES[name]})")
        return {}
    m = re.search(r"enum\s+" + name + r"\b[^{]*\{(.*?)\n\}", src, re.S)
    if not m:
        ENUM_WARNINGS.append(f"{name}: enum govdesi bulunamadi")
        return {}
    body = re.sub(r"//.*?$|/\*.*?\*/", "", m.group(1), flags=re.S | re.M)
    out = {}
    for mem, val in re.findall(r"^\s*([A-Za-z_]\w*)\s*=\s*(\d+)", body, re.M):
        out[val] = mem
    if not out:
        ENUM_WARNINGS.append(f"{name}: uye okunamadi")
    return out


TASK_TYPES = parse_enum("TaskType")
TRIGGER_RAW = parse_enum("TriggerType")
TRIGGER = {int(v): n.lower() for v, n in TRIGGER_RAW.items()} or \
          {0: "manual", 1: "automatic", 2: "scheduled", 3: "event"}
ERROR_ACTIONS = parse_enum("ErrorAction")
EXT_TYPE_RAW = parse_enum("ExtensionType")
EXT_SCOPE_RAW = parse_enum("ExtensionScope")
EXT_TYPE = {int(v): n for v, n in EXT_TYPE_RAW.items()}
EXT_SCOPE = {int(v): n for v, n in EXT_SCOPE_RAW.items()}

# Task types whose latency belongs to a backend outside the runtime. This set is a
# JUDGEMENT, not an enum fact, so it is written as member NAMES and resolved against
# the parsed enum — a renumbering can no longer silently change what it matches, and a
# rename is reported instead of swallowed.
# Deliberately excluded: StateStore / CacheAside (they talk to the Dapr state store,
# which is the fix for this cost rather than an instance of it), Python (runs in the
# Execution service, not a third-party backend), and every in-process type.
OUTBOUND_TASK_NAMES = {"DaprHttpEndpoint", "DaprBinding", "DaprService", "DaprPubSub",
                       "Http", "Soap", "ExternalHttp"}
NETWORK_TYPES = {v for v, n in TASK_TYPES.items() if n in OUTBOUND_TASK_NAMES}
_missing = OUTBOUND_TASK_NAMES - set(TASK_TYPES.values())
if TASK_TYPES and _missing:
    ENUM_WARNINGS.append(f"TaskType icinde bulunamayan uye: {sorted(_missing)} "
                         "- enum degismis, OUTBOUND_TASK_NAMES guncellenmeli")

# Runtime capabilities a domain package should be reaching for; reported when absent.
CAPABILITY_TASK_NAMES = ["GetInstances", "StateStore", "CacheAside", "FanOut"]


def load(p):
    with open(p, encoding="utf-8") as fh:
        return json.load(fh)


def jsons(root, sub):
    return [f for f in glob.glob(os.path.join(root, sub, "**", "*.json"), recursive=True)
            if f"{os.sep}.meta{os.sep}" not in f]


def state_view(state):
    """Both authoring shapes: legacy singular `view`, current `views[]`."""
    v = state.get("view")
    if isinstance(v, dict):
        return [v]
    return list(state.get("views") or [])


def hooks_of(state):
    for h in ("onEntries", "onExits", "onExecutionTasks"):
        for t in state.get(h) or []:
            yield h, t
    for tr in state.get("transitions") or []:
        for t in tr.get("onExecutionTasks") or []:
            yield f"transition:{tr.get('key')}", t


def longest_auto_chain(states):
    g = {s["key"]: [t["target"] for t in (s.get("transitions") or [])
                    if t.get("triggerType") == 1 and t.get("target")] for s in states}
    best = []
    def walk(n, path, seen):
        nonlocal best
        if len(path) > len(best):
            best = list(path)
        for m in g.get(n, []):
            if m not in seen:
                walk(m, path + [m], seen | {m})
    for s in states:
        walk(s["key"], [s["key"]], {s["key"]})
    return best


def scan(root):
    cfg = load(os.path.join(root, "vnext.config.json"))
    paths = cfg.get("paths", {})
    croot = os.path.join(root, paths.get("componentsRoot", "."))
    out = {
        "domain": cfg.get("domain"),
        "runtimeVersion": cfg.get("runtimeVersion"),
        "schemaVersion": cfg.get("schemaVersion"),
    }

    # ---- tasks -------------------------------------------------------------
    tasks, ttypes, notimeout, net_tasks = {}, collections.Counter(), [], {}
    signatures = collections.defaultdict(list)
    for f in jsons(croot, paths.get("tasks", "Tasks")):
        d = load(f); a = d.get("attributes", {}) or {}
        t = str(a.get("type")); cfgt = a.get("config") or {}
        tasks[d["key"]] = {"type": t, "file": f, "timeout": cfgt.get("timeoutSeconds"),
                           "url": cfgt.get("url") or cfgt.get("methodName") or ""}
        ttypes[f"{t} {TASK_TYPES.get(t, '?')}"] += 1
        # identical (type, config) under two keys: a change has to be made twice.
        # Only meaningful when config actually carries the behaviour — a Script task keeps its
        # code in `mapping`, so its empty config would make every script task look like a twin.
        if cfgt:
            signatures[(t, json.dumps(cfgt, sort_keys=True, ensure_ascii=False))].append(d["key"])
        if t in NETWORK_TYPES:
            net_tasks[d["key"]] = tasks[d["key"]]
            if cfgt.get("timeoutSeconds") is None:
                notimeout.append(d["key"])
    out["duplicateTasks"] = sorted((sorted(v) for v in signatures.values() if len(v) > 1),
                                   key=lambda g: g[0])
    out["tasks"] = {"total": len(tasks), "byType": dict(ttypes), "missingTimeout": notimeout}
    present = {x.split(" ", 1)[0] for x in ttypes}
    out["unusedCapabilities"] = [f"{v} {n}" for v, n in sorted(TASK_TYPES.items(), key=lambda kv: int(kv[0]))
                                 if n in CAPABILITY_TASK_NAMES and v not in present]
    out["enumWarnings"] = ENUM_WARNINGS

    # self-domain dapr/http calls
    dom = cfg.get("domain")
    out["selfCalls"] = sorted(k for k, v in net_tasks.items()
                              if dom and f"/{dom}/" in (v["url"] or ""))
    # a network task reading through the INSTANCE-scoped function address:
    # .../instances/{id}/functions/{fn} — the instance is loaded before the function runs
    out["instanceScopedReads"] = sorted(
        k for k, v in net_tasks.items()
        if re.search(r"/instances/[^/?]+/functions/", v["url"] or ""))

    # ---- workflows ---------------------------------------------------------
    wfs, subedges, eb = [], [], collections.Counter()
    for f in sorted(jsons(croot, paths.get("workflows", "Workflows"))):
        d = load(f); a = d["attributes"]; states = a.get("states") or []
        tt = collections.Counter(); decision = []; multihook = {}; cumulative = 0; nettouch = 0
        for s in states:
            trs = s.get("transitions") or []
            for tr in trs:
                tt[TRIGGER.get(tr.get("triggerType"), "?")] += 1
            k = ((s.get("subFlow") or {}).get("process") or {}).get("key")
            if k:
                subedges.append((d["key"], s["key"], k, (s.get("subFlow") or {}).get("type")))
            if not state_view(s) and not any(hooks_of(s)) and trs \
               and all(t.get("triggerType") == 1 for t in trs):
                decision.append(s["key"])
            grouped = collections.defaultdict(list)
            for hook, t in hooks_of(s):
                tk = (t.get("task") or {}).get("key")
                grouped[hook].append((t.get("order"), tk))
                info = tasks.get(tk)
                if info and info["type"] in NETWORK_TYPES:
                    nettouch += 1
                    cumulative += info["timeout"] or 0
            for hook, lst in grouped.items():
                if len(lst) >= 2:
                    multihook[hook] = lst
        chain = longest_auto_chain(states)
        txt = open(f, encoding="utf-8").read()
        selfTargets = len(re.findall(r'"target"\s*:\s*"\$self"', txt))
        loadData = len(re.findall(r'"loadData"\s*:\s*true', txt))
        sig = tuple(sorted(x["key"] for x in states))
        wfs.append({
            "key": d["key"], "type": a.get("type"), "file": f,
            "states": len(states), "transitions": sum(tt.values()), "trigger": dict(tt),
            "decisionOnlyStates": decision, "multiTaskHooks": multihook,
            "networkCalls": nettouch, "cumulativeTimeoutSeconds": cumulative,
            "longestAutoChain": chain, "hasTimeout": bool(a.get("timeout")),
            "selfTargets": selfTargets, "loadData": loadData, "signature": sig,
            "hasErrorBoundary": '"onError"' in txt,
        })
        for m in re.finditer(r'"action"\s*:\s*(\d+)', open(f, encoding="utf-8").read()):
            eb[m.group(1)] += 1
    out["workflows"] = wfs
    out["subflowEdges"] = subedges
    out["subflowTypes"] = dict(collections.Counter(e[3] for e in subedges))
    out["errorBoundaryActions"] = dict(eb)
    out["workflowsWithoutErrorBoundary"] = sum(1 for w in wfs if not w["hasErrorBoundary"])
    out["eventTransitions"] = sum(w["trigger"].get("event", 0) for w in wfs)
    out["selfTargets"] = sum(w["selfTargets"] for w in wfs)
    out["loadDataTotal"] = sum(w["loadData"] for w in wfs)

    # workflows whose state sets are identical -> copy-pasted flows
    bysig = collections.defaultdict(list)
    for w in wfs:
        bysig[w["signature"]].append(w["key"])
    out["duplicateWorkflows"] = [v for v in bysig.values() if len(v) > 1]

    # repeated state templates across the whole domain
    tmpl = collections.Counter()
    for w in wfs:
        d = load(w["file"])
        for st in d["attributes"].get("states") or []:
            key = (st.get("stateType"), st.get("subType"),
                   len(st.get("transitions") or []),
                   tuple(sorted((t.get("task") or {}).get("key") or "" for _, t in hooks_of(st))),
                   bool(state_view(st)))
            tmpl[key] += 1
    out["stateTemplates"] = [{"count": c, "stateType": k[0], "subType": k[1],
                              "transitions": k[2], "tasks": [x for x in k[3] if x]}
                             for k, c in tmpl.most_common(5) if c >= 3]
    wfkeys = {w["key"] for w in wfs}
    out["missingSubflowTargets"] = sorted({e[2] for e in subedges} - wfkeys)

    # ---- extensions --------------------------------------------------------
    exts = []
    for f in sorted(jsons(croot, paths.get("extensions", "Extensions"))):
        d = load(f); a = d["attributes"]
        tk = ((a.get("task") or {}).get("task") or {}).get("key") or (a.get("task") or {}).get("key")
        info = tasks.get(tk) or {}
        exts.append({"key": d["key"], "type": EXT_TYPE.get(a.get("type"), a.get("type")),
                     "scope": EXT_SCOPE.get(a.get("scope"), a.get("scope")),
                     "task": tk, "taskType": TASK_TYPES.get(info.get("type"), info.get("type")),
                     "network": info.get("type") in NETWORK_TYPES})
    out["extensions"] = exts

    # extension attach density per view point
    attach = collections.Counter(); perstate = []
    for w in wfs:
        d = load(w["file"])
        for s in d["attributes"].get("states") or []:
            views = state_view(s)
            names = []
            for v in views:
                names += list(v.get("extensions") or [])
            for n in set(names):
                attach[n] += 1
            netset = sorted({n for n in names
                             if any(e["key"] == n and e["network"] for e in exts)})
            if netset:
                perstate.append({"workflow": w["key"], "state": s["key"],
                                 "views": len(views), "networkExtensions": netset,
                                 "callsPerRead": len(netset)})
    out["extensionAttachments"] = dict(attach)
    out["extensionHotStates"] = perstate

    # ---- functions ---------------------------------------------------------
    fns = []
    for f in sorted(jsons(croot, paths.get("functions", "Functions"))):
        d = load(f); a = d["attributes"]
        tk = ((a.get("task") or {}).get("task") or {}).get("key")
        fns.append({"key": d["key"], "scope": a.get("scope"), "task": tk,
                    "taskType": TASK_TYPES.get((tasks.get(tk) or {}).get("type"))})
    out["functions"] = fns
    # TaskScope codes are letters, not numbers (src/.../Definitions/TaskScope.cs):
    # D = Domain, F = Flow, I = Instance. "I" forces the client onto the instance URL.
    out["instanceScopedFunctions"] = sorted(f["key"] for f in fns
                                            if str(f["scope"]).upper() == "I")

    # ---- views / schemas: orphans -----------------------------------------
    def orphans(sub):
        defined = {}
        for f in jsons(croot, sub):
            try:
                defined[load(f)["key"]] = f
            except Exception:
                pass
        others = " ".join(open(f, encoding="utf-8").read()
                          for f in glob.glob(os.path.join(croot, "**", "*.json"), recursive=True)
                          if f"{os.sep}{sub}{os.sep}" not in f and f"{os.sep}.meta{os.sep}" not in f)
        return len(defined), sorted(k for k in defined if f'"{k}"' not in others)
    for label, sub in (("views", paths.get("views", "Views")),
                       ("schemas", paths.get("schemas", "Schemas")),
                       ("tasks", paths.get("tasks", "Tasks"))):
        n, orp = orphans(sub)
        out[f"{label}Orphans"] = {"defined": n, "unreferenced": orp}

    # schema wiring
    bound = 0; nulls = 0
    for w in wfs:
        txt = open(w["file"], encoding="utf-8").read()
        nulls += len(re.findall(r'"schema"\s*:\s*null', txt))
        bound += len(re.findall(r'"schema"\s*:\s*\{', txt))
    out["schemaBinding"] = {"bound": bound, "null": nulls}

    # ---- scripts -----------------------------------------------------------
    cs = sorted(((os.path.getsize(f), f) for f in
                 glob.glob(os.path.join(croot, "**", "*.csx"), recursive=True)), reverse=True)
    out["scripts"] = {"count": len(cs), "totalKB": round(sum(s for s, _ in cs) / 1024, 1),
                      "largest": [{"bytes": s, "file": f} for s, f in cs[:10]]}

    csx_text = ""
    for _, f in cs:
        try:
            csx_text += open(f, encoding="utf-8").read()
        except OSError:
            pass
    task_text = " ".join(open(v["file"], encoding="utf-8").read() for v in tasks.values())
    out["scriptCalls"] = {
        "GetSecret": len(re.findall(r"\bGetSecret\s*\(", csx_text)),
        "GetConfigValue": len(re.findall(r"\bGetConfigValue\s*\(", csx_text)),
        "contextRelated": len(re.findall(r"\bcontext\.Related\b", csx_text)),
        "InstanceQueryFluent": len(re.findall(r"\bInstanceQuery\b", csx_text)),
        "handBuiltFilterJson": len(re.findall(r"JsonSerializer\.Serialize", csx_text)),
        # a payload serialized INTO a log call — a per-execution serialize plus a log write,
        # and the body often carries PII. Counted separately: most Serialize hits are this,
        # not a hand-built filter.
        "serializeInLog": len(re.findall(
            r"\bLog(?:Information|Warning|Error|Debug|Critical|Trace)\s*\([^;]*?"
            r"JsonSerializer\.Serialize", csx_text)),
    }
    out["filterPaths"] = sorted(set(re.findall(r'attributes\.[A-Za-z0-9_.]+',
                                               csx_text + task_text)))

    # views: heaviest payloads
    vfiles = sorted(((os.path.getsize(f), f) for f in jsons(croot, paths.get("views", "Views"))),
                    reverse=True)
    out["heaviestViews"] = [{"bytes": b, "file": f} for b, f in vfiles[:5]]

    # embedded base64 weight + duplicate blobs
    total = dup = blobs = 0
    seen = collections.Counter()
    for f in glob.glob(os.path.join(croot, "**", "*.json"), recursive=True):
        if f"{os.sep}.meta{os.sep}" in f:
            continue
        s = open(f, encoding="utf-8").read(); total += len(s)
        for m in re.finditer(r'"code"\s*:\s*"([^"]{200,})"', s):
            blobs += 1; seen[m.group(1)] += 1
    code = sum(len(k) * v for k, v in seen.items())
    dup = sum((v - 1) * len(k) for k, v in seen.items() if v > 1)
    out["embeddedCode"] = {"jsonBytes": total, "codeBytes": code,
                           "sharePct": round(code * 100 / total, 1) if total else 0,
                           "blobs": blobs, "distinct": len(seen),
                           "duplicateBytes": dup,
                           "maxRepeat": max(seen.values()) if seen else 0}
    return out


def human(o):
    p = print
    p(f"# domain: {o['domain']}  runtime: {o['runtimeVersion']}  schema: {o['schemaVersion']}")
    for w in o.get("enumWarnings", []):
        p(f"  !! ENUM: {w}")
    p("")
    p("## tasks")
    p(f"  total {o['tasks']['total']}  " + ", ".join(f"{k}={v}" for k, v in sorted(o['tasks']['byType'].items())))
    if o["tasks"]["missingTimeout"]:
        p(f"  !! timeout YOK: {o['tasks']['missingTimeout']}")
    if o["unusedCapabilities"]:
        p(f"  hic kullanilmayan yetenek: {', '.join(o['unusedCapabilities'])}")
    if o["selfCalls"]:
        p(f"  !! kendi domainine cagri ({len(o['selfCalls'])}): {', '.join(o['selfCalls'])}")
    if o["instanceScopedReads"]:
        p(f"  !! instance adresinden okuma ({len(o['instanceScopedReads'])}): "
          f"{', '.join(o['instanceScopedReads'])}")
    for grp in o["duplicateTasks"]:
        p(f"  !! ikiz task tanimi: {', '.join(grp)}")
    p("")
    p("## workflows")
    for w in o["workflows"]:
        p(f"  {w['key']:44} {w['type']} states={w['states']:3} trans={w['transitions']:3} "
          f"{w['trigger']} net={w['networkCalls']} cumTimeout={w['cumulativeTimeoutSeconds']}s "
          f"chain={len(w['longestAutoChain'])} decision={len(w['decisionOnlyStates'])} "
          f"timeout={'var' if w['hasTimeout'] else 'yok'}")
        for hook, lst in w["multiTaskHooks"].items():
            orders = [x[0] for x in lst]
            if len(set(orders)) == len(orders) and len(orders) > 1:
                p(f"      seri hook {hook}: {lst}")
    p(f"  subflow kenari {len(o['subflowEdges'])} tip={o['subflowTypes']}")
    if o["missingSubflowTargets"]:
        p(f"  !! tanimi olmayan subflow: {o['missingSubflowTargets']}")
    ebl = ", ".join(f"{k} {ERROR_ACTIONS.get(k, '?')}={v}"
                    for k, v in sorted(o["errorBoundaryActions"].items(), key=lambda kv: int(kv[0])))
    p(f"  errorBoundary: {ebl or 'hic tanim yok'}  "
      f"({o['workflowsWithoutErrorBoundary']} workflow'da hic yok)")
    if o["duplicateWorkflows"]:
        for grp in o["duplicateWorkflows"]:
            p(f"  !! ayni state kumesine sahip workflow: {', '.join(grp)}")
    for t in o["stateTemplates"]:
        p(f"  tekrarlayan state kalibi x{t['count']}: stateType={t['stateType']} "
          f"subType={t['subType']} trans={t['transitions']} tasks={t['tasks']}")
    p(f"  event transition (triggerType 3): {o['eventTransitions']}   "
      f"$self hedefi: {o['selfTargets']}   loadData:true: {o['loadDataTotal']}")
    p("")
    p("## extensions")
    for e in o["extensions"]:
        p(f"  {e['key']:38} type={e['type']} scope={e['scope']} task={e['task']} "
          f"({e['taskType']}){'  NETWORK' if e['network'] else ''}")
    broken = [e for e in o["extensions"] if e["taskType"] is None]
    if broken:
        p(f"  !! task tanimi bulunamadi: {[e['key'] for e in broken]}")
    for h in sorted(o["extensionHotStates"], key=lambda x: -x["callsPerRead"]):
        p(f"  !! {h['workflow']}::{h['state']} okuma basina {h['callsPerRead']} agli cagri "
          f"({h['views']} view): {', '.join(h['networkExtensions'])}")
    p("")
    p("## functions")
    for f in o["functions"]:
        p(f"  {f['key']:52} scope={f['scope']} task={f['task']} ({f['taskType']})")
    if o["instanceScopedFunctions"]:
        p(f"  !! instance scope ({len(o['instanceScopedFunctions'])}): "
          f"{', '.join(o['instanceScopedFunctions'])}")
    p("")
    p("## olu agirlik")
    for k in ("viewsOrphans", "schemasOrphans", "tasksOrphans"):
        d = o[k]
        p(f"  {k:16} tanimli={d['defined']:3} referanssiz={len(d['unreferenced'])}")
        if d["unreferenced"]:
            p(f"      {', '.join(d['unreferenced'][:20])}{' ...' if len(d['unreferenced']) > 20 else ''}")
    p(f"  schema baglama: bagli={o['schemaBinding']['bound']} null={o['schemaBinding']['null']}")
    p("")
    p("## sorgu / gizli bilgi")
    sc = o["scriptCalls"]
    p(f"  GetSecret={sc['GetSecret']} GetConfigValue={sc['GetConfigValue']} "
      f"context.Related={sc['contextRelated']} InstanceQuery(fluent)={sc['InstanceQueryFluent']} "
      f"JsonSerializer.Serialize={sc['handBuiltFilterJson']} "
      f"(log icinde={sc['serializeInLog']})")
    if o["filterPaths"]:
        p(f"  filtrelenen attributes yolu ({len(o['filterPaths'])}): {', '.join(o['filterPaths'][:12])}"
          f"{' ...' if len(o['filterPaths']) > 12 else ''}")
    p("")
    p("## en agir viewlar")
    for v in o["heaviestViews"]:
        p(f"    {v['bytes']:7} {v['file']}")
    p("")
    p("## scriptler")
    s = o["scripts"]
    p(f"  {s['count']} dosya, {s['totalKB']} KB")
    for x in s["largest"][:6]:
        p(f"    {x['bytes']:7} {x['file']}")
    e = o["embeddedCode"]
    p(f"  gomulu base64: {e['codeBytes']//1024} KB / {e['jsonBytes']//1024} KB JSON (%{e['sharePct']}), "
      f"{e['blobs']} blob {e['distinct']} farkli, tekrar {e['duplicateBytes']//1024} KB, en cok {e['maxRepeat']}x")


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        print(__doc__); sys.exit(1)
    res = scan(args[0])
    if "--json" in sys.argv:
        print(json.dumps(res, indent=2, ensure_ascii=False))
    else:
        human(res)
