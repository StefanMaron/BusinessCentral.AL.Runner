table 62050 "SIS Setup"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Primary Key"; Code[10]) { }
        field(2; "Currency Code"; Code[10]) { }
    }

    keys
    {
        key(PK; "Primary Key") { Clustered = true; }
    }
}

/// <summary>
/// Deliberately shaped like Base App codeunit 347 "Auto Format": SingleInstance, with a
/// global record it reads once and caches behind a boolean. That global is a record HANDLE,
/// and the handle is what goes null when the tree it hangs off is disposed.
/// </summary>
codeunit 62050 "SIS Cache"
{
    SingleInstance = true;

    var
        Setup: Record "SIS Setup";
        SetupRead: Boolean;
        ReadCount: Integer;

    procedure GetCurrencyCode(): Code[10]
    begin
        if not SetupRead then begin
            Setup.Get('MAIN');
            ReadCount += 1;
        end;
        SetupRead := true;
        exit(Setup."Currency Code");
    end;

    procedure GetReadCount(): Integer
    begin
        exit(ReadCount);
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"SIS Publisher", 'OnResolveCurrency', '', false, false)]
    local procedure OnResolveCurrency(var CurrencyCode: Code[10])
    begin
        CurrencyCode := GetCurrencyCode();
    end;
}

/// The control for the SingleInstance tests: identical shape, but per-call, so it must NOT
/// share state between scopes.
codeunit 62052 "SIS Per Call"
{
    SingleInstance = false;

    var
        Bumps: Integer;

    procedure Bump()
    begin
        Bumps += 1;
    end;

    procedure GetBumps(): Integer
    begin
        exit(Bumps);
    end;
}

/// Publishes the event the SingleInstance cache subscribes to. The real failure (Base App
/// codeunit 347 "Auto Format") is reached exactly this way — through event dispatch, not a
/// direct call — and dispatch is what resolves the codeunit against a scope that then ends.
codeunit 62053 "SIS Publisher"
{
    [IntegrationEvent(false, false)]
    procedure OnResolveCurrency(var CurrencyCode: Code[10])
    begin
    end;

    procedure Resolve(): Code[10]
    var
        CurrencyCode: Code[10];
    begin
        OnResolveCurrency(CurrencyCode);
        exit(CurrencyCode);
    end;
}

/// Primes the SingleInstance cache from inside a Codeunit.Run scope — a real, disposable
/// scope of its own, which is the shape the failing Base App path had.
codeunit 62054 "SIS Runner"
{
    trigger OnRun()
    var
        Cache: Codeunit "SIS Cache";
    begin
        Cache.GetCurrencyCode();
    end;
}

/// Primes the cache and then FAILS. The rollback path is where a scope is disposed outright
/// rather than merely detached, which is what takes the cached instance with it.
codeunit 62057 "SIS Failing Runner"
{
    trigger OnRun()
    var
        Cache: Codeunit "SIS Cache";
    begin
        Cache.GetCurrencyCode();
        Error('deliberate');
    end;
}
