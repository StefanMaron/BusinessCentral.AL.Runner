namespace AlRunner.Runtime;

/// <summary>
/// Stub for <c>NavXmlPortHandle</c> / AL's <c>XmlPort "X"</c> variable.
///
/// BC emits <c>new NavXmlPortHandle(this, xmlPortId)</c> in scope-class
/// field initialisers. The rewriter rewrites the type to
/// <c>MockXmlPortHandle</c> and strips the ITreeObject <c>this</c> arg
/// so only the XmlPort ID remains.
///
/// All instance methods are no-ops — XmlPort I/O and schema iteration
/// require the BC service tier, which is out of scope for al-runner.
/// Inject via an AL interface to make XmlPort-dependent code unit-testable.
/// </summary>
public class MockXmlPortHandle
{
    public int XmlPortId { get; }

    public MockXmlPortHandle() { }

    public MockXmlPortHandle(int xmlPortId) { XmlPortId = xmlPortId; }

    // ------------------------------------------------------------------
    // Stream properties — BC sets via xP.Target.Source / xP.Target.Destination
    // (after .Target stripping the property assignments land here directly).
    // ------------------------------------------------------------------

    public MockInStream? Source { get; set; }
    public MockOutStream? Destination { get; set; }

    // ------------------------------------------------------------------
    // Delimiter / separator properties — AL getter/setter methods lower to
    // property assignments in BC's generated C#.
    // ------------------------------------------------------------------

    public string FieldDelimiter { get; set; } = "";
    public string FieldSeparator { get; set; } = "";
    public string Filename { get; set; } = "";
    public string RecordSeparator { get; set; } = "";
    public string TableSeparator { get; set; } = "";
    public object? TextEncoding { get; set; }

    // ------------------------------------------------------------------
    // Instance I/O methods — no-ops (require service tier in real BC)
    // ------------------------------------------------------------------

    public void Import(object? errorLevel = null) { }

    public void Export(object? errorLevel = null) { }

    public void Run(object? errorLevel = null) { }

    // ------------------------------------------------------------------
    // Configuration methods
    // ------------------------------------------------------------------

    public void SetTableView(MockRecordHandle rec) { }

    // ------------------------------------------------------------------
    // Control-flow methods (called from XmlPort triggers in real BC)
    // ------------------------------------------------------------------

    public void Break() { }
    public void BreakUnbound() { }
    public void Quit() { }
    public void Skip() { }

    public string CurrentPath() => "";

    // ------------------------------------------------------------------
    // Invocation dispatch for helper procedures defined in the XmlPort object
    // ------------------------------------------------------------------

    public object? Invoke(int memberId, object[] args) => null;

    // ------------------------------------------------------------------
    // Static form: XmlPort.Import(XmlPort::"X", InStr [, Rec])
    // BC emits: NavXmlPort.Import(DataError, xmlPortId, inStr [, rec])
    // Rewriter redirects these to the statics below.
    // ------------------------------------------------------------------

    public static void StaticImport(object? errorLevel, int xmlPortId, object? stream, object? rec = null) { }

    public static void StaticExport(object? errorLevel, int xmlPortId, object? stream, object? rec = null) { }

    /// <summary>
    /// Static <c>Xmlport.Run(portId [, showPage [, showXml]])</c> — no-op standalone.
    /// `params object?[]` accepts any trailing arg shape BC emits.
    /// </summary>
    public static void StaticRun(int xmlPortId, params object?[] args) { }
}
