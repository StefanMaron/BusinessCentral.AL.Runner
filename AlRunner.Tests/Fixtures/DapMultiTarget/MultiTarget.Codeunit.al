/// <summary>
/// #3820: ONE source line carrying more than one executable statement.
///
/// A DAP breakpoint on such a line used to bind one of them — DapBreakpointResolver.Resolve
/// stopped at the first exact line match — so it fired once instead of at each target. Which
/// one it bound was arbitrary: the instrumented statement indexes come out of a HashSet.
///
/// Four shapes, so a fact cannot pass by handling only the easy one:
///
///   line 36  two statements, one scope   — the base case
///   line 35  one statement               — the control: a normal line still stops once
///   line 37  THREE statements, one scope — pins "every", not "at least two"
///   line 26  two one-line procedures     — fan-out ACROSS scope types, not within one
///
/// Line 36's second statement starts at column 22, which is what the column-breakpoint fact
/// uses. The line numbers and that column are asserted against exactly by
/// DapMultiTargetLineTests. Keep them in sync.
/// </summary>
codeunit 60311 "Dap Multi Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Dap Multi Assert";

    procedure Seven(): Integer begin exit(7); end;  procedure Nine(): Integer begin exit(9); end;

    [Test]
    procedure TwoStatementsOnOneLine()
    var
        First: Integer;
        Second: Integer;
        Third: Integer;
    begin
        First := 1;
        Second := 2; First := First + Second;
        Third := 1; Third := Third + 1; Third := Third + 1;
        Assert.AreEqual(3, First, 'both statements on the shared line ran');
        Assert.AreEqual(2, Second, 'the first of the two ran');
        Assert.AreEqual(3, Third, 'all three statements on the shared line ran');
    end;

    [Test]
    procedure TwoProceduresOnOneLine()
    var
        Total: Integer;
    begin
        Total := Seven() + Nine();
        Assert.AreEqual(16, Total, 'both one-line procedures ran');
    end;
}
