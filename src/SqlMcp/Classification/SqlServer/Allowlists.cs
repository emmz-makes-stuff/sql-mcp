using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlMcp.Classification.SqlServer;

/// <summary>
/// The node types each category accepts. A type joins a list only alongside a classifier-table case that needs it
/// (design D6); see docs/classifier.md. Meaning that depends on more than the type (SELECT INTO, table hint kinds,
/// dynamic EXEC) is checked separately, in <see cref="SideEffectChecks"/>.
/// </summary>
internal static class Allowlists
{
    /// <summary>The candidate category of each top-level statement type. Any other statement is rejected.</summary>
    public static readonly IReadOnlyDictionary<Type, StatementCategory> StatementCategories = new Dictionary<Type, StatementCategory>
    {
        [typeof(SelectStatement)] = StatementCategory.Query,
        [typeof(InsertStatement)] = StatementCategory.Command,
        [typeof(UpdateStatement)] = StatementCategory.Command,
        [typeof(DeleteStatement)] = StatementCategory.Command,
        [typeof(MergeStatement)] = StatementCategory.Command,
        [typeof(CreateTableStatement)] = StatementCategory.Ddl,
        [typeof(DropTableStatement)] = StatementCategory.Ddl,
        [typeof(AlterTableAddTableElementStatement)] = StatementCategory.Ddl,
        [typeof(AlterTableAlterColumnStatement)] = StatementCategory.Ddl,
        [typeof(AlterTableDropTableElementStatement)] = StatementCategory.Ddl,
        [typeof(AlterTableConstraintModificationStatement)] = StatementCategory.Ddl,
        [typeof(TruncateTableStatement)] = StatementCategory.Ddl,
        [typeof(CreateIndexStatement)] = StatementCategory.Ddl,
        [typeof(AlterIndexStatement)] = StatementCategory.Ddl,
        [typeof(DropIndexStatement)] = StatementCategory.Ddl,
        [typeof(CreateViewStatement)] = StatementCategory.Ddl,
        [typeof(AlterViewStatement)] = StatementCategory.Ddl,
        [typeof(CreateOrAlterViewStatement)] = StatementCategory.Ddl,
        [typeof(DropViewStatement)] = StatementCategory.Ddl,
        [typeof(CreateSchemaStatement)] = StatementCategory.Ddl,
        [typeof(DropSchemaStatement)] = StatementCategory.Ddl,
        [typeof(CreateSequenceStatement)] = StatementCategory.Ddl,
        [typeof(AlterSequenceStatement)] = StatementCategory.Ddl,
        [typeof(DropSequenceStatement)] = StatementCategory.Ddl,
        [typeof(ExecuteStatement)] = StatementCategory.Execute,
    };

    /// <summary>
    /// Names, literals, scalar expressions and predicates: what a query's expressions use, and what a DDL default,
    /// CHECK constraint or computed column may use (design W5). Subqueries are not here.
    /// </summary>
    private static readonly HashSet<Type> Scalars =
    [
        // Names
        typeof(Identifier),
        typeof(MultiPartIdentifier),
        typeof(SchemaObjectName),
        typeof(IdentifierOrValueExpression),
        typeof(SqlDataTypeReference),

        // Literals
        typeof(IntegerLiteral),
        typeof(NumericLiteral),
        typeof(RealLiteral),
        typeof(MoneyLiteral),
        typeof(StringLiteral),
        typeof(BinaryLiteral),
        typeof(NullLiteral),
        typeof(IdentifierLiteral),

        // Scalar expressions. Functions can't write; CLR and extended-procedure side effects are the gap that
        // ADR-0002 accepts.
        typeof(ColumnReferenceExpression),
        typeof(BinaryExpression),
        typeof(UnaryExpression),
        typeof(ParenthesisExpression),
        typeof(FunctionCall),
        typeof(MultiPartIdentifierCallTarget),
        typeof(ParameterlessCall),
        typeof(CastCall),
        typeof(ConvertCall),
        typeof(TryCastCall),
        typeof(TryConvertCall),
        typeof(IIfCall),
        typeof(CoalesceExpression),
        typeof(NullIfExpression),
        typeof(SearchedCaseExpression),
        typeof(SearchedWhenClause),
        typeof(SimpleCaseExpression),
        typeof(SimpleWhenClause),

        // Predicates
        typeof(BooleanComparisonExpression),
        typeof(BooleanBinaryExpression),
        typeof(BooleanParenthesisExpression),
        typeof(BooleanIsNullExpression),
        typeof(BooleanTernaryExpression),
        typeof(InPredicate),
        typeof(LikePredicate),
        typeof(ExistsPredicate),
        typeof(SubqueryComparisonPredicate),
    ];

    private static readonly HashSet<Type> Query =
    [
        typeof(SelectStatement),

        // Query structure
        typeof(QuerySpecification),
        typeof(BinaryQueryExpression),
        typeof(QueryParenthesisExpression),
        typeof(WithCtesAndXmlNamespaces),
        typeof(CommonTableExpression),
        typeof(SelectScalarExpression),
        typeof(SelectStarExpression),
        typeof(FromClause),
        typeof(WhereClause),
        typeof(GroupByClause),
        typeof(HavingClause),
        typeof(OrderByClause),
        typeof(ExpressionWithSortOrder),
        typeof(TopRowFilter),
        typeof(OffsetClause),

        // Table sources
        typeof(NamedTableReference),
        typeof(QualifiedJoin),
        typeof(UnqualifiedJoin),
        typeof(QueryDerivedTable),
        typeof(InlineDerivedTable),
        typeof(RowValue),
        typeof(SchemaObjectFunctionTableReference),
        typeof(PivotedTableReference),
        typeof(UnpivotedTableReference),
        typeof(OpenJsonTableReference),
        typeof(SchemaDeclarationItemOpenjson),
        typeof(ColumnDefinitionBase),
        typeof(TemporalClause),

        // Table hints: the hint kind is checked in SideEffectChecks.
        typeof(TableHint),
        typeof(IndexTableHint),
        typeof(ForceSeekTableHint),

        // Grouping
        typeof(ExpressionGroupingSpecification),
        typeof(CompositeGroupingSpecification),
        typeof(RollupGroupingSpecification),
        typeof(GroupingSetsGroupingSpecification),
        typeof(GrandTotalGroupingSpecification),

        // Windows
        typeof(OverClause),
        typeof(WindowFrameClause),
        typeof(WindowDelimiter),
        typeof(WithinGroupClause),

        // FOR JSON / FOR XML
        typeof(JsonForClause),
        typeof(JsonForClauseOption),
        typeof(XmlForClause),
        typeof(XmlForClauseOption),

        // OPTION (...)
        typeof(OptimizerHint),
        typeof(LiteralOptimizerHint),
        typeof(OptimizeForOptimizerHint),

        .. Scalars,
        typeof(ScalarSubquery),
    ];

    /// <summary>
    /// A command reads with everything a query may use, plus the DML nodes (design W5). Locking hints and
    /// <c>NEXT VALUE FOR</c> are fine here: the write takes those locks and advances sequences anyway.
    /// </summary>
    private static readonly HashSet<Type> Command =
    [
        .. Query,

        typeof(InsertStatement),
        typeof(InsertSpecification),
        typeof(ValuesInsertSource),
        typeof(SelectInsertSource),
        typeof(UpdateStatement),
        typeof(UpdateSpecification),
        typeof(AssignmentSetClause),
        typeof(DeleteStatement),
        typeof(DeleteSpecification),
        typeof(MergeStatement),
        typeof(MergeSpecification),
        typeof(MergeActionClause),
        typeof(UpdateMergeAction),
        typeof(InsertMergeAction),
        typeof(DeleteMergeAction),
        typeof(OutputClause),
        typeof(OutputIntoClause),
        typeof(NextValueForExpression),
    ];

    /// <summary>
    /// Declarative objects only (design W5): tables, columns, constraints, indexes, views, schemas and sequences.
    /// Defaults, CHECK constraints and computed columns use the scalar expressions. A view's SELECT is walked under
    /// the query category, so the query list is not needed here.
    /// </summary>
    private static readonly HashSet<Type> Ddl =
    [
        .. Scalars,

        // Tables, columns and constraints
        typeof(CreateTableStatement),
        typeof(TableDefinition),
        typeof(ColumnDefinition),
        typeof(NullableConstraintDefinition),
        typeof(UniqueConstraintDefinition),
        typeof(DefaultConstraintDefinition),
        typeof(CheckConstraintDefinition),
        typeof(ForeignKeyConstraintDefinition),
        typeof(IdentityOptions),
        typeof(ColumnWithSortOrder),
        typeof(DropTableStatement),
        typeof(AlterTableAddTableElementStatement),
        typeof(AlterTableAlterColumnStatement),
        typeof(AlterTableDropTableElementStatement),
        typeof(AlterTableDropTableElement),
        typeof(AlterTableConstraintModificationStatement),
        typeof(TruncateTableStatement),

        // Indexes
        typeof(CreateIndexStatement),
        typeof(AlterIndexStatement),
        typeof(DropIndexStatement),
        typeof(DropIndexClause),
        typeof(IndexDefinition),

        // Views: the SELECT itself is checked as a query.
        typeof(CreateViewStatement),
        typeof(AlterViewStatement),
        typeof(CreateOrAlterViewStatement),
        typeof(DropViewStatement),
        typeof(ViewOption),

        // Schemas: CREATE SCHEMA only without elements or AUTHORIZATION (SideEffectChecks).
        typeof(CreateSchemaStatement),
        typeof(DropSchemaStatement),
        typeof(StatementList),

        // Sequences
        typeof(CreateSequenceStatement),
        typeof(AlterSequenceStatement),
        typeof(DropSequenceStatement),
        typeof(DataTypeSequenceOption),
        typeof(ScalarExpressionSequenceOption),

        // A sequence-backed default (DEFAULT NEXT VALUE FOR s) advances the sequence only when a row is inserted.
        typeof(NextValueForExpression),
    ];

    private static readonly HashSet<Type> Execute =
    [
        typeof(ExecuteStatement),
        typeof(ExecuteSpecification),
        typeof(ExecutableProcedureReference),
        typeof(ProcedureReferenceName),
        typeof(ProcedureReference),
        typeof(ExecuteParameter),

        // A parameter name (@a = 1). EXEC of a procedure *named* by a variable, and a variable as a parameter's
        // value, are rejected in SideEffectChecks.
        typeof(VariableReference),

        typeof(Identifier),
        typeof(SchemaObjectName),

        // Parameter values: literals, NULL, and negated numbers (SideEffectChecks keeps UnaryExpression to those).
        typeof(IntegerLiteral),
        typeof(NumericLiteral),
        typeof(RealLiteral),
        typeof(MoneyLiteral),
        typeof(StringLiteral),
        typeof(BinaryLiteral),
        typeof(NullLiteral),
        typeof(UnaryExpression),
    ];

    public static IReadOnlySet<Type> For(StatementCategory category) => category switch
    {
        StatementCategory.Query => Query,
        StatementCategory.Command => Command,
        StatementCategory.Ddl => Ddl,
        StatementCategory.Execute => Execute,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };
}
