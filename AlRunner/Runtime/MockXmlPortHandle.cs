namespace AlRunner.Runtime;

/// <summary>
/// Stub for <c>NavXmlPortHandle</c> / AL's <c>XmlPort "X"</c> variable.
///
/// BC emits <c>new NavXmlPortHandle(this, xmlPortId)</c> in scope-class
/// field initialisers. The rewriter rewrites the type to
/// <c>MockXmlPortHandle</c> and strips the ITreeObject <c>this</c> arg
/// so only the XmlPort ID remains.
///
/// After the existing <c>.Target</c>-stripping rewrite the generated code
/// calls members directly on the handle, e.g.:
/// <code>
///   xP.Source = inStr.Value;
///   xP.Import();
/// </code>
/// This class satisfies those calls so files compile. The import/export
/// methods throw <see cref="NotSupportedException"/> at runtime — XmlPort
/// I/O requires the BC service tier. Inject via an AL interface to make
/// XmlPort-dependent code unit-testable.
/// </summary>
public class MockXmlPortHandle
{
    public int XmlPortId { get; }

    public MockXmlPortHandle() { }

    public MockXmlPortHandle(int xmlPortId) { XmlPortId = xmlPortId; }

    /// <summary>Source stream for import. BC sets this via <c>xP.Target.Source = ...</c>.</summary>
    public MockInStream? Source { get; set; }

    /// <summary>Destination stream for export. BC sets this via <c>xP.Target.Destination = ...</c>.</summary>
    public MockOutStream? Destination { get; set; }

    /// <summary>
    /// Instance <c>XP.Run([showRequestPage [, isImport]])</c>. No-op in standalone mode —
    /// XmlPort.Run opens the request dialog and executes I/O, both of which require the
    /// BC service tier. In tests, call the code that uses the XmlPort result directly.
    /// </summary>
    public void ALRun(object? errorLevel = null, object? showRequestPage = null, object? isImport = null) { }

    /// <summary>
    /// Instance <c>XP.Import()</c>. Always throws — XmlPort I/O requires the service tier.
    /// Use an AL interface to inject a testable implementation.
    /// </summary>
    public void Import(object? errorLevel = null)
        => throw new NotSupportedException(
            $"XmlPort {XmlPortId} Import requires the BC service tier and is not supported by al-runner. " +
            "Use AL interface injection to abstract XmlPort dependencies for testing.");

    /// <summary>
    /// Instance <c>XP.Export()</c>. Always throws — XmlPort I/O requires the service tier.
    /// Use an AL interface to inject a testable implementation.
    /// </summary>
    public void Export(object? errorLevel = null)
        => throw new NotSupportedException(
            $"XmlPort {XmlPortId} Export requires the BC service tier and is not supported by al-runner. " +
            "Use AL interface injection to abstract XmlPort dependencies for testing.");

    /// <summary>
    /// Dispatch a plain helper procedure on the XmlPort (e.g. custom triggers).
    /// Returns null — XmlPort procedural dispatch requires the service tier.
    /// </summary>
    public object? Invoke(int memberId, object[] args) => null;

    // ------------------------------------------------------------------
    // Static forms: XmlPort.Import/Export/Run
    // BC emits: NavXmlPort.Import(DataError, xmlPortId, inStr [, rec])
    //           NavXmlPort.Export(DataError, xmlPortId, outStr [, rec])
    //           NavXmlPort.Run(DataError) — no portId arg; port resolved via generated class
    // Rewriter redirects these to the statics below.
    // ------------------------------------------------------------------

    /// <summary>
    /// Static <c>XmlPort.Import(portId, InStr [, Rec])</c> stub.
    /// Throws — XmlPort I/O requires the service tier.
    /// </summary>
    public static void StaticImport(object? errorLevel, int xmlPortId, object? stream, object? rec = null)
        => throw new NotSupportedException(
            $"XmlPort {xmlPortId} Import requires the BC service tier and is not supported by al-runner. " +
            "Use AL interface injection to abstract XmlPort dependencies for testing.");

    /// <summary>
    /// Static <c>XmlPort.Export(portId, OutStr [, Rec])</c> stub.
    /// Throws — XmlPort I/O requires the service tier.
    /// </summary>
    public static void StaticExport(object? errorLevel, int xmlPortId, object? stream, object? rec = null)
        => throw new NotSupportedException(
            $"XmlPort {xmlPortId} Export requires the BC service tier and is not supported by al-runner. " +
            "Use AL interface injection to abstract XmlPort dependencies for testing.");

    /// <summary>
    /// Static <c>XmlPort.Run([showRequestPage [, isImport]])</c> no-op stub.
    /// BC emits <c>NavXmlPort.Run(DataError)</c> for the static AL form — no portId is
    /// passed as an argument; the port identity is resolved through the generated XmlPort
    /// derived class, not via a parameter. XmlPort.Run shows a request dialog and executes
    /// I/O, both of which require the BC service tier. In standalone mode this is a no-op.
    /// </summary>
    public static void StaticRun(object? errorLevel = null, object? showRequestPage = null, object? isImport = null) { }
}
