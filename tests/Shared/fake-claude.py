#!/usr/bin/env python3
"""A stand-in for `claude -p --input-format stream-json --output-format stream-json`, for avamux's tests.

It speaks the same protocol Claude Code does (as observed with 2.1.x): control requests (initialize, interrupt,
set_model), can_use_tool permission prompts and AskUserQuestion through --permission-prompt-tool stdio, stream events
with text and thinking deltas, assistant/user messages with tool use, and a result with usage. What it does depends on
the message text: "ask" asks a question, "write" asks to use Write, "slow" writes slowly until interrupted, "fail"
ends with an error; anything else is echoed back. Its arguments are appended to $FAKE_AGENT_LOG, one JSON list a line.
"""
import json, os, sys, time, uuid, threading

args = sys.argv[1:]
log = os.environ.get("FAKE_AGENT_LOG")
if log:
    with open(log, "a") as f: f.write(json.dumps(args) + "\n")

def arg(name):
    return args[args.index(name) + 1] if name in args else None

session = arg("--session-id") or arg("--resume") or str(uuid.uuid4())
if "--fork-session" in args: session = str(uuid.uuid4())
model = arg("--model") or "default-model"
out_lock = threading.Lock()
def send(o):
    with out_lock:
        sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()

pending = {}      # request_id -> threading.Event, response
interrupted = threading.Event()

def ask(request):
    rid = "req-" + uuid.uuid4().hex[:8]
    ev = threading.Event(); pending[rid] = [ev, None]
    send({"type": "control_request", "request_id": rid, "request": request})
    ev.wait(30)
    return pending.pop(rid)[1]

def stream_text(text, slow=False):
    send({"type": "stream_event", "parent_tool_use_id": None, "event": {"type": "content_block_start", "index": 0, "content_block": {"type": "text"}}})
    for word in text.split(" "):
        if slow and interrupted.is_set(): return False
        send({"type": "stream_event", "parent_tool_use_id": None, "event": {"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": word + " "}}})
        if slow: time.sleep(0.2)
    return True

def assistant(content, uid=None):
    send({"type": "assistant", "uuid": uid or str(uuid.uuid4()), "parent_tool_use_id": None, "session_id": session,
          "message": {"role": "assistant", "content": content, "usage": {"input_tokens": 1000, "cache_read_input_tokens": 500, "cache_creation_input_tokens": 0, "output_tokens": 20}}})

def result(ok=True, subtype="success", text=""):
    send({"type": "result", "subtype": subtype, "is_error": not ok, "result": text, "session_id": session, "total_cost_usd": 0.0012,
          "usage": {"input_tokens": 1000, "output_tokens": 20}, "modelUsage": {model: {"contextWindow": 200000}}})

def turn(text):
    interrupted.clear()
    send({"type": "system", "subtype": "init", "session_id": session, "model": model, "cwd": os.getcwd()})
    if "ask" in text:
        r = ask({"subtype": "can_use_tool", "tool_name": "AskUserQuestion", "input": {"questions": [{"question": "Which colour?", "header": "Colour", "multiSelect": False, "options": [{"label": "Red", "description": "warm"}, {"label": "Blue", "description": "cool"}]}]}})
        answer = (r or {}).get("response", {}).get("updatedInput", {}).get("answers", {}).get("Which colour?", "none")
        stream_text(f"You chose {answer}")
        assistant([{"type": "text", "text": f"You chose {answer}"}]); result(); return
    if "write" in text:
        r = ask({"subtype": "can_use_tool", "tool_name": "Write", "input": {"file_path": "/tmp/x.txt", "content": "hi"}})
        resp = (r or {}).get("response", {})
        if resp.get("behavior") == "allow":
            assistant([{"type": "tool_use", "id": "tool1", "name": "Write", "input": {"file_path": "/tmp/x.txt", "content": "hi"}}])
            send({"type": "user", "parent_tool_use_id": None, "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": "tool1", "content": "File written", "is_error": False}]}})
            stream_text("written")
            assistant([{"type": "text", "text": "written"}]); result(); return
        stream_text("denied: " + resp.get("message", ""))
        assistant([{"type": "text", "text": "denied"}]); result(); return
    if "fail" in text:
        result(False, "error_during_execution", "Something broke"); return
    # Thinking, then the text.
    send({"type": "stream_event", "parent_tool_use_id": None, "event": {"type": "content_block_start", "index": 0, "content_block": {"type": "thinking"}}})
    send({"type": "stream_event", "parent_tool_use_id": None, "event": {"type": "content_block_delta", "index": 0, "delta": {"type": "thinking_delta", "thinking": "Let me think."}}})
    slow = "slow" in text
    done = stream_text(("one two three four five six seven eight nine ten " * 5) if slow else "Echo: " + text, slow)
    if not done:
        result(False, "error_during_execution", ""); return
    assistant([{"type": "text", "text": "Echo: " + text}], uid="assistant-" + uuid.uuid4().hex[:6])
    result()

def reader():
    for line in sys.stdin:
        line = line.strip()
        if not line: continue
        o = json.loads(line)
        if o.get("type") == "control_request":
            sub = o["request"].get("subtype")
            if sub == "interrupt": interrupted.set()
            send({"type": "control_response", "response": {"subtype": "success", "request_id": o["request_id"], "response": {}}})
        elif o.get("type") == "control_response":
            rid = o["response"]["request_id"]
            if rid in pending:
                pending[rid][1] = o["response"]; pending[rid][0].set()
        elif o.get("type") == "user":
            content = o["message"]["content"]
            text = " ".join(c.get("text", "") for c in content if c.get("type") == "text")
            images = sum(1 for c in content if c.get("type") == "image")
            if images and log:
                with open(log, "a") as f: f.write(json.dumps({"images": images}) + "\n")
            threading.Thread(target=turn, args=(text,), daemon=True).start()
    os._exit(0)

reader()
