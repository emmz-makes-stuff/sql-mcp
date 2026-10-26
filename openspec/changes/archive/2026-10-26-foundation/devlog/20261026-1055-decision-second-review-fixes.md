# Decision: §17, fixes from the second code-review

<!-- Finishing step 3 · HEAD: dc26c92 (nothing built yet) -->

`code-review` (medium effort) ran on `86646f3..dc26c92`, the fix sections §13–§16. It reported 8 findings, all of
them unverified. I checked each against the code:

- **1, holds.** `ApprovalStore.Read` reads `value?["sha256"]` from each `databases` entry. On a non-object node,
  JsonNode's string indexer calls `AsObject()` and throws `InvalidOperationException`. Callers catch only
  `ConfigException`, so a damaged store crashes `serve`, `config show` and `approve` instead of failing closed. → 17.1
- **7, holds.** This predates the fixes (config files have gone through `AtomicFile` since §8). The rename leaves a
  fresh `0644` file, so a `0600` config loses its mode. Since §16 a symlink's target does too, as does
  `approvals.json`. → 17.2
- **3, holds but is narrow.** On the exception path, a dispose error other than `SqlException` or
  `InvalidOperationException` escapes `CloseReaderAsync`'s `finally` and replaces the original error. → 17.3,
  verified by review only.
- **5, partly holds.** Binary values buffer in a doubling `MemoryStream`, so memory is a constant times the budget,
  which is still bounded. But no `maxBytes` above about 1 GiB can work, because .NET strings top out near 1G chars,
  and the schema allowed up to `long.MaxValue`. → 17.4: Emmz capped it at 1 GiB.
- **6, refuted by probe.** `maxBytes` values of `9223372036854775808`, `…807.5` and `1e19` all come out as a
  `ConfigException` naming the JSON path. JsonSchema.Net compares exactly.
- **2** (sync xml), **4** (the agent sees `ex.Message`) and **8** (`AtomicFile.Target` vs `CanonicalPath`) are
  Emmz's earlier decisions, from §15, §14 and §16. Not reopened.

Emmz agreed to all four tasks.

## Next

- Build **§17, task 17.1**. Base: dc26c92.
