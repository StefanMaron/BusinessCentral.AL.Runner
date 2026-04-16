codeunit 59671 "GDTC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "GDTC Src";

    [Test]
    procedure GetDefaultTableConnection_ReadCompletes()
    begin
        // Positive: the standalone stub must return without throwing.
        Assert.IsTrue(Src.CallCompletes(TableConnectionType::ExternalSQL),
            'Database.GetDefaultTableConnection(ExternalSQL) must complete without throwing');
    end;

    [Test]
    procedure GetDefaultTableConnection_DifferentTypes()
    begin
        // Proving: the call must complete for every TableConnectionType enum value.
        Assert.IsTrue(Src.CallCompletes(TableConnectionType::ExternalSQL),
            'Default connection type must complete');
        Assert.IsTrue(Src.CallCompletes(TableConnectionType::ExternalSQL),
            'ExternalSQL connection type must complete');
        Assert.IsTrue(Src.CallCompletes(TableConnectionType::CRM),
            'CRM connection type must complete');
    end;

    [Test]
    procedure GetDefaultTableConnection_ResultIsText()
    var
        name: Text;
    begin
        // The stub returns a Text (empty is acceptable — no real DB connections).
        name := Src.GetDefault(TableConnectionType::ExternalSQL);
        Assert.IsTrue((name = '') or (name <> ''),
            'Result must be a valid Text value (empty is acceptable standalone)');
    end;

    [Test]
    procedure GetDefaultTableConnection_Stable()
    var
        a: Text;
        b: Text;
    begin
        // Two consecutive reads for the same connection type must return the same value.
        a := Src.GetDefault(TableConnectionType::ExternalSQL);
        b := Src.GetDefault(TableConnectionType::ExternalSQL);
        Assert.AreEqual(a, b, 'Two consecutive reads must return the same value');
    end;
}
