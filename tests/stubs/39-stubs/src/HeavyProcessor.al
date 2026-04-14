codeunit 53901 "Heavy Processor"
{
    /// This codeunit would use HTTP, XmlPort, etc. in production.
    /// For testing, we stub it out via --stubs.
    procedure ProcessData(var Rec: Record "Stub Table"): Boolean
    begin
        // In production: call external API, use XmlPort, etc.
        // In standalone: this will be replaced by a stub
        Rec."Value" := Rec."Value" * 3;
        exit(true);
    end;
}
