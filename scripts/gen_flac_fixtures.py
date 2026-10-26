#!/usr/bin/env python3
"""Generates FLAC test fixtures with ffmpeg plus the exact reference samples (interleaved int32 LE).
Run from the repo root: python3 scripts/gen_flac_fixtures.py"""
import subprocess, tempfile, wave
from pathlib import Path
import numpy as np

OUT = Path("tests/MaSatellite.Core.Tests/Fixtures")
OUT.mkdir(parents=True, exist_ok=True)
rng = np.random.default_rng(1234)


def signal(rate, channels, bits, seconds=0.3):
    n = int(rate * seconds)
    t = np.arange(n) / rate
    amp = (2 ** (bits - 1)) - 1
    cols = []
    for c in range(channels):
        tone = 0.45 * np.sin(2 * np.pi * (440 * (c + 1)) * t)
        sweep = 0.2 * np.sin(2 * np.pi * (200 + 3000 * t) * t)
        noise = 0.05 * rng.standard_normal(n)
        sig = tone + sweep + noise
        sig[int(n * 0.6):int(n * 0.7)] = 0.0  # silence -> constant subframes
        sig[int(n * 0.8):int(n * 0.85)] = np.sign(np.sin(2 * np.pi * 50 * t[int(n * 0.8):int(n * 0.85)])) * 0.9  # square, near clipping
        cols.append(np.clip(sig, -1, 1) * amp)
    return np.stack(cols, axis=1).astype(np.int64)


def write_wav(path, data, rate, bits):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(data.shape[1]); w.setsampwidth(bits // 8); w.setframerate(rate)
        if bits == 16:
            w.writeframes(data.astype("<i2").tobytes())
        else:  # 24-bit packed
            b = data.astype("<i4").tobytes()
            packed = bytearray()
            for i in range(0, len(b), 4):
                packed += b[i:i + 3]
            w.writeframes(bytes(packed))


CASES = [
    ("s16_44k_st_l0", 44100, 2, 16, ["-compression_level", "0"]),
    ("s16_44k_st_l5", 44100, 2, 16, ["-compression_level", "5"]),
    ("s16_44k_st_l8", 44100, 2, 16, ["-compression_level", "8"]),
    ("s16_44k_st_leftside", 44100, 2, 16, ["-compression_level", "5", "-ch_mode", "left_side"]),
    ("s16_44k_st_rightside", 44100, 2, 16, ["-compression_level", "5", "-ch_mode", "right_side"]),
    ("s16_44k_st_midside", 44100, 2, 16, ["-compression_level", "5", "-ch_mode", "mid_side"]),
    ("s16_44k_st_indep", 44100, 2, 16, ["-compression_level", "5", "-ch_mode", "indep"]),
    ("s16_48k_mono_l5", 48000, 1, 16, ["-compression_level", "5"]),
    ("s16_44k_st_blk1000", 44100, 2, 16, ["-compression_level", "5", "-frame_size", "1000"]),
    ("s16_44k_st_blk4608", 44100, 2, 16, ["-compression_level", "8", "-frame_size", "4608"]),
    ("s24_48k_st_l8", 48000, 2, 24, ["-compression_level", "8", "-sample_fmt", "s32", "-bits_per_raw_sample", "24"]),
    ("s16_96k_st_l5", 96000, 2, 16, ["-compression_level", "5"]),
]

with tempfile.TemporaryDirectory() as tmp:
    for name, rate, ch, bits, extra in CASES:
        data = signal(rate, ch, bits)
        wav = Path(tmp) / f"{name}.wav"
        write_wav(wav, data, rate, bits)
        flac = OUT / f"{name}.flac"
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav), "-c:a", "flac", *extra, str(flac)], check=True)
        (OUT / f"{name}.pcm32").write_bytes(data.astype("<i4").tobytes())
        print(name, flac.stat().st_size, "bytes")
