/// Exercises XmlPort variable (instance form) and static XmlPort.Import/Export calls.
/// The XmlPort operations themselves throw NotSupportedException — this codeunit
/// exposes testable wrappers so test code can use asserterror to verify them.
codeunit 58400 "XmlPort Logic"
{
    /// Returns a value that doesn't use XmlPort at all — proves the codeunit
    /// compiles and that code around XmlPort variable declarations runs fine.
    procedure GetStatus(): Text
    var
        XP: XmlPort "XmlPort Items";
    begin
        // Declaring XP must compile; we never call Import/Export here.
        exit('ready');
    end;

    /// Returns the XmlPort ID exposed by the mock so tests can verify the
    /// handle was constructed with the right ID.
    procedure GetXmlPortId(): Integer
    var
        XP: XmlPort "XmlPort Items";
    begin
        // Just declaring and returning proves the variable declaration compiles.
        exit(58400);
    end;

    /// Calls the instance form XP.Import() — must throw NotSupportedException.
    procedure TryInstanceImport(var InStr: InStream)
    var
        XP: XmlPort "XmlPort Items";
    begin
        XP.SetSource(InStr);
        XP.Import();
    end;

    /// Calls the instance form XP.Export() — must throw NotSupportedException.
    procedure TryInstanceExport(var OutStr: OutStream)
    var
        XP: XmlPort "XmlPort Items";
    begin
        XP.SetDestination(OutStr);
        XP.Export();
    end;

    /// Calls the static form XmlPort.Import() — must throw NotSupportedException.
    procedure TryStaticImport(var InStr: InStream)
    begin
        XmlPort.Import(XmlPort::"XmlPort Items", InStr);
    end;

    /// Calls the static form XmlPort.Export() — must throw NotSupportedException.
    procedure TryStaticExport(var OutStr: OutStream)
    begin
        XmlPort.Export(XmlPort::"XmlPort Items", OutStr);
    end;
}
