// EDIT-MARKER: 1
codeunit 71845 "WER Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "WER Assert";

    [Test]
    procedure EnumValueFormatsAsItsDeclaredCaption()
    var
        K: Enum "WER Kind";
    begin
        K := K::Archived;
        Assert.AreEqual('Archived Item', Format(K), 'Format(enum) must return the declared Caption');
    end;

    [Test]
    procedure EnumExtensionValueFormatsAsItsDeclaredCaption()
    var
        K: Enum "WER Kind";
    begin
        K := K::Restored;
        Assert.AreEqual('Restored Item', Format(K), 'Format(enum) must return the enumextension value''s declared Caption');
    end;

    [Test]
    procedure ReportHonoursItsDeclaredMaxIteration()
    var
        Row: Record "WER Row";
        Counter: Record "WER Counter";
        i: Integer;
    begin
        Row.DeleteAll();
        Counter.DeleteAll();
        for i := 1 to 5 do begin
            Row.Init();
            Row."No." := i;
            Row.Insert();
        end;
        Report.Run(Report::"WER Bounded", false);
        Assert.AreEqual(1, Counter.Count(), 'MaxIteration = 1 must stop the data item after one of five rows');
    end;

    [Test]
    procedure ReportLayoutListCarriesTheDeclaredLayout()
    var
        Layouts: Record "Report Layout List";
    begin
        Layouts.SetRange("Report ID", Report::"WER Layout");
        Assert.AreEqual(1, Layouts.Count(), 'the report declares exactly one rendering layout');
        Layouts.FindFirst();
        Assert.AreEqual('WerExcel', Layouts.Name, 'the layout row must carry the declared name');
        Assert.AreEqual('WER Excel Layout', Layouts.Caption, 'the layout row must carry the declared caption');
    end;
}
