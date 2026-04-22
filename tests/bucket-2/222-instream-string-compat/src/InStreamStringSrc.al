/// Helper codeunit: XmlDocument.ReadFrom(InStream, ...) — issue #1081.
///
/// BC emits NavXmlDocument.ALReadFrom(DataError, NavInStream, ByRef<NavXmlDocument>)
/// for the InStream overload. After NavInStream→MockInStream rewrite the InStream form
/// fails with CS1503 because NavXmlDocument only has a string overload in BC's DLL.
/// The fix: the rewriter redirects NavXmlDocument.ALReadFrom calls to
/// AlCompat.XmlDocumentReadFrom which has overloads for both NavText and MockInStream.
///
/// ReadXmlFromStream() and ReadXmlFromStreamWithOptions() are compilation probes;
/// they are never called by tests (actual InStream population requires infrastructure).
///
/// GetVersion() is a pure-logic sentinel; tests call it to confirm the codeunit compiled,
/// which proves the CS1503 error is gone.
codeunit 62220 "InStream String Src"
{
    /// Proof-of-compilation: XmlDocument.ReadFrom(InStream, var Document).
    /// BC emits NavXmlDocument.ALReadFrom(DataError, NavInStream, ByRef<NavXmlDocument>).
    /// After NavInStream→MockInStream rename this caused CS1503 before the fix.
    procedure ReadXmlFromStream(var InStr: InStream): Boolean
    var
        doc: XmlDocument;
    begin
        exit(XmlDocument.ReadFrom(InStr, doc));
    end;

    /// Proof-of-compilation: XmlDocument.ReadFrom(InStream, Options, var Document).
    /// BC emits NavXmlDocument.ALReadFrom(DataError, NavInStream, XmlReadOptions, ByRef<NavXmlDocument>).
    procedure ReadXmlFromStreamWithOptions(var InStr: InStream): Boolean
    var
        doc: XmlDocument;
        options: XmlReadOptions;
    begin
        exit(XmlDocument.ReadFrom(InStr, options, doc));
    end;

    /// Pure-logic sentinel: no InStream variables, confirms the codeunit compiled.
    procedure GetVersion(): Integer
    begin
        exit(1081);
    end;
}
