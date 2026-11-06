#!/usr/bin/env python3
"""Synthesises BDNukes' sound effects into GameData/BDNukes/Sounds (16-bit PCM, 44.1 kHz, mono).

    python3 tools/make_sounds.py

KSP's GameDatabase loads them as "BDNukes/Sounds/<name>" (no extension). Deterministic (fixed seeds), needs numpy.
"""
import os
import wave

import numpy as np

SR = 44100
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "GameData", "BDNukes", "Sounds")


def t_axis(dur):
    return np.arange(int(round(dur * SR))) / SR


def onepole_lp(x, fc):
    """One-pole low-pass at fc Hz (fc may be an array, per sample)."""
    fc = np.broadcast_to(np.asarray(fc, dtype=np.float64), x.shape)
    a = 1.0 - np.exp(-2.0 * np.pi * fc / SR)
    y = np.empty_like(x)
    acc = 0.0
    for i in range(len(x)):
        acc += (x[i] - acc) * a[i]
        y[i] = acc
    return y


def lp(x, fc, order=2):
    for _ in range(order):
        x = onepole_lp(x, fc)
    return x


def hp(x, fc):
    return x - onepole_lp(x, fc)


def bandpass(x, lo, hi, order=2):
    return lp(hp(hp(x, lo), lo), hi, order)


def pink(n, rng):
    """Pink noise by FFT shaping (1/f power)."""
    w = rng.standard_normal(n)
    f = np.fft.rfft(w)
    k = np.arange(len(f), dtype=np.float64)
    k[0] = 1.0
    f /= np.sqrt(k)
    f[0] = 0.0
    y = np.fft.irfft(f, n)
    return y / (np.max(np.abs(y)) + 1e-12)


def brown(n, rng):
    """Brown (1/f^2) noise by FFT shaping, DC removed."""
    w = rng.standard_normal(n)
    f = np.fft.rfft(w)
    k = np.arange(len(f), dtype=np.float64)
    k[0] = 1.0
    f /= k
    f[0] = 0.0
    y = np.fft.irfft(f, n)
    return y / (np.max(np.abs(y)) + 1e-12)


def phase_of(freq):
    """Phase accumulator for a time-varying frequency array."""
    return 2.0 * np.pi * np.cumsum(freq) / SR


def fade(x, fin=0.004, fout=0.02):
    n = len(x)
    a = min(int(fin * SR), n // 2)
    b = min(int(fout * SR), n // 2)
    env = np.ones(n)
    if a > 0:
        env[:a] = np.linspace(0.0, 1.0, a)
    if b > 0:
        env[-b:] = np.linspace(1.0, 0.0, b)
    return x * env


def normalize(x, peak=0.89):
    x = x - np.mean(x)
    return x * (peak / (np.max(np.abs(x)) + 1e-12))


def write(name, x):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    pcm = np.clip(np.round(x * 32767.0), -32768, 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    return path


# ------------------------------------------------------------------------------------------------------------


def lightdrive_charge():
    """3 s: a capacitor-bank whine rising in pitch over a 50/100 Hz mains-like hum, swelling to the end."""
    rng = np.random.default_rng(101)
    t = t_axis(3.0)
    u = t / 3.0
    # whine: exponential sweep 300 Hz -> 3.2 kHz with a little vibrato, plus two harmonics
    f = 300.0 * (3200.0 / 300.0) ** (u ** 1.3)
    f *= 1.0 + 0.004 * np.sin(2 * np.pi * 7.0 * t)
    ph = phase_of(f)
    whine = np.sin(ph) + 0.35 * np.sin(2 * ph + 0.3) + 0.12 * np.sin(3 * ph + 1.1)
    whine *= (0.15 + 0.85 * u ** 1.5)
    # hum: 50 Hz fundamental and buzz harmonics, beating slightly as the load rises
    hum = (np.sin(2 * np.pi * 50 * t) + 0.6 * np.sin(2 * np.pi * 100 * t + 0.5)
           + 0.3 * np.sin(2 * np.pi * 150 * t + 1.0) + 0.15 * np.sin(2 * np.pi * 201.5 * t))
    hum *= 0.55 + 0.45 * u
    # crackle of corona discharge, denser towards the end
    crackle = bandpass(rng.standard_normal(len(t)), 2000.0, 9000.0)
    gate = (rng.random(len(t)) < (0.0008 + 0.01 * u ** 2)).astype(float)
    gate = lp(gate, 400.0, 1) * 40.0
    crackle *= np.clip(gate, 0, 1) * u
    x = 0.45 * whine + 0.5 * hum + 0.25 * crackle / (np.max(np.abs(crackle)) + 1e-9)
    x *= np.clip(t / 0.25, 0, 1)
    return fade(normalize(x, 0.85), 0.01, 0.03)


def lightdrive_jump():
    """1.5 s: sharp crack, deep boom, and a tail whose pitch falls away."""
    rng = np.random.default_rng(202)
    t = t_axis(1.5)
    n = len(t)
    # crack: an instant broadband transient with an N-wave
    white = rng.standard_normal(n)
    crack = hp(white, 1500.0) * np.exp(-t / 0.012)
    nw = np.where(t < 0.02, 1.0 - 2.0 * t / 0.02, 0.0)
    crack = 0.7 * crack / (np.max(np.abs(crack)) + 1e-9) + 0.8 * nw
    # boom: sine dropping 95 -> 28 Hz with low-passed noise body
    fb = 28.0 + 67.0 * np.exp(-t / 0.18)
    boom = np.sin(phase_of(fb)) * np.exp(-t / 0.55) * np.clip(t / 0.003, 0, 1)
    body = lp(white, 180.0, 3)
    body = body / (np.max(np.abs(body)) + 1e-9) * np.exp(-t / 0.35)
    # tail: a falling "whoomp" sweep 600 -> 60 Hz with noise filtered along with it
    ft = 60.0 + 540.0 * np.exp(-t / 0.25)
    tail = np.sin(phase_of(ft)) * np.exp(-t / 0.45) * 0.4
    swept = lp(white, 80.0 + 3000.0 * np.exp(-t / 0.15), 2)
    swept = swept / (np.max(np.abs(swept)) + 1e-9) * np.exp(-t / 0.5) * 0.5
    x = 0.55 * crack + 1.0 * boom + 0.5 * body + tail + swept
    x = np.tanh(1.4 * x)  # a little saturation to fatten it
    return fade(normalize(x, 0.9), 0.0005, 0.08)


def rv_release():
    """0.6 s: pyro bolt bang plus a metallic clank of the RV separating."""
    rng = np.random.default_rng(303)
    t = t_axis(0.6)
    n = len(t)
    white = rng.standard_normal(n)
    # pyro: very short broadband pop, then a dull thunk
    pop = hp(white, 800.0) * np.exp(-t / 0.004)
    thunk = np.sin(phase_of(70.0 + 110.0 * np.exp(-t / 0.02))) * np.exp(-t / 0.07)
    thunk += 0.6 * lp(white, 400.0, 2) / 0.05 * np.exp(-t / 0.05) * 0.02
    # clank: inharmonic metal modes, struck ~25 ms later
    tc = np.clip(t - 0.025, 0, None)
    on = (t >= 0.025).astype(float)
    modes = [(523.0, 0.09, 1.0), (1187.0, 0.06, 0.6), (1931.0, 0.045, 0.45), (2870.0, 0.03, 0.3), (4110.0, 0.02, 0.2)]
    clank = sum(a * np.sin(2 * np.pi * f * tc + i) * np.exp(-tc / d) for i, (f, d, a) in enumerate(modes)) * on
    clank += 0.3 * hp(white, 3000.0) * np.exp(-tc / 0.006) * on
    x = 0.8 * pop / (np.max(np.abs(pop)) + 1e-9) + 1.0 * thunk + 0.45 * clank
    return fade(normalize(x, 0.88), 0.0003, 0.05)


def heavy_motor_loop():
    """4 s seamless loop: deep rocket roar from brown + pink noise, a low rumble, and a slow throb.

    Rendered 1 s longer than the loop; the extra second is crossfaded (equal power) over the loop's start, so the
    end flows straight into the beginning with no click or level dip. Periodic modulations use whole cycles per loop.
    """
    rng = np.random.default_rng(404)
    L, X = 4.0, 1.0
    t = t_axis(L + X)
    n = len(t)
    b = brown(n, rng)
    p = pink(n, rng)
    roar = lp(b, 900.0, 1) * 1.0 + bandpass(p, 120.0, 2500.0) * 0.55
    crackle = bandpass(rng.standard_normal(n), 1500.0, 6000.0)
    # combustion crackle: noise amplitude-modulated by slow random envelope
    env = lp(np.abs(rng.standard_normal(n)), 18.0, 2)
    crackle *= env / (np.max(env) + 1e-9)
    rumble = lp(rng.standard_normal(n), 45.0, 3)
    rumble /= np.max(np.abs(rumble)) + 1e-9
    # throb at whole cycles per loop (2 Hz and 0.75 Hz -> 8 and 3 cycles in 4 s)
    throb = 1.0 + 0.08 * np.sin(2 * np.pi * 2.0 * t) + 0.06 * np.sin(2 * np.pi * 0.75 * t + 1.0)
    x = (roar / (np.max(np.abs(roar)) + 1e-9) + 0.12 * crackle / (np.max(np.abs(crackle)) + 1e-9)
         + 0.7 * rumble) * throb
    nL, nX = int(L * SR), int(X * SR)
    loop = x[:nL].copy()
    tail = x[nL:nL + nX]
    w = np.linspace(0.0, 1.0, nX)
    fin, fout = np.sin(0.5 * np.pi * w), np.cos(0.5 * np.pi * w)
    loop[:nX] = loop[:nX] * fin + tail * fout
    loop = np.tanh(1.2 * loop / (np.max(np.abs(loop)) + 1e-9))
    loop -= np.mean(loop)
    return loop * (0.85 / (np.max(np.abs(loop)) + 1e-12))


def interceptor_divert():
    """0.4 s: a hard RCS hiss burst with a valve snap at the start and a quick cut-off."""
    rng = np.random.default_rng(505)
    t = t_axis(0.4)
    n = len(t)
    white = rng.standard_normal(n)
    hiss = bandpass(white, 1800.0, 11000.0, 2)
    hiss /= np.max(np.abs(hiss)) + 1e-9
    env = np.clip(t / 0.008, 0, 1) * np.where(t < 0.3, 1.0, np.exp(-(t - 0.3) / 0.025))
    env *= 1.0 + 0.25 * np.exp(-t / 0.03)  # initial pressure spike
    body = lp(white, 600.0, 2)
    body /= np.max(np.abs(body)) + 1e-9
    snap = hp(white, 3000.0) * np.exp(-t / 0.002)
    snap /= np.max(np.abs(snap)) + 1e-9
    x = hiss * env + 0.25 * body * env + 0.5 * snap
    return fade(normalize(x, 0.85), 0.0005, 0.01)


SOUNDS = {
    "bdnukes_lightdrive_charge.wav": lightdrive_charge,
    "bdnukes_lightdrive_jump.wav": lightdrive_jump,
    "bdnukes_rv_release.wav": rv_release,
    "bdnukes_heavy_motor_loop.wav": heavy_motor_loop,
    "bdnukes_interceptor_divert.wav": interceptor_divert,
}


def main():
    for name, fn in SOUNDS.items():
        x = fn()
        path = write(name, x)
        with wave.open(path, "rb") as w:
            frames = w.getnframes()
            data = np.frombuffer(w.readframes(frames), dtype="<i2")
            dur = frames / w.getframerate()
        peak = np.max(np.abs(data.astype(np.int32)))
        clipped = int(np.sum(np.abs(data.astype(np.int32)) >= 32767))
        extra = ""
        if "loop" in name:
            jump = abs(int(data[-1]) - int(data[0]))
            step = np.median(np.abs(np.diff(data.astype(np.int32))))
            extra = "  loop seam step %d (median step %d)" % (jump, step)
        print("%-34s %.3f s  peak %5d (%.1f dBFS)  clipped %d%s"
              % (name, dur, peak, 20 * np.log10(peak / 32767.0), clipped, extra))


if __name__ == "__main__":
    main()
