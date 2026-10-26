

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
        { "UPDATE t SET a = 1", Command },
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
        { "DELETE t", Command },
        { "MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = s.a WHEN NOT MATCHED THEN INSERT (id, a) VALUES (s.id, s.a);", Command },

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
        { "CREATE PROCEDURE p AS SELECT 1", "CreateProcedureStatement" },

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

        // Spec: unrecognised syntax names the element.
        { "SELECT @x = 1", "assigning to a @variable" },
        { "SELECT * FROM t TABLESAMPLE (10 PERCENT)", "TableSampleClause" },
    };
}
