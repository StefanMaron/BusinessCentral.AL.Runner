codeunit 65401 "Package Value Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NewValue_IsItsOwnValue()
    var
        T: Enum "Loyalty Tier";
    begin
        T := "Loyalty Tier"::Gold;
        if Format(T) <> 'Gold' then
            Error('Expected Gold, got %1', Format(T));
        if T = "Loyalty Tier"::Bronze then
            Error('Gold must not be Bronze');
        if T = "Loyalty Tier"::Silver then
            Error('Gold must not be Silver');
    end;

    [Test]
    procedure ExistingValue_IsThePackagesOwn()
    var
        T: Enum "Loyalty Tier";
    begin
        T := "Loyalty Tier"::Silver;
        if T.AsInteger() <> 1 then
            Error('Expected ordinal 1, got %1', T.AsInteger());
    end;

    [Test]
    procedure TwoNewValues_AreTwoValues()
    var
        T: Enum "Loyalty Tier";
        Other: Enum "Loyalty Tier";
    begin
        T := "Loyalty Tier"::Gold;
        Other := "Loyalty Tier"::Platinum;
        if T = Other then
            Error('Gold and Platinum must differ');
        if Format(Other) <> 'Platinum' then
            Error('Expected Platinum, got %1', Format(Other));
    end;
}
