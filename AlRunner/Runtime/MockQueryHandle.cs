using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

/// <summary>
/// Stub for <c>NavQueryHandle</c> / AL's <c>Query "X"</c> variable, and also
/// serves as the base class for generated Query object classes (QueryNNNN : NavQuery).
///
/// <b>Query handle (variable):</b>
/// BC emits <c>new NavQueryHandle(this, queryId, SecurityFiltering)</c> in
/// scope-class field initialisers.  The rewriter rewrites the type to
/// <c>MockQueryHandle</c> and strips the ITreeObject <c>this</c> arg, leaving
/// <c>new MockQueryHandle(queryId, SecurityFiltering.Filtered)</c>.
///
/// After the existing <c>.Target</c>-stripping rewrite, the generated code
/// calls members directly on the handle, e.g.:
/// <code>
///   q.ALOpen(DataError.ThrowError);
///   q.ALRead(DataError.ThrowError);
///   q.ALClose();
///   q.ALSetFilter(columnHash, "ITEM001");
///   q.ALTopNumberOfRowsToReturn = 10;
/// </code>
///
/// <b>Query object class:</b>
/// Generated Query classes (QueryNNNN : NavQuery) are replaced by the rewriter
/// with minimal stubs that extend <c>MockQueryHandle</c>, similar to how XmlPort
/// classes extend <c>MockXmlPortHandle</c>.
///
/// Query data access (Open/Read) requires the BC service tier (SQL views).
/// <c>ALOpen</c> and <c>ALRead</c> throw <see cref="NotSupportedException"/>
/// so tests fail clearly.  <c>ALClose</c> is a no-op.  Filter/range methods
/// are no-ops to allow pre-Open setup code to compile and run.
/// Inject query dependencies via an AL interface to make query-dependent code
/// unit-testable.
/// </summary>
public class MockQueryHandle
{
    public int QueryId { get; }

    public MockQueryHandle() { }

    public MockQueryHandle(int queryId)
    {
        QueryId = queryId;
    }

    public MockQueryHandle(int queryId, SecurityFiltering securityFiltering)
    {
        QueryId = queryId;
        ALSecurityFiltering = securityFiltering;
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>Q.Open()</c> — opens the query for reading.
    /// Throws <see cref="NotSupportedException"/> because query data access
    /// requires the BC service tier (SQL views).
    /// </summary>
    public bool ALOpen(DataError errorLevel = default)
        => throw new NotSupportedException(
            $"Query.Open (Query {QueryId}) is not supported in al-runner standalone mode. " +
            "Query data access requires the BC service tier. " +
            "Inject the query dependency via an AL interface to make this code unit-testable.");

    /// <summary>
    /// <c>Q.Read()</c> — reads the next row from the query result set.
    /// Throws <see cref="NotSupportedException"/>.
    /// </summary>
    public bool ALRead(DataError errorLevel = default)
        => throw new NotSupportedException(
            $"Query.Read (Query {QueryId}) is not supported in al-runner standalone mode. " +
            "Query data access requires the BC service tier. " +
            "Inject the query dependency via an AL interface to make this code unit-testable.");

    /// <summary>
    /// <c>Q.Close()</c> — closes the query. No-op in standalone mode.
    /// </summary>
    public void ALClose() { }

    // ------------------------------------------------------------------
    // Filter / range operations (no-ops — allow pre-Open setup code to run)
    // ------------------------------------------------------------------

    /// <summary><c>Q.SetFilter(columnNo, expression, args...)</c> — no-op.</summary>
    public void ALSetFilter(int columnNo, string expression, params NavValue[] values) { }

    /// <summary><c>Q.SetRange(columnNo)</c> — clear range, no-op.</summary>
    public void ALSetRangeSafe(int columnNo, NavType expectedType) { }

    /// <summary><c>Q.SetRange(columnNo, value)</c> — single-value range, no-op.</summary>
    public void ALSetRangeSafe(int columnNo, NavType expectedType, NavValue value) { }

    /// <summary><c>Q.SetRange(columnNo, from, to)</c> — range, no-op.</summary>
    public void ALSetRangeSafe(int columnNo, NavType expectedType, NavValue fromValue, NavValue toValue) { }

    // ------------------------------------------------------------------
    // Column access (stubs — return defaults)
    // ------------------------------------------------------------------

    /// <summary><c>Q.ColumnValue(columnId)</c> — returns default NavValue.</summary>
    public NavValue GetColumnValueSafe(int columnId, NavType expectedType)
    {
        return expectedType switch
        {
            NavType.Integer => NavInteger.Default,
            NavType.Decimal => NavDecimal.Default,
            NavType.Text => NavText.Default(0),
            NavType.Code => new NavCode(20, ""),
            NavType.Boolean => NavBoolean.Default,
            NavType.Date => NavDate.Default,
            NavType.Time => NavTime.Default,
            NavType.DateTime => NavDateTime.Default,
            NavType.BigInteger => NavBigInteger.Default,
            NavType.GUID => new NavGuid(Guid.Empty),
            NavType.Option => NavInteger.Default,
            _ => NavText.Default(0)
        };
    }

    /// <summary><c>Q.ColumnValue(columnId)</c> for Option columns — returns default.</summary>
    public NavValue GetColumnOptionSafe(int columnId, NavType expectedType)
        => GetColumnValueSafe(columnId, expectedType);

    // ------------------------------------------------------------------
    // Column metadata (stubs)
    // ------------------------------------------------------------------

    /// <summary><c>Q.ColumnCaption(columnNo)</c> — returns stub caption.</summary>
    public string ALColumnCaption(int columnNo) => $"Column{columnNo}";

    /// <summary><c>Q.ColumnName(columnNo)</c> — returns stub name.</summary>
    public string ALColumnName(int columnNo) => $"Column{columnNo}";

    /// <summary><c>Q.ColumnNo(columnNo)</c> — returns the column number as-is.</summary>
    public int ALColumnNo(int columnNo) => columnNo;

    /// <summary><c>Q.GetFilter(columnNo)</c> — returns empty string (filters not tracked).</summary>
    public string ALGetFilter(int columnNo) => "";

    // ------------------------------------------------------------------
    // Properties
    // ------------------------------------------------------------------

    /// <summary><c>Q.TopNumberOfRows</c> — get/set the top N rows to return.</summary>
    public int ALTopNumberOfRowsToReturn { get; set; }

    /// <summary><c>Q.SkipNumberOfRows</c> — get/set the number of rows to skip.</summary>
    public int ALSkipNumberOfRows { get; set; }

    /// <summary><c>Q.SecurityFiltering</c> — get/set security filtering mode.</summary>
    public SecurityFiltering ALSecurityFiltering { get; set; } = SecurityFiltering.Filtered;

    /// <summary><c>Q.GetFilters</c> — returns empty string (filters not tracked).</summary>
    public string ALGetFilters => "";

    /// <summary><c>Q.Caption</c> — returns stub caption.</summary>
    public string Caption => $"Query{QueryId}";

    // ------------------------------------------------------------------
    // Handle operations
    // ------------------------------------------------------------------

    /// <summary><c>ALAssign</c> — assign from another query handle.</summary>
    public void ALAssign(MockQueryHandle other)
    {
        // In standalone mode, assignment is a no-op — the mock doesn't track state.
    }

    /// <summary><c>ALByValue</c> — return a copy of this handle.</summary>
    public MockQueryHandle ALByValue(object? parentOfResult = null)
        => new MockQueryHandle(QueryId);

    /// <summary><c>Clear()</c> — reset the handle.</summary>
    public void Clear() { }

    /// <summary>
    /// Dispatch a helper procedure on the query (e.g. custom triggers).
    /// Returns null — query procedural dispatch requires the service tier.
    /// </summary>
    public object? Invoke(int memberId, object[] args) => null;

    // ------------------------------------------------------------------
    // Static methods (SaveAsCsv, SaveAsXml, etc.)
    // These are called as Query.SaveAsCsv(queryId, ...) in AL and transpiled
    // as NavQuery.ALSaveAsCsv(DataError, queryId, ...).
    // All throw NotSupportedException.
    // ------------------------------------------------------------------

    /// <summary>Static <c>Query.SaveAsCsv(queryId, filename)</c> — throws.</summary>
    public static bool ALSaveAsCsv(DataError errorLevel, int queryId, string fileName)
        => throw new NotSupportedException(
            $"Query.SaveAsCsv (Query {queryId}) is not supported in al-runner standalone mode.");

    /// <summary>Static <c>Query.SaveAsXml(queryId, filename)</c> — throws.</summary>
    public static bool ALSaveAsXml(DataError errorLevel, int queryId, string fileName)
        => throw new NotSupportedException(
            $"Query.SaveAsXml (Query {queryId}) is not supported in al-runner standalone mode.");

    /// <summary>Static <c>Query.SaveAsXml(queryId, outStream)</c> — throws.</summary>
    public static bool ALSaveAsXml(DataError errorLevel, int queryId, MockOutStream outStream)
        => throw new NotSupportedException(
            $"Query.SaveAsXml (Query {queryId}) is not supported in al-runner standalone mode.");

    /// <summary>Instance <c>Q.SaveAsCsv(filename)</c> — throws.</summary>
    public bool ALSaveAsCsv(DataError errorLevel, string fileName)
        => throw new NotSupportedException(
            $"Query.SaveAsCsv (Query {QueryId}) is not supported in al-runner standalone mode.");

    /// <summary>Instance <c>Q.SaveAsXml(filename)</c> — throws.</summary>
    public bool ALSaveAsXml(DataError errorLevel, string fileName)
        => throw new NotSupportedException(
            $"Query.SaveAsXml (Query {QueryId}) is not supported in al-runner standalone mode.");

    /// <summary>Instance <c>Q.SaveAsXml(outStream)</c> — throws.</summary>
    public bool ALSaveAsXml(DataError errorLevel, MockOutStream outStream)
        => throw new NotSupportedException(
            $"Query.SaveAsXml (Query {QueryId}) is not supported in al-runner standalone mode.");

    /// <summary>Static <c>Query.SaveAsJson(queryId, outStream)</c> — throws.</summary>
    public static bool ALSaveAsJson(DataError errorLevel, int queryId, MockOutStream outStream)
        => throw new NotSupportedException(
            $"Query.SaveAsJson (Query {queryId}) is not supported in al-runner standalone mode.");

    /// <summary>Instance <c>Q.SaveAsJson(outStream)</c> — throws.</summary>
    public bool ALSaveAsJson(DataError errorLevel, MockOutStream outStream)
        => throw new NotSupportedException(
            $"Query.SaveAsJson (Query {QueryId}) is not supported in al-runner standalone mode.");

    /// <summary>Static <c>Query.SaveAsExcel(queryId, filename)</c> — throws.</summary>
    public static bool ALSaveAsExcel(DataError errorLevel, int queryId, string fileName)
        => throw new NotSupportedException(
            $"Query.SaveAsExcel (Query {queryId}) is not supported in al-runner standalone mode.");

    /// <summary>Instance <c>Q.SaveAsExcel(filename)</c> — throws.</summary>
    public bool ALSaveAsExcel(DataError errorLevel, string fileName)
        => throw new NotSupportedException(
            $"Query.SaveAsExcel (Query {QueryId}) is not supported in al-runner standalone mode.");
}
