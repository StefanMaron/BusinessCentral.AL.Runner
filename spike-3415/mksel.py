"""Write the probe-1 selections (by test, by codeunit) as AL_RUNNER_EXACT_TESTS files."""
import json, os, xml.etree.ElementTree as ET
S = os.path.dirname(os.path.abspath(__file__))
ev = json.load(open(f"{S}/runs/srv-noseed-1/events-0-record.json")); ev.pop("<bundle>")
allt = [f"{tc.get('classname')}.{tc.get('name')}" for tc in ET.parse(f"{S}/runs/cli-probe0/junit.xml").getroot().iter("testcase")]
sel = {t for t, v in ev.items() if "ev|Codeunit|80|OnBeforePostSalesDoc" in v}
cus = {t.split(".")[0] for t in sel}
selcu = [t for t in allt if t.split(".")[0] in cus]
open(f"{S}/sel-p1-test.txt", "w").write("\n".join(sorted(sel)) + "\n")
open(f"{S}/sel-p1-cu.txt", "w").write("\n".join(selcu) + "\n")
print(len(sel), len(selcu))
