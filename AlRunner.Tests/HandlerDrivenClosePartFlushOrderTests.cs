// HandlerDrivenClosePartFlushOrderTests — issue #3701.
//
// The defect: #3672 made a [ModalPageHandler]'s built-in OK().Invoke() attempt the close
// (LiveNavTestPage.AttemptHandlerDrivenClose), but that attempt raised OnQueryClosePage while a
// row typed into a PART was still only a buffer. Invoke() flushes the page's own row and nothing
// else, so a page that reads its part in OnQueryClosePage saw one row too few and a page that
// materialises its part contents on OK saved nothing.
//
// The BC-behaviour claim — that pressing OK saves the typed part row BEFORE OnQueryClosePage
// runs — is not asserted here. It is measured on a real service tier by corpus codeunit 60663
// "Opf Ok Part Flush Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#315), and confirmed
// in a BC 28.4.53241.0 container before that PR was opened.
//
// What IS asserted here is the runner-internal mechanism that makes it true, and it is an
// ORDERING, which no return value exposes: inside AttemptHandlerDrivenClose, the call to
// FlushParts must come before the call to RaiseOnClosePage. A regression that dropped the flush,
// or moved it after the raise, restores the exact defect while every unit-level return value
// stays the same — so the order is read out of the IL, which is where it lives.
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class HandlerDrivenClosePartFlushOrderTests
{
    private static MethodDefinition LiveNavTestPageMethod(string name)
    {
        // Any type from the runner assembly locates the image; LiveNavTestPage itself is
        // internal, and this test reads it through Cecil rather than through the type system.
        var path = typeof(AlRunner.TestExecutor).Assembly.Location;
        var module = ModuleDefinition.ReadModule(path);
        var type = module.GetTypes().Single(t => t.FullName == "AlRunner.LiveNavTestPage");
        return type.Methods.Single(m => m.Name == name);
    }

    private static MethodDefinition AttemptHandlerDrivenClose()
        => LiveNavTestPageMethod("AttemptHandlerDrivenClose");

    private static MethodDefinition Close() => LiveNavTestPageMethod("Close");

    private static int IndexOfCallTo(MethodDefinition method, string calleeName)
    {
        var body = method.Body.Instructions;
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i].OpCode != OpCodes.Call && body[i].OpCode != OpCodes.Callvirt) continue;
            if (body[i].Operand is MethodReference callee && callee.Name == calleeName) return i;
        }
        return -1;
    }

    // The regression row. Both calls must be present, and the flush must come first.
    [Fact]
    public void AttemptHandlerDrivenClose_FlushesParts_BeforeItRaisesOnClosePage()
    {
        var method = AttemptHandlerDrivenClose();

        var flush = IndexOfCallTo(method, "FlushParts");
        var raise = IndexOfCallTo(method, "RaiseOnClosePage");

        Assert.True(flush >= 0,
            "AttemptHandlerDrivenClose must call FlushParts — without it a row the handler typed "
            + "into a part is still a buffer when OnQueryClosePage runs (#3701).");
        Assert.True(raise >= 0,
            "AttemptHandlerDrivenClose must still raise OnQueryClosePage — that is the close "
            + "attempt #3672 added.");
        Assert.True(flush < raise,
            $"FlushParts must be called BEFORE RaiseOnClosePage (found FlushParts at IL index "
            + $"{flush} and RaiseOnClosePage at {raise}). Flushing afterwards is the #3701 defect "
            + "with the call present.");
    }

    // The row that stops the assertion above from being satisfied by an unconditional flush at
    // the top of the method: the flush belongs after the guards, so a page the TEST opened, a
    // torn-down page, and a non-confirming result still take no action at all.
    [Fact]
    public void AttemptHandlerDrivenClose_FlushesPartsOnlyAfterItsGuards()
    {
        var method = AttemptHandlerDrivenClose();

        var flush = IndexOfCallTo(method, "FlushParts");
        var wasClosedFromAl = IndexOfCallTo(method, "WasClosedFromAl");

        Assert.True(wasClosedFromAl >= 0,
            "the guard reading WasClosedFromAl must still be in AttemptHandlerDrivenClose.");
        Assert.True(flush > wasClosedFromAl,
            $"FlushParts (IL index {flush}) must come after the guards (WasClosedFromAl at "
            + $"{wasClosedFromAl}) — a page the test opened, or one already closed from AL, must "
            + "not be touched by this route.");
    }

    // ── The TestPage.Close() twin — issue #3708 ──────────────────────────────────────────────
    //
    // Same defect shape on the other close route, and pre-existing rather than a #3672
    // regression: Close() raised OnQueryClosePage and only afterwards ran FlushParts();
    // FlushRow();, so a row typed into a part through Card.Lines.New() did not exist yet when
    // the trigger read the part. The BC-behaviour claim is measured upstream by corpus codeunit
    // 60438 "Opc Close Part Flush Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#320),
    // verified in a BC 28.4.53241.0 container before that PR was opened. What is asserted here
    // is again the ordering, which no return value exposes.

    [Fact]
    public void Close_FlushesPartsAndTheRow_BeforeItRaisesOnClosePage()
    {
        var method = Close();

        var flushParts = IndexOfCallTo(method, "FlushParts");
        var flushRow = IndexOfCallTo(method, "FlushRow");
        var raise = IndexOfCallTo(method, "RaiseOnClosePage");

        Assert.True(flushParts >= 0,
            "Close() must call FlushParts — without it a row typed into a part is still a buffer "
            + "when OnQueryClosePage runs (#3708), and nothing writes it back at all (#3682).");
        Assert.True(flushRow >= 0,
            "Close() must still call FlushRow — FlushParts does not write the host page's own row.");
        Assert.True(raise >= 0,
            "Close() must still raise OnQueryClosePage — that is the close the test asked for.");
        Assert.True(flushParts < raise,
            $"FlushParts (IL index {flushParts}) must be called BEFORE RaiseOnClosePage ({raise}). "
            + "Flushing afterwards is the #3708 defect with the call present.");
        Assert.True(flushRow < raise,
            $"FlushRow (IL index {flushRow}) must be called BEFORE RaiseOnClosePage ({raise}): BC's "
            + "client sends the edited row and only then drives the close.");
        Assert.True(flushParts < flushRow,
            $"Close() flushes parts THEN the row (found FlushParts at {flushParts}, FlushRow at "
            + $"{flushRow}) — a part's OnValidate can touch the header, so the header write must "
            + "come second. The OK route is row-then-parts only because Invoke() has already "
            + "written the host row (#3701).");
    }

    // The counterpart to AttemptHandlerDrivenClose_FlushesPartsOnlyAfterItsGuards: a torn-down
    // page raises "The TestPage is not open." and flushes nothing, so the flush belongs after
    // that guard rather than at the very top of the method.
    [Fact]
    public void Close_FlushesOnlyAfterTheTornDownGuard()
    {
        var method = Close();

        var flushParts = IndexOfCallTo(method, "FlushParts");
        var notOpen = IndexOfCallTo(method, "MakeTestPageNotOpenException");

        Assert.True(notOpen >= 0,
            "the torn-down guard (MakeTestPageNotOpenException) must still be in Close().");
        Assert.True(flushParts > notOpen,
            $"FlushParts (IL index {flushParts}) must come after the torn-down guard "
            + $"(MakeTestPageNotOpenException at {notOpen}) — a torn-down page throws instead of "
            + "closing, and must not write anything back on the way out.");
    }
}
