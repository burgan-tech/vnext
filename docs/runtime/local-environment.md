# Local environment — bring up domain X (agent runbook)

The procedure an agent follows to run a domain locally. Entry point: `etc/docker/run-docker.sh`
(`--help` lists everything); ports and the other modes: `AGENTS.md` § Build, run, test. Related:
[integration testing](../testing/integration-testing.md), [multi-domain hosting](multi-domain-hosting.md).

**What `up` starts.** Only `postgres`, `redis`, `vault`, `dapr-scheduler` and `otel-collector`, plus the
domain's sidecars and hosts. The observability backends the MCP servers read — `openobserve`,
`elasticsearch`, `kibana`, `apm-server` — are services of the same `etc/docker/docker-compose.yml` but
are **not** started by `up` (this is the "full docker profile" the SessionStart hook mentions). Start
them beside a running domain with
`docker compose -f etc/docker/docker-compose.yml up -d openobserve elasticsearch kibana apm-server`,
or run plain `./run-docker.sh` (the whole compose file, attached). Without them, report results as
"not confirmed against traces" ([observability verification](../testing/observability-verification.md)).

Commands run without a terminal, so the script never prompts — pass everything explicitly.

1. `cd etc/docker && ./run-docker.sh status` and read `ai-docs/local-environments/README.md` (if present):
   is X already registered, which offset does it have, is anything else running?
2. Decide the offset: `core` → none (offset 0). Another domain → its recorded offset, else the next
   free multiple of 10 (`./run-docker.sh plan X --offset N` shows ports and app-ids; it refuses collisions).
   Tell the user the offset you picked before starting. Several domains at once and memory is tight →
   co-host them on one host set instead: `./run-docker.sh up <primary> --with X,Y` (one database, co-hosted
   domains in `<domain>_`-prefixed schemas; `Y=vNext_Y` keeps Y in its own database;
   [Multi-Domain Hosting](multi-domain-hosting.md)).
3. `./run-docker.sh up X --offset N` (`--no-build` only if the build is
   known to be fresh). It brings up infra + sidecars, runs DbMigrator, starts the hosts and waits for
   `/health`. Expect a few minutes on a cold build.
4. If it refuses with "docker infra is running from another compose file", stop: another stack (for
   example a cross-domain lab) owns the infra. Report it and let the user decide — do not `down` it yourself.
5. Load components with the vNext CLI (`wf`, from `burgan-tech/vnext-workflow-cli`, installed
   globally) from the domain package repo — for the examples that is `../vnext-example`. `up` has
   already registered the domain in `wf` with the right API port and database; you only switch to it:
   ```bash
   cd ../vnext-example
   wf domain use X && wf domain active      # must print X — the CLI keeps ONE global active domain
   wf check && wf sync                      # sync = add missing; update = changed; reset = force
   ```
   Never run `wf sync` without the `use` step: it publishes to whatever domain was active last time.
   Each of `sync` / `update` / `reset` ends with one `POST definitions/publish/completed` — the
   runtime's post-deployment hook, and the **only automatic** invalidation of the discovery endpoint
   cache (which no longer has a TTL). An older `wf` still calls the removed `definitions/re-initialize`
   and swallows the 404 silently, so cross-domain endpoints stay as they were at startup. See
   [Publish-completed hook](publish-completed-hook.md).
   System flows (`@burgan-tech/vnext-core-runtime`) go through **that domain's** init container
   (`init` for core on :3005, `init-X` on :3005+offset, already aimed at X's orchestration):
   `curl -X POST localhost:<3005+offset>/api/package/runtime/publish -H 'content-type: application/json' -d '{"appDomain":"X"}'`
   — the call is **asynchronous**: it answers `{"statusUrl": "/api/package/publish/status/<id>"}`; poll
   that URL (or `docker logs init-X`) until the job says completed before running `wf sync`.
   Known quirk: `wf check` may print "API: Not accessible" while `/health` is 200 and `wf sync` works;
   trust `curl localhost:<port>/health`. Verified 2026-09-08 on core: system + example workflows
   loaded, smoke workflow start → transition → Completed.
   Every port above is written in `ai-docs/local-environments/X.md` — read it instead of computing.
6. Report the base URL and the record path `ai-docs/local-environments/X.md`; point integration tests
   at `VNEXT_BASE_URL=http://localhost:<4201+offset>`. Stop with `./run-docker.sh down X`.
