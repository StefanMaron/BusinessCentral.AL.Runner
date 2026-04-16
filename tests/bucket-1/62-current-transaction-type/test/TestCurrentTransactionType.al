codeunit 62401 "Test CurrentTransactionType"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CurrentTransactionType_ReturnsUpdate()
    var
        TxType: TransactionType;
    begin
        // Runner stub must return TransactionType::Update (the most common real-world value)
        TxType := CurrentTransactionType();
        Assert.AreEqual(TransactionType::Update, TxType, 'CurrentTransactionType() must return Update in runner');
    end;

    [Test]
    procedure CurrentTransactionType_IsStable()
    var
        T1: TransactionType;
        T2: TransactionType;
    begin
        // Must return the same value on consecutive calls
        T1 := CurrentTransactionType();
        T2 := CurrentTransactionType();
        Assert.AreEqual(T1, T2, 'CurrentTransactionType() must return a stable value');
    end;
}
