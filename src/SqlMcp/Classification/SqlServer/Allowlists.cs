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
        [typeof(TruncateTableStatement)] = StatementCategory.Ddl,
        [typeof(ExecuteStatement)] = StatementCategory.Execute,
    };

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
        typeof(ScalarSubquery),

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

    private static readonly HashSet<Type> Command =
    [
        typeof(InsertStatement),
        typeof(InsertSpecification),
        typeof(ValuesInsertSource),
        typeof(RowValue),
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

        // The SELECT of INSERT ... SELECT, and filters.
        typeof(QuerySpecification),
        typeof(SelectScalarExpression),
        typeof(FromClause),
        typeof(WhereClause),
        typeof(NamedTableReference),

        typeof(Identifier),
        typeof(MultiPartIdentifier),
        typeof(SchemaObjectName),
        typeof(ColumnReferenceExpression),
        typeof(IntegerLiteral),
        typeof(StringLiteral),
        typeof(BinaryExpression),
        typeof(BooleanComparisonExpression),
    ];

    private static readonly HashSet<Type> Ddl =
    [
        typeof(CreateTableStatement),
        typeof(TableDefinition),
        typeof(ColumnDefinition),
        typeof(NullableConstraintDefinition),
        typeof(UniqueConstraintDefinition),
        typeof(DropTableStatement),
        typeof(AlterTableAddTableElementStatement),
        typeof(TruncateTableStatement),

        typeof(Identifier),
        typeof(SchemaObjectName),
        typeof(SqlDataTypeReference),
        typeof(IntegerLiteral),
    ];

    private static readonly HashSet<Type> Execute =
    [
        typeof(ExecuteStatement),
        typeof(ExecuteSpecification),
        typeof(ExecutableProcedureReference),
        typeof(ProcedureReferenceName),
        typeof(ProcedureReference),
        typeof(ExecuteParameter),

        // A parameter name (@a = 1). EXEC of a procedure *named* by a variable is rejected in SideEffectChecks.
        typeof(VariableReference),

        typeof(Identifier),
        typeof(SchemaObjectName),
        typeof(IntegerLiteral),
        typeof(StringLiteral),
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
