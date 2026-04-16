/// XmlPort used to prove that XmlPort.Run executes without error.
/// XmlPort.Run(portId) is a static no-op in standalone mode — no UI, no I/O.

xmlport 61910 "XR XmlPort"
{
    Direction = Import;
    Format = Xml;
    schema
    {
        textelement(Root) { }
    }
}

codeunit 61911 "XR Helper"
{
    /// Call XmlPort.Run(XmlPort::"XR XmlPort") — must be a no-op stub.
    procedure RunXmlPort()
    begin
        XmlPort.Run(XmlPort::"XR XmlPort");
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
