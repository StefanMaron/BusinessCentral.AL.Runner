/// Source codeunit exercising bool & FormResult operator — issue #985.
/// Root cause: MockFilterPageBuilder.ALRunModal() returned FormResult (wrong type),
/// causing CS0019 when used in compound boolean expressions.
/// FilterPageBuilder.RunModal() returns Boolean in AL; BC emits ALRunModal → bool.
table 132000 "FRO FPB Table"
{
    fields { field(1; Id; Integer) { } }
    keys { key(PK; Id) { Clustered = true; } }
}

codeunit 132001 "FRO Source"
{
    // AL compound condition: SomeBoolean and (Action = Action::OK)
    // BC emits: boolVar & (formResult == FormResult.OK)
    // The & operator between bool and FormResult must be defined.

    procedure BoolAndFormResult_True(): Boolean
    var
        Cond: Boolean;
        Act: Action;
    begin
        Cond := true;
        Act := Action::OK;
        // In AL: (Cond) and (Act = Action::OK)
        // BC emits: Cond & (Act == FormResult.OK) — must not CS0019
        exit(Cond and (Act = Action::OK));
    end;

    procedure BoolAndFormResult_False(): Boolean
    var
        Cond: Boolean;
        Act: Action;
    begin
        Cond := true;
        Act := Action::Cancel;
        exit(Cond and (Act = Action::OK));
    end;

    procedure BoolOrFormResult_True(): Boolean
    var
        Cond: Boolean;
        Act: Action;
    begin
        Cond := false;
        Act := Action::OK;
        exit(Cond or (Act = Action::OK));
    end;

    procedure FormResultAsAction_Compiles(): Action
    var
        Act: Action;
    begin
        Act := Action::LookupOK;
        exit(Act);
    end;

    // FilterPageBuilder.RunModal() returns Boolean in AL.
    // BC emits: cond & fPB.ALRunModal(DataError.TrapError)
    // MockFilterPageBuilder.ALRunModal must return bool (not FormResult)
    // to avoid CS0019 when used in compound boolean expressions.

    procedure FilterBuilderAndBool_True(Cond: Boolean): Boolean
    var
        FPB: FilterPageBuilder;
    begin
        FPB.AddTable('FRO FPB Table', DATABASE::"FRO FPB Table");
        // FilterPageBuilder.RunModal() returns Boolean — in standalone mode always true
        exit(Cond and FPB.RunModal());
    end;

    procedure FilterBuilderAndBool_CondFalse(): Boolean
    var
        FPB: FilterPageBuilder;
    begin
        FPB.AddTable('FRO FPB Table', DATABASE::"FRO FPB Table");
        // false and anything = false
        exit(false and FPB.RunModal());
    end;

    procedure FilterBuilderOrBool_True(): Boolean
    var
        FPB: FilterPageBuilder;
    begin
        FPB.AddTable('FRO FPB Table', DATABASE::"FRO FPB Table");
        // false or RunModal() = RunModal() = true in standalone
        exit(false or FPB.RunModal());
    end;
}
