# Mapping form: `.csx` vs declarative blocks - src at 48c26d3f (harness added in the same commit as this file)

Question: is a declarative block faster than the `.csx` `IMapping` it replaces?

Command: `dotnet run -c Release --project test/BBT.Workflow.Benchmarks -- --filter '*MappingForm*' --job short`
Job: short (IterationCount=3, WarmupCount=3, LaunchCount=1, Server GC). Machine: Apple M3 (8 cores), macOS 27.2.1,
.NET SDK 10.0.201 / runtime 10.0.5, BenchmarkDotNet 0.14.0. Allocation is the stable metric; means are indicative
(ShortRun error bars are wide, see the `Error` column).

## Shape

The same mapping in both forms, run through the runner production uses (`ScriptTaskMappingRunner` /
`DeclarativeTaskMappingRunner`) with a new `TaskExecutorContext` per op, as the executor builds one per task execution.
Setup checks that both forms produce the same output and the same task before anything is timed.

- **Output**: take the task response's `customer` subtree, copy `customer.name` from instance data, stamp the time
  (`select $.body.customer into $.customer`, `set $.ownerName = data.customer.name`, `set $.stamp = now`).
  `…Persisted` adds the UTF-8 serialization the instance-data append needs, because the forms hand it different
  objects (`ExpandoObject` vs `JsonNode`).
- **Input**: build the URL (base-URL replace + URL-encoded id), copy the instance's `customer` into the body, add one
  header. Both start from a clone of the cached task.
- **Cold**: the first execution after publish or a pod start. Roslyn compiles the `.csx`; Dynamic Expresso parses the
  block's expressions. A unique class name / expression text per call defeats each cache.
- Not measured, the same for both forms: filing the task response on the script context, the invocation itself.

## Steady state (compile caches warm)

```
| Method                | DocKb | Mean       | Allocated | Ratio |
|---------------------- |------ |-----------:|----------:|------:|
| CsxOutputProduction   | 10    |   673.4 ns |   2.48 KB |  1.00 |
| BlockOutputProduction | 10    | 1,538.1 ns |    5.2 KB |  2.29 |
| CsxOutputPersisted    | 10    | 1,002.5 ns |   3.04 KB |  1.49 |
| BlockOutputPersisted  | 10    | 1,762.2 ns |   5.38 KB |  2.62 |
| CsxInputProduction    | 10    | 1,338.9 ns |   3.68 KB |  1.99 |
| BlockInputProduction  | 10    | 3,477.5 ns |   9.95 KB |  5.17 |
| CsxOutputProduction   | 50    |   674.4 ns |   2.48 KB |  1.00 |
| BlockOutputProduction | 50    | 1,533.0 ns |    5.2 KB |  2.27 |
| CsxOutputPersisted    | 50    |   985.2 ns |   3.04 KB |  1.46 |
| BlockOutputPersisted  | 50    | 1,740.4 ns |   5.38 KB |  2.58 |
| CsxInputProduction    | 50    | 1,315.0 ns |   3.68 KB |  1.95 |
| BlockInputProduction  | 50    | 3,450.6 ns |   9.95 KB |  5.12 |
| CsxOutputProduction   | 200   |   641.4 ns |   2.48 KB |  1.00 |
| BlockOutputProduction | 200   | 1,573.8 ns |    5.2 KB |  2.45 |
| CsxOutputPersisted    | 200   | 1,121.8 ns |   3.04 KB |  1.75 |
| BlockOutputPersisted  | 200   | 1,755.3 ns |   5.38 KB |  2.74 |
| CsxInputProduction    | 200   | 2,402.7 ns |   3.68 KB |  3.75 |
| BlockInputProduction  | 200   | 3,722.0 ns |   9.95 KB |  5.80 |
```

(`CsxInputProduction` at 200 KB has an error bar wider than its mean; its median is 1,617.6 ns.)

## First execution (cold compile)

```
| Method          | Mean       | Allocated  | Ratio |
|---------------- |-----------:|-----------:|------:|
| BlockOutputCold |   479.7 us |  163.62 KB |  0.07 |
| CsxOutputCold   | 7,128.2 us | 1586.19 KB |  1.00 |
```

Retained heap after 20 cold compiles of each form (full GC, results kept alive), measured twice in setup:
block +88 KB / +81 KB (about 4 KB per mapping), `.csx` +227 KB / +226 KB (about 11 KB per mapping).
Neither form added an `AssemblyLoadContext` (the Roslyn scripts load into the shared script context).

## Reading

| Phase | Result |
|---|---|
| Steady state, per execution | `.csx` is faster: output 2.3x (0.67 vs 1.54 us), input 2.6x (1.3 vs 3.5 us); the block allocates 2.1x / 2.7x more. The gap is 1-2 us and about 3-6 KB per mapping. |
| Payload size | Flat from 10 KB to 200 KB for both forms: neither copies the whole response or instance data. |
| First execution | The block is 15x faster to prepare (0.48 vs 7.1 ms) and allocates 10x less (164 KB vs 1.6 MB). |
| Retained memory | The block keeps about 4 KB per mapping, the `.csx` about 11 KB. |

A block is not faster per call. It is faster to get running and lighter to keep: the cost of a `.csx` is paid
once per mapping per pod, after a publish or a restart, and that is where a domain with many mappings spends it.
The per-call gap is microseconds against a transition hop that runs tens of database commands.
