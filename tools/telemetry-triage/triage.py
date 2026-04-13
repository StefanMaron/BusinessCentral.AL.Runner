#!/usr/bin/env python3
"""
telemetry-triage: Query Application Insights for al-runner crash reports,
group them by exception type, and create/update GitHub Issues.

Required environment variables:
  APPINSIGHTS_API_KEY   Application Insights API key (read access)
  GITHUB_TOKEN          GitHub token with issues:write permission
  GITHUB_REPOSITORY     owner/repo (set automatically by Actions)
  APPINSIGHTS_APP_ID    Application Insights application ID

Optional:
  FROM_TIME             ISO-8601 UTC timestamp to query from (overrides last-run detection)
  DRY_RUN               Set to "1" to print what would happen without creating issues
"""

import json
import os
import re
import sys
import urllib.request
import urllib.error
from datetime import datetime, timezone, timedelta

# ─── Config ───────────────────────────────────────────────────────────────────

APP_ID       = os.environ.get("APPINSIGHTS_APP_ID", "3986aa86-ec55-4392-a3fc-0a8cac86a6d3")
API_KEY      = os.environ["APPINSIGHTS_API_KEY"]
GH_TOKEN     = os.environ["GITHUB_TOKEN"]
GH_REPO      = os.environ["GITHUB_REPOSITORY"]   # e.g. "StefanMaron/BusinessCentral.AL.Runner"
DRY_RUN      = os.environ.get("DRY_RUN", "0") == "1"

AI_QUERY_URL = f"https://api.applicationinsights.io/v1/apps/{APP_ID}/query"
GH_API_BASE  = "https://api.github.com"
ISSUE_LABEL  = "telemetry"

# ─── Helpers ──────────────────────────────────────────────────────────────────

def http_get(url, headers):
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read().decode())

def http_post(url, headers, body):
    data = json.dumps(body).encode()
    headers = {**headers, "Content-Type": "application/json"}
    req = urllib.request.Request(url, data=data, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        print(f"  HTTP {e.code}: {e.read().decode()}", file=sys.stderr)
        raise

def http_patch(url, headers, body):
    data = json.dumps(body).encode()
    headers = {**headers, "Content-Type": "application/json"}
    req = urllib.request.Request(url, data=data, headers=headers, method="PATCH")
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        print(f"  HTTP {e.code}: {e.read().decode()}", file=sys.stderr)
        raise

def gh_headers():
    return {
        "Authorization": f"Bearer {GH_TOKEN}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }

# ─── Step 1: Determine time window ────────────────────────────────────────────

def get_from_time() -> str:
    """
    Return ISO-8601 UTC timestamp to query from.
    Priority: FROM_TIME env var > last successful run of this workflow > 24h ago.
    """
    if from_time_env := os.environ.get("FROM_TIME"):
        print(f"Using FROM_TIME from environment: {from_time_env}")
        return from_time_env

    # Try to get the completion time of the last successful run of this workflow.
    # GITHUB_WORKFLOW_REF has the form "owner/repo/.github/workflows/file.yml@refs/..."
    current_run_id = str(os.environ.get("GITHUB_RUN_ID", ""))
    workflow_file = os.environ.get("GITHUB_WORKFLOW_REF", "").split("@")[0].split("/")[-1]
    if workflow_file:
        try:
            url = (
                f"{GH_API_BASE}/repos/{GH_REPO}/actions/workflows/{workflow_file}/runs"
                f"?conclusion=success&per_page=5"
            )
            data = http_get(url, gh_headers())
            runs = [r for r in data.get("workflow_runs", []) if str(r.get("id", "")) != current_run_id]
            if runs:
                last_time = runs[0]["updated_at"]
                print(f"Last successful run completed at: {last_time}")
                return last_time
        except Exception as e:
            print(f"Could not determine last run time: {e}", file=sys.stderr)

    # Default: 24 hours ago
    fallback = (datetime.now(timezone.utc) - timedelta(hours=24)).strftime("%Y-%m-%dT%H:%M:%SZ")
    print(f"Defaulting to 24h lookback: {fallback}")
    return fallback

# ─── Step 2: Query Application Insights ───────────────────────────────────────

KQL = """
exceptions
| where timestamp > datetime({from_time})
| where cloud_RoleName == "al-runner"
| extend ver = tostring(customDimensions["ai.application.ver"])
| extend os  = tostring(customDimensions["os"])
| summarize
    occurrences  = count(),
    first_seen   = min(timestamp),
    last_seen    = max(timestamp),
    versions     = make_set(ver, 20),
    os_list      = make_set(os, 10),
    sample_stack = any(tostring(details)),
    sample_msg   = any(outerMessage)
  by type
| order by occurrences desc
"""

def query_telemetry(from_time: str) -> list[dict]:
    kql = KQL.replace("{from_time}", from_time)
    headers = {"x-api-key": API_KEY, "Accept": "application/json"}
    print(f"Querying Application Insights from {from_time}…")
    data = http_post(AI_QUERY_URL, headers, {"query": kql})

    tables = data.get("tables", [])
    if not tables:
        return []

    table = tables[0]
    cols  = [c["name"] for c in table["columns"]]
    rows  = []
    for row in table["rows"]:
        rows.append(dict(zip(cols, row)))
    return rows

# ─── Step 3: Interact with GitHub Issues ──────────────────────────────────────

def ensure_label_exists():
    url = f"{GH_API_BASE}/repos/{GH_REPO}/labels/{ISSUE_LABEL}"
    try:
        http_get(url, gh_headers())
    except urllib.error.HTTPError as e:
        if e.code == 404:
            if DRY_RUN:
                print(f"[DRY RUN] Would create label '{ISSUE_LABEL}'")
                return
            http_post(
                f"{GH_API_BASE}/repos/{GH_REPO}/labels",
                gh_headers(),
                {"name": ISSUE_LABEL, "color": "d93f0b", "description": "Auto-reported crash from al-runner telemetry"},
            )
            print(f"Created label '{ISSUE_LABEL}'")
        else:
            raise

def find_existing_issue(exception_type: str) -> dict | None:
    """Search for an open or closed issue whose title matches the exception type fingerprint."""
    expected_title = f"[telemetry] {exception_type}"
    # Check open issues first; if not found, check closed (to avoid duplicates on re-occurring crashes).
    for state in ("open", "closed"):
        url = f"{GH_API_BASE}/repos/{GH_REPO}/issues?state={state}&labels={ISSUE_LABEL}&per_page=100"
        try:
            issues = http_get(url, gh_headers())
            for issue in issues:
                if issue.get("title", "").startswith(expected_title):
                    return issue
        except Exception as e:
            print(f"  Warning: could not search {state} issues: {e}", file=sys.stderr)
    return None

def build_issue_body(row: dict, from_time: str) -> str:
    exc_type    = row.get("type", "Unknown")
    sample_msg  = row.get("sample_msg", "")  or "(no message)"
    sample_stk  = row.get("sample_stack", "") or "(no stack)"
    occurrences = row.get("occurrences", 0)
    first_seen  = row.get("first_seen", "")
    last_seen   = row.get("last_seen", "")
    versions    = row.get("versions", [])
    os_list     = row.get("os_list", [])

    ver_str = ", ".join(v for v in versions if v) or "unknown"
    os_str  = ", ".join(o for o in os_list if o) or "unknown"

    return f"""## Crash report: `{exc_type}`

**Occurrences:** {occurrences}  
**First seen:** {first_seen}  
**Last seen:** {last_seen}  
**Versions affected:** {ver_str}  
**OS:** {os_str}  

### Sample message

```
{sample_msg}
```

### Sample stack (runner frames only)

```
{sample_stk}
```

---
*Automatically created by the telemetry triage workflow from Application Insights data since `{from_time}`.*  
*Only `AlRunner.*` stack frames are collected — no AL source code or user file paths.*
<!-- telemetry-fingerprint: {exc_type} -->
"""

def build_update_comment(row: dict, from_time: str) -> str:
    exc_type    = row.get("type", "Unknown")
    occurrences = row.get("occurrences", 0)
    first_seen  = row.get("first_seen", "")
    last_seen   = row.get("last_seen", "")
    versions    = row.get("versions", [])
    sample_msg  = row.get("sample_msg", "") or "(no message)"
    sample_stk  = row.get("sample_stack", "") or "(no stack)"
    ver_str     = ", ".join(v for v in versions if v) or "unknown"
    now         = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    return f"""### Triage update — {now}

**New occurrences since `{from_time}`:** {occurrences}  
**First seen:** {first_seen} | **Last seen:** {last_seen}  
**Versions:** {ver_str}  

**Sample message:** `{sample_msg}`

```
{sample_stk}
```
"""

def create_issue(row: dict, from_time: str):
    exc_type = row.get("type", "Unknown")
    title    = f"[telemetry] {exc_type}"
    body     = build_issue_body(row, from_time)

    if DRY_RUN:
        print(f"  [DRY RUN] Would create issue: {title} ({row.get('occurrences')} occurrence(s))")
        return

    result = http_post(
        f"{GH_API_BASE}/repos/{GH_REPO}/issues",
        gh_headers(),
        {"title": title, "body": body, "labels": [ISSUE_LABEL]},
    )
    print(f"  Created issue #{result['number']}: {title}")

def update_issue(issue: dict, row: dict, from_time: str):
    issue_number = issue["number"]
    issue_state  = issue.get("state", "open")
    comment_body = build_update_comment(row, from_time)

    if DRY_RUN:
        reopen_note = " + reopen" if issue_state == "closed" else ""
        print(f"  [DRY RUN] Would comment{reopen_note} on issue #{issue_number}: {issue['title']} (+{row.get('occurrences')} occurrence(s))")
        return

    # Reopen the issue if it was previously closed (crash re-occurring).
    if issue_state == "closed":
        http_patch(
            f"{GH_API_BASE}/repos/{GH_REPO}/issues/{issue_number}",
            gh_headers(),
            {"state": "open"},
        )
        print(f"  Reopened issue #{issue_number}: {issue['title']}")

    http_post(
        f"{GH_API_BASE}/repos/{GH_REPO}/issues/{issue_number}/comments",
        gh_headers(),
        {"body": comment_body},
    )
    print(f"  Updated issue #{issue_number}: {issue['title']} (+{row.get('occurrences')} occurrence(s))")

# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    from_time = get_from_time()
    to_time   = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    print(f"Time window: {from_time} → {to_time}")
    print()

    rows = query_telemetry(from_time)
    if not rows:
        print("No exceptions found in the time window. Nothing to do.")
        return

    total_occurrences = sum(r.get("occurrences", 0) for r in rows)
    print(f"Found {len(rows)} unique exception type(s), {total_occurrences} total occurrence(s).")
    print()

    ensure_label_exists()

    created = 0
    updated = 0

    for row in rows:
        exc_type = row.get("type", "Unknown")
        count    = row.get("occurrences", 0)
        print(f"  {exc_type} ({count}×)")

        existing = find_existing_issue(exc_type)
        if existing:
            update_issue(existing, row, from_time)
            updated += 1
        else:
            create_issue(row, from_time)
            created += 1

    print()
    print(f"Done. Created {created} issue(s), updated {updated} issue(s).")
    if DRY_RUN:
        print("[DRY RUN] No changes were made.")

if __name__ == "__main__":
    main()
