namespace Fairplay.Audio;

public delegate void FrameSink(ReadOnlySpan<float> interleaved);

/// <summary>
/// Streaming stereo resampler: Kaiser-windowed sinc, polyphase table with linear interpolation
/// between phases, continuously adjustable ratio. The ratio trim is how the capture clock is
/// slaved to the sender clock without dropping or repeating samples.
/// </summary>
public sealed class Resampler
{
    const int HalfTaps = 48;
    const int Taps = HalfTaps * 2;
    const int Phases = 512;

    readonly float[] _table; // (Phases + 1) * Taps
    readonly double _baseStep;
    float[] _buf = new float[8192 * 2];
    int _bufFrames;
    double _pos; // position (in input frames) of the next output sample, relative to _buf[0]

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>Relative speed trim: +0.001 consumes input 0.1% faster (produces fewer output frames).</summary>
    public double Trim { get; set; }

    public Resampler(int inputRate, int outputRate)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        _baseStep = (double)inputRate / outputRate;

        // Cutoff (cycles per input sample): just below the lower of the two Nyquist rates.
        double fc = 0.5 * Math.Min(1.0, (double)outputRate / inputRate) * 0.93;
        const double beta = 9.0;
        double i0b = BesselI0(beta);
        _table = new float[(Phases + 1) * Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int k = 0; k < Taps; k++)
            {
                // tap k sits at input offset (k - HalfTaps + 1) relative to floor(pos); distance from pos:
                double x = (k - HalfTaps + 1) - frac;
                double sinc = x == 0 ? 1.0 : Math.Sin(2 * Math.PI * fc * x) / (2 * Math.PI * fc * x);
                double r = x / HalfTaps;
                double w = Math.Abs(r) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - r * r)) / i0b;
                double h = 2 * fc * sinc * w;
                _table[p * Taps + k] = (float)h;
                sum += h;
            }
            // normalize each phase to unity DC gain
            for (int k = 0; k < Taps; k++) _table[p * Taps + k] = (float)(_table[p * Taps + k] / sum);
        }
        // Start with HalfTaps-1 frames of silent history so the first output is centered on frame 0.
        _bufFrames = HalfTaps - 1;
        _pos = HalfTaps - 1;
    }

    static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 50; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }

    /// <summary>Push interleaved stereo input; appends produced interleaved stereo output via <paramref name="sink"/>.</summary>
    public void Process(ReadOnlySpan<float> input, FrameSink sink)
    {
        int inFrames = input.Length / 2;
        EnsureCapacity(_bufFrames + inFrames);
        input.CopyTo(_buf.AsSpan(_bufFrames * 2));
        _bufFrames += inFrames;

        double step = _baseStep * (1.0 + Trim);
        Span<float> outBuf = stackalloc float[512 * 2];
        int outCount = 0;
        var table = _table;
        var buf = _buf;

        while (true)
        {
            int ip = (int)_pos;
            if (ip + HalfTaps >= _bufFrames) break;
            double frac = _pos - ip;
            double pf = frac * Phases;
            int p0 = (int)pf;
            float mix = (float)(pf - p0);
            int t0 = p0 * Taps;
            int t1 = t0 + Taps;
            int start = (ip - HalfTaps + 1) * 2;
            float l = 0, r = 0;
            for (int k = 0; k < Taps; k++)
            {
                float c = table[t0 + k] + (table[t1 + k] - table[t0 + k]) * mix;
                l += buf[start + 2 * k] * c;
                r += buf[start + 2 * k + 1] * c;
            }
            outBuf[outCount * 2] = l;
            outBuf[outCount * 2 + 1] = r;
            if (++outCount == 512)
            {
                sink(outBuf);
                outCount = 0;
            }
            _pos += step;
        }
        if (outCount > 0) sink(outBuf[..(outCount * 2)]);

        // Drop input we no longer need (keep HalfTaps-1 frames of history before floor(pos)).
        int keepFrom = Math.Max(0, (int)_pos - HalfTaps + 1);
        if (keepFrom > 0)
        {
            Array.Copy(_buf, keepFrom * 2, _buf, 0, (_bufFrames - keepFrom) * 2);
            _bufFrames -= keepFrom;
            _pos -= keepFrom;
        }
    }

    void EnsureCapacity(int frames)
    {
        if (_buf.Length >= frames * 2) return;
        Array.Resize(ref _buf, Math.Max(frames * 2, _buf.Length * 2));
    }
}
