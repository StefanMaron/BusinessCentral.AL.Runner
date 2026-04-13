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
    /// Instance <c>XP.Import()</c>. Always throws — XmlPort I/O requires the service tier.
    /// Use an AL interface to inject a testable implementation.
    /// </summary>
    public void Import(object? errorLevel = null)
        => throw new NotSupportedException(
            $"XmlPort.Import is not supported in al-runner standalone mode. " +
            "Inject the XmlPort dependency via an AL interface to make this code unit-testable.");

    /// <summary>
    /// Instance <c>XP.Export()</c>. Always throws — XmlPort I/O requires the service tier.
    /// Use an AL interface to inject a testable implementation.
    /// </summary>
    public void Export(object? errorLevel = null)
        => throw new NotSupportedException(
            $"XmlPort.Export is not supported in al-runner standalone mode. " +
            "Inject the XmlPort dependency via an AL interface to make this code unit-testable.");

    /// <summary>
    /// Dispatch a plain helper procedure on the XmlPort (e.g. custom triggers).
    /// Returns null — XmlPort procedural dispatch requires the service tier.
    /// </summary>
    public object? Invoke(int memberId, object[] args) => null;

    // ------------------------------------------------------------------
    // Static form: XmlPort.Import(XmlPort::"X", InStr [, Rec])
    // BC emits: NavXmlPort.Import(DataError, xmlPortId, inStr [, rec])
    // Rewriter redirects these to the statics below.
    // ------------------------------------------------------------------

    /// <summary>
    /// Static <c>XmlPort.Import(portId, InStr [, Rec])</c> stub.
    /// Throws — XmlPort I/O requires the service tier.
    /// </summary>
    public static void StaticImport(object? errorLevel, int xmlPortId, object? stream, object? rec = null)
        => throw new NotSupportedException(
            $"XmlPort.Import (XmlPort {xmlPortId}) is not supported in al-runner standalone mode. " +
            "Inject the XmlPort dependency via an AL interface to make this code unit-testable.");

    /// <summary>
    /// Static <c>XmlPort.Export(portId, OutStr [, Rec])</c> stub.
    /// Throws — XmlPort I/O requires the service tier.
    /// </summary>
    public static void StaticExport(object? errorLevel, int xmlPortId, object? stream, object? rec = null)
        => throw new NotSupportedException(
            $"XmlPort.Export (XmlPort {xmlPortId}) is not supported in al-runner standalone mode. " +
            "Inject the XmlPort dependency via an AL interface to make this code unit-testable.");
}
