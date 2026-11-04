#!/usr/bin/env python3
"""Voice command sidecar: speech -> text (whisper.cpp) -> engine /voice endpoint.

Backends (auto-selected, both are local/offline):
  - pywhispercpp   : official whisper.cpp Python bindings (preferred)
  - faster-whisper : CTranslate2 fallback if pywhispercpp is not installed

Usage:
  python tools/voice.py --mic                  # listen loop: speak, pause, it runs
  python tools/voice.py --file clip.wav        # transcribe one file and send it
  python tools/voice.py --mic --engine http://localhost:8080 --model base.en --no-auto
"""

from __future__ import annotations

import argparse
import sys
import time
import urllib.request
import wave
from pathlib import Path

import numpy as np

SAMPLE_RATE = 16000
SILENCE_SECONDS = 1.2
MAX_SECONDS = 12.0
RMS_THRESHOLD = 0.006


def transcribe(wav_path: str, backend, model_name: str) -> str:
    if backend == "pywhispercpp":
        from pywhispercpp.model import Model
        model = Model(model_name)
        segments = model.transcribe(wav_path)
        return " ".join(segment.text for segment in segments).strip()
    if backend == "faster-whisper":
        from faster_whisper import WhisperModel
        model = WhisperModel(model_name, device="cpu", compute_type="int8")
        segments, _ = model.transcribe(wav_path)
        return " ".join(segment.text for segment in segments).strip()
    raise SystemExit(f"unknown backend: {backend}")


def pick_backend(preferred: str | None) -> str:
    if preferred:
        return preferred
    try:
        import pywhispercpp  # noqa: F401
        return "pywhispercpp"
    except ImportError:
        pass
    try:
        import faster_whisper  # noqa: F401
        return "faster-whisper"
    except ImportError:
        pass
    raise SystemExit("No whisper backend installed. Try: pip install pywhispercpp  (or)  pip install faster-whisper")


def send_to_engine(text: str, engine_url: str, auto_run: bool) -> None:
    import json
    import urllib.error
    body = json.dumps({"text": text, "auto": auto_run}).encode()
    request = urllib.request.Request(engine_url + "/voice", data=body,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            print("engine:", response.read().decode())
    except (urllib.error.URLError, OSError) as error:
        print(f"engine not reachable ({error}) - transcript kept in the console only")


def record_utterance() -> str | None:
    """Records from the mic until silence; returns a 16 kHz mono WAV path."""
    import sounddevice as sd

    frames: list[np.ndarray] = []
    voiced: list[np.ndarray] = []
    speaking = False
    quiet = 0.0
    started = time.time()
    print("listening... speak now (pause 1.2s to send, Ctrl+C to quit)")
    with sd.InputStream(samplerate=SAMPLE_RATE, channels=1, dtype="float32", blocksize=800):
        pass
    stream = sd.InputStream(samplerate=SAMPLE_RATE, channels=1, dtype="float32", blocksize=800)
    stream.start()
    try:
        while True:
            data, _ = stream.read(800)
            block = data[:, 0]
            frames.append(block.copy())
            rms = float(np.sqrt(np.mean(np.square(block))))
            if rms > RMS_THRESHOLD:
                speaking = True
                quiet = 0.0
                voiced = list(frames)
            elif speaking:
                quiet += len(block) / SAMPLE_RATE
                if quiet >= SILENCE_SECONDS:
                    break
            if time.time() - started > MAX_SECONDS and speaking:
                break
            if not speaking and time.time() - started > MAX_SECONDS:
                stream.stop()
                stream.close()
                return None
    finally:
        stream.stop()
        stream.close()

    if not voiced:
        return None
    audio = np.concatenate(voiced).astype(np.float32)
    out_path = Path(__file__).parent / "utterance.wav"
    with wave.open(str(out_path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(SAMPLE_RATE)
        handle.writeframes((np.clip(audio, -1, 1) * 32767).astype(np.int16).tobytes())
    return str(out_path)


def main() -> int:
    parser = argparse.ArgumentParser(description="Voice commands for the robot platform")
    parser.add_argument("--mic", action="store_true", help="Listen to the microphone in a loop")
    parser.add_argument("--file", help="Transcribe one WAV file and send it")
    parser.add_argument("--engine", default="http://localhost:8080")
    parser.add_argument("--model", default="tiny.en", help="Whisper model (tiny.en/base.en/...)")
    parser.add_argument("--backend", choices=("pywhispercpp", "faster-whisper"), default=None)
    parser.add_argument("--no-auto", action="store_true", help="Fill the command box but do not auto-run")
    args = parser.parse_args()

    backend = pick_backend(args.backend)
    auto = not args.no_auto
    print(f"voice sidecar ready | backend={backend} model={args.model} engine={args.engine}")

    if args.file:
        text = transcribe(args.file, backend, args.model)
        print("transcript:", text)
        if text:
            send_to_engine(text, args.engine, auto)
        return 0

    while args.mic:
        wav = record_utterance()
        if wav is None:
            continue
        print("transcribing...")
        started = time.time()
        text = transcribe(wav, backend, args.model)
        print(f"transcript ({time.time() - started:.1f}s):", text)
        if text:
            send_to_engine(text, args.engine, auto)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
