table 50760 "NS Item"
{
    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Name"; Text[100]) { }
        field(3; "Amount"; Integer) { }
    }
    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}

codeunit 50760 "NS Record Factory"
{
    // A method that returns a record causes the BC compiler to emit
    // a hidden NavScope parameter (γReturnValueParent) for ownership
    // tracking.  When another method in the same codeunit calls this
    // method directly, the scope object (extending AlScope, not
    // NavScope) is passed as the first argument — triggering CS1503
    // if NavScope is left unrewritten.

    procedure CreateItem(ItemCode: Code[20]; ItemName: Text[100]; ItemAmount: Integer): Record "NS Item"
    var
        Item: Record "NS Item";
    begin
        Item.Code := ItemCode;
        Item.Name := ItemName;
        Item.Amount := ItemAmount;
        Item.Insert();
        exit(Item);
    end;

    procedure CreateAndGetName(ItemCode: Code[20]): Text[100]
    var
        Item: Record "NS Item";
    begin
        // Direct same-codeunit call to a record-returning method.
        // The compiler passes 'this' (the scope) as NavScope.
        Item := CreateItem(ItemCode, 'Auto', 99);
        exit(Item.Name);
    end;
}
