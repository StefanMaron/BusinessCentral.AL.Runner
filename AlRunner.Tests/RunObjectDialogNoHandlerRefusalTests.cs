// A RunObject action whose target is a dialog page, invoked with no [ModalPageHandler] bound,
// raises BC's own NavTestPageInvokedWithoutHandlerException (issue #3223;
// RunnerPageInstance.ActionRunObject.cs). The AL-observable claim is adjudicated by corpus
// codeunit 60285 arms 7-9. These pin the two runner-side properties that claim depends on.
using System;
using System.Globalization;
using AlRunner;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunObjectDialogNoHandlerRefusalTests
{
    // The corpus asserts BC's resource text. The runner raises BC's type rather than a message
    // of its own, so the text comes from BC's resources and names the target page.
    [Fact]
    public void TheRefusalCarriesBcsOwnMessageNamingThePage()
    {
        var ex = NavTestPageInvokedWithoutHandlerException.Create(CultureInfo.InvariantCulture, 60284);

        Assert.Equal(
            "TestPages that are invoked from a RunObject action must have a handler. Page No. = 60284",
            ex.Message);
        Assert.IsAssignableFrom<NavTestBaseException>(ex);
    }

    // `asserterror Host.RunDialogOnRec.Invoke()` must catch it: on real BC the invoke raises, so
    // the asserterror passes. A runner asserterror that let this type tear through (the way it
    // lets BcShapeGapException through) would fail that arm on a correct refusal.
    [Fact]
    public void AssertError_CatchesTheRefusal()
    {
        var ex = Record.Exception(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => throw NavTestPageInvokedWithoutHandlerException.Create(
                CultureInfo.InvariantCulture, 60284)));

        Assert.Null(ex);
    }
}
