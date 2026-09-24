using Microsoft.SqlServer.TransactSql.ScriptDom;
using Workbench.Models;

namespace Workbench.Services;

public static class SelectValidator
{
    public static void Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 50000) throw new WorkbenchException("Enter a SELECT query of at most 50,000 characters.");
        var parser = new TSql160Parser(true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0) throw new WorkbenchException($"Invalid SELECT syntax at line {errors[0].Line}, column {errors[0].Column}.");
        if (fragment is not TSqlScript script || script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1 || script.Batches[0].Statements[0] is not SelectStatement select)
            throw new WorkbenchException("Only one SELECT statement is allowed. CTEs are supported; batches and write statements are not.");
        if (select.Into != null) throw new WorkbenchException("SELECT INTO is not allowed.");
        if (select.OptimizerHints.Count > 0) throw new WorkbenchException("Query hints are not supported in the workbench.");
        var visitor = new ReadOnlyVisitor();
        fragment.Accept(visitor);
        if (visitor.Blocked) throw new WorkbenchException("This SELECT uses an unsupported operation (external access, sequences, variables, or locking hints).");
    }

    private sealed class ReadOnlyVisitor : TSqlFragmentVisitor
    {
        public bool Blocked { get; private set; }
        public override void Visit(TSqlFragment node)
        {
            // These nodes can contact other servers, consume sequences, or request update locks.
            var name = node.GetType().Name;
            if (name is "NextValueForExpression" or "OpenQueryTableReference" or "OpenRowsetTableReference" or "BulkOpenRowset" or "AdHocTableReference" or "SelectSetVariable" or "VariableReference" or "TableHint" or "TableHintWithValue" or "OptimizerHint") Blocked = true;
        }
        public override void ExplicitVisit(SchemaObjectName node)
        {
            if (node.ServerIdentifier != null || node.DatabaseIdentifier != null) Blocked = true;
            base.ExplicitVisit(node);
        }
    }
    public static string Identifier(string name) => "[" + name.Replace("]", "]]") + "]";
    public static string Qualified(DatabaseObject table) => Identifier(table.Schema) + "." + Identifier(table.Name);
}
