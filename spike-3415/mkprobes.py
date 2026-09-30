import os, json, uuid
S = os.path.join(os.path.dirname(os.path.abspath(__file__)), "probes")
probes = {
    1: ("[EventSubscriber(ObjectType::Codeunit, Codeunit::\"Sales-Post\", 'OnBeforePostSalesDoc', '', false, false)]",
        "var SalesHeader: Record \"Sales Header\""),
    2: ("[EventSubscriber(ObjectType::Codeunit, Codeunit::\"Sales-Post\", 'OnAfterPostSalesDoc', '', false, false)]",
        "var SalesHeader: Record \"Sales Header\""),
    3: ("[EventSubscriber(ObjectType::Table, Database::\"Sales Line\", 'OnAfterValidateEvent', 'Quantity', false, false)]",
        "var Rec: Record \"Sales Line\""),
    4: ("[EventSubscriber(ObjectType::Codeunit, Codeunit::\"Sales-Quote to Order\", 'OnBeforeOnRun', '', false, false)]",
        "var SalesHeader: Record \"Sales Header\""),
}
for n, (attr, params) in probes.items():
    d = f"{S}/Probe{n}"
    os.makedirs(d, exist_ok=True)
    app = {"id": str(uuid.uuid5(uuid.NAMESPACE_DNS, f"probe3415-{n}")), "name": f"Probe3415-{n}",
           "publisher": "Spike", "version": "1.0.0.0", "platform": "28.0.0.0", "application": "28.0.0.0",
           "idRanges": [{"from": 50100, "to": 50149}], "runtime": "16.0", "target": "OnPrem"}
    json.dump(app, open(f"{d}/app.json", "w"), indent=2)
    open(f"{d}/Probe{n}.Codeunit.al", "w").write(f"""codeunit {50100 + n} "Probe3415 {n}"
{{
    {attr}
    local procedure OnProbe{n}({params})
    begin
        Error('PROBE-{n}');
    end;
}}
""")
print(sorted(os.listdir(S)))
