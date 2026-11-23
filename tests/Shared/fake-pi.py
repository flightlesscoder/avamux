#!/usr/bin/env python3
"""A stand-in for `pi --mode rpc`, for avamux's tests. It answers get_state, get_fork_messages, get_session_stats,
set_model, set_thinking_level, fork and abort, and runs a prompt: "ask" asks through an extension UI select with
avamux's question marker, "perm" through its permission marker, "slow" writes slowly until aborted; anything else is
echoed with a thinking block and one tool call. Arguments are appended to $FAKE_AGENT_LOG as a JSON list."""
import json, os, sys, time, uuid, threading

args = sys.argv[1:]
log = os.environ.get("FAKE_AGENT_LOG")
if log:
    with open(log, "a") as f: f.write(json.dumps(args) + "\n")
    if os.environ.get("AVAMUX_PI_CONFIRM"):
        with open(log, "a") as f: f.write(json.dumps({"confirm": os.environ["AVAMUX_PI_CONFIRM"]}) + "\n")

def arg(name):
    return args[args.index(name) + 1] if name in args else None

session_dir = arg("--session-dir") or "/tmp"
session_file = arg("--session") or os.path.join(session_dir, uuid.uuid4().hex + ".jsonl")
if "--fork" in args: session_file = os.path.join(session_dir, "fork-" + uuid.uuid4().hex[:6] + ".jsonl")
prompts = []
lock = threading.Lock()
pending = {}
aborted = threading.Event()

def send(o):
    with lock:
        sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()

def ui(request):
    rid = uuid.uuid4().hex[:8]
    ev = threading.Event(); pending[rid] = [ev, None]
    request.update({"type": "extension_ui_request", "id": rid})
    send(request)
    ev.wait(30)
    return pending.pop(rid)[1] or {}

def run(text):
    aborted.clear()
    send({"type": "agent_start"})
    if "ask" in text:
        r = ui({"method": "select", "title": "[avamux:question]" + json.dumps({"question": "Which colour?", "options": ["Red", "Blue"], "multiSelect": False}), "options": ["Red", "Blue"]})
        reply = "You chose " + str(r.get("value", "nothing"))
    elif "perm" in text:
        r = ui({"method": "select", "title": "[avamux:permission]" + json.dumps({"tool": "bash", "input": {"command": "ls"}}), "options": ["Allow", "Allow for this session", "Deny"]})
        reply = "permission: " + str(r.get("value"))
    else:
        reply = "Echo: " + text
    send({"type": "message_update", "assistantMessageEvent": {"type": "thinking_start", "contentIndex": 0}})
    send({"type": "message_update", "assistantMessageEvent": {"type": "thinking_delta", "contentIndex": 0, "delta": "Thinking it over."}})
    if "ask" not in text and "perm" not in text and "slow" not in text:
        send({"type": "tool_execution_start", "toolCallId": "c1", "toolName": "read", "args": {"path": "Home.md"}})
        send({"type": "tool_execution_end", "toolCallId": "c1", "toolName": "read", "result": {"content": [{"type": "text", "text": "# Home"}]}, "isError": False})
    send({"type": "message_update", "assistantMessageEvent": {"type": "text_start", "contentIndex": 1}})
    words = ("one two three four five six seven eight " * 6).split() if "slow" in text else reply.split(" ")
    for w in words:
        if aborted.is_set(): break
        send({"type": "message_update", "assistantMessageEvent": {"type": "text_delta", "contentIndex": 1, "delta": w + " "}})
        if "slow" in text: time.sleep(0.2)
    send({"type": "agent_end", "messages": [], "willRetry": False})

for line in sys.stdin:
    line = line.strip()
    if not line: continue
    o = json.loads(line)
    t = o.get("type"); rid = o.get("id")
    def respond(data=None, ok=True):
        send({"type": "response", "id": rid, "command": t, "success": ok, "data": data})
    if t == "get_state": respond({"sessionFile": session_file, "sessionId": "s1", "isStreaming": False})
    elif t == "get_fork_messages": respond({"messages": [{"entryId": f"e{i}", "text": p} for i, p in enumerate(prompts)]})
    elif t == "get_session_stats": respond({"contextUsage": {"tokens": 4200, "contextWindow": 92160, "percent": 4.5}, "tokens": {"input": 4000, "output": 200}, "cost": 0})
    elif t == "get_available_models": respond({"models": [{"provider": "Local", "id": "small"}]})
    elif t in ("set_model", "set_thinking_level"):
        if log:
            with open(log, "a") as f: f.write(json.dumps({t: o}) + "\n")
        respond({})
    elif t == "fork":
        if log:
            with open(log, "a") as f: f.write(json.dumps({"fork": o.get("entryId")}) + "\n")
        respond({"text": "", "cancelled": False})
    elif t == "abort": aborted.set(); respond({})
    elif t == "prompt":
        prompts.append(o["message"]); respond({})
        threading.Thread(target=run, args=(o["message"],), daemon=True).start()
    elif t == "extension_ui_response":
        if o["id"] in pending:
            pending[o["id"]][1] = o; pending[o["id"]][0].set()
    else: respond(None, False)
