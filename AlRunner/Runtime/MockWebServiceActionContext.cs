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

    public static MockWebServiceActionContext Default => new MockWebServiceActionContext();

    // ------------------------------------------------------------------
    // ObjectId — integer ID of the resulting BC object
    // ------------------------------------------------------------------

    private int _objectId;

    public void ALSetObjectId(int value) { _objectId = value; }
    public void ALSetObjectId(DataError errorLevel, int value) => ALSetObjectId(value);

    public int ALGetObjectId() => _objectId;
    public int ALGetObjectId(DataError errorLevel) => _objectId;

    // ------------------------------------------------------------------
    // ObjectType — BC ObjectType enum value
    // ------------------------------------------------------------------

    private NavObjectType _objectType;

    public void ALSetObjectType(NavObjectType value) { _objectType = value; }
    public void ALSetObjectType(DataError errorLevel, NavObjectType value) => ALSetObjectType(value);

    public NavObjectType ALGetObjectType() => _objectType;
    public NavObjectType ALGetObjectType(DataError errorLevel) => _objectType;

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
    // AL signature: AddEntityKey(fieldId: Integer; value: Variant)
    // Standalone: no OData serialisation; pairs stored in memory only.
    // ------------------------------------------------------------------

    private readonly List<(int FieldId, object? Value)> _entityKeys = new();

    public void ALAddEntityKey(int fieldId, object? value)
        => _entityKeys.Add((fieldId, value));

    public void ALAddEntityKey(DataError errorLevel, int fieldId, object? value)
        => ALAddEntityKey(fieldId, value);

    /// <summary>Read-only snapshot of added entity keys (for test assertions).</summary>
    public IReadOnlyList<(int FieldId, object? Value)> EntityKeys
        => _entityKeys.AsReadOnly();
}
