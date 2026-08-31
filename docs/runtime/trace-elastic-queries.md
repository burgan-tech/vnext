# Elastic Dev Tools Queries for the vNext Trace Tree

Copy-paste into Kibana → Dev Tools. Every query here was executed against a real
`traces-apm*` index before being written down; none of them is aspirational.

Index pattern used throughout: `.ds-traces-apm*,traces-apm*`.

## Three things that will silently break a hand-written query

**1. String tags live in `labels.*`, numeric tags live in `numeric_labels.*`.**
APM splits them by type. `labels.*` are `keyword`, `numeric_labels.*` are `scaled_float`.
So `labels.vnext_script_context_memo_hits` matches nothing — the memo counters are numeric:

| tag on the span | field to query |
|---|---|
| `cache.hit`, `cache.l1.hit`, `cache.source`, `cache.key`, `cache.store` | `labels.cache_hit`, `labels.cache_l1_hit`, `labels.cache_source`, … |
| `vnext.script.cache_hit`, `vnext.script.key` | `labels.vnext_script_cache_hit`, … |
| `vnext.hook.name`, `vnext.hook.mode`, `vnext.event.name` | `labels.vnext_hook_name`, … |
| `vnext.discovery.domain`, `vnext.discovery.endpoint_kind` | `labels.vnext_discovery_domain`, … |
| `vnext.step.outcome`, `vnext.lock.acquired`, `vnext.lock.kind` | `labels.vnext_step_outcome`, … |
| **`vnext.script.context.memo.hits`** | **`numeric_labels.vnext_script_context_memo_hits`** |
| **`vnext.script.mapping.memo.hits`** | **`numeric_labels.vnext_script_mapping_memo_hits`** |
| `vnext.step.order`, `vnext.data.size_bytes`, `vnext.chain_depth` | `numeric_labels.vnext_step_order`, … |

**2. Span names carry their subject**, by design: `Cache.Get/{cacheKey}`,
`Script.Compile/{identity}`, `Discovery.Resolve/{domain}`, `Lock.Acquire/{lockKey}`.
A plain `terms` agg on `span.name` therefore explodes into thousands of one-hit buckets.
The queries below normalize with a runtime field that keeps the part before the first `/`.
It reads `doc['span.name']` (doc-values) rather than `params._source` — same result,
much cheaper on a production-sized index.

**3. Durations are INCLUSIVE of children.** Summing a parent group (`Uow.Commit`,
`Step.*`, `Transition.LoadContext`, `TransitionJob.Execute`) counts time its children
already reported. Read `total_ms` for **leaf** spans (`Db.*`, `Cache.*`, `Script.*`,
`Lock.*`) and read `p50`/`p95` for parents. True *self* time needs parent→child
arithmetic across documents, which aggregations cannot do — use
`scripts/trace-profile.py trace <id>` for that.

---

## Query 1 — Which span burns the time, and are the caches hitting?

```json
GET .ds-traces-apm*,traces-apm*/_search
{
  "size": 0,
  "track_total_hits": false,
  "runtime_mappings": {
    "span_group": {
      "type": "keyword",
      "script": {
        "source": "if (!doc.containsKey('span.name') || doc['span.name'].size()==0) return; String n = doc['span.name'].value; int i = n.indexOf('/'); emit(i > 0 ? n.substring(0, i) : n);"
      }
    }
  },
  "query": {
    "bool": {
      "filter": [
        { "exists": { "field": "span.name" } },
        { "range":  { "@timestamp": { "gte": "now-24h" } } }
      ]
    }
  },
  "aggs": {
    "by_span": {
      "terms": { "field": "span_group", "size": 60, "order": { "total_ms": "desc" } },
      "aggs": {
        "total_ms":    { "sum": { "field": "span.duration.us", "script": { "source": "_value/1000" } } },
        "p":           { "percentiles": { "field": "span.duration.us", "percents": [50, 95, 99] } },
        "max_us":      { "max": { "field": "span.duration.us" } },
        "cache_l2":    { "terms": { "field": "labels.cache_hit", "size": 2 } },
        "cache_l1":    { "terms": { "field": "labels.cache_l1_hit", "size": 2 } },
        "cache_source":{ "terms": { "field": "labels.cache_source", "size": 4 } },
        "compile_hit": { "terms": { "field": "labels.vnext_script_cache_hit", "size": 2 } },
        "ctx_memo":    { "sum": { "field": "numeric_labels.vnext_script_context_memo_hits" } },
        "map_memo":    { "sum": { "field": "numeric_labels.vnext_script_mapping_memo_hits" } }
      }
    }
  }
}
```

**Reading it.** `cache_source` splits component-cache reads into `l1` / `l2` / `backend` — the
single tag that says which layer answered, instead of deriving it from a `cache.hit` +
`cache.l1.hit` combination. A `Cache.Get` whose source is `backend` is not a cheap Redis miss: the
DB load and the `Cache.Write` write-back both run inside it, so its duration covers all three.
`cache_l2` / `cache_l1` / `compile_hit` are `true`/`false` buckets —
`compile_hit: false` means Roslyn actually ran. `ctx_memo` / `map_memo` are counter tags
stamped on the *enclosing* span, so they sum per parent rather than counting spans.

**Narrowing to the spans you care about.** Without a filter the top buckets are dominated
by expensive parents (`CallLocal`, `Dapr invoke …`, `SyncTransitionStrategy.ExecuteAsync`)
and the cheap-but-frequent spans never make the cut. Add to `filter`:

```json
{ "bool": { "minimum_should_match": 1, "should": [
  { "prefix": { "span.name": "Cache." } },
  { "prefix": { "span.name": "Script." } },
  { "prefix": { "span.name": "Db." } },
  { "prefix": { "span.name": "Instance." } },
  { "prefix": { "span.name": "Uow." } },
  { "prefix": { "span.name": "Lock." } },
  { "prefix": { "span.name": "Discovery." } },
  { "prefix": { "span.name": "EventHook." } },
  { "prefix": { "span.name": "Transition." } },
  { "prefix": { "span.name": "Step." } }
]}}
```

To rank by call count instead of total time, change the terms order to
`{ "_count": "desc" }`; by tail latency, `{ "p.95": "desc" }`.

---

## Query 2 — One trace: every node grouped, with totals

Handles **both** spans and transactions. A transaction is a node too (it is what spans
hang under), and a spans-only query undercounts a trace that crosses services.

```json
GET .ds-traces-apm*,traces-apm*/_search
{
  "size": 0,
  "track_total_hits": true,
  "runtime_mappings": {
    "node_name": {
      "type": "keyword",
      "script": {
        "source": "if (doc.containsKey('span.name') && doc['span.name'].size()>0) { String n=doc['span.name'].value; int i=n.indexOf('/'); emit(i>0? n.substring(0,i):n); return; } if (doc.containsKey('transaction.name') && doc['transaction.name'].size()>0) { String n=doc['transaction.name'].value; int i=n.indexOf('/'); emit('TXN ' + (i>0? n.substring(0,i):n)); }"
      }
    },
    "node_ms": {
      "type": "double",
      "script": {
        "source": "if (doc.containsKey('span.duration.us') && doc['span.duration.us'].size()>0) { emit(doc['span.duration.us'].value/1000.0); return; } if (doc.containsKey('transaction.duration.us') && doc['transaction.duration.us'].size()>0) { emit(doc['transaction.duration.us'].value/1000.0); }"
      }
    }
  },
  "query": {
    "bool": { "filter": [ { "term": { "trace.id": "PASTE_TRACE_ID_HERE" } } ] }
  },
  "aggs": {
    "by_node": {
      "terms": { "field": "node_name", "size": 100, "order": { "total_ms": "desc" } },
      "aggs": {
        "total_ms": { "sum": { "field": "node_ms" } },
        "max_ms":   { "max": { "field": "node_ms" } },
        "p":        { "percentiles": { "field": "node_ms", "percents": [50, 95] } }
      }
    },
    "grand_total_ms": { "sum": { "field": "node_ms" } },
    "services":       { "terms": { "field": "service.name", "size": 10 } },
    "cache_l2":       { "terms": { "field": "labels.cache_hit", "size": 2 } },
    "cache_l1":       { "terms": { "field": "labels.cache_l1_hit", "size": 2 } },
    "compile_hit":    { "terms": { "field": "labels.vnext_script_cache_hit", "size": 2 } },
    "ctx_memo":       { "sum": { "field": "numeric_labels.vnext_script_context_memo_hits" } },
    "map_memo":       { "sum": { "field": "numeric_labels.vnext_script_mapping_memo_hits" } }
  }
}
```

`hits.total.value` is the node count for the trace. `grand_total_ms` is the sum of every
node's inclusive duration, so it is **much larger than wall-clock** — on a verified sample
it was 127 s of summed duration for a 191 ms trace, because every level re-counts its
children. Use it as a relative weight, never as elapsed time.

### Finding a trace id to paste

```json
GET .ds-traces-apm*,traces-apm*/_search
{
  "size": 10,
  "query": { "bool": { "filter": [
    { "prefix": { "span.name": "Uow.Commit" } },
    { "range": { "@timestamp": { "gte": "now-1h" } } }
  ]}},
  "sort": [ { "@timestamp": "desc" } ],
  "_source": ["trace.id", "span.name", "span.duration.us", "service.name"]
}
```

Swap the `prefix` for whatever you are hunting — `Discovery.Resolve`,
`EventHook.`, `Script.Compile`, `Instance.Query.Prepare`. To find the *slow* ones instead
of the recent ones, sort by `{ "span.duration.us": "desc" }`.

---

## Query 3 — Cache effectiveness on its own

```json
GET .ds-traces-apm*,traces-apm*/_search
{
  "size": 0,
  "track_total_hits": false,
  "query": { "bool": { "filter": [ { "range": { "@timestamp": { "gte": "now-24h" } } } ] } },
  "aggs": {
    "component_cache": {
      "filter": { "exists": { "field": "labels.cache_hit" } },
      "aggs": {
        "l2":      { "terms": { "field": "labels.cache_hit", "size": 2 } },
        "l1":      { "terms": { "field": "labels.cache_l1_hit", "size": 2 } },
        "by_store":{ "terms": { "field": "labels.cache_store", "size": 20 },
                     "aggs": { "hit": { "terms": { "field": "labels.cache_hit", "size": 2 } } } }
      }
    },
    "compile_cache": {
      "filter": { "exists": { "field": "labels.vnext_script_cache_hit" } },
      "aggs": {
        "hit":       { "terms": { "field": "labels.vnext_script_cache_hit", "size": 2 } },
        "miss_cost": { "filter": { "term": { "labels.vnext_script_cache_hit": "false" } },
                       "aggs": { "ms": { "sum": { "field": "span.duration.us", "script": { "source": "_value/1000" } } },
                                 "p":  { "percentiles": { "field": "span.duration.us", "percents": [50, 95, 99] } } } }
      }
    },
    "memo_hits": {
      "filter": { "bool": { "should": [
        { "exists": { "field": "numeric_labels.vnext_script_context_memo_hits" } },
        { "exists": { "field": "numeric_labels.vnext_script_mapping_memo_hits" } }
      ], "minimum_should_match": 1 } },
      "aggs": {
        "script_context": { "sum": { "field": "numeric_labels.vnext_script_context_memo_hits" } },
        "mapping_factory": { "sum": { "field": "numeric_labels.vnext_script_mapping_memo_hits" } }
      }
    }
  }
}
```

`compile_cache.miss_cost` is the one to watch: it isolates the spans where Roslyn actually
compiled and reports what that cost. A healthy warm process has a high hit ratio and a
miss cost concentrated in the first minutes after start — see
[`script-compile-measurement-2026-08-27.md`](script-compile-measurement-2026-08-27.md).

---

## Local baseline (for comparison)

Measured on the local stack over 30 days, using Query 1 with the prefix filter:

| span | n | total ms | p50 | p95 | cache |
|---|---|---|---|---|---|
| `Cache.Get` | 32 817 | 65 156 | 0.41 | 4.71 | L2 97 %, L1 98 % |
| `Db.SELECT` | 30 738 | 65 650 | 1.14 | 5.27 | — |
| `Script.Compile` | 4 435 | 84 953 | 0.03 | 97.95 | compile 84 % hit |
| `Instance.Load` | 4 103 | 25 568 | 2.44 | 28.10 | — |
| `Uow.Commit` | 3 491 | 20 919 | 0.28 | 20.74 | — |
| `Lock.Acquire` | 3 285 | 6 434 | 0.94 | 4.01 | — |
| `Cache.GenerationGet` | 1 735 | 121 610 | 1.67 | 12.68 | max **114 s** |

That `Cache.GenerationGet` maximum is a real outlier against a 1.67 ms median and is worth
its own look; everything else is unremarkable.

## Related

- [Trace/span tree reference](trace-span-tree.md) — every span, its source and its tags.
- `scripts/trace-profile.py` — the same two queries as a CLI, plus real **self** time per
  span in `trace` mode, which aggregations cannot compute.
