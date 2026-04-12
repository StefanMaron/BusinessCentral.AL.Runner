namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Mock for NavFieldRef — wraps a field slot on a MockRecordHandle (accessed
/// through MockRecordRef). Provides ALValue get/set, ALNumber, ALName, etc.
/// </summary>
public class MockFieldRef
{
    private MockRecordRef? _owner;
    private int _fieldNo;

    public MockFieldRef() { }

    /// <summary>
    /// ALAssign — called when the BC compiler emits <c>fldRef.ALAssign(recRef.ALField(n))</c>.
    /// Binds this MockFieldRef to the given source (another MockFieldRef returned by ALField).
    /// </summary>
    public void ALAssign(MockFieldRef source)
    {
        _owner = source._owner;
        _fieldNo = source._fieldNo;
    }

    /// <summary>
    /// ALValue — property that gets/sets the field value on the underlying record.
    /// BC compiler emits <c>fldRef.ALValue</c> for reads and <c>fldRef.ALValue = x</c> for writes.
    /// </summary>
    public NavValue ALValue
    {
        get
        {
            if (_owner == null)
                return NavText.Default(0);
            return _owner.GetFieldValue(_fieldNo);
        }
        set
        {
            if (_owner != null)
                _owner.SetFieldValue(_fieldNo, value);
        }
    }

    /// <summary>ALNumber — property returning the field number.</summary>
    public int ALNumber => _fieldNo;

    /// <summary>ALName — property returning the field name (stub: "FieldNN").</summary>
    public NavText ALName => new NavText($"Field{_fieldNo}");

    /// <summary>ALCaption — property returning the field caption (stub: "FieldNN").</summary>
    public NavText ALCaption => new NavText($"Field{_fieldNo}");

    /// <summary>ALLength — property returning field length (stub: 0).</summary>
    public int ALLength => 0;

    /// <summary>ALType — property returning field type (stub: NavType.Text).</summary>
    public NavType ALType => NavType.Text;

    /// <summary>ALClass — field class (Normal/FlowField/FlowFilter). Stub: always 0 (Normal).</summary>
    public int ALClass => 0;

    /// <summary>ALRelation — table relation target table ID. Stub: 0.</summary>
    public int ALRelation => 0;

    /// <summary>ALActive — whether the field is active. Stub: always true.</summary>
    public bool ALActive => true;

    /// <summary>ALGetFilter — returns filter string on this field. Stub: empty string.</summary>
    public string ALGetFilter => "";

    /// <summary>ALGetRangeMin — returns range min. Stub: NavText.Default(0).</summary>
    public NavValue ALGetRangeMin => NavText.Default(0);

    /// <summary>ALGetRangeMax — returns range max. Stub: NavText.Default(0).</summary>
    public NavValue ALGetRangeMax => NavText.Default(0);

    /// <summary>ALOptionCaption — option caption string. Stub: empty.</summary>
    public string ALOptionCaption => "";

    /// <summary>ALOptionString — option string. Stub: empty.</summary>
    public string ALOptionString => "";

    /// <summary>ALRecord — returns the owning record ref. Stub for compile compat.</summary>
    public MockRecordRef ALRecord() => _owner ?? new MockRecordRef();

    /// <summary>ALValidate — set value and fire OnValidate trigger.</summary>
    public void ALValidate(NavValue value)
    {
        if (_owner != null)
            _owner.ValidateFieldValue(_fieldNo, value);
    }

    /// <summary>ALValidate — no-arg overload (re-validate current value).</summary>
    public void ALValidate()
    {
        if (_owner != null)
            _owner.ValidateFieldValue(_fieldNo, ALValue);
    }

    /// <summary>ALValidateSafe — safe variant of Validate.</summary>
    public void ALValidateSafe(NavValue value) => ALValidate(value);

    /// <summary>ALSetRange — delegate to owning RecordRef's handle.</summary>
    public void ALSetRange(NavValue value)
    {
        _owner?.SetRange(_fieldNo, value);
    }

    public void ALSetRange(NavValue fromValue, NavValue toValue)
    {
        _owner?.SetRange(_fieldNo, fromValue, toValue);
    }

    public void ALSetRange()
    {
        _owner?.ClearRange(_fieldNo);
    }

    /// <summary>ALSetFilter — delegate to owning RecordRef's handle.</summary>
    public void ALSetFilter(string filterExpression, params NavValue[] args)
    {
        _owner?.SetFilter(_fieldNo, filterExpression, args);
    }

    /// <summary>ALTestField — verify field has a non-default value.</summary>
    public void ALTestField()
    {
        _owner?.TestField(_fieldNo);
    }

    /// <summary>ALTestField — verify field equals expected value.</summary>
    public void ALTestField(NavValue expectedValue)
    {
        _owner?.TestField(_fieldNo, expectedValue);
    }

    /// <summary>ALCalcField — no-op in standalone mode (FlowFields not supported).</summary>
    public void ALCalcField() { }

    /// <summary>ALFieldError — throws a field-level error.</summary>
    public void ALFieldError(string message)
    {
        throw new Exception($"Field {_fieldNo}: {message}");
    }

    public void ALFieldError(string message, string secondaryMessage)
    {
        throw new Exception($"Field {_fieldNo}: {message} {secondaryMessage}");
    }

    /// <summary>ALByValue — returns value copy (no-op in mock).</summary>
    public MockFieldRef ALByValue() => this;

    /// <summary>Static Default factory — mirrors NavFieldRef.Default(ITreeObject).</summary>
    public static MockFieldRef Default() => new MockFieldRef();

    // -- Internal wiring --

    internal void Bind(MockRecordRef owner, int fieldNo)
    {
        _owner = owner;
        _fieldNo = fieldNo;
    }
}
