codeunit 84100 "FROM Src"
{
    procedure OptionMembersOnOptionField(): Text
    var
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        RecRef.Open(Database::"FROM Test Table");
        FRef := RecRef.Field(2);
        exit(FRef.OptionMembers);
    end;

    procedure OptionMembersOnTextField(): Text
    var
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        RecRef.Open(Database::"FROM Test Table");
        FRef := RecRef.Field(3);
        exit(FRef.OptionMembers);
    end;

    procedure IsTextSearchOptimized(): Boolean
    var
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        RecRef.Open(Database::"FROM Test Table");
        FRef := RecRef.Field(2);
        exit(FRef.IsOptimizedForTextSearch());
    end;
}
