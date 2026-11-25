#!/usr/bin/env python3
"""avamux's Kokoro helper: keeps a Kokoro model loaded and turns sentences into WAV files for avamux to play.

Run on a Python that has kokoro_onnx (and onnxruntime, numpy):
    python kokoro_helper.py --model kokoro-v1.0.int8.onnx --voices voices-v1.0.bin --out /tmp/dir
It reads JSON lines on stdin and answers in JSON lines on stdout:
    {"say": "A sentence.", "id": 3, "voice": "af_nova", "speed": 1.05}  ->  {"wav": "/tmp/dir/3.wav", "id": 3, "seconds": 1.2}
    {"voices": true}                                                     ->  {"voices": ["af_heart", ...]}
    {"cancel": true}   drops sentences not yet made
It prints {"ready": true} once the model is loaded, or {"error": "..."} and exits if it cannot be.
"""
import argparse, json, os, queue, sys, threading, wave

def send(o):
    sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()

parser = argparse.ArgumentParser()
parser.add_argument("--model", required=True)
parser.add_argument("--voices", required=True)
parser.add_argument("--out", required=True)
a = parser.parse_args()

try:
    try:
        import numpy as np
    except ImportError:  # only stand-ins for Kokoro lack numpy (kokoro_onnx itself needs it)
        np = None
    from kokoro_onnx import Kokoro
    kokoro = Kokoro(a.model, a.voices)
except Exception as e:  # noqa: BLE001
    send({"error": f"{type(e).__name__}: {e}"}); sys.exit(1)

os.makedirs(a.out, exist_ok=True)
jobs: "queue.Queue[dict]" = queue.Queue()
generation = 0
lock = threading.Lock()

def english_voices():
    out = []
    for vid in sorted(kokoro.get_voices()):
        if len(vid) >= 4 and vid[0] in "ab" and vid[1] in "fm":
            out.append(vid)
    return out

def worker():
    while True:
        job = jobs.get()
        with lock:
            if job.get("gen") != generation: continue
        text = (job.get("say") or "").strip()
        if not text: continue
        voice = job.get("voice") or "af_nova"
        try:
            lang = "en-gb" if voice.startswith("b") else "en-us"
            samples, rate = kokoro.create(text, voice=voice, speed=float(job.get("speed") or 1.0), lang=lang)
            with lock:
                if job.get("gen") != generation: continue
            if np is not None:
                pcm = (np.clip(samples, -1, 1) * 32767).astype(np.int16).tobytes()
            else:
                import array
                pcm = array.array("h", [int(max(-1.0, min(1.0, x)) * 32767) for x in samples]).tobytes()
            path = os.path.join(a.out, f"{job.get('id', 0)}.wav")
            with wave.open(path, "wb") as w:
                w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate); w.writeframes(pcm)
            send({"wav": path, "id": job.get("id"), "seconds": round(len(pcm) / 2 / rate, 2)})
        except Exception as e:  # noqa: BLE001
            send({"error": f"{type(e).__name__}: {e}", "id": job.get("id")})

threading.Thread(target=worker, daemon=True).start()
send({"ready": True})

for line in sys.stdin:
    line = line.strip()
    if not line: continue
    try: msg = json.loads(line)
    except json.JSONDecodeError: continue
    if msg.get("voices"): send({"voices": english_voices()})
    elif msg.get("cancel"):
        with lock: generation += 1
    elif "say" in msg:
        with lock: msg["gen"] = generation
        jobs.put(msg)
