/// <summary>
/// A report may set CurrReport.Language / CurrReport.FormatRegion from its own triggers.
///
/// This is ordinary AL, not an edge case: the Base App's document reports do it per record
/// (Standard Sales - Invoice sets the language from the customer's language code in its
/// Header data item). The assignment routes through NavReport's update-language action into
/// ReportLocalLanguageScope.UpdateLanguage, which reads the SESSION's language and format
/// state. On the skeleton session those live on stacks left null, so the assignment threw a
/// bare NullReferenceException from inside BC — a report that switches language did not
/// merely ignore the request, it died.
///
/// Both directions:
///   * a language the report sets is readable back and the run completes;
///   * a language of 0 is rejected by BC as an invalid language id, so the guard that
///     distinguishes "uninitialized" from "set" is not silently swallowing real values.
/// </summary>
codeunit 62022 "RCL Tests"
{
    Subtype = Test;

    local procedure Seed(LanguageId: Integer)
    var
        Row: Record "RCL Row";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."Entry No." := 1;
        Row.LanguageId := LanguageId;
        Row.Insert();
        Row.Init();
        Row."Entry No." := 2;
        Row.LanguageId := LanguageId;
        Row.Insert();
    end;

    [Test]
    procedure SettingCurrReportLanguage_RunsAndIsReadableBack()
    var
        Probe: Report "RCL Language Report";
        TempBlob: Codeunit "Temp Blob";
        ResultOutStream: OutStream;
    begin
        // SaveAs(Xml) is the entry point proven to execute a report's body in this runner
        // (see the report-run-execution suite) — the claim here is about the language
        // assignment, so the entry point must not be the variable.
        Seed(1031);
        Clear(Probe);
        TempBlob.CreateOutStream(ResultOutStream);
        Probe.SaveAs('', ReportFormat::Xml, ResultOutStream);

        if Probe.RowsProcessed() <> 2 then
            Error('Expected both rows to be processed, got %1 — the report stopped early.',
                Probe.RowsProcessed());
        // 1031 (de-DE), deliberately NOT the session default: a getter that ignored the
        // assignment and answered the default language would still have "passed" on 1033.
        if Probe.LanguageSeen() <> 1031 then
            Error('CurrReport.Language read back as %1, expected 1031.', Probe.LanguageSeen());
        if Probe.FormatRegionSeen() <> 'en-US' then
            Error('CurrReport.FormatRegion read back as "%1", expected "en-US".',
                Probe.FormatRegionSeen());
    end;

    [Test]
    procedure SettingCurrReportLanguageToZero_IsRejectedAsInvalid()
    var
        Probe: Report "RCL Language Report";
        TempBlob: Codeunit "Temp Blob";
        ResultOutStream: OutStream;
    begin
        // 0 is not a language id. BC rejects it outright, and it must keep doing so: the
        // runner uses "localLanguage = 0" as its marker for "never set", so a 0 that slipped
        // through would be indistinguishable from an uninitialized report.
        Seed(0);
        Clear(Probe);
        TempBlob.CreateOutStream(ResultOutStream);
        asserterror Probe.SaveAs('', ReportFormat::Xml, ResultOutStream);
        if StrPos(GetLastErrorText(), 'anguage') = 0 then
            Error('Expected an error naming the language, got: %1', GetLastErrorText());
    end;
}
