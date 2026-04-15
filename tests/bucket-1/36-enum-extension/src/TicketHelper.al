codeunit 52820 "Ticket Helper"
{
    procedure GetPendingApproval(): Enum "Ticket Status"
    begin
        exit(Enum::"Ticket Status"::"Pending Approval");
    end;

    procedure GetRejected(): Enum "Ticket Status"
    begin
        exit(Enum::"Ticket Status"::Rejected);
    end;

    procedure GetOpen(): Enum "Ticket Status"
    begin
        exit(Enum::"Ticket Status"::Open);
    end;

    procedure FormatStatus(Status: Enum "Ticket Status"): Text
    begin
        exit(Format(Status));
    end;

    procedure PendingOrdinal(): Integer
    var
        Status: Enum "Ticket Status";
    begin
        Status := Status::"Pending Approval";
        exit(Status.AsInteger());
    end;
}
