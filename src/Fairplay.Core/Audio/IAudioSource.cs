namespace Fairplay.Audio;

/// <summary>
/// Supplies 44.1 kHz stereo float audio to the sender. The sender calls <see cref="Read"/> on
/// its own (QPC-paced) schedule; clocked sources are responsible for slaving themselves to it.
/// </summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }

    void Start();
    void Stop();

    /// <summary>
    /// Fill <paramref name="dest"/> (interleaved L/R, 44.1 kHz) completely. Returns false when the
    /// output is padding because no audio was available.
    /// </summary>
    bool Read(Span<float> dest);

    /// <summary>Raised when the source fails permanently (device removed, etc.).</summary>
    event Action<string>? Faulted;
}

/// <summary>Synthetic test signal: a few tone bursts, then silence.</summary>
public sealed class ToneSource : IAudioSource
{
    readonly double[] _freqs;
    readonly double _toneSeconds;
    readonly double _gapSeconds;
    readonly float _amplitude;
    readonly int _pan;
    long _frame;
    double _phase;

    public string Name => "Test tone";
    public event Action<string>? Faulted { add { } remove { } }

    /// <param name="pan">-1 = left channel only, +1 = right channel only, 0 = both.</param>
    public ToneSource(double[]? freqs = null, double toneSeconds = 0.4, double gapSeconds = 0.25, double dbfs = -20, int pan = 0)
    {
        _pan = pan;
        _freqs = freqs ?? [523.25, 659.25, 783.99, 1046.5];
        _toneSeconds = toneSeconds;
        _gapSeconds = gapSeconds;
        _amplitude = (float)Math.Pow(10, dbfs / 20);
    }

    public double DurationSeconds => _freqs.Length * (_toneSeconds + _gapSeconds);

    public void Start() { }
    public void Stop() { }

    public bool Read(Span<float> dest)
    {
        const double sr = 44100;
        double slot = _toneSeconds + _gapSeconds;
        for (int i = 0; i < dest.Length / 2; i++, _frame++)
        {
            double t = _frame / sr;
            int idx = (int)(t / slot);
            double inSlot = t - idx * slot;
            float v = 0;
            if (idx < _freqs.Length && inSlot < _toneSeconds)
            {
                double env = Math.Min(1, Math.Min(inSlot, _toneSeconds - inSlot) / 0.01);
                _phase += 2 * Math.PI * _freqs[idx] / sr;
                v = (float)(Math.Sin(_phase) * _amplitude * env);
            }
            dest[2 * i] = _pan > 0 ? 0 : v;
            dest[2 * i + 1] = _pan < 0 ? 0 : v;
        }
        return true;
    }

    public void Dispose() { }
}
