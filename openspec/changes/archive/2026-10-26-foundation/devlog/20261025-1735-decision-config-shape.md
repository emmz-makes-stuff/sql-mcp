# Decision: config limit names and identity edge cases

<!-- Section: 2 · Base: cbd4e2c -->

Emmz's answers before §2 started:

- **Limit property names:** `"limits": { "maxRows", "maxBytes", "pageSize", "timeoutSeconds" }`, plain integers with
  the unit in the name, so the byte cap is in bytes (default 5242880). *Rejected:* `maxMegabytes`, which is
  friendlier but coarser.
- **A named instance without a port** is unavailable. Its display identifier is the data source as written,
  lower-cased, plus the database: `sqlhost\sqlexpress/sales`. Having no `:port`, it can never match a tool-call
  identifier. *Rejected:* identifying it by entry index.
- **Anything that can't give a TCP `host:port/database` identity** loads but is **unavailable with a reason**:
  - non-TCP prefixes (`np:`, `lpc:`, `admin:`), with the reason "only TCP connections are supported";
  - shared memory (`.` and `(local)`), which counts as non-TCP;
  - a missing Initial Catalog, with the reason "connection string names no database".

  *Rejected:* failing the whole file, and mapping `.` or `(local)` to localhost.

## Next

- Build §2, starting with task 2.1.
