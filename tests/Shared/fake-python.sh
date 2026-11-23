#!/bin/sh
# Python with the stand-in kokoro_onnx (tests/Shared/fake-kokoro) on its path, for avamux's speech tests.
here=$(dirname "$0")
PYTHONPATH="$here/fake-kokoro${PYTHONPATH:+:$PYTHONPATH}" exec python3 "$@"
