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

    /// <summary>ALName — property returning the field name from metadata, or "FieldNN" fallback.</summary>
    public NavText ALName
    {
        get
        {
            if (_owner != null)
            {
                var name = TableFieldRegistry.GetFieldName(_owner.Number, _fieldNo);
                if (name != null) return new NavText(name);
            }
            return new NavText($"Field{_fieldNo}");
        }
    }

    /// <summary>ALCaption — property returning the field caption from metadata, or "FieldNN" fallback.</summary>
    public NavText ALCaption
    {
        get
        {
            if (_owner != null)
            {
                var caption = TableFieldRegistry.GetFieldCaption(_owner.Number, _fieldNo);
                if (caption != null) return new NavText(caption);
            }
            return new NavText($"Field{_fieldNo}");
        }
    }

    /// <summary>ALLength — property returning field length from metadata (for Text[N]/Code[N]), or 0.</summary>
    public int ALLength
    {
        get
        {
            if (_owner != null)
            {
                var length = TableFieldRegistry.GetFieldLength(_owner.Number, _fieldNo);
                if (length.HasValue) return length.Value;
            }
            return 0;
        }
    }

    /// <summary>ALType — property returning field type from metadata, or NavType.Text fallback.</summary>
    public NavType ALType
    {
        get
        {
            if (_owner != null)
            {
                var typeName = TableFieldRegistry.GetFieldTypeName(_owner.Number, _fieldNo);
                if (typeName != null)
                    return MapAlTypeToNavType(typeName);
            }
            return NavType.Text;
        }
    }

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

    private static NavType MapAlTypeToNavType(string typeName)
    {
        return typeName.ToLowerInvariant() switch
        {
            "integer" => NavType.Integer,
            "decimal" => NavType.Decimal,
            "text" => NavType.Text,
            "code" => NavType.Code,
            "boolean" => NavType.Boolean,
            "date" => NavType.Date,
            "time" => NavType.Time,
            "datetime" => NavType.DateTime,
            "option" => NavType.Option,
            "biginteger" => NavType.BigInteger,
            "guid" => NavType.GUID,
            "blob" => NavType.BLOB,
            "recordid" => NavType.RecordID,
            "duration" => NavType.Duration,
            "dateformula" => NavType.DateFormula,
            _ => NavType.Text, // fallback for enum, interface, etc.
        };
    }
}
