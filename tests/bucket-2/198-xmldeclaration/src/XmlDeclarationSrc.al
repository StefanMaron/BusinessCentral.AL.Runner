/// Exercises XmlDeclaration — Create, Version, Encoding, Standalone.
codeunit 60250 "XDL Src"
{
    procedure CreateAndGetVersion(): Text
    var
        decl: XmlDeclaration;
    begin
        decl := XmlDeclaration.Create('1.0', 'utf-8', 'yes');
        exit(decl.Version);
    end;

    procedure CreateAndGetEncoding(): Text
    var
        decl: XmlDeclaration;
    begin
        decl := XmlDeclaration.Create('1.0', 'utf-8', 'yes');
        exit(decl.Encoding);
    end;

    procedure CreateAndGetStandalone(): Text
    var
        decl: XmlDeclaration;
    begin
        decl := XmlDeclaration.Create('1.0', 'utf-8', 'yes');
        exit(decl.Standalone);
    end;

    procedure SetVersion(v: Text): Text
    var
        decl: XmlDeclaration;
    begin
        decl := XmlDeclaration.Create('1.0', 'utf-8', '');
        decl.Version := v;
        exit(decl.Version);
    end;

    procedure SetEncoding(enc: Text): Text
    var
        decl: XmlDeclaration;
    begin
        decl := XmlDeclaration.Create('1.0', 'utf-8', '');
        decl.Encoding := enc;
        exit(decl.Encoding);
    end;

    procedure SetStandalone(sa: Text): Text
    var
        decl: XmlDeclaration;
    begin
        decl := XmlDeclaration.Create('1.0', '', '');
        decl.Standalone := sa;
        exit(decl.Standalone);
    end;
}
