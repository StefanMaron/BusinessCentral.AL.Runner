#!/usr/bin/env python3
"""
telemetry-triage: Query Application Insights for al-runner crash reports,
use GitHub Copilot to extract individual distinct problems, and create
focused GitHub Issues — one per problem, skipping already-tracked ones.

Required environment variables:
  APPINSIGHTS_API_KEY   Application Insights API key (read access)
  GITHUB_TOKEN          GitHub token with issues:write and models:read
  GITHUB_REPOSITORY     owner/repo (set automatically by Actions)

Optional:
  APPINSIGHTS_APP_ID    Application Insights application ID
  FROM_TIME             ISO-8601 UTC timestamp to query from (overrides last-run detection)
  DRY_RUN               Set to "1" to print what would happen without creating issues
"""

import json
import os
import sys
import urllib.request
import urllib.error
from datetime import datetime, timezone, timedelta

# ─── Config ───────────────────────────────────────────────────────────────────

APP_ID       = os.environ.get("APPINSIGHTS_APP_ID", "3986aa86-ec55-4392-a3fc-0a8cac86a6d3")
API_KEY      = os.environ["APPINSIGHTS_API_KEY"]
GH_TOKEN     = os.environ["GITHUB_TOKEN"]
GH_REPO      = os.environ["GITHUB_REPOSITORY"]
DRY_RUN      = os.environ.get("DRY_RUN", "0") == "1"

AI_QUERY_URL    = f"https://api.applicationinsights.io/v1/apps/{APP_ID}/query"
GH_API_BASE     = "https://api.github.com"
GH_MODELS_URL   = "https://models.github.ai/inference/chat/completions"
COPILOT_MODEL   = "openai/gpt-4o-mini"
ISSUE_LABEL     = "telemetry"

# ─── HTTP helpers ─────────────────────────────────────────────────────────────

def http_get(url, headers):
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read().decode())

def http_post(url, headers, body):
    data = json.dumps(body).encode()
    headers = {**headers, "Content-Type": "application/json"}
    req = urllib.request.Request(url, data=data, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
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
    if from_time_env := os.environ.get("FROM_TIME"):
        print(f"Using FROM_TIME from environment: {from_time_env}")
        return from_time_env

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
  by type, outerMessage
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
        r = dict(zip(cols, row))
        # make_set returns dynamic arrays; parse if they came back as strings
        for field in ("versions", "os_list"):
            if isinstance(r.get(field), str):
                try:
                    r[field] = json.loads(r[field])
                except Exception:
                    r[field] = []
        rows.append(r)
    return rows

# ─── Step 3: Fetch existing issues for deduplication ──────────────────────────

def fetch_all_open_issues() -> list[dict]:
    """Return all open issues (title + number + body excerpt) for Copilot matching."""
    issues = []
    page = 1
    while True:
        url = f"{GH_API_BASE}/repos/{GH_REPO}/issues?state=open&per_page=100&page={page}"
        try:
            batch = http_get(url, gh_headers())
            if not batch:
                break
            issues.extend(batch)
            if len(batch) < 100:
                break
            page += 1
        except Exception as e:
            print(f"Warning: could not fetch issues page {page}: {e}", file=sys.stderr)
            break
    return issues

# ─── Step 4: Copilot analysis ─────────────────────────────────────────────────

SYSTEM_PROMPT = """You are a triage assistant for al-runner, a Business Central AL unit test runner.
al-runner transpiles AL source to C# and compiles it in-memory. It mocks BC runtime types (NavRecordHandle,
NavInStream, NavOutStream, NavDialog, etc.) so tests can run without a BC service tier.

Your job: given raw telemetry crash reports and a list of existing GitHub issues, extract every distinct
technical problem and classify it.

Known limitations (by design — do NOT file issues for these):
- Report, XMLPort — not supported, by design
- HTTP — not supported, by design
- Events/subscribers — not supported, by design
- Page variables other than TestPage — partially supported via MockFormHandle

Rules:
1. Extract one entry per distinct root cause (not one per exception type — a single exception can embed many problems).
2. For each problem, check the existing issues list. Match semantically, not just by keyword.
3. If already tracked: set "existing_issue_number" to the issue number (integer).
4. If genuinely new and actionable: set "existing_issue_number" to null.
5. Skip known-by-design limitations entirely — do not include them at all.
6. Titles must be concise and technical (under 80 chars). No "telemetry:" prefix.
7. Do NOT generate a body — the script builds the body from raw telemetry data.
8. Set "source_exception_types" to the list of exception type strings this problem came from.

Return ONLY valid JSON matching this schema (no markdown, no explanation):
{
  "problems": [
    {
      "title": "string",
      "existing_issue_number": null or integer,
      "source_exception_types": ["string"]
    }
  ]
}"""

def analyze_with_copilot(rows: list[dict], existing_issues: list[dict]) -> list[dict]:
    """Call GitHub Copilot to classify problems. Returns list of classified problems."""

    issues_summary = "\n".join(
        f"#{i['number']}: {i['title']}"
        for i in existing_issues
    )

    # For classification we only need the type + message + first ~1000 chars of stack
    summary_rows = []
    for r in rows:
        summary_rows.append({
            "type":        r.get("type"),
            "occurrences": r.get("occurrences"),
            "sample_msg":  r.get("sample_msg", "")[:1000],
            "sample_stack": r.get("sample_stack", "")[:1000],
        })

    user_content = f"""Existing open GitHub issues:
{issues_summary}

Telemetry crash reports (summarised for classification):
{json.dumps(summary_rows, indent=2)}

Classify each distinct problem. Return JSON only."""

    headers = {
        "Authorization": f"Bearer {GH_TOKEN}",
        "Content-Type": "application/json",
    }
    body = {
        "model": COPILOT_MODEL,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user",   "content": user_content},
        ],
        "response_format": {"type": "json_object"},
        "temperature": 0,
    }

    print("Calling GitHub Copilot for problem classification…")
    result = http_post(GH_MODELS_URL, headers, body)
    content = result["choices"][0]["message"]["content"]

    try:
        return json.loads(content).get("problems", [])
    except json.JSONDecodeError as e:
        print(f"Copilot returned invalid JSON: {e}\n{content}", file=sys.stderr)
        return []


def build_body(problem: dict, rows: list[dict], from_time: str) -> str:
    """Build a structured issue/comment body from raw telemetry rows."""
    source_types = set(problem.get("source_exception_types", []))
    # Fall back to all rows if Copilot didn't populate source_exception_types
    relevant = [r for r in rows if r.get("type") in source_types] if source_types else rows

    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    sections = [f"*Telemetry update — {now} | window: since `{from_time}`*\n"]

    for r in relevant:
        exc_type    = r.get("type", "Unknown")
        occurrences = r.get("occurrences", 0)
        first_seen  = r.get("first_seen", "")
        last_seen   = r.get("last_seen", "")
        versions    = r.get("versions", [])
        os_list     = r.get("os_list", [])
        sample_msg  = r.get("sample_msg", "") or "(no message)"
        sample_stack = r.get("sample_stack", "") or "(no stack)"

        ver_str = ", ".join(v for v in versions if v) or "unknown"
        os_str  = ", ".join(o for o in os_list  if o) or "unknown"

        sections.append(
            f"### `{exc_type}`\n\n"
            f"**Occurrences:** {occurrences} &nbsp;|&nbsp; "
            f"**First seen:** {first_seen} &nbsp;|&nbsp; "
            f"**Last seen:** {last_seen}  \n"
            f"**Versions:** {ver_str}  \n"
            f"**OS:** {os_str}  \n\n"
            f"**Message:**\n```\n{sample_msg}\n```\n\n"
            f"**Stack / details:**\n```\n{sample_stack}\n```\n"
        )

    sections.append(
        "\n---\n*Only `AlRunner.*` stack frames are collected — "
        "no AL source code or user file paths.*"
    )
    return "\n".join(sections)

# ─── Step 5: GitHub issue management ──────────────────────────────────────────

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

def create_issue(title: str, body: str):
    if DRY_RUN:
        print(f"  [DRY RUN] Would create: {title}")
        return
    result = http_post(
        f"{GH_API_BASE}/repos/{GH_REPO}/issues",
        gh_headers(),
        {"title": title, "body": body, "labels": [ISSUE_LABEL]},
    )
    print(f"  Created issue #{result['number']}: {title}")

def comment_on_issue(issue_number: int, title: str, body: str):
    # Reopen if closed
    try:
        issue = http_get(f"{GH_API_BASE}/repos/{GH_REPO}/issues/{issue_number}", gh_headers())
        if issue.get("state") == "closed":
            if DRY_RUN:
                print(f"  [DRY RUN] Would reopen #{issue_number}")
            else:
                http_patch(
                    f"{GH_API_BASE}/repos/{GH_REPO}/issues/{issue_number}",
                    gh_headers(),
                    {"state": "open"},
                )
                print(f"  Reopened #{issue_number}")
    except Exception as e:
        print(f"  Warning: could not check state of #{issue_number}: {e}", file=sys.stderr)

    if DRY_RUN:
        print(f"  [DRY RUN] Would comment on #{issue_number}: {title}")
        return
    http_post(
        f"{GH_API_BASE}/repos/{GH_REPO}/issues/{issue_number}/comments",
        gh_headers(),
        {"body": body},
    )
    print(f"  Commented on #{issue_number}: {title}")

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

    total = sum(r.get("occurrences", 0) for r in rows)
    print(f"Found {len(rows)} exception type(s), {total} total occurrence(s).")
    print()

    existing_issues = fetch_all_open_issues()
    print(f"Fetched {len(existing_issues)} open issue(s) for deduplication.")
    print()

    problems = analyze_with_copilot(rows, existing_issues)
    if not problems:
        print("Copilot found no actionable new problems.")
        return

    print(f"Copilot identified {len(problems)} problem(s):")
    print()

    ensure_label_exists()

    created = 0
    commented = 0

    for p in problems:
        title    = p.get("title", "Unknown problem")
        existing = p.get("existing_issue_number")
        body     = build_body(p, rows, from_time)

        if existing:
            print(f"  Already tracked in #{existing}: {title}")
            comment_on_issue(existing, title, body)
            commented += 1
        else:
            print(f"  New: {title}")
            create_issue(title, body)
            created += 1

    print()
    print(f"Done. Created {created} issue(s), commented on {commented} existing issue(s).")
    if DRY_RUN:
        print("[DRY RUN] No changes were made.")

if __name__ == "__main__":
    main()
