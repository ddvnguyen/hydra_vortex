#!/usr/bin/env python3
"""Instrumentor agent driver for hydra_vortex orchestration.

Runs MiniCPM5-1B-Agentic-Tooluse (or any small XML tool-calling model) behind
an OpenAI-compatible endpoint (llama-server). The model NEVER does main work:
it fires canary tasks into the Paseo pipeline, observes vitals, and reports.

Implements the model card's runtime contract:
  - tools defined in the prompt, deterministic decoding (temp 0)
  - stop after the first complete </function>
  - validate function name + args against the schema
  - execute the tool OUTSIDE the model, feed result back in a new turn

All side effects go through a hard whitelist below. The model cannot run
arbitrary commands. If the model derails, the driver files a WARN report
itself — the sweep always produces a report.

Config (env):
  LLM_URL          default http://127.0.0.1:8090/v1/chat/completions
  LLM_MODEL        default minicpm5-1b-instrumentor
  REPO_DIR         default: git toplevel of cwd
  CANARY_PROVIDER  default: opencode        (use your tier-3 local provider)
  MAX_STEPS        default 8
"""
import json, os, re, subprocess, sys, time, urllib.request
from datetime import datetime, timezone

LLM_URL = os.environ.get("LLM_URL", "http://127.0.0.1:8090/v1/chat/completions")
LLM_MODEL = os.environ.get("LLM_MODEL", "minicpm5-1b-instrumentor")
REPO_DIR = os.environ.get("REPO_DIR") or subprocess.run(
    ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True
).stdout.strip() or os.getcwd()
CANARY_PROVIDER = os.environ.get("CANARY_PROVIDER", "opencode")
MAX_STEPS = int(os.environ.get("MAX_STEPS", "8"))
STATE = os.path.join(REPO_DIR, "orchestration", "state")
REPORT = os.path.join(STATE, "instrumentor-report.md")
HISTORY = os.path.join(STATE, "instrumentor-history.log")

def sh(cmd, timeout=60):
    try:
        r = subprocess.run(cmd, shell=True, capture_output=True, text=True,
                           timeout=timeout, cwd=REPO_DIR)
        return (r.stdout + r.stderr).strip()[:4000]
    except subprocess.TimeoutExpired:
        return f"(timeout: {cmd})"

# ── Whitelisted tools ────────────────────────────────────────────────────────
def t_get_vitals():
    return sh("bash orchestration/scripts/vitals.sh", timeout=90)

def t_run_canary(kind="echo"):
    name = f"canary-{int(time.time())}"
    if kind == "worktree":
        cmd = (f'paseo run --provider {CANARY_PROVIDER} --worktree canary-scratch '
               f'--detach --name {name} '
               f'"CANARY TASK: append the current date to canary-ping.txt, '
               f'print CANARY_OK, then stop. Do nothing else."')
    else:  # echo — tests spawn/log/attach path without touching the repo
        cmd = (f'paseo run --provider {CANARY_PROVIDER} --detach --name {name} '
               f'"CANARY TASK: print CANARY_OK and the current date, then stop. '
               f'Do not read or edit any files."')
    out = sh(cmd, timeout=60)
    return f"spawned canary agent name={name} kind={kind}\n{out}"

def t_check_canary(name=""):
    if not re.fullmatch(r"canary-\d+", name or ""):
        return "error: name must look like canary-1234567890"
    logs = sh(f"paseo logs {name} --tail 15", timeout=45)
    ok = "CANARY_OK" in logs
    # deterministic cleanup — never leave canaries running
    sh(f"paseo stop {name}", timeout=30)
    sh("git worktree remove --force canary-scratch 2>/dev/null; "
       "git branch -D canary-scratch 2>/dev/null; true", timeout=30)
    return f"canary_ok={ok}\n--- last logs ---\n{logs}"

def t_submit_report(status="WARN", summary="(no summary)"):
    status = status.upper() if status.upper() in ("PASS", "WARN", "FAIL") else "WARN"
    ts = datetime.now(timezone.utc).isoformat(timespec="seconds")
    os.makedirs(STATE, exist_ok=True)
    with open(REPORT, "w") as f:
        f.write(f"# Instrumentor report — {status}\n\ntime: {ts}\n\n{summary}\n")
    with open(HISTORY, "a") as f:
        f.write(f"{ts} {status} {summary.splitlines()[0][:120]}\n")
    if status == "FAIL":
        existing = sh('gh issue list --label source:instrumentor --state open '
                      '--json number --jq ".[0].number"')
        body = f"Automated pipeline probe failed at {ts}.\n\n{summary}"
        if existing.strip().isdigit():
            sh(f'gh issue comment {existing.strip()} --body {json.dumps(body)}')
        else:
            sh('gh issue create --title "Instrumentor: pipeline probe FAIL" '
               f'--label source:instrumentor --body {json.dumps(body)}')
    return "REPORT_SAVED"

TOOLS = {
    "get_vitals":   {"fn": lambda a: t_get_vitals(), "params": []},
    "run_canary":   {"fn": lambda a: t_run_canary(a.get("kind", "echo")),
                     "params": ["kind"]},
    "check_canary": {"fn": lambda a: t_check_canary(a.get("name", "")),
                     "params": ["name"]},
    "submit_report": {"fn": lambda a: t_submit_report(a.get("status", "WARN"),
                                                      a.get("summary", "")),
                      "params": ["status", "summary"]},
}

SYSTEM = """You are the Instrumentor, a monitoring probe for a software pipeline.
You never write code. You may ONLY call these tools, one per turn, XML format:

<function name="get_vitals"></function>
<function name="run_canary"><param name="kind">echo</param></function>
<function name="check_canary"><param name="name">canary-123</param></function>
<function name="submit_report"><param name="status">PASS</param><param name="summary">text</param></function>

Protocol, in order:
1. get_vitals
2. run_canary (kind: echo)
3. check_canary with the exact canary name you were given
4. submit_report. status rules: PASS if canary_ok=True and schedules exist and
   agents are not stuck; WARN if something looks off; FAIL if the canary failed
   or schedules are missing. Summary: 3 short lines a human can read on a phone.
Call exactly one tool per turn. No prose outside the function tag."""

FUNC_RE = re.compile(r'<function name="([\w-]+)">(.*?)</function>', re.S)
PARAM_RE = re.compile(r'<param name="([\w-]+)">(.*?)</param>', re.S)

def llm(messages):
    req = urllib.request.Request(
        LLM_URL, method="POST",
        headers={"Content-Type": "application/json"},
        data=json.dumps({"model": LLM_MODEL, "messages": messages,
                         "temperature": 0, "max_tokens": 400,
                         "stop": ["</function>"]}).encode())
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read())["choices"][0]["message"]["content"] or ""

def main():
    msgs = [{"role": "system", "content": SYSTEM},
            {"role": "user", "content": "Begin the instrumentation sweep."}]
    bad = 0
    for _ in range(MAX_STEPS):
        try:
            out = llm(msgs)
        except Exception as e:
            t_submit_report("WARN", f"Instrumentor LLM unreachable: {e}. "
                            "Sweep aborted; check llama-server."); return
        if "<function" in out and "</function>" not in out:
            out += "</function>"  # we stop-string on the closing tag
        m = FUNC_RE.search(out)
        if not m:
            bad += 1
            if bad >= 2:
                t_submit_report("WARN", "Model failed to emit a valid tool call "
                                "twice; sweep incomplete. Raw vitals attached:\n"
                                + t_get_vitals()[:1500]); return
            msgs.append({"role": "user", "content":
                         "Invalid. Reply with exactly one <function ...></function> call."})
            continue
        name, body = m.group(1), m.group(2)
        args = {k: v.strip() for k, v in PARAM_RE.findall(body)}
        if name not in TOOLS:
            msgs.append({"role": "user", "content":
                         f"Unknown tool '{name}'. Valid: {', '.join(TOOLS)}."})
            continue
        result = TOOLS[name]["fn"](args)
        print(f"[instrumentor] {name}({args}) -> {result[:120]!r}")
        if result == "REPORT_SAVED":
            return
        msgs.append({"role": "assistant", "content": m.group(0)})
        msgs.append({"role": "user", "content": f"Tool result:\n{result}"})
    # Step budget exhausted — the driver reports so the sweep never vanishes
    t_submit_report("WARN", "Step budget exhausted before submit_report; "
                    "model likely looped. Vitals:\n" + t_get_vitals()[:1500])

if __name__ == "__main__":
    sys.exit(main())
