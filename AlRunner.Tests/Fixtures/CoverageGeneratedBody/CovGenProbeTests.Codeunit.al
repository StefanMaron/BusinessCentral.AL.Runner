codeunit 50941 "Cov Gen Probe Tests RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure RaiseInside_ReportsTheIncrementedValue()
    var
        Probe: Codeunit "Cov Gen Probe RXT";
        Got: Text;
    begin
        Got := Probe.RaiseInside(5);
        if Got <> 'raised 6' then
            Error('expected raised 6, got %1', Got);
        asserterror
        begin
            Got := 'test body';
            Error(Got);
        end;
        if GetLastErrorText() <> 'test body' then
            Error('expected test body, got %1', GetLastErrorText());
    end;
}
