/// Source codeunit exercising Record.Init() with field InitValue declarations.
codeunit 100400 "InitValue Src"
{
    /// Call Rec.Init() and return the Priority field (InitValue = 5).
    procedure GetPriorityAfterInit(): Integer
    var
        Rec: Record "IV Test Table";
    begin
        Rec.Init();
        exit(Rec.Priority);
    end;

    /// Call Rec.Init() and return the IsActive field (InitValue = true).
    procedure GetIsActiveAfterInit(): Boolean
    var
        Rec: Record "IV Test Table";
    begin
        Rec.Init();
        exit(Rec.IsActive);
    end;

    /// Call Rec.Init() and return the Score field (InitValue = 9.99).
    procedure GetScoreAfterInit(): Decimal
    var
        Rec: Record "IV Test Table";
    begin
        Rec.Init();
        exit(Rec.Score);
    end;

    /// Call Rec.Init() on a field with no InitValue; must return type default.
    procedure GetNoAfterInit(): Code[20]
    var
        Rec: Record "IV Test Table";
    begin
        Rec.Init();
        exit(Rec."No.");
    end;

    /// Call Rec.Init() on a Text field with no InitValue; must return empty.
    procedure GetDescAfterInit(): Text[100]
    var
        Rec: Record "IV Test Table";
    begin
        Rec.Init();
        exit(Rec.Description);
    end;

    /// Set Priority to a non-default value, call Init(), verify InitValue is restored.
    procedure GetPriorityAfterReinit(): Integer
    var
        Rec: Record "IV Test Table";
    begin
        Rec.Priority := 99;
        Rec.Init();
        exit(Rec.Priority);
    end;

    /// PK field must be preserved across Init().
    procedure GetNoPkPreserved(PkVal: Code[20]): Code[20]
    var
        Rec: Record "IV Test Table";
    begin
        Rec."No." := PkVal;
        Rec.Init();
        exit(Rec."No.");
    end;
}
