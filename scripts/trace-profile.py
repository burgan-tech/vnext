#!/usr/bin/env python3
"""
Span profiling against Elastic APM for the vNext trace tree.

Two modes:

  profile              Aggregate: which span kind burns how much time, and are the caches hitting?
  trace <trace-id>     One trace: every span grouped, inclusive AND self time.

Usage:
  python3 scripts/trace-profile.py profile
  python3 scripts/trace-profile.py profile --since 2h --service vnext-orchestration
  python3 scripts/trace-profile.py profile --raw            # print the ES query JSON and exit
  python3 scripts/trace-profile.py trace 4682ca695dac4f7021c1a1bc4419faa1

Env:
  ES_URL   default http://localhost:9200

Three things this script exists to get right — see docs/runtime/trace-span-tree.md:

1. STRING tags land in `labels.*` (keyword); NUMERIC tags land in `numeric_labels.*`
   (scaled_float). Querying `labels.vnext_script_context_memo_hits` silently returns
   nothing — the memo counters are numeric and live under `numeric_labels`.

2. Span names carry their subject: `Cache.Get/{key}`, `Script.Compile/{identity}`,
   `Discovery.Resolve/{domain}`, `Lock.Acquire/{lockKey}`. A plain terms agg on
   `span.name` explodes into thousands of buckets, so we normalize with a runtime
   field that keeps only the part before the first '/'.

3. Span durations are INCLUSIVE of children. Summing a parent group (Transition.LoadContext,
   Uow.Commit, Step.*) double-counts the time its children already report. In `profile`
   mode read total_ms for LEAF spans (Db.*, Cache.*, Script.*) and read p50/p95 for
   parents. `trace` mode computes real self time, so there the numbers add up.
"""
import argparse, json, os, sys, urllib.request

ES = os.environ.get("ES_URL", "http://localhost:9200")
IDX = ".ds-traces-apm*,traces-apm*"

# Keeps only the part before the first '/', so Cache.Get/foo and Cache.Get/bar
# collapse to one bucket. Task.Execute.{key} is left alone deliberately: the task
# key is the interesting dimension there, not noise.
SPAN_GROUP_RUNTIME = {
    "span_group": {
        "type": "keyword",
        "script": {
            "source": (
                "def n = params._source?.span?.name; if (n == null) return; "
                "int i = n.indexOf('/'); emit(i > 0 ? n.substring(0, i) : n);"
            )
        },
    }
}


def es(path, body=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(
        f"{ES}/{IDX}/{path}", data=data,
        headers={"Content-Type": "application/json"},
        method=method or ("POST" if data else "GET"),
    )
    try:
        return json.loads(urllib.request.urlopen(req, timeout=120).read())
    except urllib.error.URLError as e:
        sys.exit(f"Elastic unreachable at {ES} ({e}). Is the stack up?")


def ms(us):
    return (us or 0) / 1000.0


# ---------------------------------------------------------------- profile mode

def profile_query(since, service, only, size, sort):
    must = [{"exists": {"field": "span.name"}},
            {"range": {"@timestamp": {"gte": f"now-{since}"}}}]
    if service:
        must.append({"term": {"service.name": service}})
    # --only narrows to the span families you are actually profiling. Without it the
    # top-N is dominated by expensive PARENTS (CallLocal, Dapr invoke, transition),
    # and the cheap-but-frequent spans this tree was built to expose never make the cut.
    if only:
        must.append({"bool": {"should": [{"prefix": {"span.name": p}} for p in only],
                              "minimum_should_match": 1}})
    args_size = size
    SORT_KEY = {"total": "total_us", "count": "_count", "p95": "pct.95"}[sort]
    return {
        "size": 0,
        "runtime_mappings": SPAN_GROUP_RUNTIME,
        "query": {"bool": {"must": must}},
        "aggs": {
            "by_span": {
                "terms": {"field": "span_group", "size": args_size, "order": {SORT_KEY: "desc"}},
                "aggs": {
                    "total_us": {"sum": {"field": "span.duration.us"}},
                    "avg_us": {"avg": {"field": "span.duration.us"}},
                    "pct": {"percentiles": {"field": "span.duration.us", "percents": [50, 95, 99]}},
                    "max_us": {"max": {"field": "span.duration.us"}},
                    # component cache (CacheSet<T>): L2 hit/miss and the L1 flag
                    "cache_hit": {"terms": {"field": "labels.cache_hit", "size": 3}},
                    "cache_l1": {"terms": {"field": "labels.cache_l1_hit", "size": 3}},
                    # compile cache: false means Roslyn actually ran
                    "script_hit": {"terms": {"field": "labels.vnext_script_cache_hit", "size": 3}},
                    # the two memo counters are NUMERIC -> numeric_labels, not labels
                    "ctx_memo": {"sum": {"field": "numeric_labels.vnext_script_context_memo_hits"}},
                    "map_memo": {"sum": {"field": "numeric_labels.vnext_script_mapping_memo_hits"}},
                },
            }
        },
    }


def run_profile(args):
    body = profile_query(args.since, args.service, args.only, args.size, args.sort)
    if args.raw:
        print(json.dumps(body, indent=2))
        return
    r = es("_search", body)
    buckets = r["aggregations"]["by_span"]["buckets"]
    if not buckets:
        print(f"No spans in the last {args.since}."); return

    print(f"SPAN PROFILE  ·  last {args.since}"
          + (f"  ·  service={args.service}" if args.service else "")
          + f"  ·  {ES}\n")
    print(f"{'span':<34}{'n':>7}{'total ms':>11}{'avg':>8}{'p50':>8}{'p95':>9}{'max':>9}  cache")
    print("-" * 108)
    for b in buckets:
        p = b["pct"]["values"]
        note = []
        for label, agg in (("L2", "cache_hit"), ("L1", "cache_l1"), ("compile", "script_hit")):
            t = {x["key"]: x["doc_count"] for x in b[agg]["buckets"]}
            if t:
                hit = t.get("true", 0); miss = t.get("false", 0); tot = hit + miss
                if tot:
                    note.append(f"{label} {hit}/{tot} ({100*hit/tot:.0f}%)")
        for label, agg in (("ctx-memo", "ctx_memo"), ("map-memo", "map_memo")):
            v = b[agg]["value"]
            if v:
                note.append(f"{label} {int(v)}")
        print(f"{b['key']:<34}{b['doc_count']:>7}{ms(b['total_us']['value']):>11.0f}"
              f"{ms(b['avg_us']['value']):>8.2f}{ms(p.get('50.0')):>8.2f}"
              f"{ms(p.get('95.0')):>9.2f}{ms(b['max_us']['value']):>9.1f}  {', '.join(note)}")

    print("\ntotal ms is INCLUSIVE of children — meaningful for leaf spans (Db.*, Cache.*, Script.*),")
    print("double-counted for parents (Step.*, Uow.Commit, Transition.LoadContext). Read p50/p95 there.")
    print("cache column: hits/total. compile 'false' = Roslyn actually ran; memo counters are per-parent sums.")


# ------------------------------------------------------------------ trace mode

def run_trace(args):
    tid = args.trace_id
    hits, after = [], None
    while True:
        body = {"size": 1000, "query": {"term": {"trace.id": tid}},
                "sort": [{"timestamp.us": "asc"}, {"_doc": "asc"}],
                "_source": ["span.name", "span.duration.us", "span.id", "parent.id",
                            "transaction.name", "transaction.duration.us", "transaction.id",
                            "processor.event", "service.name", "timestamp.us",
                            "labels.cache_hit", "labels.cache_l1_hit",
                            "labels.vnext_script_cache_hit", "labels.vnext_hook_name",
                            "numeric_labels.vnext_script_context_memo_hits",
                            "numeric_labels.vnext_script_mapping_memo_hits"]}
        if after:
            body["search_after"] = after
        r = es("_search", body)
        h = r["hits"]["hits"]
        if not h:
            break
        hits += h
        if len(h) < 1000:
            break
        after = h[-1]["sort"]

    if not hits:
        sys.exit(f"No documents for trace.id={tid}. Wrong id, or outside the index retention.")

    # Normalize transactions and spans into one shape. A transaction is a node too:
    # it is what spans hang under, and ignoring it makes the tree look rootless.
    nodes = {}
    for h in hits:
        s = h["_source"]
        ev = s.get("processor", {}).get("event")
        if ev == "transaction":
            nid = s["transaction"]["id"]
            name = s["transaction"].get("name", "?")
            dur = s["transaction"].get("duration", {}).get("us", 0)
            kind = "txn"
        elif ev == "span":
            nid = s["span"]["id"]
            name = s["span"].get("name", "?")
            dur = s["span"].get("duration", {}).get("us", 0)
            kind = "span"
        else:
            continue
        nodes[nid] = {"id": nid, "name": name, "dur": dur, "kind": kind,
                      "parent": s.get("parent", {}).get("id"),
                      "svc": s.get("service", {}).get("name", ""),
                      "src": s}

    # Self time = own duration minus the duration of DIRECT children. Children can run
    # in parallel (FanOut, Task.WhenAll), so the subtraction can go negative — clamp at 0
    # and report how many nodes were clamped rather than hiding it.
    child_sum, clamped = {}, 0
    for n in nodes.values():
        if n["parent"] in nodes:
            child_sum[n["parent"]] = child_sum.get(n["parent"], 0) + n["dur"]
    for n in nodes.values():
        self_us = n["dur"] - child_sum.get(n["id"], 0)
        if self_us < 0:
            self_us = 0
            clamped += 1
        n["self"] = self_us

    groups = {}
    for n in nodes.values():
        key = n["name"].split("/")[0]
        g = groups.setdefault(key, {"n": 0, "incl": 0, "self": 0, "max": 0, "kind": n["kind"]})
        g["n"] += 1; g["incl"] += n["dur"]; g["self"] += n["self"]
        g["max"] = max(g["max"], n["dur"])

    roots = [n for n in nodes.values() if n["parent"] not in nodes]
    wall = max((n["dur"] for n in roots), default=0)

    print(f"TRACE {tid}")
    print(f"{len(nodes)} nodes ({sum(1 for n in nodes.values() if n['kind']=='txn')} transactions)"
          f"  ·  root wall time {ms(wall):.1f} ms"
          f"  ·  services: {', '.join(sorted({n['svc'] for n in nodes.values() if n['svc']}))}\n")

    print(f"{'span':<40}{'n':>5}{'self ms':>10}{'%':>7}{'incl ms':>10}{'max':>9}")
    print("-" * 81)
    tot_self = sum(g["self"] for g in groups.values()) or 1
    for k, g in sorted(groups.items(), key=lambda kv: -kv[1]["self"]):
        print(f"{k:<40}{g['n']:>5}{ms(g['self']):>10.2f}{100*g['self']/tot_self:>6.1f}%"
              f"{ms(g['incl']):>10.2f}{ms(g['max']):>9.2f}")
    print("-" * 81)
    print(f"{'TOTAL self':<40}{len(nodes):>5}{ms(tot_self):>10.2f}{100:>6.1f}%")

    cache = {}
    for n in nodes.values():
        lb = n["src"].get("labels", {}) or {}
        nl = n["src"].get("numeric_labels", {}) or {}
        for f, lbl in (("cache_hit", "component cache (L2)"),
                       ("cache_l1_hit", "component cache (L1)"),
                       ("vnext_script_cache_hit", "script compile cache")):
            if f in lb:
                d = cache.setdefault(lbl, {"hit": 0, "miss": 0})
                d["hit" if str(lb[f]).lower() == "true" else "miss"] += 1
        for f, lbl in (("vnext_script_context_memo_hits", "ScriptContext memo hits"),
                       ("vnext_script_mapping_memo_hits", "mapping-factory memo hits")):
            if f in nl:
                cache.setdefault(lbl, {"sum": 0})["sum"] += int(nl[f])

    if cache:
        print("\nCACHE")
        for k, v in cache.items():
            if "sum" in v:
                print(f"  {k:<28} {v['sum']}")
            else:
                t = v["hit"] + v["miss"]
                print(f"  {k:<28} {v['hit']}/{t} hit ({100*v['hit']/t:.0f}%), {v['miss']} miss")
    else:
        print("\nCACHE: no cache-tagged spans in this trace.")

    if clamped:
        print(f"\n{clamped} node(s) had children running in parallel (self time clamped to 0) —"
              " expected under FanOut / Task.WhenAll.")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("profile", help="aggregate span profile + cache effectiveness")
    p.add_argument("--since", default="24h", help="ES date-math window, e.g. 30m, 2h, 7d (default 24h)")
    p.add_argument("--service", help="filter to one service.name")
    p.add_argument("--only", help="comma-separated span-name prefixes, e.g. Cache.,Script.,Db.,Instance.")
    p.add_argument("--size", type=int, default=40, help="max span groups (default 40)")
    p.add_argument("--sort", choices=["total", "count", "p95"], default="total",
                   help="order buckets by total time (default), call count, or p95")
    p.add_argument("--raw", action="store_true", help="print the ES query JSON and exit")
    p.set_defaults(fn=run_profile)

    t = sub.add_parser("trace", help="one trace: every span, self and inclusive time")
    t.add_argument("trace_id")
    t.set_defaults(fn=run_trace)

    args = ap.parse_args()
    if getattr(args, "only", None):
        args.only = [x.strip() for x in args.only.split(",") if x.strip()]
    args.fn(args)


if __name__ == "__main__":
    main()
