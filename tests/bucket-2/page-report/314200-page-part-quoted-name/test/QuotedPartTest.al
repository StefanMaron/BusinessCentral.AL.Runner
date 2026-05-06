// Tests for issue #1600: page part with double-quoted page reference containing spaces
// when the same page also has a fileupload block with '}' inside a string literal.
//
// Root cause: StripPatternedBlock's brace-depth counter did not skip AL single-quoted
// string literals. A '}' inside ToolTip = 'Upload } a file' caused the scanner to stop
// prematurely, corrupting the source after the fileupload block. The subsequent
// part(UnquotedName; "Page Name With Spaces") declarations then produced AL0104/AL0124
// parse errors because the quoted page reference appeared to start mid-expression.
//
// Fix: FindMatchingCloseBrace skips single-quoted strings and line comments.
codeunit 1320203 "Quoted Part Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    /// <summary>
    /// RED test: before the fix, StripPatternedBlock stopped at the '}' inside
    /// ToolTip = 'Upload } a document', leaving the actual closing '}' of the
    /// fileupload block in the source. The AL parser then reported AL0104/AL0124
    /// at the part() declarations: "The property 'Quoted' cannot be used..."
    /// GREEN test: FindMatchingCloseBrace skips the string-literal brace, so
    /// the fileupload block is stripped correctly and part() declarations parse fine.
    /// </summary>
    [Test]
    procedure Page_FileuploadBraceInString_PartWithSpacesParses_IsNoOp()
    var
        TestPage: TestPage "Quoted Part Host Page";
    begin
        // [GIVEN] A page with:
        //         - fileupload() block whose ToolTip contains '}' inside a string
        //         - part(QuotedPartFactboxA; "Quoted Part Factbox A") declarations
        //         - angle-bracket group/action identifiers
        // [WHEN]  The page is opened as a TestPage
        TestPage.OpenView();
        // [THEN]  No AL0104/AL0124 parse errors: the fileupload block was stripped
        //         with correct brace matching (string-literal brace skipped), and the
        //         quoted part page references were parsed as identifiers, not split.
        TestPage.Close();
        Assert.IsTrue(true, 'Page with fileupload ToolTip-brace + quoted part refs must open without parse errors');
    end;

    [Test]
    procedure FactboxA_QuotedNameWithSpaces_CompilesStandalone_IsNoOp()
    var
        TestPage: TestPage "Quoted Part Factbox A";
    begin
        // [GIVEN] "Quoted Part Factbox A" — referenced by the host via
        //         part(QuotedPartFactboxA; "Quoted Part Factbox A")
        // [WHEN]  Opened directly as a TestPage
        TestPage.OpenView();
        // [THEN]  The page with spaces in its name compiles and opens.
        //         This verifies the double-quoted identifier with spaces is correctly
        //         parsed as a page name, not split into separate tokens.
        TestPage.Close();
        Assert.IsTrue(true, 'Factbox page "Quoted Part Factbox A" must compile with spaces in name');
    end;

    [Test]
    procedure FactboxB_QuotedNameWithSpaces_CompilesStandalone_IsNoOp()
    var
        TestPage: TestPage "Quoted Part Factbox B";
    begin
        // [GIVEN] "Quoted Part Factbox B" — second factbox referenced by host
        // [WHEN]  Opened as a TestPage
        TestPage.OpenView();
        // [THEN]  Second page with spaces also compiles correctly.
        TestPage.Close();
        Assert.IsTrue(true, 'Factbox page "Quoted Part Factbox B" must compile with spaces in name');
    end;
}
