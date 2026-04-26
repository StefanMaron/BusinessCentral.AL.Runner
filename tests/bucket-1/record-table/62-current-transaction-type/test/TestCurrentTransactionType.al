codeunit 62000 "Test CurrentTransactionType"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CurrentTransactionType_ReturnsUpdate()
    var
        TxType: TransactionType;
    begin
        // [GIVEN] The runner has no real transaction system
        // [WHEN] CurrentTransactionType() is called
        TxType := CurrentTransactionType();

        // [THEN] Returns TransactionType::Update (the stable stub value)
        Assert.AreEqual(TransactionType::Update, TxType, 'CurrentTransactionType() must return Update in runner');
    end;

    [Test]
    procedure CurrentTransactionType_IsStable()
    var
        T1: TransactionType;
        T2: TransactionType;
    begin
        // [GIVEN] Multiple calls to CurrentTransactionType()
        T1 := CurrentTransactionType();
        T2 := CurrentTransactionType();

        // [THEN] Always returns the same value
        Assert.AreEqual(T1, T2, 'CurrentTransactionType() must return a stable value');
    end;

    [Test]
    procedure CurrentTransactionType_NotBrowse()
    var
        TxType: TransactionType;
    begin
        // [GIVEN] The stub always returns Update
        TxType := CurrentTransactionType();

        // [THEN] It does not return Browse (proves it is not defaulting to ordinal 0)
        Assert.AreNotEqual(TransactionType::Browse, TxType, 'CurrentTransactionType() must not return Browse');
    end;
}
