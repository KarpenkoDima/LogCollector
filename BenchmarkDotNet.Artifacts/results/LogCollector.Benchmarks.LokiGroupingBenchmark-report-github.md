```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.2965/22H2/2022Update)
Intel Pentium Gold G5400 CPU 3.70GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 9.0.11 (9.0.1125.51716), X64 RyuJIT SSE4.2
  DefaultJob : .NET 9.0.11 (9.0.1125.51716), X64 RyuJIT SSE4.2


```
| Method                                                     | DistinctHosts | Mean       | Error    | StdDev    | Median     | Ratio | RatioSD | Rank | Gen0     | Gen1     | Gen2     | Allocated  | Alloc Ratio |
|----------------------------------------------------------- |-------------- |-----------:|---------:|----------:|-----------:|------:|--------:|-----:|---------:|---------:|---------:|-----------:|------------:|
| **&#39;GroupBy(string key) — current LokiLogSink&#39;**                | **3**             | **1,901.7 μs** | **63.03 μs** | **168.23 μs** | **1,846.2 μs** |  **1.01** |    **0.12** |    **3** | **886.7188** | **546.8750** | **464.8438** | **2931.57 KB** |        **1.00** |
| &#39;GroupBy(Span comparer key)&#39;                               | 3             | 1,297.7 μs | 18.76 μs |  16.63 μs | 1,298.6 μs |  0.69 |    0.05 |    2 | 562.5000 | 431.6406 | 425.7813 | 2306.04 KB |        0.79 |
| &#39;Manual partition (Dictionary&lt;Memory,List&lt;int&gt;&gt;), no LINQ&#39; | 3             |   392.9 μs |  7.73 μs |  11.81 μs |   388.4 μs |  0.21 |    0.02 |    1 |  47.3633 |        - |        - |   97.05 KB |        0.03 |
|                                                            |               |            |          |           |            |       |         |      |          |          |          |            |             |
| **&#39;GroupBy(string key) — current LokiLogSink&#39;**                | **50**            | **1,542.8 μs** | **14.60 μs** |  **13.66 μs** | **1,542.9 μs** |  **1.00** |    **0.01** |    **3** | **513.6719** | **482.4219** |        **-** | **3034.89 KB** |        **1.00** |
| &#39;GroupBy(Span comparer key)&#39;                               | 50            | 1,199.8 μs | 10.06 μs |   8.92 μs | 1,196.7 μs |  0.78 |    0.01 |    2 | 458.9844 | 359.3750 |        - | 2413.41 KB |        0.80 |
| &#39;Manual partition (Dictionary&lt;Memory,List&lt;int&gt;&gt;), no LINQ&#39; | 50            |   429.1 μs |  4.11 μs |   3.43 μs |   428.2 μs |  0.28 |    0.00 |    1 |  54.1992 |        - |        - |  111.19 KB |        0.04 |
