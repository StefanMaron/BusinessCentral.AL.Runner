namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Metadata;

/// <summary>
/// Mock for NavFieldRef — wraps a field slot on a MockRecordHandle (accessed
/// through MockRecordRef). Provides ALValue get/set, ALNumber, ALName, etc.
/// </summary>
public class MockFieldRef
{
    private MockRecordRef? _owner;
    private int _fieldNo;
    private NavValue? _calcSumResult;

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
            if (_calcSumResult != null)
            {
                var result = _calcSumResult;
                _calcSumResult = null;
                return result;
            }
            if (_owner == null)
                return NavText.Default(0);
            return _owner.GetFieldValue(_fieldNo);
        }
        set
        {
            _calcSumResult = null;
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

    /// <summary>ALClass — field class (Normal/FlowField/FlowFilter). Stub: always Normal.</summary>
    public FieldClass ALClass => FieldClass.Normal;

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

    /// <summary>ALValidateSafe — no-arg overload (re-validate current value).</summary>
    public void ALValidateSafe() => ALValidate();

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

    /// <summary>
    /// ALSetRange(object) — overload for when BC emits ALSetRange(NavComplexValue)
    /// and the rewriter replaces NavComplexValue with object. Extracts the NavValue
    /// from known mock types (MockVariant, MockRecordRef) or casts directly.
    /// Note: The MockVariant-specific overload was removed because MockVariant has
    /// implicit conversions to/from NavValue, which caused CS0121 ambiguity when C#
    /// could not choose between ALSetRange(NavValue) and ALSetRange(MockVariant).
    /// MockVariant's implicit operator to NavValue? now returns a proper NavValue for
    /// primitive CLR types (string→NavText, int→NavInteger, etc.) so that Variant
    /// values passed to ALSetRange(NavValue) work correctly without a separate overload.
    /// </summary>
    public void ALSetRange(object value)
    {
        if (value is NavValue nv)
            ALSetRange(nv);
        else if (value is MockVariant mv)
        {
            NavValue? mvNv = mv;  // use improved implicit operator
            if (mvNv != null)
                ALSetRange(mvNv);
            else
                _owner?.SetRange(_fieldNo, new NavText(mv.Value?.ToString() ?? ""));
        }
        else
            _owner?.SetRange(_fieldNo, new NavText(value?.ToString() ?? ""));
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

    /// <summary>ALCalcField with DataError — no-op in standalone mode.</summary>
    public void ALCalcField(DataError errorLevel) { }

    // -- Enum introspection --

    /// <summary>
    /// Helper to look up enum members for this field via TableFieldRegistry + EnumRegistry.
    /// Returns null if the field is not an enum type.
    /// </summary>
    private IReadOnlyList<(int Ordinal, string Name)>? GetEnumMembers()
    {
        if (_owner == null) return null;
        var enumName = TableFieldRegistry.GetEnumName(_owner.TableId, _fieldNo);
        if (enumName == null) return null;
        var members = EnumRegistry.GetMembersByName(enumName);
        return members.Count > 0 ? members : null;
    }

    /// <summary>ALIsEnum — whether this field is an enum type.</summary>
    public bool ALIsEnum => GetEnumMembers() != null;

    /// <summary>ALOptionValueCount — number of enum values for this field.</summary>
    public int ALOptionValueCount() => GetEnumMembers()?.Count ?? 0;

    /// <summary>ALGetOptionValueName — returns the enum value name at a 1-based index.</summary>
    public string ALGetOptionValueName(int index)
    {
        var members = GetEnumMembers();
        if (members == null || index < 1 || index > members.Count)
            throw new Exception($"Index {index} is out of range. The enum has {members?.Count ?? 0} values.");
        return members![index - 1].Name;
    }

    /// <summary>ALGetOptionValueCaption — returns the enum value caption at a 1-based index (same as name; no caption infrastructure).</summary>
    public string ALGetOptionValueCaption(int index)
    {
        var members = GetEnumMembers();
        if (members == null || index < 1 || index > members.Count)
            throw new Exception($"Index {index} is out of range. The enum has {members?.Count ?? 0} values.");
        return members![index - 1].Name;
    }

    /// <summary>ALGetOptionValueOrdinal — returns the enum ordinal at a 1-based index.</summary>
    public int ALGetOptionValueOrdinal(int index)
    {
        var members = GetEnumMembers();
        if (members == null || index < 1 || index > members.Count)
            throw new Exception($"Index {index} is out of range. The enum has {members?.Count ?? 0} values.");
        return members![index - 1].Ordinal;
    }

    // -- CalcSum --

    /// <summary>
    /// ALCalcSum — sums this field's values across all filtered records in the
    /// underlying table. The result is stored and returned via the next ALValue read.
    /// Always stores as NavDecimal (matching BC behavior where sums are always Decimal).
    /// </summary>
    public void ALCalcSum(DataError errorLevel = DataError.ThrowError)
    {
        if (_owner?.Handle == null)
        {
            _calcSumResult = NavDecimal.Default;
            return;
        }
        decimal sum = _owner.Handle.CalcSumField(_fieldNo);
        _calcSumResult = NavDecimal.Create(new Decimal18(sum));
    }

    /// <summary>ALFieldError — throws a field-level error.</summary>
    public void ALFieldError(string message)
    {
        throw new Exception($"Field {_fieldNo}: {message}");
    }

    public void ALFieldError(string message, string secondaryMessage)
    {
        throw new Exception($"Field {_fieldNo}: {message} {secondaryMessage}");
    }

    /// <summary>Clear — resets the field value to default.</summary>
    public void Clear()
    {
        if (_owner != null)
            _owner.SetFieldValue(_fieldNo, NavText.Default(0));
    }

    /// <summary>ALByValue — returns value copy (no-op in mock).</summary>
    public MockFieldRef ALByValue() => this;

    /// <summary>
    /// ALSetTable — some BC-generated code (notably page API extensions) emits
    /// <c>fieldRef.ALSetTable(record, shareTable)</c>. NavFieldRef in the SDK does
    /// not have this method, but the emitted code references it on the MockFieldRef
    /// after the rewriter renames types. No-op stub for compile compatibility.
    /// </summary>
    public void ALSetTable(object record, bool shareTable = false) { }

    /// <summary>Static Default factory — mirrors NavFieldRef.Default(ITreeObject).</summary>
    public static MockFieldRef Default() => new MockFieldRef();

    // -- Internal wiring --

    internal void Bind(MockRecordRef owner, int fieldNo)
    {
        _owner = owner;
        _fieldNo = fieldNo;
    }
}
