/// <summary>
/// UserSecurityId() must answer with one stable, non-empty GUID for the whole session.
///
/// Per-user singleton tables key on it, so two different answers mean a row is written under
/// one identity and looked up under another. That does not fail where the mistake is — it
/// fails much later, as "the &lt;table&gt; does not exist. Identification fields and values:
/// UserSecurityId='{00000000-...}'", which reads like a missing-row bug rather than an
/// identity bug.
/// </summary>
codeunit 62081 "USI Tests"
{
    Subtype = Test;

    [Test]
    procedure UserSecurityId_IsNotTheEmptyGuid()
    var
        Id: Guid;
    begin
        Id := UserSecurityId();

        // The empty GUID is what an unseeded identity reads as, and it is a legal value for a
        // Guid field — so nothing downstream rejects it. It has to be caught here.
        if IsNullGuid(Id) then
            Error('UserSecurityId() returned the empty GUID.');
    end;

    [Test]
    procedure UserSecurityId_IsStableAcrossCallsAndObjects()
    var
        Reader: Codeunit "USI Reader";
        First: Guid;
    begin
        First := UserSecurityId();

        if UserSecurityId() <> First then
            Error('UserSecurityId() changed between two calls in the same scope: <%1> then <%2>.',
                First, UserSecurityId());

        // Read from another object. A per-method-scope identity seed answers this one
        // differently while the two calls above still agree.
        if Reader.Read() <> First then
            Error('UserSecurityId() read from another codeunit was <%1>, expected <%2>.',
                Reader.Read(), First);
    end;

    [Test]
    procedure UserKeyedSingletonRow_IsReachableAfterItIsCreated()
    var
        Buffer: Record "USI Buffer";
    begin
        Buffer.DeleteAll();

        Buffer.GetForCurrentUser();
        Buffer.Payload := 'first';
        Buffer.Modify();

        // The raising form: if the row went in under a different identity than the one this
        // Get asks for, this errors instead of silently reading blank.
        Clear(Buffer);
        Buffer.Get(UserSecurityId());
        if Buffer.Payload <> 'first' then
            Error('The current user''s row read back <%1>, expected <first>.', Buffer.Payload);

        // And the singleton really is a singleton — a second call must find the row, not
        // insert a second one under a drifted identity.
        Clear(Buffer);
        Buffer.GetForCurrentUser();
        if Buffer.Payload <> 'first' then
            Error('GetForCurrentUser created a second row: payload was <%1>, expected <first>.',
                Buffer.Payload);
        if Buffer.Count() <> 1 then
            Error('Expected exactly 1 buffer row, found %1.', Buffer.Count());
    end;

    [Test]
    procedure UserKeyedSingletonRow_IsNotStoredUnderTheEmptyGuid()
    var
        Buffer: Record "USI Buffer";
        EmptyId: Guid;
    begin
        Buffer.DeleteAll();
        Buffer.GetForCurrentUser();

        // The negative direction: the row must NOT be findable under the empty GUID. If the
        // identity was never seeded, GetForCurrentUser happily writes a row keyed on
        // {00000000-...} and every assertion above still passes.
        Clear(Buffer);
        asserterror Buffer.Get(EmptyId);
        if StrPos(GetLastErrorText(), 'does not exist') = 0 then
            Error('Expected no row under the empty GUID, got: <%1>', GetLastErrorText());
    end;
}
