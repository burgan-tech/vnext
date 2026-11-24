# Expression Mapping Baseline (BEFORE) - base commit e4faefa8, branch claude/issue-1227-analysis-50e96f

Suite: `ExpressionMappingBenchmarks` (names are reused by the AFTER run in Task 9).
Command: `dotnet run -c Release --project test/BBT.Workflow.Benchmarks -- --filter '*ExpressionMapping*' --job short`
Job: **short** (ShortRun: IterationCount=3, LaunchCount=1, WarmupCount=3, Server GC). Task 9 must use the same job.
Machine: Apple M3 (8 cores), macOS 27.2.1, .NET SDK 10.0.201 / runtime 10.0.5, BenchmarkDotNet 0.14.0.

Notes:
- ShortRun has wide error bars (3 iterations); read the Mean as order of magnitude, not a precise figure.
- Context has Body (DocKb) + metadata; Instance/Workflow/Transition are null, so the eager build under-states production (no Instance.Data parse).
- `DeConditionWarm` / `DeValueWarm` include the eager context build (as in production); expressions are `context.Body.customer.segment.ToString() == "retail"` and `"k:" + context.Body.customer.name.ToString()`.
- `RoslynOutputHandlerWarm` uses a trivial handler (does not read Body) - it measures dispatch only, not mapping logic.
- `RoslynOutputHandlerCold` uses a unique class name per invocation (not [IterationSetup]: BDN batches invocations per iteration, which would turn all but the first into cache hits).
- Local build needed `EfCorePackageVersion` 10.0.4.0 -> 10.0.15 in Directory.Build.props (NU1605 downgrade vs Npgsql); applied temporarily, not committed.

```
| Method                  | DocKb | Mean            | Error             | StdDev         | Gen0    | Gen1    | Gen2    | Allocated |
|------------------------ |------ |----------------:|------------------:|---------------:|--------:|--------:|--------:|----------:|
| EagerContextBuild       | 10    |    47,179.95 ns |     15,252.541 ns |     836.043 ns |  0.6714 |  0.0610 |       - |   56728 B |
| DeConditionWarm         | 10    |    54,929.64 ns |    278,192.910 ns |  15,248.692 ns |  0.6104 |       - |       - |   57024 B |
| DeValueWarm             | 10    |    44,878.79 ns |        633.313 ns |      34.714 ns |  0.6714 |       - |       - |   56984 B |
| RoslynOutputHandlerWarm | 10    |        20.08 ns |          2.605 ns |       0.143 ns |  0.0025 |       - |       - |     208 B |
| RoslynOutputHandlerCold | 10    | 3,497,120.34 ns |  1,887,107.335 ns | 103,438.721 ns |  7.8125 |       - |       - | 1277068 B |
| EagerContextBuild       | 50    |   253,542.42 ns |     67,253.724 ns |   3,686.404 ns |  6.8359 |  6.3477 |  6.3477 |  275964 B |
| DeConditionWarm         | 50    |   260,084.31 ns |    217,922.331 ns |  11,945.058 ns |  6.8359 |  6.3477 |  6.3477 |  276271 B |
| DeValueWarm             | 50    |   251,058.89 ns |     23,359.366 ns |   1,280.406 ns |  6.3477 |  5.8594 |  5.8594 |  276228 B |
| RoslynOutputHandlerWarm | 50    |        20.02 ns |         37.217 ns |       2.040 ns |  0.0025 |       - |       - |     208 B |
| RoslynOutputHandlerCold | 50    | 3,485,772.03 ns |    413,241.846 ns |  22,651.180 ns |       - |       - |       - | 1148652 B |
| EagerContextBuild       | 200   |   971,216.38 ns |    157,111.978 ns |   8,611.838 ns | 25.3906 | 25.3906 | 25.3906 | 1097798 B |
| DeConditionWarm         | 200   | 1,072,874.17 ns |  1,317,043.018 ns |  72,191.572 ns | 27.3438 | 27.3438 | 27.3438 | 1098086 B |
| DeValueWarm             | 200   | 1,007,042.73 ns |    340,321.866 ns |  18,654.190 ns | 27.3438 | 27.3438 | 27.3438 | 1098042 B |
| RoslynOutputHandlerWarm | 200   |        19.07 ns |         22.953 ns |       1.258 ns |  0.0025 |       - |       - |     208 B |
| RoslynOutputHandlerCold | 200   | 6,192,317.48 ns | 14,161,412.715 ns | 776,234.820 ns |       - |       - |       - | 1135412 B |

```
