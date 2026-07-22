// Proves trappable collection errors surface BC's REAL error text on the
// skeleton runtime.
//
// RED (before the fix): every trappable error was replaced by
// "Value cannot be null." because TrappableOperationExecutor.HandleError logs
// the caught exception via NavServerEventSource before mapping it, and the
// skeleton NavServerEventSource (RuntimeHelpers.GetUninitializedObject) had a
// null 'mutex' field -> lock(null) -> ArgumentNullException masks the real one.
//
// GREEN (after the fix): the skeleton singleton's mutex is field-poked to a
// real object right where it is created, so logging no-ops cleanly and BC's
// own NavNCLArgumentOutOfRangeException.CreateGeneric("List") text
// ("An invalid argument was passed to a 'List' data type method.") surfaces.
codeunit 60711 "LTE Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "LTE Assert";

    // Negative: empty list, out-of-range index -> BC's real error text.
    // The two-arg Get(index, var) form compiles to SharedNavObjectList.Get(DataError.ThrowError,...)
    // whose HandleError path calls LogWriterHelper.LogExceptionEvent BEFORE mapping the
    // exception — the exact path that was masked by the null-mutex skeleton logger.
    [Test]
    procedure EmptyListGetVar_SurfacesRealOutOfRangeError()
    var
        L: List of [Integer];
        V: Integer;
    begin
        asserterror L.Get(5, V);
        Assert.ExpectedError('An invalid argument was passed to a ''List'' data type method.', GetLastErrorText());
    end;

    // The value-returning Get form maps through ExecuteFactory (no logging) — must
    // surface the same real error text on both paths.
    [Test]
    procedure EmptyListGet_SurfacesRealOutOfRangeError()
    var
        L: List of [Integer];
        V: Integer;
    begin
        asserterror V := L.Get(5);
        Assert.ExpectedError('An invalid argument was passed to a ''List'' data type method.', GetLastErrorText());
    end;

    // Positive: in-range Get still returns the real value (the logging fix
    // must not perturb the success path).
    [Test]
    procedure ListGet_InRange_ReturnsValue()
    var
        L: List of [Integer];
    begin
        L.Add(11);
        L.Add(22);
        Assert.AreEqual(22, L.Get(2), 'L.Get(2) must return the second element');
    end;
}
