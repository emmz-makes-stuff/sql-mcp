# Decision: page shape, caps across result sets, and bigint

<!-- Section: 6 · Base: 4098a7a -->

Emmz's answers:
- **A page holds rows from one result set only.** It never crosses a result-set boundary, so each page has a
  single column list. For example, 250 rows in set 0 and 5 rows in set 1 make 4 pages with a page size of 100.
- **When a cap is hit, everything stops.** The command is cancelled, and later result sets are not read.
  `truncated` is true, and the message names the result set where buffering stopped.
- **`bigint` is serialised as a string**, like `decimal`, so JavaScript clients can't round it. `int`,
  `smallint` and `tinyint` stay JSON numbers.

Why: a page with one column list is the simplest shape for an agent to read. The caps are global, so after a cap is
hit, reading later sets would almost never buffer anything more. A bigint above 2^53 is rounded by JS-based
clients.

Choices of my own, stated to Emmz in the plan:
- When the result fits in one page, the result store issues no handle.
- The idle timeout is a fixed 10 minutes. The schema has no field for it, so it isn't configurable.
- Expiry is checked on every access, and a `TimeProvider` timer also sweeps idle handles. `FakeTime` gets timer
  support, so no new package is needed.
- Values are converted into `JsonNode`s. The byte cap counts the UTF-8 JSON size of each converted value.
- When a cap is hit, `command.Cancel()` runs before the reader is disposed, so the rest of the rows are never read.

## Next

- Build §6, starting with task 6.1.
