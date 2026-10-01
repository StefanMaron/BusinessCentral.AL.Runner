namespace AlRunner.Extras.ExternalBusinessEvent;

using System.Integration;

codeunit 65661 "Ebe Publisher"
{
    var
        StepCount: Integer;

    procedure Steps(): Integer
    begin
        exit(StepCount);
    end;

    procedure RaiseBetweenSteps()
    begin
        StepCount += 1;
        OnEbeCreated(CreateGuid());
        StepCount += 1;
    end;

    procedure RaiseWithPayload()
    begin
        StepCount += 1;
        OnEbePayload('payload text', 12.5, 7, true, Today());
        StepCount += 1;
    end;

    procedure RaiseThenFail()
    begin
        StepCount += 1;
        OnEbeCreated(CreateGuid());
        StepCount += 1;
        Error('Ebe unrelated failure after the raise');
    end;

    procedure FailThenRaise()
    begin
        StepCount += 1;
        Error('Ebe unrelated failure before the raise');
        OnEbeCreated(CreateGuid());
    end;

    [ExternalBusinessEvent('ebe_created', 'Ebe created', 'An example record was created.', EventCategory::"Ebe Example", '1.0')]
    local procedure OnEbeCreated(EntityId: Guid)
    begin
    end;

    [ExternalBusinessEvent('ebe_payload', 'Ebe payload', 'Carries every payload kind GetPayloadJson converts.', EventCategory::"Ebe Example", '2.0')]
    local procedure OnEbePayload(Description: Text[250]; Amount: Decimal; Quantity: Integer; Flag: Boolean; PostingDate: Date)
    begin
    end;
}
