"""Behavioral evals against a running Leona backend and a real Ollama model.

Each case starts a run, follows its events, answers approvals as the case says and scores
the result with simple automatic checks. Results are written as JSON so runs can be compared.

    python3 tests/evals/run_evals.py --model qwen3.5:4b
    python3 tests/evals/run_evals.py --only web-direct-url,files-list --skip-web
    python3 tests/evals/run_evals.py --baseline tests/evals/results/<earlier>.json
"""

import argparse
import io
import json
import pathlib
import re
import shutil
import sys
import time
import urllib.error
import urllib.request
import zipfile
from datetime import datetime, timezone

HERE = pathlib.Path(__file__).resolve().parent
SWEDISH = {"och", "att", "det", "är", "som", "för", "med", "på", "inte", "ett", "jag", "du", "kan", "har", "av", "till", "den", "om", "eller"}
ENGLISH = {"the", "and", "is", "to", "of", "that", "for", "with", "you", "it", "are", "can", "this", "be", "or", "in", "on", "an"}


class Api:
    def __init__(self, base):
        self.base = base.rstrip("/") + "/api"

    def call(self, path, method="GET", data=None, timeout=30):
        body = json.dumps(data).encode() if data is not None else None
        request = urllib.request.Request(self.base + path, data=body, method=method, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                raw = response.read()
                return response.status, json.loads(raw) if raw and "json" in response.headers.get("Content-Type", "") else None
        except urllib.error.HTTPError as error:
            return error.code, None

    def events(self, run_id, timeout):
        """Yields run events until the run finishes. Reconnects with Last-Event-ID if the stream drops."""
        last = "0"
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            request = urllib.request.Request(f"{self.base}/runs/{run_id}/events", headers={"Last-Event-ID": last})
            try:
                with urllib.request.urlopen(request, timeout=max(5, deadline - time.monotonic())) as response:
                    for raw in response:
                        line = raw.decode().rstrip("\n")
                        if line.startswith("id: "):
                            last = line[4:]
                        elif line.startswith("data: "):
                            event = json.loads(line[6:])
                            yield event
                            if event["type"] == "run_finished":
                                return
            except (urllib.error.URLError, TimeoutError):
                time.sleep(1)
        raise TimeoutError("run did not finish in time")


def make_pdf(pages):
    """A minimal text PDF (Helvetica, one text block per page) without third-party libraries."""
    def escape(line):
        return line.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")

    page_ids = [4 + 2 * i for i in range(len(pages))]
    objects = {
        1: b"<< /Type /Catalog /Pages 2 0 R >>",
        2: f"<< /Type /Pages /Kids [{' '.join(f'{p} 0 R' for p in page_ids)}] /Count {len(pages)} >>".encode(),
        3: b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    }
    for page_id, text in zip(page_ids, pages):
        lines = " ".join(f"({escape(line)}) Tj T*" for line in text.split("\n"))
        stream = f"BT /F1 12 Tf 16 TL 50 750 Td {lines} ET".encode("latin-1")
        objects[page_id] = (f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                            f"/Resources << /Font << /F1 3 0 R >> >> /Contents {page_id + 1} 0 R >>").encode()
        objects[page_id + 1] = b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream"
    out = bytearray(b"%PDF-1.4\n")
    offsets = {}
    for number in sorted(objects):
        offsets[number] = len(out)
        out += f"{number} 0 obj\n".encode() + objects[number] + b"\nendobj\n"
    xref = len(out)
    out += f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode()
    for number in sorted(objects):
        out += f"{offsets[number]:010d} 00000 n \n".encode()
    out += f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    return bytes(out)


def make_docx(paragraphs):
    """A minimal Word document; lines starting with # become Heading 1."""
    body = ""
    for text in paragraphs:
        style = '<w:pPr><w:pStyle w:val="Heading1"/></w:pPr>' if text.startswith("# ") else ""
        text = text.removeprefix("# ").replace("&", "&amp;").replace("<", "&lt;")
        body += f"<w:p>{style}<w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>"
    xml = f'<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>'
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        archive.writestr("word/document.xml", xml)
    return buffer.getvalue()


def write_fixtures(workspace, case, stamp):
    """Writes a case's files into the backend workspace and returns the top-level folders created."""
    created = set()
    for relative, content in case.get("fixtures", {}).items():
        target = workspace / relative.replace("{stamp}", stamp)
        target.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, dict) and "pdf" in content:
            target.write_bytes(make_pdf(content["pdf"]))
        elif isinstance(content, dict) and "docx" in content:
            target.write_bytes(make_docx(content["docx"]))
        else:
            target.write_text(content, encoding="utf-8")
        created.add(workspace / pathlib.Path(relative.replace("{stamp}", stamp)).parts[0] / pathlib.Path(relative.replace("{stamp}", stamp)).parts[1])
    return created


def language(text):
    words = re.findall(r"[a-zåäö]+", text.lower())
    swedish = sum(word in SWEDISH for word in words) + 2 * len(re.findall(r"[åäö]", text.lower()))
    english = sum(word in ENGLISH for word in words)
    if swedish == english == 0:
        return "unknown"
    return "sv" if swedish > english else "en"


def hosts(text):
    return {match.lower().removeprefix("www.") for match in re.findall(r"https?://([^/\s)\]>\"']+)", text)}


def run_case(api, case, model, capabilities, timeout, stamp):
    text = case["text"].replace("{stamp}", stamp)
    status, conversation = api.call("/conversations", "POST")
    if status != 200:
        raise RuntimeError(f"could not create a conversation ({status})")
    started = time.monotonic()
    status, run = api.call("/runs", "POST", {
        "conversationId": conversation["id"],
        "input": {
            "text": text,
            "model": model,
            "think": bool(case.get("think")) and capabilities["thinking"],
            "web": bool(case.get("web")),
            "files": bool(case.get("files")),
            "commands": bool(case.get("commands")),
            # Personal cases use the real connected accounts; they only read, and every change is declined.
            "accounts": bool(case.get("accounts")),
        },
    })
    if status != 202:
        raise RuntimeError(f"could not start the run ({status})")
    events = []
    try:
        for event in api.events(run["id"], timeout):
            events.append(event)
            if event["type"] == "approval_required":
                api.call(f"/runs/{run['id']}/approvals/{event['approvalId']}", "POST", {"approve": bool(case.get("approve"))})
    except TimeoutError:
        api.call(f"/runs/{run['id']}/cancel", "POST")
        events.append({"type": "run_finished", "status": "timeout"})
    seconds = time.monotonic() - started
    _, messages = api.call(f"/conversations/{conversation['id']}/messages")
    answer = next((m["content"] for m in reversed(messages or []) if m["role"] == "assistant"), "")
    return conversation["id"], run["id"], events, answer, seconds


def score(case, events, answer, workspace=None, stamp=""):
    expect = case.get("expect", {})
    started = [e for e in events if e["type"] == "tool_started"]
    finished = {e["id"]: e for e in events if e["type"] == "tool_finished"}
    called = [e["name"] for e in started]
    status = next((e["status"] for e in events if e["type"] == "run_finished"), "unknown")
    body = answer.split("\n### Sources")[0]
    lowered = body.lower()
    checks = {
        "completed": status == "completed",
        "no_error": not any(e["type"] == "error" for e in events),
        "not_truncated": not any(e["type"] == "truncated" for e in events),
        "answered": len(body.strip()) > 0,
    }
    if "tools" in expect:
        checks["tools"] = all(name in called for name in expect["tools"])
    if expect.get("no_tools") is True:
        checks["no_tools"] = not called
    elif "no_tools" in expect:
        checks["no_tools"] = not any(name in called for name in expect["no_tools"])
    if expect.get("read_before_answer"):
        checks["read_before_answer"] = any(e["name"] == "read_page" and finished.get(e["id"], {}).get("status") == "completed" for e in started)
    if "language" in expect:
        # A few words such as "Fil skapad: notes.md" carry no language signal; only longer answers must match.
        detected = language(body)
        checks["language"] = detected == expect["language"] or (
            detected == "unknown" and len(re.findall(r"[a-zåäö]+", body.lower())) < 8)
    if "contains_any" in expect:
        checks["contains_any"] = any(term.lower() in lowered for term in expect["contains_any"])
    if "contains_all" in expect:
        checks["contains_all"] = all(term.lower() in lowered for term in expect["contains_all"])
    for name, wanted in expect.get("action_status", {}).items():
        statuses = [finished.get(e["id"], {}).get("status") for e in started if e["name"] == name]
        checks[f"{name}_{wanted}"] = wanted in statuses
    for name, wanted_args in expect.get("tool_args", {}).items():
        # Some call of the tool must have each argument containing the given text (case-insensitive).
        calls = [e.get("arguments") or {} for e in started if e["name"] == name]
        checks[f"{name}_args"] = any(
            all(str(wanted).lower() in str(arguments.get(key, "")).lower() for key, wanted in wanted_args.items())
            for arguments in calls)
    if expect.get("draft_cards"):
        checks["draft_cards"] = any((e.get("detail") or {}).get("drafts") for e in finished.values())
    for relative, wanted in expect.get("file_contains", {}).items():
        target = workspace / relative.replace("{stamp}", stamp) if workspace else None
        checks["file_contains"] = bool(target and target.exists() and wanted in target.read_text(encoding="utf-8"))
    if case.get("web"):
        # Every link the model writes itself must point to a host a tool actually returned.
        seen = set()
        for event in started:
            seen |= hosts(json.dumps(event.get("arguments", {})))
        for event in finished.values():
            seen |= hosts(json.dumps(event.get("detail", {}))) | hosts(json.dumps(event.get("sources", [])))
        checks["no_invented_urls"] = hosts(body) <= seen
    usage = [e for e in events if e["type"] == "usage"]
    eval_tokens = sum(e.get("evalTokens") or 0 for e in usage)
    eval_ms = sum(e.get("evalDurationMs") or 0 for e in usage)
    return checks, {
        "status": status,
        "tools": called,
        "rounds": len(usage),
        "promptTokens": max((e.get("promptTokens") or 0 for e in usage), default=0),
        "outputTokens": eval_tokens,
        "tokensPerSecond": round(eval_tokens / (eval_ms / 1000), 1) if eval_ms else None,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", default="http://127.0.0.1:5080", help="backend URL (default: %(default)s)")
    parser.add_argument("--model", help="model name; defaults to the first installed model")
    parser.add_argument("--cases", default=str(HERE / "cases.json"))
    parser.add_argument("--only", help="comma-separated case IDs")
    parser.add_argument("--skip-web", action="store_true", help="skip cases that need SearXNG and internet access")
    parser.add_argument("--timeout", type=int, default=300, help="seconds per case")
    parser.add_argument("--keep", action="store_true",
                        help="keep eval conversations, fixture files and saved memories instead of deleting them")
    parser.add_argument("--baseline", help="earlier results file to compare against")
    parser.add_argument("--out", default=str(HERE / "results"))
    args = parser.parse_args()

    api = Api(args.base)
    status, models = api.call("/models")
    if status != 200 or not models:
        sys.exit("Cannot list models. Start the backend and Ollama first.")
    model = args.model or models[0]
    _, capabilities = api.call(f"/models/capabilities?model={urllib.request.quote(model)}")
    status, folders = api.call("/folders")
    workspace = pathlib.Path(folders["workspace"]) if status == 200 else None
    _, settings = api.call("/settings")
    memory_enabled = bool(settings and settings.get("memoryEnabled"))
    _, memories_before = api.call("/memories")
    known_memories = {m["id"] for m in memories_before or []}
    _, accounts = api.call("/accounts")
    connected = {a["kind"] for a in accounts or []}
    fixture_folders = set()
    cases = json.loads(pathlib.Path(args.cases).read_text(encoding="utf-8"))
    if args.only:
        wanted = set(args.only.split(","))
        cases = [c for c in cases if c["id"] in wanted]
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    print(f"Model {model} · {len(cases)} cases · tools={capabilities['tools']} thinking={capabilities['thinking']}\n")

    results = []
    for case in cases:
        requires = set(case.get("requires", []))
        skipped = ("web" in requires and args.skip_web) or ("tools" in requires and not capabilities["tools"]) or (
            "thinking" in requires and not capabilities["thinking"]) or ("memory" in requires and not memory_enabled) or (
            "fixtures" in case and workspace is None) or any(
            kind in requires and kind not in connected for kind in ("mail", "calendar", "home"))
        if skipped:
            print(f"SKIP {case['id']}")
            results.append({"id": case["id"], "skipped": True})
            continue
        try:
            if "fixtures" in case:
                fixture_folders |= write_fixtures(workspace, case, stamp)
            conversation_id, run_id, events, answer, seconds = run_case(api, case, model, capabilities, args.timeout, stamp)
            checks, metrics = score(case, events, answer, workspace, stamp)
        except Exception as error:  # A broken case is reported, not fatal for the whole suite.
            print(f"ERROR {case['id']}: {error}")
            results.append({"id": case["id"], "error": str(error), "checks": {"ran": False}})
            continue
        if not args.keep and metrics["status"] != "timeout":
            api.call(f"/conversations/{conversation_id}", "DELETE")
        failed = [name for name, ok in checks.items() if not ok]
        speed = f"{metrics['tokensPerSecond']} tok/s" if metrics["tokensPerSecond"] else "-"
        print(f"{'PASS' if not failed else 'FAIL'} {case['id']:<24} {seconds:6.1f}s  {speed:>12}  tools={','.join(metrics['tools']) or '-'}"
              + (f"  failed={','.join(failed)}" if failed else ""))
        results.append({"id": case["id"], "runId": run_id, "seconds": round(seconds, 1), "checks": checks, "metrics": metrics, "answer": answer[:4000]})

    if not args.keep:
        # Remove fixture files and any memories the eval cases saved.
        for folder in fixture_folders:
            shutil.rmtree(folder, ignore_errors=True)
        _, memories_after = api.call("/memories")
        for memory in memories_after or []:
            if memory["id"] not in known_memories:
                api.call(f"/memories/{memory['id']}", "DELETE")

    scored = [r for r in results if not r.get("skipped")]
    passed_cases = sum(all(r["checks"].values()) for r in scored)
    total_checks = sum(len(r["checks"]) for r in scored)
    passed_checks = sum(sum(r["checks"].values()) for r in scored)
    print(f"\n{passed_cases}/{len(scored)} cases passed · {passed_checks}/{total_checks} checks")

    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    path = out / f"{stamp}-{re.sub(r'[^A-Za-z0-9.-]+', '_', model)}.json"
    path.write_text(json.dumps({"model": model, "base": args.base, "at": stamp, "results": results}, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Results: {path}")

    if args.baseline:
        before = {r["id"]: r for r in json.loads(pathlib.Path(args.baseline).read_text(encoding="utf-8"))["results"]}
        print("\nChanges against baseline:")
        changed = False
        for result in scored:
            old = before.get(result["id"], {}).get("checks", {})
            for name, ok in result["checks"].items():
                if name in old and old[name] != ok:
                    changed = True
                    print(f"  {'fixed ' if ok else 'broken'} {result['id']}: {name}")
        if not changed:
            print("  none")


if __name__ == "__main__":
    main()
