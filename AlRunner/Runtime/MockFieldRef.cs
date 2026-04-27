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

    /// <summary>ALGetFilter — returns the active filter expression for this field.</summary>
    public string ALGetFilter => _owner?.GetFieldFilter(_fieldNo) ?? "";

    /// <summary>ALGetRangeMin — returns the range minimum value for this field.
    /// Returns the unwrapped CLR value (int, string, etc.) so Convert.ToInt32 etc. work.</summary>
    public object ALGetRangeMin => UnwrapNavValue(_owner?.GetFieldRangeMin(_fieldNo));

    /// <summary>ALGetRangeMax — returns the range maximum value for this field.
    /// Returns the unwrapped CLR value (int, string, etc.) so Convert.ToInt32 etc. work.</summary>
    public object ALGetRangeMax => UnwrapNavValue(_owner?.GetFieldRangeMax(_fieldNo));

    /// <summary>ALOptionCaption — option caption string. Stub: empty.</summary>
    public string ALOptionCaption => "";

    /// <summary>ALOptionString — alias for OptionMembers; BC may emit either property name.</summary>
    public string ALOptionString
    {
        get
        {
            var members = GetEnumMembers();
            if (members != null) return string.Join(",", members.Select(m => m.Name));
            if (_owner != null)
            {
                var inline = TableFieldRegistry.GetOptionMembers(_owner.TableId, _fieldNo);
                if (inline != null) return inline;
            }
            return "";
        }
    }

    /// <summary>ALOptionMembers — comma-separated option member names for this field; empty for non-option fields.</summary>
    public string ALOptionMembers
    {
        get
        {
            var members = GetEnumMembers();
            if (members != null) return string.Join(",", members.Select(m => m.Name));
            if (_owner != null)
            {
                var inline = TableFieldRegistry.GetOptionMembers(_owner.TableId, _fieldNo);
                if (inline != null) return inline;
            }
            return "";
        }
    }

    /// <summary>ALOptimizedForTextSearch — always false; no full-text index in standalone mode.</summary>
    public bool ALOptimizedForTextSearch => false;

    /// <summary>ALRecord — returns the owning record ref. Stub for compile compat.</summary>
    public MockRecordRef ALRecord() => _owner ?? new MockRecordRef();

    /// <summary>
    /// ALKeyIndex — called when the BC transpiler lowers
    /// <c>KeyRef := FldRef.Record().KeyIndex(n)</c> to
    /// <c>KeyRef.ALAssign(FldRef.ALKeyIndex(compilationTarget, n))</c>.
    /// Delegates to the owning RecordRef's KeyIndex implementation.
    /// </summary>
    public MockKeyRef ALKeyIndex(int index)
        => (_owner ?? new MockRecordRef()).ALKeyIndex(index);

    /// <summary>
    /// ALKeyIndex with compilation-target argument — BC emits an extra object
    /// parameter (the compilation target) before the index for chained calls.
    /// </summary>
    public MockKeyRef ALKeyIndex(object compilationTarget, int index)
        => ALKeyIndex(index);

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

    /// <summary>ALValidate — object/Variant overload; unwraps to NavValue then validates.</summary>
    public void ALValidate(object value)
        => ALValidate(AlCompat.ToNavValue(value));

    /// <summary>ALValidate — MockVariant overload; unwraps to NavValue via AlCompat then validates.</summary>
    public void ALValidate(MockVariant value)
        => ALValidate(AlCompat.ToNavValue(value));

    /// <summary>ALValidateSafe — safe variant of Validate.</summary>
    public void ALValidateSafe(NavValue value) => ALValidate(value);

    /// <summary>ALValidateSafe — MockVariant overload; unwraps to NavValue via AlCompat then validates.</summary>
    public void ALValidateSafe(MockVariant value) => ALValidate(AlCompat.ToNavValue(value));

    /// <summary>ALValidateSafe — object/Variant overload; unwraps to NavValue then validates.</summary>
    public void ALValidateSafe(object value) => ALValidate(AlCompat.ToNavValue(value));

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

    /// <summary>
    /// ALGetEnumValueCaptionFromOrdinalValue — returns the caption of the enum member
    /// whose ordinal value equals <paramref name="ordinalValue"/>. Distinct from the
    /// 1-based-index ALGetOptionValueCaption: this one takes the enum's assigned
    /// integer value (e.g. 0/5/10, not 1/2/3).
    ///
    /// Note: EnumRegistry does not track captions separately from names in standalone
    /// mode (the AL parser only captures the member name at transpile time). Consumers
    /// treating the caption as a display string should still get a reasonable value;
    /// tests asserting caption-specific formatting should compare against the name.
    /// </summary>
    public string ALGetEnumValueCaptionFromOrdinalValue(int ordinalValue)
    {
        var members = GetEnumMembers();
        if (members == null)
            return "";
        foreach (var m in members)
        {
            if (m.Ordinal == ordinalValue)
                return m.Name;
        }
        return "";
    }

    /// <summary>
    /// ALGetEnumValueNameFromOrdinalValue — returns the AL identifier of the enum
    /// member whose ordinal value equals <paramref name="ordinalValue"/>.
    /// </summary>
    public string ALGetEnumValueNameFromOrdinalValue(int ordinalValue)
    {
        var members = GetEnumMembers();
        if (members == null)
            return "";
        foreach (var m in members)
        {
            if (m.Ordinal == ordinalValue)
                return m.Name;
        }
        return "";
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

    /// <summary>
    /// ALFieldError() — 0-arg overload: BC FieldRef.FieldError() with no arguments.
    /// Produces the same error format as the Record.FieldError() default:
    /// "&lt;FieldCaption&gt; must have a value in &lt;TableCaption&gt;: &lt;PK&gt;."
    /// Delegates to MockRecordHandle.ALFieldError when an owner record is available.
    /// </summary>
    public void ALFieldError()
    {
        if (_owner?.Handle != null)
        {
            _owner.Handle.ALFieldError(_fieldNo);
            return;
        }
        // Fallback when not bound to a record (e.g. standalone FieldRef).
        var caption = (_owner != null
            ? TableFieldRegistry.GetFieldCaption(_owner.Number, _fieldNo)
              ?? TableFieldRegistry.GetFieldName(_owner.Number, _fieldNo)
            : null) ?? $"Field{_fieldNo}";
        throw new Exception($"{caption} must have a value");
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

    /// <summary>
    /// Implicit conversion from int (field number) to MockFieldRef.
    /// The BC compiler sometimes emits an int (from ALFieldNo) where a MockFieldRef
    /// parameter is expected — e.g. FilterPageBuilder.AddFieldNo in AL compiles to
    /// ALAddField(DataError, NavText, int) but MockFilterPageBuilder.ALAddField
    /// expects MockFieldRef. This operator lets the C# compiler auto-convert.
    /// </summary>
    public static implicit operator MockFieldRef(int fieldNo)
    {
        var fr = new MockFieldRef();
        fr._fieldNo = fieldNo;
        return fr;
    }

    // -- Internal helpers --

    // Cached PropertyInfo for NavDecimal.Value — resolved once at type-init,
    // matching the same pattern used in MockRecordHandle.NavValueToString.
    private static readonly System.Reflection.PropertyInfo? _navDecimalValueProp =
        typeof(NavDecimal).GetProperty("Value");

    /// <summary>
    /// Extract the underlying CLR value from a NavValue so that Convert.ToInt32 etc. work.
    /// BC compiler emits <c>Convert.ToInt32(fldRef.ALGetRangeMin)</c> which fails on NavValue
    /// because NavValue doesn't implement IConvertible.
    /// </summary>
    private static object UnwrapNavValue(NavValue? value)
    {
        if (value == null) return 0;
        switch (value)
        {
            case NavInteger ni: return (int)ni;
            case NavText nt: return (string)nt;
            case NavCode nc: return (string)nc;
            case NavBoolean nb: return (bool)nb;
            case NavBigInteger nbi: return (long)nbi;
            case NavGuid ng: return (Guid)ng;
            case NavOption nopt: return nopt.Value;
        }
        if (value is NavDecimal nd)
        {
            try
            {
                var raw = _navDecimalValueProp?.GetValue(nd);
                if (raw != null) return Convert.ToDecimal(raw);
            }
            catch { }
        }
        return 0;
    }

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
