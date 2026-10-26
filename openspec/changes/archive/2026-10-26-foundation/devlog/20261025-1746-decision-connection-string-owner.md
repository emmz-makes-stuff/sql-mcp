# Decision: the user file owns connection strings

<!-- Section: 3 · Base: 4e8f8e4 -->

Emmz's answer: when the user file and the project file both define the same identity, the **user's connection
string is used**. The project entry contributes only grants and limits.

Why: otherwise a project could quietly swap in a more privileged login for a database the user has already set
up, with no approval.

*Rejected:*
- the project's string only while the file is approved;
- the project's string always.

A database that only the project file defines uses the project's connection string, as the spec's "Project adds a
database" scenario requires.

## Next

- Build §3, starting with task 3.1.
