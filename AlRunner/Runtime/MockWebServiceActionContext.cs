namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory stub for <c>NavWebServiceActionContext</c> / AL's <c>WebServiceActionContext</c>.
///
/// Used in OData action handlers. The real type hooks into BC service-tier OData
/// dispatch; the mock stores state in fields for unit testing.
///
/// All state-mutation methods correspond 1-to-1 to AL's WebServiceActionContext methods.
/// AddEntityKey stores key-value pairs in memory; results are discarded (no OData
/// serialisation in standalone mode).
/// </summary>
public class MockWebServiceActionContext
{
    public MockWebServiceActionContext() { }

    // ------------------------------------------------------------------
    // ObjectId — integer ID of the resulting BC object
    // ------------------------------------------------------------------

    private int _objectId;

    public void ALSetObjectId(int value) { _objectId = value; }
    public void ALSetObjectId(DataError errorLevel, int value) => ALSetObjectId(value);

    public int ALGetObjectId() => _objectId;
    public int ALGetObjectId(DataError errorLevel) => _objectId;

    // ------------------------------------------------------------------
    // ObjectType — object type integer (BC ObjectType enum value)
    // ------------------------------------------------------------------

    private int _objectType;

    public void ALSetObjectType(int value) { _objectType = value; }
    public void ALSetObjectType(DataError errorLevel, int value) => ALSetObjectType(value);

    public int ALGetObjectType() => _objectType;
    public int ALGetObjectType(DataError errorLevel) => _objectType;

    // ------------------------------------------------------------------
    // ResultCode — WebServiceActionResultCode enum
    // ------------------------------------------------------------------

    private WebServiceActionResultCode _resultCode;

    public void ALSetResultCode(WebServiceActionResultCode value) { _resultCode = value; }
    public void ALSetResultCode(DataError errorLevel, WebServiceActionResultCode value) => ALSetResultCode(value);

    public WebServiceActionResultCode ALGetResultCode() => _resultCode;
    public WebServiceActionResultCode ALGetResultCode(DataError errorLevel) => _resultCode;

    // ------------------------------------------------------------------
    // AddEntityKey — stores field key-value pairs for the OData response.
    // Standalone: no OData serialisation; pairs stored in memory only.
    // ------------------------------------------------------------------

    private readonly List<(int TableId, string FieldName, string FieldValue)> _entityKeys = new();

    public void ALAddEntityKey(int tableId, string fieldName, string fieldValue)
        => _entityKeys.Add((tableId, fieldName, fieldValue));

    public void ALAddEntityKey(DataError errorLevel, int tableId, string fieldName, string fieldValue)
        => ALAddEntityKey(tableId, fieldName, fieldValue);

    /// <summary>Read-only snapshot of added entity keys (for test assertions).</summary>
    public IReadOnlyList<(int TableId, string FieldName, string FieldValue)> EntityKeys
        => _entityKeys.AsReadOnly();
}
