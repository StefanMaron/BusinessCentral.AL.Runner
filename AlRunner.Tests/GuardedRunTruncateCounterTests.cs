using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #5527: a guarded <c>Codeunit.Run</c> that truncates a table with an AutoIncrement
/// key, inserts and then fails. The scope's rollback restored the rows but not the counter, so the caller's next
/// zero-key Insert collided with a restored row (<c>Entry No.='2'</c>).
///
/// What BC does is a claim about BC and is measured upstream by corpus codeunit 67840 "Test AutoInc Guarded Run"
/// (ten cloud legs and the Windows nightly). This test is the half the corpus cannot be: every table below is used
/// for the first time in a fresh runner process, so the counter starts at 1 and each test names absolute keys, and the
/// virgin-table shape (the counter has never been taken) needs that. The BC answers it asserts are the corpus's; the
/// Error() text of each test carries the observed values, so no Library Assert dependency (no "application" in
/// app.json, .claude/rules/no-base-app-in-csharp-tests.md).
/// </summary>
public class GuardedRunTruncateCounterTests
{
    [SkippableFact]
    public async Task GuardedRun_TruncateInsertError_PutsTheCounterBack_ForTheCallersNextInsert()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-guarded-truncate-counter-5527");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b5527000-0000-4000-8000-000000005527",
          "name": "GuardedTruncateCounter5527",
          "publisher": "Repro5527",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 65270, "to": 65299 } ],
          "runtime": "16.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "GuardedTruncate.al"), GuardedTruncateSource);

        var r = await SuiteServer.RunViaServer(root);

        Assert.True(r.ExitCode == 0,
            $"Expected every guarded-run Truncate counter test to pass (exit 0); got exit {r.ExitCode}.\n{r}");
        r.AssertNoFailures();
        foreach (var test in new[]
                 {
                     "TruncateTrue_InGuardedRun_CounterIsBack",
                     "TruncateFalse_InGuardedRun_CounterIsBack",
                     "NoTruncate_InGuardedRun_TheValueIsConsumed",
                     "TruncateTrue_InGuardedRun_DeletedLastRow_CounterIsNotTheHighWaterMark",
                     "NestedInnerFails_OuterSucceeds_CounterIsBackForTheOuter",
                     "NestedInnerFails_OuterFails_OnlyTheInnerTruncateIsRestored",
                     "TruncateFalse_InGuardedRun_NeverUsedTable_CounterIsUnstarted",
                 })
            r.AssertPassed("Codeunit65299." + test);
    }

    private const string GuardedTruncateSource = """
        table 65270 "GT Probe A"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { AutoIncrement = true; }
                field(2; Payload; Text[30]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        table 65271 "GT Probe B"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { AutoIncrement = true; }
                field(2; Payload; Text[30]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        table 65272 "GT Probe C"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { AutoIncrement = true; }
                field(2; Payload; Text[30]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        table 65273 "GT Probe F"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { AutoIncrement = true; }
                field(2; Payload; Text[30]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        table 65274 "GT Probe H"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { AutoIncrement = true; }
                field(2; Payload; Text[30]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        table 65275 "GT Probe I"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { AutoIncrement = true; }
                field(2; Payload; Text[30]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        table 65276 "GT Probe V"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { AutoIncrement = true; }
                field(2; Payload; Text[30]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        codeunit 65270 "GT Inner A"
        {
            trigger OnRun()
            var
                R: Record "GT Probe A";
            begin
                R.Truncate(true);
                R.Payload := 'inner';
                R.Insert(true);
                Error('inner failed');
            end;
        }

        codeunit 65271 "GT Inner B"
        {
            trigger OnRun()
            var
                R: Record "GT Probe B";
            begin
                R.Truncate(false);
                R.Payload := 'inner';
                R.Insert(true);
                Error('inner failed');
            end;
        }

        codeunit 65272 "GT Inner C"
        {
            trigger OnRun()
            var
                R: Record "GT Probe C";
            begin

                R.Payload := 'inner';
                R.Insert(true);
                Error('inner failed');
            end;
        }

        codeunit 65273 "GT Inner F"
        {
            trigger OnRun()
            var
                R: Record "GT Probe F";
            begin
                R.Truncate(true);
                R.Payload := 'inner';
                R.Insert(true);
                Error('inner failed');
            end;
        }

        codeunit 65274 "GT Inner H"
        {
            trigger OnRun()
            var
                R: Record "GT Probe H";
            begin
                R.Truncate(true);
                R.Payload := 'inner';
                R.Insert(true);
                Error('inner failed');
            end;
        }

        codeunit 65275 "GT Inner I"
        {
            trigger OnRun()
            var
                R: Record "GT Probe I";
            begin
                R.Truncate(true);
                R.Payload := 'inner';
                R.Insert(true);
                Error('inner failed');
            end;
        }

        codeunit 65276 "GT Inner V"
        {
            trigger OnRun()
            var
                R: Record "GT Probe V";
            begin
                R.Truncate(false);
                R.Payload := 'inner';
                R.Insert(true);
                Error('inner failed');
            end;
        }

        codeunit 65280 "GT Outer H"
        {
            trigger OnRun()
            var
                R: Record "GT Probe H";
                Ok: Boolean;
            begin
                Ok := Codeunit.Run(Codeunit::"GT Inner H");
                R.Payload := 'outer';
                R.Insert(true);
            end;
        }

        codeunit 65281 "GT Outer I"
        {
            trigger OnRun()
            var
                R: Record "GT Probe I";
                Ok: Boolean;
            begin
                Ok := Codeunit.Run(Codeunit::"GT Inner I");
                R.Payload := 'outer';
                R.Insert(true);
                Error('outer failed');
            end;
        }

        codeunit 65299 "GT Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            [Test]
            procedure TruncateTrue_InGuardedRun_CounterIsBack()
            var
                R: Record "GT Probe A";
                Ok: Boolean;
                Next: Integer;
            begin
                R.Truncate(true);
                AddA();
                AddA();
                AddA();
                Commit();
                Ok := Codeunit.Run(Codeunit::"GT Inner A");
                if Ok <> false then
                    Error('TruncateTrue_InGuardedRun_CounterIsBack: guarded run returned %1', Ok);
                Next := AddA();
                if (Next <> 4) or (KeysA() <> '1,2,3,4,') then
                    Error('TruncateTrue_InGuardedRun_CounterIsBack: next key %1 (expected 4), keys %2 (expected 1,2,3,4,)', Next, KeysA());
            end;

            [Test]
            procedure TruncateFalse_InGuardedRun_CounterIsBack()
            var
                R: Record "GT Probe B";
                Ok: Boolean;
                Next: Integer;
            begin
                R.Truncate(true);
                AddB();
                AddB();
                AddB();
                Commit();
                Ok := Codeunit.Run(Codeunit::"GT Inner B");
                if Ok <> false then
                    Error('TruncateFalse_InGuardedRun_CounterIsBack: guarded run returned %1', Ok);
                Next := AddB();
                if (Next <> 4) or (KeysB() <> '1,2,3,4,') then
                    Error('TruncateFalse_InGuardedRun_CounterIsBack: next key %1 (expected 4), keys %2 (expected 1,2,3,4,)', Next, KeysB());
            end;

            [Test]
            procedure NoTruncate_InGuardedRun_TheValueIsConsumed()
            var
                R: Record "GT Probe C";
                Ok: Boolean;
                Next: Integer;
            begin
                R.Truncate(true);
                AddC();
                AddC();
                AddC();
                Commit();
                Ok := Codeunit.Run(Codeunit::"GT Inner C");
                if Ok <> false then
                    Error('NoTruncate_InGuardedRun_TheValueIsConsumed: guarded run returned %1', Ok);
                Next := AddC();
                if (Next <> 5) or (KeysC() <> '1,2,3,5,') then
                    Error('NoTruncate_InGuardedRun_TheValueIsConsumed: next key %1 (expected 5), keys %2 (expected 1,2,3,5,)', Next, KeysC());
            end;

            [Test]
            procedure TruncateTrue_InGuardedRun_DeletedLastRow_CounterIsNotTheHighWaterMark()
            var
                R: Record "GT Probe F";
                Ok: Boolean;
                Next: Integer;
            begin
                R.Truncate(true);
                AddF();
                AddF();
                R.Get(AddF());
                R.Delete();
                Commit();
                Ok := Codeunit.Run(Codeunit::"GT Inner F");
                if Ok <> false then
                    Error('TruncateTrue_InGuardedRun_DeletedLastRow_CounterIsNotTheHighWaterMark: guarded run returned %1', Ok);
                Next := AddF();
                if (Next <> 4) or (KeysF() <> '1,2,4,') then
                    Error('TruncateTrue_InGuardedRun_DeletedLastRow_CounterIsNotTheHighWaterMark: next key %1 (expected 4), keys %2 (expected 1,2,4,)', Next, KeysF());
            end;

            [Test]
            procedure NestedInnerFails_OuterSucceeds_CounterIsBackForTheOuter()
            var
                R: Record "GT Probe H";
                Ok: Boolean;
                Next: Integer;
            begin
                R.Truncate(true);
                AddH();
                AddH();
                AddH();
                Commit();
                Ok := Codeunit.Run(Codeunit::"GT Outer H");
                if Ok <> true then
                    Error('NestedInnerFails_OuterSucceeds_CounterIsBackForTheOuter: guarded run returned %1', Ok);
                Next := AddH();
                if (Next <> 5) or (KeysH() <> '1,2,3,4,5,') then
                    Error('NestedInnerFails_OuterSucceeds_CounterIsBackForTheOuter: next key %1 (expected 5), keys %2 (expected 1,2,3,4,5,)', Next, KeysH());
            end;

            [Test]
            procedure NestedInnerFails_OuterFails_OnlyTheInnerTruncateIsRestored()
            var
                R: Record "GT Probe I";
                Ok: Boolean;
                Next: Integer;
            begin
                R.Truncate(true);
                AddI();
                AddI();
                AddI();
                Commit();
                Ok := Codeunit.Run(Codeunit::"GT Outer I");
                if Ok <> false then
                    Error('NestedInnerFails_OuterFails_OnlyTheInnerTruncateIsRestored: guarded run returned %1', Ok);
                Next := AddI();
                if (Next <> 5) or (KeysI() <> '1,2,3,5,') then
                    Error('NestedInnerFails_OuterFails_OnlyTheInnerTruncateIsRestored: next key %1 (expected 5), keys %2 (expected 1,2,3,5,)', Next, KeysI());
            end;

            [Test]
            procedure TruncateFalse_InGuardedRun_NeverUsedTable_CounterIsUnstarted()
            var
                R: Record "GT Probe V";
                Ok: Boolean;
                Next: Integer;
            begin
                R.Truncate(false);
                Commit();
                Ok := Codeunit.Run(Codeunit::"GT Inner V");
                if Ok <> false then
                    Error('TruncateFalse_InGuardedRun_NeverUsedTable_CounterIsUnstarted: guarded run returned %1', Ok);
                Next := AddV();
                if (Next <> 1) or (KeysV() <> '1,') then
                    Error('TruncateFalse_InGuardedRun_NeverUsedTable_CounterIsUnstarted: next key %1 (expected 1), keys %2 (expected 1,)', Next, KeysV());
            end;

            local procedure AddA(): Integer
            var
                R: Record "GT Probe A";
            begin
                R."Entry No." := 0;
                R.Payload := 'zero';
                R.Insert(true);
                exit(R."Entry No.");
            end;

            local procedure KeysA(): Text
            var
                R: Record "GT Probe A";
                T: Text;
            begin
                if R.FindSet() then
                    repeat
                        T += Format(R."Entry No.") + ',';
                    until R.Next() = 0;
                exit(T);
            end;

            local procedure AddB(): Integer
            var
                R: Record "GT Probe B";
            begin
                R."Entry No." := 0;
                R.Payload := 'zero';
                R.Insert(true);
                exit(R."Entry No.");
            end;

            local procedure KeysB(): Text
            var
                R: Record "GT Probe B";
                T: Text;
            begin
                if R.FindSet() then
                    repeat
                        T += Format(R."Entry No.") + ',';
                    until R.Next() = 0;
                exit(T);
            end;

            local procedure AddC(): Integer
            var
                R: Record "GT Probe C";
            begin
                R."Entry No." := 0;
                R.Payload := 'zero';
                R.Insert(true);
                exit(R."Entry No.");
            end;

            local procedure KeysC(): Text
            var
                R: Record "GT Probe C";
                T: Text;
            begin
                if R.FindSet() then
                    repeat
                        T += Format(R."Entry No.") + ',';
                    until R.Next() = 0;
                exit(T);
            end;

            local procedure AddF(): Integer
            var
                R: Record "GT Probe F";
            begin
                R."Entry No." := 0;
                R.Payload := 'zero';
                R.Insert(true);
                exit(R."Entry No.");
            end;

            local procedure KeysF(): Text
            var
                R: Record "GT Probe F";
                T: Text;
            begin
                if R.FindSet() then
                    repeat
                        T += Format(R."Entry No.") + ',';
                    until R.Next() = 0;
                exit(T);
            end;

            local procedure AddH(): Integer
            var
                R: Record "GT Probe H";
            begin
                R."Entry No." := 0;
                R.Payload := 'zero';
                R.Insert(true);
                exit(R."Entry No.");
            end;

            local procedure KeysH(): Text
            var
                R: Record "GT Probe H";
                T: Text;
            begin
                if R.FindSet() then
                    repeat
                        T += Format(R."Entry No.") + ',';
                    until R.Next() = 0;
                exit(T);
            end;

            local procedure AddI(): Integer
            var
                R: Record "GT Probe I";
            begin
                R."Entry No." := 0;
                R.Payload := 'zero';
                R.Insert(true);
                exit(R."Entry No.");
            end;

            local procedure KeysI(): Text
            var
                R: Record "GT Probe I";
                T: Text;
            begin
                if R.FindSet() then
                    repeat
                        T += Format(R."Entry No.") + ',';
                    until R.Next() = 0;
                exit(T);
            end;

            local procedure AddV(): Integer
            var
                R: Record "GT Probe V";
            begin
                R."Entry No." := 0;
                R.Payload := 'zero';
                R.Insert(true);
                exit(R."Entry No.");
            end;

            local procedure KeysV(): Text
            var
                R: Record "GT Probe V";
                T: Text;
            begin
                if R.FindSet() then
                    repeat
                        T += Format(R."Entry No.") + ',';
                    until R.Next() = 0;
                exit(T);
            end;
        }
        """;
}
