"""A stand-in for kokoro_onnx, for avamux's tests: silence the length of the text, and a few voice names."""
import os, time

class Kokoro:
    def __init__(self, model, voices):
        if not os.path.exists(model) or not os.path.exists(voices):
            raise FileNotFoundError(f"{model} / {voices}")
        log = os.environ.get("FAKE_KOKORO_LOG")
        if log:
            with open(log, "a") as f: f.write("loaded\n")

    def get_voices(self):
        return ["af_heart", "af_nova", "bm_george", "jf_alpha"]

    def create(self, text, voice="af_nova", speed=1.0, lang="en-us"):
        log = os.environ.get("FAKE_KOKORO_LOG")
        if log:
            with open(log, "a") as f: f.write(f"say {voice} {speed} {text}\n")
        time.sleep(0.05)
        return [0.0] * (240 * max(1, len(text))), 24000
