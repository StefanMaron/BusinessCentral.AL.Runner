/// Regression fixture for issue #1501.
///
/// Simulates the multi-overload pattern that triggers the NavOption/NavCode cast error
/// in auto-stubbed codeunits: a codeunit with two CreateHeader overloads that share
/// the same parameter count but differ in the second parameter type
/// (Enum "Doc Type" vs Code[20]).
///
/// The fix in Pipeline.cs / StubGenerator.cs changes the dedup strategy from
/// first-seen-wins to type-merge: when the same (name, arity) pair appears with
/// different types, the differing positions are widened to Variant so that both
/// NavOption (Enum callers) and NavCode (Code callers) pass without a cast error.

enum 1315001 "Multi OL Doc Type"
{
    Extensible = false;

    value(0; " ") { Caption = ' '; }
    value(1; Order) { Caption = 'Order'; }
    value(2; Quote) { Caption = 'Quote'; }
    value(3; Invoice) { Caption = 'Invoice'; }
}

/// Codeunit that deliberately overloads CreateHeader with (Enum, Code) and (Code, Code).
/// In production use this would come from an auto-stubbed library package.
codeunit 1315002 "Multi OL Sales Lib"
{
    /// Enum-typed overload — call site emits NavOption for the second arg.
    procedure CreateHeader(var DocHeader: Record "Multi OL Doc Header"; DocType: Enum "Multi OL Doc Type"; CustomerNo: Code[20])
    begin
        DocHeader."Doc Type Ordinal" := DocType.AsInteger();
        DocHeader."Customer No" := CustomerNo;
    end;

    /// Code-typed overload — same arity as the Enum overload.
    procedure CreateHeader(var DocHeader: Record "Multi OL Doc Header"; TemplateCode: Code[20]; SeriesCode: Code[20])
    begin
        DocHeader."Customer No" := TemplateCode + SeriesCode;
    end;

    /// Returns the stored ordinal so tests can assert a non-default value.
    procedure GetDocTypeOrdinal(var DocHeader: Record "Multi OL Doc Header"): Integer
    begin
        exit(DocHeader."Doc Type Ordinal");
    end;
}

/// Minimal table used as the var-record parameter in CreateHeader.
table 1315003 "Multi OL Doc Header"
{
    DataClassification = SystemMetadata;
    fields
    {
        field(1; PK; Integer) { }
        field(2; "Doc Type Ordinal"; Integer) { }
        field(3; "Customer No"; Code[20]) { }
    }
    keys
    {
        key(PK; PK) { Clustered = true; }
    }
}
