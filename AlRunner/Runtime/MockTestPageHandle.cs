using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

/// <summary>
/// Mock for NavTestPageHandle — the BC type emitted for TestPage variables in test codeunits.
///
/// BC generates: new NavTestPageHandle(this, pageId)
/// Rewriter transforms to: new MockTestPageHandle(pageId)
///
/// Supports:
/// - ALOpenEdit(), ALOpenView(), ALOpenNew(), ALClose() — lifecycle no-ops
/// - ALTrap() — marks page as expecting modal open (no-op)
/// - GetField(fieldHash) — returns MockTestPageField for value get/set
/// - GetBuiltInAction(FormResult) — returns MockTestPageAction for OK/Cancel/Close
/// </summary>
public class MockTestPageHandle
{
    public int PageId { get; }

    private readonly Dictionary<int, MockTestPageField> _fields = new();

    public MockTestPageHandle() { }

    public MockTestPageHandle(int pageId)
    {
        PageId = pageId;
    }

    // Lifecycle methods — no-ops in standalone mode
    public void ALOpenEdit() { }
    public void ALOpenView() { }
    public void ALOpenNew() { }
    public void ALClose() { }
    public void ALTrap() { }

    /// <summary>
    /// Returns a MockTestPageField for the given field hash.
    /// BC generates field hashes (not field IDs) for TestPage field access.
    /// The field stores values in memory for get/set assertions.
    /// </summary>
    public MockTestPageField GetField(int fieldHash)
    {
        if (!_fields.TryGetValue(fieldHash, out var field))
        {
            field = new MockTestPageField(fieldHash);
            _fields[fieldHash] = field;
        }
        return field;
    }

    /// <summary>
    /// Returns a MockTestPageAction for built-in actions (OK, Cancel, Close, etc.).
    /// BC casts FormResult enum values: GetBuiltInAction((FormResult)1) for OK.
    /// </summary>
    public MockTestPageAction GetBuiltInAction(object formResult)
    {
        return new MockTestPageAction();
    }
}

/// <summary>
/// Mock for TestPage field access. BC generates:
///   tP.GetField(fieldHash).ALSetValue(this.Session, value)
///   tP.GetField(fieldHash).ALValue
///
/// Stores the last set value as a NavValue and returns it via ALValue.
/// </summary>
public class MockTestPageField
{
    private readonly int _fieldHash;
    private NavValue _value;

    public MockTestPageField(int fieldHash)
    {
        _fieldHash = fieldHash;
        _value = new NavText("");
    }

    /// <summary>
    /// Set the field value. BC passes (session, navValue) — session is null in standalone mode.
    /// </summary>
    public void ALSetValue(object? session, NavValue value)
    {
        _value = value;
    }

    /// <summary>
    /// Get the current field value as a NavValue.
    /// BC reads this to pass to Assert.AreEqual via ALCompiler.ToVariant.
    /// </summary>
    public NavValue ALValue => _value;
}

/// <summary>
/// Mock for TestPage built-in actions (OK, Cancel, Close).
/// BC generates: tP.GetBuiltInAction((FormResult)1).ALInvoke()
/// </summary>
public class MockTestPageAction
{
    public void ALInvoke() { }
}
