namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using System.Reflection;

/// <summary>
/// Lightweight replacement for NavVariant which requires ITreeObject.
/// NavVariant is the AL Variant type — a box that can hold any value.
/// In standalone mode, we store the boxed value directly.
/// </summary>
public class MockVariant
{
    private object? _value;

    public MockVariant()
    {
    }

    public MockVariant(object? value)
    {
        _value = value;
    }

    /// <summary>
    /// NavVariant.Default(ITreeObject) — creates a default (empty) variant.
    /// In standalone mode, ignores the ITreeObject parameter.
    /// </summary>
    public static MockVariant Default(object? parent = null)
    {
        return new MockVariant();
    }

    /// <summary>
    /// NavVariant.ALAssign — assigns a value to this variant.
    /// </summary>
    public void ALAssign(object? value)
    {
        if (value is MockVariant mv)
            _value = mv._value;
        else
            _value = value;
    }

    /// <summary>
    /// Implicit conversion to allow MockVariant in NavValue contexts.
    /// Returns a NavValue representation of the stored value, converting
    /// primitive CLR types (string, int, bool, long, decimal) to their
    /// NavValue equivalents so that Variant values work correctly in
    /// filter/range operations (e.g. FieldRef.SetRange(v)).
    /// </summary>
    public static implicit operator NavValue?(MockVariant? v)
    {
        switch (v?._value)
        {
            case null: return null;
            case NavValue nv: return nv;
            case string s: return new NavText(s);
            case int i: return NavInteger.Create(i);
            case bool b: return NavBoolean.Create(b);
            case long l: return NavBigInteger.Create(l);
            default: return new NavText(v._value?.ToString() ?? "");
        }
    }

    /// <summary>
    /// Implicit conversion from NavValue.
    /// </summary>
    public static implicit operator MockVariant(NavValue v)
    {
        return new MockVariant(v);
    }

    // Type-check properties (AL's Variant.IsText, Variant.IsCode, etc.)
    public bool ALIsText => _value is NavText or string;
    public bool ALIsCode => _value is NavCode;
    public bool ALIsInteger => _value is NavInteger or int;
    public bool ALIsDecimal => _value is NavDecimal or Decimal18 or decimal;
    public bool ALIsBoolean => _value is NavBoolean or bool;
    public bool ALIsDate => _value is NavDate;
    public bool ALIsTime => _value is NavTime;
    public bool ALIsDateTime => _value is NavDateTime;
    public bool ALIsGuid => _value is NavGuid or Guid;
    public bool ALIsOption => _value is NavOption;
    public bool ALIsRecord => _value is MockRecordHandle;
    public bool ALIsRecordRef => _value is MockRecordRef;
    public bool ALIsRecordId => false;
    public bool ALIsBigInteger => _value is NavBigInteger or long;
    public bool ALIsDuration => false;
    public bool ALIsChar => _value is NavChar or char;
    public bool ALIsByte => _value is byte;
    public bool ALIsDateFormula => _value is NavDateFormula;
    public bool ALIsFieldRef => false;

    // JSON Is* — check BC runtime subtypes of NavJsonToken
    public bool ALIsJsonToken => _value is NavJsonToken;
    public bool ALIsJsonObject => _value is NavJsonToken && _value.GetType().Name == "NavJsonObject";
    public bool ALIsJsonArray => _value is NavJsonToken && _value.GetType().Name == "NavJsonArray";
    public bool ALIsJsonValue => _value is NavJsonToken && _value.GetType().Name == "NavJsonValue";

    // Stream Is* — check mock types used in standalone mode
    public bool ALIsInStream => _value is MockInStream;
    public bool ALIsOutStream => _value is MockOutStream;

    // Notification
    public bool ALIsNotification => _value is MockNotification;

    // TextBuilder (rewriter maps NavTextBuilder → MockTextBuilder)
    public bool ALIsTextBuilder => _value is MockTextBuilder;

    // List of [T] — NavList<T> is generic; check the open generic definition
    public bool ALIsList => _value != null &&
        _value.GetType().IsGenericType &&
        _value.GetType().GetGenericTypeDefinition().FullName?.StartsWith("Microsoft.Dynamics.Nav.Runtime.NavList", StringComparison.Ordinal) == true;

    // IsDictionary — NavDictionary<K,V> is generic; check open generic definition
    public bool ALIsDictionary => _value != null &&
        _value.GetType().IsGenericType &&
        _value.GetType().GetGenericTypeDefinition().FullName?.StartsWith(
            "Microsoft.Dynamics.Nav.Runtime.NavDictionary", StringComparison.Ordinal) == true;

    // XML Is* — check BC runtime types (NavXml* work standalone; XmlNameTable is mocked)
    public bool ALIsXmlAttribute => _value is NavXmlAttribute;
    public bool ALIsXmlAttributeCollection => _value is NavXmlAttributeCollection;
    public bool ALIsXmlCData => _value is NavXmlCData;
    public bool ALIsXmlComment => _value is NavXmlComment;
    public bool ALIsXmlDeclaration => _value is NavXmlDeclaration;
    public bool ALIsXmlDocument => _value is NavXmlDocument;
    public bool ALIsXmlDocumentType => _value is NavXmlDocumentType;
    public bool ALIsXmlElement => _value is NavXmlElement;
    public bool ALIsXmlNamespaceManager => _value is NavXmlNamespaceManager;
    public bool ALIsXmlNameTable => _value is MockXmlNameTable;
    public bool ALIsXmlNode => _value is NavXmlNode;
    public bool ALIsXmlNodeList => _value is NavXmlNodeList;
    public bool ALIsXmlProcessingInstruction => _value is NavXmlProcessingInstruction;
    public bool ALIsXmlReadOptions => _value is NavXmlReadOptions;
    public bool ALIsXmlText => _value is NavXmlText;
    public bool ALIsXmlWriteOptions => _value is NavXmlWriteOptions;

    // Misc stubs — types not representable in standalone mode
    public bool ALIsAction => false;
    public bool ALIsAutomation => false;
    public bool ALIsBinary => false;
    public bool ALIsClientType => false;
    public bool ALIsCodeunit => _value is MockCodeunitHandle;
    public bool ALIsDataClassification => false;
    public bool ALIsDataClassificationType => false;
    public bool ALIsDefaultLayout => false;
    public bool ALIsDotNet => false;
    public bool ALIsExecutionMode => false;
    public bool ALIsFile => false;
    public bool ALIsFilterPageBuilder => false;
    public bool ALIsObjectType => false;
    public bool ALIsPromptMode => false;
    public bool ALIsReportFormat => false;
    public bool ALIsSecurityFiltering => false;
    public bool ALIsTableConnectionType => false;
    public bool ALIsTestPermissions => false;
    public bool ALIsTextConstant => false;
    public bool ALIsTextEncoding => false;
    public bool ALIsTransactionType => false;
    public bool ALIsWideChar => false;

    /// <summary>
    /// Clear(VariantVar) — resets the variant to its default (empty) state.
    /// AL's Clear() built-in on a Variant sets it back to no value.
    /// </summary>
    public void Clear()
    {
        _value = null;
    }

    /// <summary>Get the underlying value.</summary>
    public object? Value => _value;

    /// <summary>
    /// Explicit cast to MockRecordHandle.
    /// Supports the AL pattern: MyRec := MyVariant;
    /// The BC compiler emits ALCompiler.ObjectToExactINavRecordHandle(variant),
    /// which the rewriter transforms to (MockRecordHandle)(variant).
    /// </summary>
    public static explicit operator MockRecordHandle(MockVariant v)
    {
        if (v._value is MockRecordHandle rec) return rec;
        throw new InvalidCastException(
            $"Cannot cast Variant (holding {v._value?.GetType().Name ?? "null"}) to Record. " +
            "The Variant must contain a Record value.");
    }

    /// <summary>
    /// Explicit cast to MockRecordRef.
    /// Supports the AL pattern: MyRecRef := MyVariant;
    /// The BC compiler emits ALCompiler.NavIndirectValueToNavValue&lt;NavRecordRef&gt;(variant),
    /// which the rewriter transforms to (MockRecordRef)(variant).
    /// </summary>
    public static explicit operator MockRecordRef(MockVariant v)
    {
        if (v._value is MockRecordRef rr) return rr;
        throw new InvalidCastException(
            $"Cannot cast Variant (holding {v._value?.GetType().Name ?? "null"}) to RecordRef. " +
            "The Variant must contain a RecordRef value.");
    }

    /// <summary>
    /// Stub ITreeObject for NavVariant.Factory constructor.
    /// NavVariant.Factory(ITreeObject) requires non-null parent.
    /// We implement ITreeObject minimally to satisfy the null check.
    /// </summary>
    public static Microsoft.Dynamics.Nav.Runtime.ITreeObject StubTreeObject { get; } = new StubTree();

    private class StubTree : Microsoft.Dynamics.Nav.Runtime.ITreeObject
    {
        public Microsoft.Dynamics.Nav.Runtime.TreeHandler Tree => null!;
        public Microsoft.Dynamics.Nav.Runtime.TreeObjectType Type => default;
        public bool SingleThreaded => false;
    }

    public override string ToString()
    {
        return _value?.ToString() ?? "";
    }
}
