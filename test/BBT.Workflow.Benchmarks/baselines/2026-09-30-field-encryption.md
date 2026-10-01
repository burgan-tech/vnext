# Field Encryption Micro Baseline — 2026-09-30, branch feature/field-masking-x-masking (E3)

Council `2026-09-29-field-encryption-encrypt`, experiment E3 (cost). Suite: `FieldEncryptionBenchmarks`.
Machine: Apple M3, macOS 26.6.2, .NET 10.0.5, Server GC. Hosts of the local runtime were running but idle.

What it measures: CPU of the `x-encryption` passes on ONE InstanceData row (L1 secret hit included, no database).
`DocKb` = size of the rest of the document; `ProtectedFields` = number of `encrypt` fields and, separately, of `hash`
fields. `OpenPlain` (the lazy open of a row without tokens) is the ratio baseline.

Reading:
- A row WITHOUT tokens pays only the prefix scan: `OpenPlain` 0.23–2.4 µs, `SanitizePlainDelta` 15 ns, 40 B.
- Every pass over a row WITH protected fields re-reads and rewrites the whole document, so cost follows `DocKb`
  far more than `ProtectedFields`: ~15–21 µs at 2 KB, ~130–160 µs at 20 KB. AES-GCM itself is ~3 µs per field
  (`SealFresh` − `SealCarry` at 2 KB / 10 fields).
- A protected write runs three such passes (open head, hash, seal): ≈ 55 µs at 2 KB, ≈ 410 µs at 20 KB.
- `KeyedDataHash` costs the same as the plain SHA-1 DataHash (both dominated by `NormalizedJson`): no extra.
- Database cost is NOT here — see the macro section of the E3 report (secret INSERT + SELECT on every protected
  write, p50 ≈ 0.27 ms together; warm reads issue no secret query).

```

BenchmarkDotNet v0.14.0, macOS 26.6.2 (25G83) [Darwin 25.6.0]
Apple M3, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.526.15411), Arm64 RyuJIT AdvSIMD
  Job-XRAFMW : .NET 10.0.5 (10.0.526.15411), Arm64 RyuJIT AdvSIMD

Server=True  

```
| Method             | DocKb | ProtectedFields | Mean          | Error        | StdDev       | Ratio   | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------- |------ |---------------- |--------------:|-------------:|-------------:|--------:|--------:|-------:|-------:|----------:|------------:|
| **SealFresh**          | **2**     | **2**               |  **21,348.37 ns** |    **48.234 ns** |    **45.118 ns** |   **91.45** |    **1.01** | **0.2441** |      **-** |   **20888 B** |      **522.20** |
| SealCarry          | 2     | 2               |  15,395.64 ns |    87.375 ns |    72.962 ns |   65.95 |    0.78 | 0.2136 |      - |   19736 B |      493.40 |
| Open               | 2     | 2               |  21,390.68 ns |   130.001 ns |   101.496 ns |   91.63 |    1.08 | 0.2136 |      - |   18920 B |      473.00 |
| OpenPlain          | 2     | 2               |     233.48 ns |     3.000 ns |     2.660 ns |    1.00 |    0.02 | 0.0005 |      - |      40 B |        1.00 |
| Hash               | 2     | 2               |  15,912.47 ns |   110.584 ns |    86.337 ns |   68.16 |    0.82 | 0.2136 |      - |   19432 B |      485.80 |
| HashCarry          | 2     | 2               |  15,033.06 ns |    45.644 ns |    40.462 ns |   64.40 |    0.72 | 0.1678 |      - |   14304 B |      357.60 |
| KeyedDataHash      | 2     | 2               |  65,454.85 ns |   513.462 ns |   480.293 ns |  280.38 |    3.64 | 0.9766 |      - |   99946 B |    2,498.65 |
| PlainDataHash      | 2     | 2               |  63,952.68 ns |   275.390 ns |   229.963 ns |  273.95 |    3.13 | 0.9766 |      - |   98738 B |    2,468.45 |
| SanitizePlainDelta | 2     | 2               |      14.74 ns |     0.175 ns |     0.155 ns |    0.06 |    0.00 | 0.0005 |      - |      40 B |        1.00 |
|                    |       |                 |               |              |              |         |         |        |        |           |             |
| **SealFresh**          | **2**     | **10**              |  **45,258.73 ns** |   **295.942 ns** |   **276.824 ns** |  **182.39** |    **1.22** | **0.3662** |      **-** |   **35528 B** |      **888.20** |
| SealCarry          | 2     | 10              |  19,216.87 ns |   130.496 ns |   115.681 ns |   77.44 |    0.51 | 0.3662 |      - |   29768 B |      744.20 |
| Open               | 2     | 10              |  45,105.70 ns |   250.196 ns |   195.336 ns |  181.77 |    0.94 | 0.3662 |      - |   32928 B |      823.20 |
| OpenPlain          | 2     | 10              |     248.15 ns |     0.902 ns |     0.800 ns |    1.00 |    0.00 | 0.0005 |      - |      40 B |        1.00 |
| Hash               | 2     | 10              |  20,577.33 ns |   105.899 ns |    82.679 ns |   82.92 |    0.41 | 0.3357 |      - |   28536 B |      713.40 |
| HashCarry          | 2     | 10              |  17,222.40 ns |    72.612 ns |    60.634 ns |   69.40 |    0.32 | 0.2441 |      - |   20488 B |      512.20 |
| KeyedDataHash      | 2     | 10              |  70,087.03 ns |   212.589 ns |   198.856 ns |  282.44 |    1.17 | 0.9766 |      - |  109835 B |    2,745.88 |
| PlainDataHash      | 2     | 10              |  68,808.64 ns |   755.131 ns |   706.350 ns |  277.29 |    2.89 | 1.2207 |      - |  105403 B |    2,635.07 |
| SanitizePlainDelta | 2     | 10              |      14.68 ns |     0.036 ns |     0.030 ns |    0.06 |    0.00 | 0.0005 |      - |      40 B |        1.00 |
|                    |       |                 |               |              |              |         |         |        |        |           |             |
| **SealFresh**          | **20**    | **2**               | **138,091.35 ns** |   **374.655 ns** |   **332.121 ns** |  **58.293** |    **0.26** | **1.4648** |      **-** |  **156496 B** |    **3,912.40** |
| SealCarry          | 20    | 2               | 131,619.60 ns |   485.321 ns |   453.969 ns |  55.561 |    0.29 | 1.7090 | 0.2441 |  155344 B |    3,883.60 |
| Open               | 20    | 2               | 138,568.15 ns | 1,043.237 ns |   975.844 ns |  58.494 |    0.46 | 1.4648 | 0.2441 |  136128 B |    3,403.20 |
| OpenPlain          | 20    | 2               |   2,368.96 ns |    10.793 ns |     9.568 ns |   1.000 |    0.01 |      - |      - |      40 B |        1.00 |
| Hash               | 20    | 2               | 132,747.68 ns | 1,084.971 ns |   961.799 ns |  56.037 |    0.45 | 1.7090 | 0.2441 |  155048 B |    3,876.20 |
| HashCarry          | 20    | 2               | 130,774.06 ns |   547.065 ns |   456.824 ns |  55.204 |    0.28 | 1.2207 | 0.2441 |  113104 B |    2,827.60 |
| KeyedDataHash      | 20    | 2               | 564,701.59 ns | 2,131.559 ns | 1,889.571 ns | 238.379 |    1.21 | 7.8125 |      - |  898820 B |   22,470.50 |
| PlainDataHash      | 20    | 2               | 568,493.95 ns | 5,635.077 ns | 4,399.498 ns | 239.980 |    2.01 | 7.8125 |      - |  897609 B |   22,440.22 |
| SanitizePlainDelta | 20    | 2               |      14.69 ns |     0.030 ns |     0.025 ns |   0.006 |    0.00 | 0.0005 |      - |      40 B |        1.00 |
|                    |       |                 |               |              |              |         |         |        |        |           |             |
| **SealFresh**          | **20**    | **10**              | **161,230.09 ns** |   **485.443 ns** |   **454.084 ns** |  **67.311** |    **0.40** | **1.9531** |      **-** |  **171136 B** |    **4,278.40** |
| SealCarry          | 20    | 10              | 136,112.95 ns |   312.219 ns |   260.717 ns |  56.825 |    0.32 | 1.9531 | 0.2441 |  165376 B |    4,134.40 |
| Open               | 20    | 10              | 162,449.39 ns | 1,244.232 ns | 1,038.990 ns |  67.820 |    0.55 | 1.4648 |      - |  150136 B |    3,753.40 |
| OpenPlain          | 20    | 10              |   2,395.38 ns |    14.132 ns |    13.219 ns |   1.000 |    0.01 |      - |      - |      40 B |        1.00 |
| Hash               | 20    | 10              | 139,521.96 ns | 2,304.756 ns | 1,924.575 ns |  58.248 |    0.83 | 1.9531 | 0.2441 |  164152 B |    4,103.80 |
| HashCarry          | 20    | 10              | 132,625.04 ns |   923.203 ns |   818.395 ns |  55.369 |    0.44 | 1.4648 | 0.2441 |  119288 B |    2,982.20 |
| KeyedDataHash      | 20    | 10              | 580,222.94 ns | 5,271.611 ns | 4,402.034 ns | 242.233 |    2.19 | 7.8125 |      - |  908703 B |   22,717.58 |
| PlainDataHash      | 20    | 10              | 572,825.23 ns | 1,932.579 ns | 1,713.181 ns | 239.144 |    1.45 | 7.8125 |      - |  904277 B |   22,606.92 |
| SanitizePlainDelta | 20    | 10              |      14.58 ns |     0.020 ns |     0.019 ns |   0.006 |    0.00 | 0.0005 |      - |      40 B |        1.00 |
