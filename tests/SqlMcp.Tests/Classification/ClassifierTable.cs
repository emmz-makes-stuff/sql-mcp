

namespace SqlMcp.Tests.Classification;

/// <summary>
/// The classifier statement table: the main safeguard against misclassification (ADR-0002). Every allowlist entry
/// is here because a row needs it. Add a row for every bug found.
/// </summary>
internal static class ClassifierTable
{
    private const string Query = "query";
    private const string Command = "command";
    private const string Ddl = "ddl";
    private const string Execute = "execute";

    /// <summary>Single statements that classify into a category.</summary>
    public static TheoryData<string, string> Accepted => new()
    {
        // Spec: each category is recognised.
        { "SELECT 1", Query },
        { "UPDATE t SET a = 1 WHERE id = 2", Command },
        { "ALTER TABLE t ADD c int", Ddl },
        { "TRUNCATE TABLE t", Ddl },
        { "EXEC dbo.p", Execute },

        // Spec: keywords in comments and strings do not count.
        { "SELECT 'DROP TABLE t' AS x -- DELETE", Query },
        { "/* DELETE FROM t */ SELECT N'EXEC sp_executesql' AS [drop]", Query },

        // Command basics.
        { "INSERT INTO t (a, b) VALUES (1, 'x')", Command },
        { "INSERT INTO dbo.t (a) SELECT a FROM s WHERE b > 0", Command },
        { "UPDATE t SET a = a + 1 WHERE id = 3", Command },
        { "DELETE FROM t WHERE id = 3", Command },
        { "DELETE t WHERE id = 3", Command },
        { "MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = s.a WHEN NOT MATCHED THEN INSERT (id, a) VALUES (s.id, s.a);", Command },

        // Command statements (write-tools §2): spec scenarios.
        { "UPDATE o SET o.status = 'late' FROM dbo.orders o JOIN dbo.customers c ON c.id = o.customer_id WHERE c.region = 'EU'", Command },
        { "WITH old AS (SELECT TOP (100) * FROM dbo.jobs WHERE done = 1 ORDER BY id) DELETE FROM old OUTPUT deleted.id", Command },
        { "INSERT INTO dbo.t (id, name) VALUES (NEXT VALUE FOR dbo.seq, 'a')", Command },
        { "UPDATE dbo.users SET active = 0 WHERE last_login < '2025-01-01'", Command },

        // Command corpus: reads use the query allowlist.
        { "INSERT INTO dbo.archive (id, total) SELECT o.id, SUM(l.qty * l.price) FROM dbo.orders o JOIN dbo.lines l ON l.order_id = o.id WHERE o.closed_at < DATEADD(year, -1, GETDATE()) GROUP BY o.id", Command },
        { "UPDATE t SET total = (SELECT SUM(x) FROM u WHERE u.t_id = t.id), updated = SYSUTCDATETIME() WHERE EXISTS (SELECT 1 FROM u WHERE u.t_id = t.id)", Command },
        { "DELETE FROM dbo.sessions WHERE user_id IN (SELECT id FROM dbo.users WHERE active = 0)", Command },
        { "DELETE TOP (10) s FROM dbo.sessions s JOIN dbo.users u ON u.id = s.user_id WHERE u.active = 0", Command },
        { "MERGE dbo.t AS tgt USING dbo.s AS src ON tgt.id = src.id WHEN MATCHED AND tgt.a <> src.a THEN UPDATE SET a = src.a WHEN NOT MATCHED BY TARGET THEN INSERT (id, a) VALUES (src.id, src.a) WHEN NOT MATCHED BY SOURCE THEN DELETE OUTPUT $action, inserted.id, deleted.id;", Command },
        { "MERGE dbo.t AS tgt USING (VALUES (1, 'a'), (2, 'b')) AS src (id, a) ON tgt.id = src.id WHEN NOT MATCHED THEN INSERT (id, a) VALUES (src.id, src.a);", Command },
        { "UPDATE dbo.t SET a = 0 OUTPUT deleted.id, deleted.a INTO dbo.audit (id, old_a) WHERE a < 0", Command },

        // Locking hints are allowed in a command: the write takes those locks anyway.
        { "UPDATE t WITH (ROWLOCK, UPDLOCK) SET a = 1 WHERE id = 2", Command },
        { "DELETE FROM t WITH (TABLOCK) WHERE a < 0", Command },
        { "INSERT INTO t (a) SELECT a FROM s WITH (UPDLOCK, HOLDLOCK) WHERE b = 1", Command },

        // The WHERE rule exempts a CTE target: the CTE's query does the filtering.
        { "WITH x AS (SELECT a FROM dbo.t WHERE b = 1) UPDATE x SET a = 0", Command },

        // Queries may still read other databases; only writes are kept to the target database.
        { "SELECT a FROM otherdb.dbo.t", Query },

        // DDL basics.
        { "CREATE TABLE t (id int NOT NULL PRIMARY KEY, name nvarchar(50) NULL)", Ddl },
        { "DROP TABLE t", Ddl },
        { "DROP TABLE IF EXISTS dbo.t", Ddl },
        { "TRUNCATE TABLE dbo.t", Ddl },
        { "ALTER TABLE dbo.t ADD d datetime2(3) NULL", Ddl },

        // Execute basics.
        { "EXECUTE p", Execute },
        { "EXEC dbo.p 1, 'x'", Execute },
        { "EXEC dbo.p @a = 1, @b = N'x'", Execute },

        // DDL (write-tools §3): spec scenarios.
        { "CREATE INDEX ix_orders_customer ON dbo.orders (customer_id)", Ddl },
        { "CREATE VIEW dbo.open_orders AS SELECT id FROM dbo.orders WHERE closed = 0", Ddl },

        // Tables, with defaults, CHECK constraints, computed columns and keys.
        { "CREATE TABLE dbo.orders (id int IDENTITY(1, 1) NOT NULL CONSTRAINT pk_orders PRIMARY KEY, customer_id int NOT NULL REFERENCES dbo.customers (id), qty int NOT NULL CONSTRAINT ck_orders_qty CHECK (qty > 0), price decimal(10, 2) NOT NULL DEFAULT (0), total AS (qty * price) PERSISTED, created datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(), CONSTRAINT uq_orders UNIQUE (customer_id, created))", Ddl },
        { "CREATE TABLE dbo.t (id bigint NOT NULL CONSTRAINT df_t_id DEFAULT (NEXT VALUE FOR dbo.seq), a int NULL, INDEX ix_t_a (a))", Ddl },

        // The ALTER TABLE forms.
        { "ALTER TABLE dbo.t ADD CONSTRAINT df_t_c DEFAULT (0) FOR c", Ddl },
        { "ALTER TABLE dbo.t ADD CONSTRAINT ck_t_c CHECK (c >= 0 AND c < 100)", Ddl },
        { "ALTER TABLE dbo.t ADD CONSTRAINT fk_t_u FOREIGN KEY (u_id) REFERENCES dbo.u (id) ON DELETE CASCADE", Ddl },
        { "ALTER TABLE dbo.t ADD total AS (qty * price)", Ddl },
        { "ALTER TABLE dbo.t ALTER COLUMN c bigint NOT NULL", Ddl },
        { "ALTER TABLE dbo.t DROP COLUMN c", Ddl },
        { "ALTER TABLE dbo.t DROP CONSTRAINT IF EXISTS ck_t_c", Ddl },
        { "ALTER TABLE dbo.t NOCHECK CONSTRAINT fk_t_u", Ddl },
        { "ALTER TABLE dbo.t WITH CHECK CHECK CONSTRAINT fk_t_u", Ddl },

        // Indexes.
        { "CREATE UNIQUE NONCLUSTERED INDEX ix_t_ab ON dbo.t (a ASC, b DESC) INCLUDE (c) WHERE c IS NOT NULL", Ddl },
        { "ALTER INDEX ix_t_ab ON dbo.t REBUILD", Ddl },
        { "ALTER INDEX ix_t_ab ON dbo.t DISABLE", Ddl },
        { "ALTER INDEX ix_t_ab ON dbo.t REORGANIZE", Ddl },
        { "DROP INDEX ix_t_ab ON dbo.t", Ddl },
        { "DROP INDEX IF EXISTS ix_t_ab ON dbo.t", Ddl },

        // Views.
        { "CREATE OR ALTER VIEW dbo.v AS SELECT a, COUNT(*) AS n FROM dbo.t GROUP BY a", Ddl },
        { "ALTER VIEW dbo.v WITH SCHEMABINDING AS SELECT a FROM dbo.t", Ddl },
        { "DROP VIEW IF EXISTS dbo.v", Ddl },

        // Schemas and sequences.
        { "CREATE SCHEMA reporting", Ddl },
        { "DROP SCHEMA IF EXISTS reporting", Ddl },
        { "CREATE SEQUENCE dbo.seq AS bigint START WITH 1 INCREMENT BY 1", Ddl },
        { "ALTER SEQUENCE dbo.seq RESTART WITH 100", Ddl },
        { "DROP SEQUENCE dbo.seq", Ddl },

        // Execute (write-tools §3): user procedures with plain values.
        { "EXEC dbo.archive_orders @before = '2026-01-01', @limit = 500", Execute },
        { "EXEC dbo.p NULL, -5, 1.5, N'x', 0x1F", Execute },
        { "EXEC reporting.refresh @days = -7, @scale = 2.5E0, @cost = $10", Execute },

        // Query corpus (task 4.6): basics.
        { "SELECT * FROM t", Query },
        { "SELECT a, b AS bee, t.c FROM dbo.t AS t WHERE a > 1 AND (b < 2 OR c IS NULL) ORDER BY a DESC", Query },
        { "SELECT DISTINCT a FROM [my db].[dbo].[my table]", Query },
        { "SELECT \"a\" FROM \"t\"", Query },
        { "SELECT COUNT(*), MAX(a), AVG(CAST(b AS decimal(10, 2))) FROM t GROUP BY c HAVING COUNT(*) > 1", Query },
        { "SELECT a FROM t WHERE b LIKE N'x%' AND c BETWEEN 1 AND 10 AND d IN (1, 2, 3) AND e IS NOT NULL", Query },
        { "SELECT CASE WHEN a > 0 THEN 'pos' WHEN a < 0 THEN 'neg' ELSE 'zero' END, CASE b WHEN 1 THEN 'one' END FROM t", Query },
        { "SELECT COALESCE(a, b, 0), NULLIF(a, 0), IIF(a > 1, 1, 0), ISNULL(a, 0) FROM t", Query },
        { "SELECT CONVERT(varchar(10), d, 120), TRY_CAST(a AS int), TRY_CONVERT(date, b) FROM t", Query },
        { "SELECT GETDATE(), SYSUTCDATETIME(), DATEADD(day, 1, d), DATEDIFF(minute, d, e), YEAR(d) FROM t", Query },
        { "SELECT UPPER(a) + '-' + LTRIM(RTRIM(b)), LEN(a), SUBSTRING(a, 1, 3), -a, ~b, a % 2 FROM t", Query },
        { "SELECT 1.5, 0x1F, $10, 1e3, NULL, N'unicode', CURRENT_TIMESTAMP", Query },
        { "SELECT name, object_id FROM sys.tables WHERE is_ms_shipped = 0", Query },

        // Joins.
        { "SELECT * FROM a INNER JOIN b ON a.id = b.a_id LEFT JOIN c ON c.id = b.c_id RIGHT OUTER JOIN d ON d.id = c.d_id FULL JOIN e ON e.id = d.id CROSS JOIN f", Query },
        { "SELECT * FROM a, b WHERE a.id = b.id", Query },

        // CTEs.
        { "WITH x AS (SELECT a FROM t) SELECT * FROM x", Query },
        { "WITH x (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM x WHERE n < 10), y AS (SELECT n FROM x) SELECT * FROM y OPTION (MAXRECURSION 100)", Query },

        // Window functions.
        { "SELECT ROW_NUMBER() OVER (PARTITION BY a ORDER BY b DESC) AS rn, RANK() OVER (ORDER BY c) FROM t", Query },
        { "SELECT SUM(a) OVER (PARTITION BY b ORDER BY c ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW), LAG(a, 1) OVER (ORDER BY c) FROM t", Query },

        // APPLY.
        { "SELECT * FROM a CROSS APPLY (SELECT TOP (1) * FROM b WHERE b.a_id = a.id ORDER BY b.d DESC) AS latest", Query },
        { "SELECT * FROM a OUTER APPLY dbo.fn(a.id) AS f", Query },

        // PIVOT and UNPIVOT.
        { "SELECT * FROM (SELECT y, q, v FROM s) AS src PIVOT (SUM(v) FOR q IN ([1], [2], [3], [4])) AS p", Query },
        { "SELECT id, q, v FROM p UNPIVOT (v FOR q IN (q1, q2, q3)) AS u", Query },

        // JSON and XML.
        { "SELECT JSON_VALUE(doc, '$.name'), JSON_QUERY(doc, '$.tags'), ISJSON(doc) FROM t", Query },
        { "SELECT j.* FROM t CROSS APPLY OPENJSON(t.doc) WITH (name nvarchar(50) '$.name', age int '$.age') AS j", Query },
        { "SELECT a, b FROM t FOR JSON PATH, ROOT('rows')", Query },
        { "SELECT a, b FROM t FOR XML PATH('row'), ROOT('rows')", Query },
        { "SELECT x.value('(/r/@id)[1]', 'int') FROM t", Query },
        { "SELECT n.c.value('.', 'nvarchar(50)') FROM t CROSS APPLY t.x.nodes('/r/i') AS n(c)", Query },

        // Temporal tables.
        { "SELECT * FROM dbo.t FOR SYSTEM_TIME AS OF '2026-01-01'", Query },
        { "SELECT * FROM dbo.t FOR SYSTEM_TIME BETWEEN '2026-01-01' AND '2026-02-01'", Query },
        { "SELECT * FROM dbo.t FOR SYSTEM_TIME ALL", Query },

        // OPTION hints.
        { "SELECT * FROM t OPTION (RECOMPILE)", Query },
        { "SELECT * FROM t OPTION (MAXDOP 1, OPTIMIZE FOR UNKNOWN)", Query },

        // TOP and OFFSET/FETCH.
        { "SELECT TOP 10 * FROM t", Query },
        { "SELECT TOP (5) PERCENT WITH TIES a FROM t ORDER BY a", Query },
        { "SELECT a FROM t ORDER BY a OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY", Query },

        // Set operators.
        { "SELECT a FROM t UNION SELECT a FROM u UNION ALL SELECT a FROM v", Query },
        { "SELECT a FROM t EXCEPT SELECT a FROM u", Query },
        { "(SELECT a FROM t) INTERSECT (SELECT a FROM u) ORDER BY a", Query },

        // Subqueries.
        { "SELECT a, (SELECT MAX(b) FROM u WHERE u.a = t.a) AS mb FROM t", Query },
        { "SELECT a FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a) AND b IN (SELECT b FROM v) AND c > ALL (SELECT c FROM w)", Query },
        { "SELECT d.a FROM (SELECT a FROM t) AS d", Query },
        { "SELECT v.a, v.b FROM (VALUES (1, 'x'), (2, 'y')) AS v (a, b)", Query },

        // Grouping.
        { "SELECT a, b, SUM(c) FROM t GROUP BY ROLLUP (a, b)", Query },
        { "SELECT a, b, SUM(c) FROM t GROUP BY GROUPING SETS ((a), (b), ())", Query },
        { "SELECT STRING_AGG(a, ', ') WITHIN GROUP (ORDER BY a) FROM t", Query },

        // Read-only table hints.
        { "SELECT * FROM t WITH (NOLOCK)", Query },
        { "SELECT * FROM t WITH (INDEX(ix_a), FORCESEEK)", Query },
        { "SELECT * FROM t WITH (READPAST, ROWLOCK)", Query },
    };

    /// <summary>Single statements that are rejected, with a fragment the reason must contain.</summary>
    public static TheoryData<string, string> Rejected => new()
    {
        // Spec: out-of-scope statements.
        { "GRANT SELECT ON t TO u", "GRANT" },
        { "DENY SELECT ON t TO u", "DENY" },
        { "REVOKE SELECT ON t FROM u", "REVOKE" },
        { "BACKUP DATABASE db TO DISK = 'x.bak'", "BACKUP DATABASE" },
        { "USE otherdb", "USE" },
        { "SET NOCOUNT ON", "SET option" },
        { "SET @x = 1", "SET @variable" },
        { "DECLARE @x int", "DECLARE" },
        { "BEGIN TRANSACTION", "BEGIN TRANSACTION" },
        { "WAITFOR DELAY '00:00:01'", "WAITFOR" },
        { "CREATE PROCEDURE p AS SELECT 1", "procedures, functions and triggers can't be created through sql-mcp" },

        // Spec: side-effecting reads.
        { "SELECT * INTO t2 FROM t", "creates a table" },
        { "SELECT * FROM OPENQUERY(lnk, 'DELETE FROM x')", "OPENQUERY" },
        { "SELECT * FROM t WHERE a IN (SELECT a FROM OPENQUERY(lnk, 'SELECT 1'))", "OPENQUERY" },
        { "SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;Trusted_Connection=yes;', 'SELECT 1')", "OPENROWSET" },
        { "SELECT * FROM OPENROWSET(BULK 'c:\\x.csv', SINGLE_CLOB) AS b", "OPENROWSET" },
        { "SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t", "OPENDATASOURCE" },
        { "SELECT NEXT VALUE FOR dbo.seq", "NEXT VALUE FOR" },
        { "SELECT * FROM t WITH (UPDLOCK)", "UPDLOCK" },
        { "SELECT * FROM t WITH (XLOCK)", "XLOCK" },
        { "SELECT * FROM t WITH (TABLOCK)", "TABLOCK" },
        { "SELECT * FROM t WITH (TABLOCKX)", "TABLOCKX" },
        { "SELECT * FROM t WITH (HOLDLOCK)", "HOLDLOCK" },
        { "SELECT * FROM t WITH (SERIALIZABLE)", "SERIALIZABLE" },
        { "SELECT * FROM t WITH (PAGLOCK)", "PAGLOCK" },
        { "SELECT * FROM t (UPDLOCK)", "UPDLOCK" },
        { "SELECT * FROM a JOIN b WITH (NOLOCK, UPDLOCK) ON a.id = b.id", "UPDLOCK" },
        { "SELECT * FROM t WITH (KEEPIDENTITY)", "KEEPIDENTITY" },

        // Spec: dynamic and indirect SQL.
        { "EXEC('SELECT 1')", "dynamic SQL" },
        { "EXECUTE (@sql)", "dynamic SQL" },
        { "EXEC sp_executesql N'SELECT 1'", "dynamic SQL" },
        { "EXEC sys.sp_executesql N'SELECT 1'", "dynamic SQL" },
        { "EXEC @proc", "named by a variable" },
        { "EXEC ('SELECT 1') AT lnk", "linked server" },

        // Rowset functions are rejected in every category.
        { "INSERT INTO t (a) SELECT a FROM OPENQUERY(lnk, 'SELECT 1')", "OPENQUERY" },
        { "INSERT INTO t (a) SELECT BulkColumn FROM OPENROWSET(BULK 'c:\\x.csv', SINGLE_CLOB) AS b", "OPENROWSET" },

        // UPDATE and DELETE need a WHERE clause.
        { "DELETE FROM dbo.users", "DELETE needs a WHERE clause that picks the rows to change" },
        { "UPDATE dbo.users SET active = 0", "UPDATE needs a WHERE clause that picks the rows to change" },
        { "UPDATE o SET status = 'x' FROM dbo.orders o JOIN dbo.customers c ON c.id = o.customer_id", "UPDATE needs a WHERE clause that picks the rows to change" },
        { "WITH x AS (SELECT 1 AS a) DELETE FROM dbo.t", "DELETE needs a WHERE clause that picks the rows to change" },

        // Writes stay in the target database.
        { "INSERT INTO sales.dbo.orders (id) VALUES (1)", "writes may only name objects in the target database" },
        { "INSERT INTO dbo.t (a) SELECT a FROM otherdb.dbo.s WHERE b = 1", "otherdb.dbo.s" },
        { "UPDATE t SET a = 1 FROM srv.db.dbo.t WHERE id = 1", "srv.db.dbo.t" },
        { "UPDATE t SET a = db.dbo.u.b FROM dbo.u WHERE id = 1", "column db.dbo.u.b" },
        { "DROP TABLE otherdb.dbo.t", "writes may only name objects in the target database" },
        { "EXEC app.dbo.p", "writes may only name objects in the target database" },
        { "EXEC remote.app.dbo.p", "writes may only name objects in the target database" },

        // Still outside the command allowlist.
        { "UPDATE t SET a = @x WHERE id = 1", "a @variable" },
        { "INSERT INTO t EXEC dbo.p", "ExecuteInsertSource" },
        { "UPDATE t SET a = 1 WHERE CURRENT OF c", "CursorId" },
        { "DELETE FROM t OUTPUT deleted.id INTO @ids WHERE id = 1", "VariableTableReference" },

        // A view's SELECT is checked as a query.
        { "CREATE VIEW dbo.v AS SELECT * FROM dbo.t WITH (UPDLOCK)", "the view's SELECT is not a valid query" },
        { "CREATE VIEW dbo.v AS SELECT a FROM dbo.t WITH (UPDLOCK)", "UPDLOCK" },
        { "CREATE VIEW dbo.v AS SELECT * FROM OPENQUERY(lnk, 'SELECT 1')", "OPENQUERY" },
        { "CREATE TABLE otherdb.dbo.t (a int)", "writes may only name objects in the target database" },

        // ALTER INDEX takes no WITH options.
        { "ALTER INDEX ix_t_ab ON dbo.t REBUILD WITH (ONLINE = ON)", "not allowed in ddl statements" },

        // Schemas: no owner, no elements.
        { "CREATE SCHEMA reporting AUTHORIZATION bob", "security change" },
        { "CREATE SCHEMA reporting CREATE TABLE t (a int)", "each object in its own statement" },
        { "ALTER SCHEMA reporting TRANSFER dbo.t", "AlterSchemaStatement" },

        // Defaults and CHECK constraints use scalar expressions only.
        { "ALTER TABLE dbo.t ADD CONSTRAINT df_t_c DEFAULT (@x) FOR c", "a @variable" },

        // Spec: programmable objects are refused, because their bodies are not inspected.
        { "CREATE PROCEDURE dbo.p AS EXEC('DROP TABLE t')", "procedures, functions and triggers can't be created through sql-mcp" },
        { "CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1", "procedures, functions and triggers can't be created through sql-mcp" },
        { "ALTER PROCEDURE dbo.p AS SELECT 1", "procedures, functions and triggers can't be created through sql-mcp" },
        { "CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END", "procedures, functions and triggers can't be created through sql-mcp" },
        { "CREATE OR ALTER FUNCTION dbo.f() RETURNS TABLE AS RETURN SELECT 1 AS a", "procedures, functions and triggers can't be created through sql-mcp" },
        { "ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 2 END", "procedures, functions and triggers can't be created through sql-mcp" },
        { "CREATE TRIGGER dbo.tr ON dbo.t AFTER INSERT AS DELETE FROM dbo.t", "procedures, functions and triggers can't be created through sql-mcp" },
        { "CREATE OR ALTER TRIGGER dbo.tr ON dbo.t AFTER INSERT AS SELECT 1", "procedures, functions and triggers can't be created through sql-mcp" },
        { "ALTER TRIGGER dbo.tr ON dbo.t AFTER INSERT AS SELECT 1", "procedures, functions and triggers can't be created through sql-mcp" },

        // Spec: database- and security-level DDL.
        { "CREATE USER bob WITHOUT LOGIN", "CreateUserStatement" },
        { "CREATE DATABASE other", "CreateDatabaseStatement" },
        { "ALTER DATABASE CURRENT SET RECOVERY SIMPLE", "AlterDatabase" },
        { "CREATE LOGIN bob WITH PASSWORD = 'x'", "CreateLoginStatement" },
        { "CREATE ROLE readers", "CreateRoleStatement" },
        { "ALTER ROLE db_owner ADD MEMBER bob", "AlterRoleStatement" },
        { "ALTER AUTHORIZATION ON SCHEMA::reporting TO bob", "AlterAuthorizationStatement" },
        { "DROP PROCEDURE dbo.p", "DropProcedureStatement" },

        // Spec: system and extended procedures.
        { "EXEC sp_addrolemember 'db_owner', 'bob'", "system and extended procedures can't be run" },
        { "EXEC sys.sp_configure", "system and extended procedures can't be run" },
        { "EXEC xp_cmdshell 'dir'", "system and extended procedures can't be run" },
        { "EXEC SP_WHO", "system and extended procedures can't be run" },
        { "EXEC dbo.sp_mine", "system and extended procedures can't be run" },
        { "EXEC sys.p", "system and extended procedures can't be run" },

        // Execute parameters: literals, NULL and negated numbers only.
        { "EXEC dbo.p @x", "must be a literal, NULL or a negative number" },
        { "EXEC dbo.p @a = @x", "must be a literal, NULL or a negative number" },
        { "EXEC @rc = dbo.p", "return value" },

        // Spec: unrecognised syntax names the element.
        { "SELECT @x = 1", "assigning to a @variable" },
        { "SELECT * FROM t TABLESAMPLE (10 PERCENT)", "TableSampleClause" },
    };
}
