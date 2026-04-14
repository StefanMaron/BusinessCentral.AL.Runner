// Tests for IntegrationEvent(IncludeSender=true) and BusinessEvent(IncludeSender=true).
// When IncludeSender is true, the subscriber receives the publishing codeunit instance
// as its first parameter, enabling it to read/write publisher state.

codeunit 59950 "SP Publisher"
{
    var
        InternalState: Integer;

    // IncludeSender = true: subscriber gets "sender: Codeunit" as first param
    [IntegrationEvent(true, false)]
    procedure OnAfterProcess(Result: Integer)
    begin
    end;

    procedure SetState(NewState: Integer)
    begin
        InternalState := NewState;
    end;

    procedure GetState(): Integer
    begin
        exit(InternalState);
    end;

    procedure Process(Value: Integer): Integer
    var
        Result: Integer;
    begin
        Result := Value * 2;
        InternalState := Result;
        OnAfterProcess(Result);
        exit(Result);
    end;
}

codeunit 59951 "SP Subscriber"
{
    SingleInstance = true;
    var
        CapturedSenderState: Integer;
        CapturedResult: Integer;
        WasCalled: Boolean;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"SP Publisher", OnAfterProcess, '', true, true)]
    local procedure HandleAfterProcess(sender: Codeunit "SP Publisher"; Result: Integer)
    begin
        // Read state from the sender instance
        CapturedSenderState := sender.GetState();
        CapturedResult := Result;
        WasCalled := true;
    end;
}

// Second publisher using BusinessEvent(IncludeSender=true)
codeunit 59952 "SP BizPublisher"
{
    var
        OrderNo: Integer;

    [BusinessEvent(true)]
    procedure OnOrderPosted(PostedOrderNo: Integer)
    begin
    end;

    procedure PostOrder(No: Integer)
    begin
        OrderNo := No;
        OnOrderPosted(No);
    end;

    procedure GetOrderNo(): Integer
    begin
        exit(OrderNo);
    end;
}

codeunit 59953 "SP BizSubscriber"
{
    SingleInstance = true;
    var
        ReceivedOrderNo: Integer;
        SenderOrderNo: Integer;
        WasCalled: Boolean;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"SP BizPublisher", OnOrderPosted, '', true, true)]
    local procedure HandleOrderPosted(sender: Codeunit "SP BizPublisher"; PostedOrderNo: Integer)
    begin
        ReceivedOrderNo := PostedOrderNo;
        SenderOrderNo := sender.GetOrderNo();
        WasCalled := true;
    end;
}

// Third publisher: IncludeSender=true with var params (mixed pattern)
codeunit 59954 "SP MixedPublisher"
{
    var
        Tag: Text[50];

    [IntegrationEvent(true, false)]
    procedure OnBeforeValidate(var Value: Integer; var IsHandled: Boolean)
    begin
    end;

    procedure SetTag(NewTag: Text[50])
    begin
        Tag := NewTag;
    end;

    procedure GetTag(): Text[50]
    begin
        exit(Tag);
    end;

    procedure Validate(var Value: Integer): Boolean
    var
        Handled: Boolean;
    begin
        Handled := false;
        OnBeforeValidate(Value, Handled);
        if Handled then
            exit(true);
        if Value < 0 then
            exit(false);
        exit(true);
    end;
}

codeunit 59955 "SP MixedSubscriber"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"SP MixedPublisher", OnBeforeValidate, '', true, true)]
    local procedure HandleBeforeValidate(sender: Codeunit "SP MixedPublisher"; var Value: Integer; var IsHandled: Boolean)
    begin
        // Sender with tag "override" forces value to 999
        if sender.GetTag() = 'override' then begin
            Value := 999;
            IsHandled := true;
        end;
    end;
}

// Fourth publisher: IncludeSender=false as control (should still work)
codeunit 59956 "SP NoSender Publisher"
{
    [IntegrationEvent(false, false)]
    procedure OnSimpleEvent(Value: Integer)
    begin
    end;

    procedure Fire(Value: Integer)
    begin
        OnSimpleEvent(Value);
    end;
}

codeunit 59957 "SP NoSender Subscriber"
{
    SingleInstance = true;
    var
        ReceivedValue: Integer;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"SP NoSender Publisher", OnSimpleEvent, '', true, true)]
    local procedure HandleSimpleEvent(Value: Integer)
    begin
        ReceivedValue := Value;
    end;
}
