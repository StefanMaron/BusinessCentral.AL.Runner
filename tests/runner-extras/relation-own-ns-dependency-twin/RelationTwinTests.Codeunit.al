codeunit 66606 "Rtw Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    // Issue #5224. The runner described a table nobody modifies to the platform from BC's
    // compiled metadata, and one a modify(...) extension touches from its AL source by name.
    // The second route took the imported bundle table over Base Application's table of the
    // writer's own namespace, so the same AL answered two ways depending on whether an
    // extension touched the table. What is asserted is that the two routes agree and that the
    // answer is not the imported bundle table; which table BC itself binds is corpus codeunit
    // 69217 "Test Relation Own NS Dep".
    var
        Assert: Codeunit "Rtw Assert";

    [Test]
    procedure Relation_PlainAndModifiedTwin_NameTheSameTable()
    var
        PlainRef: RecordRef;
        ModRef: RecordRef;
    begin
        PlainRef.Open(66602);
        ModRef.Open(66603);

        Assert.AreNotEqual(66600, PlainRef.Field(2).Relation(), 'plain table: not the imported bundle table.');
        Assert.AreEqual(PlainRef.Field(2).Relation(), ModRef.Field(2).Relation(),
            'a modify(...) extension must not change which table the relation name means.');
    end;

    [Test]
    procedure FlowField_PlainAndModifiedTwin_CountTheSameTable()
    var
        BaseAgent: Record Microsoft.Foundation.Shipping."Shipping Agent";
        ImportedAgent: Record ALR.RelationTwin.Imported."Shipping Agent";
        PlainRef: RecordRef;
        ModRef: RecordRef;
    begin
        // One Base Application row and two imported-table rows for the code, so the two
        // candidate tables count differently.
        BaseAgent.Init();
        BaseAgent.Code := 'RTWAG';
        BaseAgent.Insert(false);
        ImportedAgent.Init();
        ImportedAgent.Code := 'RTWAG';
        ImportedAgent.Seq := 1;
        ImportedAgent.Insert(false);
        ImportedAgent.Seq := 2;
        ImportedAgent.Insert(false);

        PlainRef.Open(66602);
        PlainRef.Init();
        PlainRef.Field(1).Value := 'RTWAG';
        PlainRef.Field(3).CalcField();
        ModRef.Open(66603);
        ModRef.Init();
        ModRef.Field(1).Value := 'RTWAG';
        ModRef.Field(3).CalcField();

        Assert.AreEqual(1, PlainRef.Field(3).Value, 'plain table counts Base Application''s one row.');
        Assert.AreEqual(PlainRef.Field(3).Value, ModRef.Field(3).Value,
            'a modify(...) extension must not change which table the CalcFormula counts.');
    end;
}
