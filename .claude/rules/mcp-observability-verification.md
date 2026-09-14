# Verifying a Change Through the MCP Servers (Always Apply)

Applies whenever a change is exercised against a locally running runtime — a manual request, an
integration-test run, a load script. Governs what counts as evidence that the change works.

The servers themselves are declared in `.mcp.json` (`openobserve`, `postgres`, `elasticsearch`,
`redis`) and only answer while the docker infra is up (`etc/docker/run-docker.sh status`).

## The rule

**A green test run is not, by itself, a verified result.** After exercising a change, confirm it
through the MCP servers and quote the measured numbers in the report:

- **`openobserve`** — the runtime logs and the trace/span tree: where the time actually went,
  avg/p95/max for the endpoints and pipeline steps the change touches, and the root cause of any
  failure (`instanceId`, `flow`, `transitionKey`, trace id).
- **`postgres`** — what was actually persisted (instance, transition record, job, incident rows).
- **`redis`** — cache and lock state when the change touches either.
- **`elasticsearch`** — APM traces when the full docker profile is up.

Report what was measured, not what the test summary said. "Tests passed" and "the change is
correct" are different claims.

## Three traps that have already produced wrong conclusions

1. **A failing suite is often the environment, not the runtime.** A 79-failure run turned out to be
   a missing MockLab (`Connection refused (localhost:3001)`, started separately from
   `vnext-example/docker-compose.yml`), plus a partner domain that was not up
   (`Discovery:700002 … Service discovery is disabled`) and a task the installed system package does
   not carry (`Task:Unknown:<key> — WorkflowTask not found in runtime backend`). Cluster the failures
   by error signature from the logs before reading any of them as a regression.
2. **A long span with an unexplained gap is usually awaiting an episode traced elsewhere.** One
   activation episode is its own trace, rooted at its lane anchor (`vnext_trace_lane`,
   `vnext_trace_lane_anchor`, `vnext_lane_seq`) and deliberately not nested under the HTTP or job
   span that awaits it — so the parent looks empty in the middle. Follow the anchor instead of
   calling it a stall. Related: `docs/runtime/trace-lanes.md`.
3. **Read the component definition before calling a duration a regression.** A 3-second fan-out was
   the scenario's own `batchTimeoutSeconds: 3` over items pointed at a MockLab route seeded with
   `delayMs: 1500`, and a subflow start's latency was the child's full synchronous activation
   (runtime-generated subflow starts are always `sync=true`), not queue backpressure. Also note
   `POST /job/{jobName}` is Dapr's job callback, not an API endpoint — its duration is the job's
   work and must not be read as client latency.

## When a server is unreachable

The backends are the local docker infra, so they are often simply not up. That changes what you can
claim, and it must change the report — never let an unavailable server turn into a silently skipped
verification. Order of response:

1. **Say so.** State which server was unavailable and which check therefore did not run. A result
   reported without its evidence must be labelled as such: "tests passed, not confirmed against
   traces" is honest; "verified" is not.
2. **Use the fallbacks**, which cover most of the need:
   - host logs on disk — `.vnext-local/domains/<domain>/logs/{orchestration,execution,inbox,outbox}.log`
     (paths are listed in `ai-docs/local-environments/<domain>.md`);
   - `docker exec vnext-postgres psql -U postgres -d <db>` for persisted rows;
   - `docker exec vnext-redis redis-cli` for cache and lock keys;
   - `curl localhost:5080` / `localhost:9200` directly if only the MCP layer is broken.
   Timing evidence is the one thing with no good fallback — host logs carry no span tree, so an
   assertion about durations or about where time went cannot be made without the trace data.
3. **Offer to bring the infra up** (`etc/docker/run-docker.sh status`, then `up <domain>`) rather
   than starting it unasked — and never take down a stack owned by another compose file.

Two mechanics worth knowing: a server that failed to connect at session start stays unavailable for
that whole session — an agent cannot re-establish it, the user has to reconnect it (`/mcp` in an
interactive session), so report the need instead of retrying. And `CONNECT_TIMEOUT` means the
connection failed, not that the capability does not exist — see the `postgres` case below before
concluding anything about a server.

## OpenObserve query mechanics (verified 2026-09-11)

- Both logs and traces live in one stream named **`vnext`** — not `default`. `StreamList` with
  `type: logs` / `type: traces` confirms one stream each.
- `SearchSQL` takes the stream type as a **top-level `type`** parameter (`logs` | `traces` |
  `metrics`). A `stream_type` nested inside `request_body` is ignored, so a trace query silently
  resolves against the logs stream and fails with `unknown field 'operation_name'`.
- `start_time` / `end_time` are **microseconds since epoch** and may not be `0` ("invalid time
  range"). Mark the window before the run.
- The log message column is **`body`**, not `message`; correlation columns are lowercase
  (`instanceid`, `flow`, `transitionkey`).
- Do **not** pass `agent_options.output_format: "csv"` — it returns `hits: []` with a non-zero
  `total`, i.e. the rows are lost.
- In traces, `span_kind` is the numeric OTel enum as a string: `1` internal, `2` server, `3` client,
  `4` producer, `5` consumer. Useful attributes: `vnext_activation_outcome`, `vnext_script_cache_hit`,
  `vnext_lock_kind` / `vnext_lock_acquired`, `vnext_fanout_*`, `db_operation_name` + `db_statement`.

## Known startup failure: `postgres` times out

The `postgres` server defaults to database `Aether_WorkflowDb`, which exists only after core's
DbMigrator has run (`run-docker.sh up core`). Without it the server logs
`FATAL: database "Aether_WorkflowDb" does not exist` and then blocks ~30 s retrying **before**
answering `initialize` — exactly the client's connect timeout, so it surfaces as `CONNECT_TIMEOUT`
and looks like a broken server or a slow `uvx` cold start. It is neither. List the databases
(`docker exec vnext-postgres psql -U postgres -lqt`) before suspecting uvx, the port or the
credentials; fix by bringing core up, or by overriding `VNEXT_PG_DATABASE` in the environment —
never by editing the tracked `.mcp.json`.
