// TestPageFieldCaptionCaptureTests — issues #1776/#1777 (TestPage.Caption() returned empty,
// and TestPage field Caption() returned the field's technical name instead of its declared
// Caption).
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that our
// own field-caption capture bypasses the broken read path rather than reinventing capture
// from scratch (the #1777 handoff note's explicit ask).
//
//   RecordPatches.NCLMetaField_get_FieldCaption is JmpHooked to unconditionally answer a
//   field's NAME (BcRuntime.cs, "sync underbelly of NavRecord.ALFieldCaptionAsync" — the real
//   getter dereferences session/language-provider state the skeleton runtime never
//   populates). That hook cannot distinguish "no declared Caption" from "declared Caption we
//   chose not to read", so nothing downstream of it — including a naive TestPage field
//   Caption() fix — can answer that question either.
//
//   RecordPatches.TryGetParsedFieldCaption sidesteps the hook entirely: it reads the SAME
//   parse-time ParsedField.Caption that NclMetaTableBuilder.BuildMetaField already feeds into
//   NCLMetaField's captionML at construction (RecordPatches.NclMetaTableBuilder.cs), so it
//   answers the field's REAL declared Caption — or null when there genuinely is none.
//
// The BEHAVIORAL claim ("TestPage.<field>.Caption() returns the field's Caption, and
// TestPage.Caption() returns the page's Caption / CurrPage.Caption") is proven upstream
// against a live BC service tier — see StefanMaron/BusinessCentral.AL.Language.Tests PR for
// "Test Page Caption Tests" (codeunit 60954), per docs/rules/bc-behavior-tests-go-upstream.md.
// This test exists so a regression in OUR OWN capture pipeline fails loudly here, in
// milliseconds, without needing the BC engine loaded a second time.

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class TestPageFieldCaptionCaptureTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public TestPageFieldCaptionCaptureTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-field-caption-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void TryGetParsedFieldCaption_ReturnsDeclaredCaption_AndNullWhenAbsent()
    {
        if (!_engine.Ready) return; // no BC artifacts provisioned — skip, don't fail

        File.WriteAllText(Path.Combine(_root, "FieldCaptionCaptureRow.al"), """
            table 90210 "FieldCaptionCapture Row"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; Klass; Text[30]) { Caption = 'Severity'; }
                }
                keys
                {
                    key(PK; "No.") { Clustered = true; }
                }
            }
            """);

        RecordPatches.AddSourceDir(_root);

        // Positive: a field WITH a declared Caption returns the real caption text, not the
        // field name "Klass" — this is the exact bug #1777 reported for TestPage field
        // Caption(), and it is what NCLMetaField.FieldCaption (the hooked getter) cannot
        // answer because it always returns the field's NAME regardless of what was declared.
        Assert.Equal("Severity", RecordPatches.TryGetParsedFieldCaption(90210, 2));

        // Negative: a field with NO Caption property at all returns null — "declares none" —
        // rather than silently manufacturing a caption. The field-name fallback ($"Field {n}"
        // style logic) is the CONSUMER's job (LiveNavTestField.Caption in MockTestPage.cs),
        // asserted as a distinct, later step in the precedence chain — a fix that always
        // returned a non-null string here would make that fallback step untestable.
        Assert.Null(RecordPatches.TryGetParsedFieldCaption(90210, 1));

        // A field number that does not exist on the table at all is the same "no answer"
        // case, not a crash and not an empty string.
        Assert.Null(RecordPatches.TryGetParsedFieldCaption(90210, 999));

        // A table id nobody ever parsed is the same "no answer" case too.
        Assert.Null(RecordPatches.TryGetParsedFieldCaption(90299, 1));
    }
}
