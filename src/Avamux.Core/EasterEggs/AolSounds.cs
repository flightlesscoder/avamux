using Avamux.Core.Theming;

namespace Avamux.Core.EasterEggs;

/// <summary>
/// The noises of AOL.EXE, synthesised: a modem dialling and shaking hands, a busy line, buddies' doors opening and
/// closing, the instant-message chime, the welcome and new-mail jingles.
/// </summary>
public static class AolSounds
{
    private const int Rate = SoundService.SampleRate;

    /// <summary>Seconds into <see cref="Modem"/> at which each of the connection steps begins.</summary>
    public static readonly double[] ModemSteps = [0, 1.4, 2.95, 4.45, 8.9, 9.8, 10.6, 11.3, 12.2];

    /// <summary>How long the busy-line clip lasts.</summary>
    public const double BusyLength = 6.2;

    private sealed class Buf(double seconds)
    {
        public readonly float[] S = new float[(int)(seconds * Rate)];

        /// <summary>Adds <paramref name="f"/>(t) for t from 0 over <paramref name="length"/> seconds, in order.</summary>
        public void Add(double start, double length, Func<double, double> f)
        {
            var from = (int)(start * Rate);
            var n = (int)(length * Rate);
            for (var i = 0; i < n && from + i < S.Length; i++) S[from + i] += (float)f(i / (double)Rate);
        }

        /// <summary>A sum of sine tones with 5 ms edges, as a phone line makes them.</summary>
        public void Tone(double start, double length, double gain, params double[] freqs) => Add(start, length, t =>
        {
            var edge = Math.Min(1, Math.Min(t, length - t) / 0.005);
            return freqs.Sum(f => Math.Sin(2 * Math.PI * f * t)) / freqs.Length * gain * edge;
        });

        /// <summary>A bell: a few harmonics fading away.</summary>
        public void Bell(double start, double freq, double length, double gain) => Add(start, length, t =>
            (Math.Sin(2 * Math.PI * freq * t) + 0.4 * Math.Sin(2 * Math.PI * freq * 2.01 * t) + 0.15 * Math.Sin(2 * Math.PI * freq * 3.02 * t))
            * Math.Exp(-t * 5) * Math.Min(1, t / 0.003) * gain);

        public float[] Done(bool phoneLine)
        {
            if (phoneLine)
            {
                // A telephone only carries about 300-3400 Hz.
                double lo = 0, hi = 0, prev = 0;
                var aLo = Math.Exp(-2 * Math.PI * 3400.0 / Rate);
                var aHi = Math.Exp(-2 * Math.PI * 300.0 / Rate);
                for (var i = 0; i < S.Length; i++)
                {
                    lo = S[i] * (1 - aLo) + lo * aLo;
                    hi = aHi * (hi + lo - prev);
                    prev = lo;
                    S[i] = (float)Math.Tanh(hi * 1.4);
                }
            }
            var peak = Math.Max(0.001f, S.Max(Math.Abs));
            for (var i = 0; i < S.Length; i++) S[i] *= 0.8f / peak;
            return S;
        }
    }

    private static (double, double) Dtmf(char d) => d switch
    {
        '1' => (697, 1209), '2' => (697, 1336), '3' => (697, 1477),
        '4' => (770, 1209), '5' => (770, 1336), '6' => (770, 1477),
        '7' => (852, 1209), '8' => (852, 1336), '9' => (852, 1477),
        _ => (941, 1336),
    };

    /// <summary>
    /// Picking up, dialling 555-0196 and then either a busy signal or the whole song: ringing, the answer tone,
    /// the chirps, the bongs and the long hiss of two modems agreeing on a speed.
    /// </summary>
    public static float[] Modem(bool busy)
    {
        var b = new Buf(busy ? BusyLength : 11.6);
        var rnd = new Random(1996);
        double Noise() => rnd.NextDouble() * 2 - 1;

        b.Add(0.25, 0.025, _ => Noise() * 0.5);            // off the hook
        b.Tone(0.35, 1.0, 0.22, 350, 440);                 // dial tone
        var at = 1.5;
        foreach (var digit in "5550196")
        {
            var (lo, hi) = Dtmf(digit);
            b.Tone(at, 0.09, 0.3, lo, hi);
            at += 0.15;
        }
        at = 2.95;
        if (busy)
        {
            for (var i = 0; i < 3; i++) b.Tone(at + i, 0.5, 0.2, 480, 620);
            return b.Done(true);
        }
        b.Tone(at, 1.2, 0.18, 440, 480);                   // ringing
        at = 4.45;
        // The answering modem: 2100 Hz, its phase flipping every 450 ms.
        b.Add(at, 1.55, t => 0.3 * Math.Sin(2 * Math.PI * 2100 * t + (int)(t / 0.45) % 2 * Math.PI) * Math.Min(1, t / 0.01));
        at = 6.05;
        // Chirps: the two modems trading capabilities at 300 baud.
        double phase = 0;
        var bit = -1;
        var high = false;
        b.Add(at, 0.7, t =>
        {
            var n = (int)(t * 300);
            if (n != bit) { bit = n; high = rnd.Next(2) == 0; }
            phase += 2 * Math.PI * (high ? 1180 : 980) / Rate;
            return 0.22 * Math.Sin(phase) + 0.12 * Math.Sin(phase * 1.7);
        });
        at = 6.8;
        // Line probing: two bongs, every 150 Hz at once.
        for (var burst = 0; burst < 2; burst++)
            b.Add(at + burst * 0.32, 0.18, t =>
            {
                var sum = 0.0;
                for (var k = 1; k <= 24; k++) sum += Math.Sin(2 * Math.PI * 150 * k * t + k * k);
                return sum / 24 * 0.9 * Math.Min(1, Math.Min(t, 0.18 - t) / 0.004);
            });
        at = 7.5;
        // Training: the screech.
        double sweep = 0, lp = 0;
        b.Add(at, 1.4, t =>
        {
            sweep += 2 * Math.PI * (1500 + 500 * Math.Sin(2 * Math.PI * 2.5 * t)) / Rate;
            lp = lp * 0.6 + Noise() * 0.4;
            return 0.2 * Math.Sin(2 * Math.PI * 1800 * t) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 11 * t)) + 0.14 * Math.Sin(sweep) + 0.22 * lp;
        });
        at = 8.9;
        // Data: hiss, then quiet once the speaker is switched off.
        b.Add(at, 2.4, t => Noise() * 0.32 * (t > 2.0 ? (2.4 - t) / 0.4 : 1));
        return b.Done(true);
    }

    /// <summary>A buddy signing on: a creaky door opening.</summary>
    public static float[] DoorOpen()
    {
        var b = new Buf(0.75);
        double phase = 0;
        b.Add(0, 0.55, t =>
        {
            phase += 2 * Math.PI * (230 + 380 * t / 0.55 + 18 * Math.Sin(2 * Math.PI * 23 * t)) / Rate;
            var wave = Math.Sin(phase) + 0.5 * Math.Sin(phase * 2) + 0.3 * Math.Sin(phase * 3);
            return wave * 0.3 * Math.Min(1, t / 0.04) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 31 * t));
        });
        b.Bell(0.55, 880, 0.2, 0.15);
        return b.Done(false);
    }

    /// <summary>A buddy signing off: a door shutting.</summary>
    public static float[] DoorClose()
    {
        var b = new Buf(0.5);
        var rnd = new Random(42);
        double lp = 0;
        b.Add(0, 0.45, t =>
        {
            lp = lp * 0.85 + (rnd.NextDouble() * 2 - 1) * 0.15;
            return (Math.Sin(2 * Math.PI * (95 - 40 * t) * t) * 0.8 + lp * 2.5) * Math.Exp(-t * 14);
        });
        return b.Done(false);
    }

    /// <summary>An instant message arriving.</summary>
    public static float[] Message()
    {
        var b = new Buf(0.7);
        b.Bell(0, 1318.5, 0.6, 0.5);
        b.Bell(0.09, 1975.5, 0.6, 0.4);
        return b.Done(false);
    }

    /// <summary>"Welcome!", in a friendly voice through an 8-bit sound card.</summary>
    public static float[] WelcomeVoice() => Voice("welcome.wav") ?? Welcome();

    /// <summary>"You've got mail!"</summary>
    public static float[] MailVoice() => Voice("mail.wav") ?? Mail();

    /// <summary>
    /// A recording kept in EasterEggs/Sounds (spoken by Kokoro TTS, then cut down to 11 kHz 8-bit), stretched to
    /// the clip sample rate. Null if it is missing.
    /// </summary>
    public static float[]? Voice(string file)
    {
        using var stream = typeof(AolSounds).Assembly.GetManifestResourceStream("Avamux.EasterEggs." + file);
        if (stream is null) return null;
        var (samples, rate) = ReadWav(stream);
        var n = (int)((long)samples.Length * Rate / rate);
        var b = new Buf(n / (double)Rate + 0.01);
        for (var i = 0; i < n && i < b.S.Length; i++)
        {
            var at = i * (double)rate / Rate;
            var j = (int)at;
            var next = Math.Min(j + 1, samples.Length - 1);
            b.S[i] = (float)(samples[j] + (samples[next] - samples[j]) * (at - j));
        }
        return b.Done(false);
    }

    /// <summary>Mono PCM, 8 or 16 bit.</summary>
    private static (float[] Samples, int Rate) ReadWav(Stream stream)
    {
        using var r = new BinaryReader(stream);
        r.ReadBytes(12); // RIFF, size, WAVE
        int rate = Rate, bits = 16, channels = 1;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(r.ReadChars(4));
            var size = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadBytes(6);
                bits = r.ReadInt16();
                r.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                var bytes = r.ReadBytes(size);
                var step = bits / 8 * channels;
                var samples = new float[bytes.Length / step];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = bits == 8 ? (bytes[i * step] - 128) / 128f : BitConverter.ToInt16(bytes, i * step) / 32768f;
                return (samples, rate);
            }
            else r.ReadBytes(size + (size & 1));
        }
        return ([], rate);
    }

    /// <summary>Signed on: a cheerful rising arpeggio.</summary>
    public static float[] Welcome()
    {
        var b = new Buf(1.3);
        double[] notes = [523.25, 659.25, 783.99, 1046.5, 1318.5];
        for (var i = 0; i < notes.Length; i++) b.Bell(i * 0.11, notes[i], 0.8, i == notes.Length - 1 ? 0.6 : 0.4);
        return b.Done(false);
    }

    /// <summary>New mail: ding-dong.</summary>
    public static float[] Mail()
    {
        var b = new Buf(1.1);
        b.Bell(0, 783.99, 0.8, 0.5);
        b.Bell(0.28, 622.25, 0.8, 0.5);
        return b.Done(false);
    }
}
