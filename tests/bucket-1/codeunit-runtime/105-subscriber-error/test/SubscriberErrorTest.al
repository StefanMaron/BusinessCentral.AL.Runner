codeunit 59972 "SE Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure SubscriberErrorPropagates()
    var
        Publisher: Codeunit "SE Publisher";
    begin
        // Positive: subscriber error propagates to caller and can be caught.
        asserterror Publisher.Process(-5);
        Assert.ExpectedError('Negative value not allowed');
    end;

    [Test]
    procedure SubscriberNoErrorOnValidInput()
    var
        Publisher: Codeunit "SE Publisher";
    begin
        // Positive: valid input doesn't trigger subscriber error.
        Publisher.Process(10);
        Assert.IsTrue(true, 'Should not throw on valid input');
    end;

    [Test]
    procedure SubscriberErrorIncludesValue()
    var
        Publisher: Codeunit "SE Publisher";
    begin
        // Positive: error message includes the formatted value.
        asserterror Publisher.Process(-42);
        Assert.ExpectedError('-42');
    end;

    [Test]
    procedure MultipleCallsIndependent()
    var
        Publisher: Codeunit "SE Publisher";
    begin
        // Positive: each call is independent — error on one doesn't affect next.
        asserterror Publisher.Process(-1);
        Assert.ExpectedError('Negative value not allowed');

        // Next call with valid input should succeed
        Publisher.Process(1);
        Assert.IsTrue(true, 'Valid call after error should succeed');
    end;
}
