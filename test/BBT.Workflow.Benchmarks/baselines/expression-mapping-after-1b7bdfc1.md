# Expression Mapping AFTER - src at 1b7bdfc1 (harness edits uncommitted at run time, committed with this file)

Compare with `expression-mapping-e4faefa8.md` (BEFORE). Same job and machine class.

Command: `dotnet run -c Release --project test/BBT.Workflow.Benchmarks -- --filter '*ExpressionMapping*' --job short`
(with the temporary `sed` on Directory.Build.props from the baseline file, reverted afterwards, not committed)
Job: short (IterationCount=3, WarmupCount=3, LaunchCount=1, Server GC). Machine: Apple M3 (8 cores), macOS 27.2.1, .NET SDK 10.0.201 / runtime 10.0.5, BenchmarkDotNet 0.14.0.
Allocation is the binding metric; means are indicative (ShortRun error bars are wide).

New benchmarks: `DeclarativeOutputWarm` (3 entries through the real `DeclarativeTaskMappingRunner.ProcessOutputAsync`: `select $.body.customer into $.customer`, `set $.ownerName = data.customer.name`, `set $.stamp = now`; the per-hop plan memo is warm, the compiler cache is warm; the result document is built per call), `DeclarativeOutputCold` (unique `now.AddSeconds(n)` text and fresh context per call, so compiler cache and memo miss), `LazyScopeDataOnly` (from Task 2), and `ExpressionMappingCompilerCacheBenchmarks` (1000 hits of one key, sequential vs `Parallel.For`).

## Tables

```
| Method                  | DocKb | Mean            | Error            | StdDev         | Gen0    | Gen1    | Gen2    | Allocated |
|------------------------ |------ |----------------:|-----------------:|---------------:|--------:|--------:|--------:|----------:|
| EagerContextBuild       | 10    |    45,311.74 ns |     2,686.221 ns |     147.241 ns |  0.6714 |  0.0610 |       - |   56896 B |
| LazyScopeDataOnly       | 10    |        53.69 ns |         1.609 ns |       0.088 ns |  0.0033 |       - |       - |     272 B |
| DeConditionWarm         | 10    |    45,621.25 ns |     2,117.706 ns |     116.079 ns |  0.6714 |       - |       - |   57208 B |
| DeValueWarm             | 10    |    45,631.21 ns |     7,937.689 ns |     435.092 ns |  0.6714 |       - |       - |   57168 B |
| RoslynOutputHandlerWarm | 10    |       179.10 ns |         8.440 ns |       0.463 ns |  0.0067 |       - |       - |     560 B |
| DeclarativeOutputWarm   | 10    |    83,941.40 ns |     3,359.128 ns |     184.125 ns |  0.2441 |       - |       - |   38369 B |
| DeOutputMapping3Warm    | 10    |   134,752.89 ns |    13,335.840 ns |     730.982 ns |  1.9531 |       - |       - |  171296 B |
| EagerContextBuild       | 50    |   266,073.13 ns |   104,345.726 ns |   5,719.541 ns |  6.3477 |  5.8594 |  5.8594 |  276137 B |
| LazyScopeDataOnly       | 50    |        53.37 ns |         7.312 ns |       0.401 ns |  0.0033 |       - |       - |     272 B |
| DeConditionWarm         | 50    |   262,878.13 ns |   145,777.469 ns |   7,990.555 ns |  5.8594 |  5.8594 |  5.8594 |  276442 B |
| DeValueWarm             | 50    |   260,470.71 ns |    69,567.084 ns |   3,813.207 ns |  6.3477 |  5.8594 |  5.8594 |  276409 B |
| RoslynOutputHandlerWarm | 50    |       182.92 ns |        11.910 ns |       0.653 ns |  0.0067 |       - |       - |     560 B |
| DeclarativeOutputWarm   | 50    |   419,959.65 ns |    96,944.087 ns |   5,313.833 ns |  4.8828 |  4.8828 |  4.8828 |  175824 B |
| DeOutputMapping3Warm    | 50    |   778,056.48 ns |   301,999.265 ns |  16,553.599 ns | 19.5313 | 17.5781 | 17.5781 |  829007 B |
| EagerContextBuild       | 200   |   993,217.26 ns | 1,088,608.271 ns |  59,670.293 ns | 23.4375 | 23.4375 | 23.4375 | 1097973 B |
| LazyScopeDataOnly       | 200   |        52.39 ns |         3.581 ns |       0.196 ns |  0.0033 |       - |       - |     272 B |
| DeConditionWarm         | 200   |   957,049.94 ns |   401,831.064 ns |  22,025.717 ns | 23.4375 | 23.4375 | 23.4375 | 1098264 B |
| DeValueWarm             | 200   |   951,919.60 ns |   515,144.991 ns |  28,236.835 ns | 23.4375 | 23.4375 | 23.4375 | 1098246 B |
| RoslynOutputHandlerWarm | 200   |       183.93 ns |        26.911 ns |       1.475 ns |  0.0067 |       - |       - |     560 B |
| DeclarativeOutputWarm   | 200   | 1,671,466.53 ns |   257,646.515 ns |  14,122.475 ns | 19.5313 | 19.5313 | 19.5313 |  690290 B |
| DeOutputMapping3Warm    | 200   | 2,966,070.09 ns | 2,396,736.114 ns | 131,373.194 ns | 62.5000 | 62.5000 | 62.5000 | 3294423 B |
| Method                  | Mean       | Error      | StdDev    | Gen0    | Allocated  |
|------------------------ |-----------:|-----------:|----------:|--------:|-----------:|
| DeclarativeOutputCold   |   788.9 us |   388.9 us |  21.32 us |  1.9531 |  195.89 KB |
| RoslynOutputHandlerCold | 5,721.4 us | 3,005.0 us | 164.71 us | 15.6250 | 1307.54 KB |
| Method                     | Mean      | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------- |----------:|----------:|----------:|------:|--------:|----------:|------------:|
| CompilerCacheHitSequential |  48.29 us |  24.28 us |  1.331 us |  1.00 |    0.03 |         - |          NA |
| CompilerCacheHitParallel   | 170.61 us | 187.44 us | 10.274 us |  3.54 |    0.20 |    3618 B |          NA |
```

ALC sanity check (setup of the cold class; `AssemblyLoadContext.All` count): `before=1 afterDeclarative(25 cold)=1 afterRoslyn(5 cold)=3`. The declarative path created none; each Roslyn cold compile created one.

## Gate table

| Gate | Measured | Verdict |
|---|---|---|
| G-P1 (as written: declarative 3-entry output vs warm Roslyn `IMapping`, warm mean and allocation, DocKb 10/50/200) | Declarative 83.9 us / 38,369 B (10), 420 us / 175,824 B (50), 1,671 us / 690,290 B (200) vs Roslyn 179 ns / 560 B (10), 183 ns / 560 B (50), 184 ns / 560 B (200). Declarative is about 470x slower at 10 KB, 2,300x at 50, 9,100x at 200; allocates 68x, 314x, 1,233x more | **FAIL** |
| G-P1 (secondary: vs today's DE `DeOutputMapping3Warm`) | DE 134.8 us / 171,296 B (10), 778 us / 829,007 B (50), 2,966 us / 3,294,423 B (200); declarative is 0.62x / 0.54x / 0.56x in time and 0.22x / 0.21x / 0.21x in allocation | PASS (better than today's DE, not better than Roslyn) |
| G-P2 (lazy scope with only `data` vs eager `FromScriptContext`, DocKb 50 and 200) | Lazy 272 B at every size vs eager 276,137 B (50) and 1,097,973 B (200): 1,015x and 4,036x less. At 10 KB: 272 B vs 56,896 B (209x). Time 53 ns vs 266 us / 993 us | PASS |
| G-P3 (cold DE parse+compile reported next to Roslyn cold; no ALC) | `DeclarativeOutputCold` 789 us / 195.9 KB vs `RoslynOutputHandlerCold` 5,721 us / 1,307.5 KB (7.3x faster, 6.7x less allocation). The cold figure includes building a fresh context. ALC delta on the DE path 0 (25 cold evaluations); Roslyn +2 for 5 compiles | PASS |
| G-P4 (cache bounded under parallel cache-key load) | Unit test `ExpressionCompilerTests.Cache_ParallelCompilesBeyondCapacity_StaysBounded` (200 distinct keys via `Parallel.For`, capacity 8): `CacheCount <= 8`, passed on this SHA. The benchmark does not exercise capacity | PASS |

## Observations (not gates)

- G-P1 failure cause: the Roslyn handler's 560 B figure is a bare `ExpandoObject` fill. It does not build the `result` JSON document, does not serialize/materialize nodes and does not bind `data`. The declarative path pays those per call; its allocation scales with payload (38 KB / 176 KB / 690 KB), roughly 3.4 x the DocKb payload. The cost is in the generic runner path, not the compiler.
- Task 1 review concern (global lock on the compiler-cache hit path): `CompilerCacheHitParallel` (1000 hits, `Parallel.For`) 170.6 us vs sequential 48.3 us, i.e. 3.5x SLOWER in parallel (error bars wide, 3 iterations; parallel also allocates 3,618 B for Parallel.For itself). Consistent with contention on the cache hit path; sequential hit costs about 48 ns.
