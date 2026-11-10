using Pre2Port.Recovered;
using Raylib_cs;

namespace Pre2Port.Game.Frontend;

/// <summary>Front-end audio seam plus the in-game effect trigger (play_sfx).</summary>
public interface IGameAudio : IFrontEndAudio
{
    /// <summary>[asm 0282] play sound effect <paramref name="id"/> (0..10).</summary>
    void PlaySfx(int id);
}

/// <summary>Silent implementation for headless runs / verification.</summary>
public sealed class NullGameAudio : IGameAudio
{
    public void LoadSfxBank(string file) { }
    public void LoadSong(string file) { }
    public void PlaySfx(int id) { }
}

/// <summary>
/// Raylib output of the original audio chain: the recovered ISR (<see cref="AudioEngine"/>) produces 168-byte unsigned 8-bit mono blocks at the SB rate
/// (<see cref="SourceRate"/> Hz, ~50.02 blocks/s). There is no pump thread: a mixed-pipeline processor (Raylib.AttachAudioMixedProcessor, called by the
/// device thread whenever it needs data, float32 interleaved stereo at the device rate) pulls blocks from the engine and linearly resamples them to the
/// device rate inside the callback, keeping the phase between calls. The device rate is not exposed by Raylib, so it is measured over the first
/// <see cref="CalibMs"/> ms (silence meanwhile; the engine is not consumed) and snapped to a standard rate.
/// <see cref="Underruns"/> counts callbacks that failed to produce data (filled with silence) or took longer than the audio they delivered (log on Dispose).
/// </summary>
public sealed class RaylibGameAudio : IGameAudio, IDisposable
{
    public const int SourceRate = 8403;     // SB time constant: 1_000_000 / (256 - tc) (pre2/audio/assets.py SOURCE_RATE)
    const int CalibMs = 400;
    /// <summary>Mixed-stream buffer in frames: raylib's default 4096 (~93 ms at 44.1 kHz) is doubled so scheduling stalls of the main thread do not starve the device callback.</summary>
    public const int StreamBufferFrames = 8192;
    static readonly int[] StdRates = { 22050, 32000, 44100, 48000, 88200, 96000, 192000 };

    readonly AudioEngine _engine;
    readonly AudioCallback<float> _cb;      // kept in a field so the GC never frees the native thunk
    readonly bool _ready;
    readonly System.Diagnostics.Stopwatch _clock = new();
    long _cbFrames;                         // frames seen by the callback (calibration)
    double _step;                           // SourceRate / deviceRate; 0 while calibrating
    int _underruns;
    public int Underruns => _underruns;

    // resampler state: source samples queue + fractional read position (persist across callbacks)
    byte[] _src = new byte[AudioMix.BlockLen * 8];
    int _srcLen;
    double _pos;                            // read position inside _src (source samples)

    public AudioEngine Engine => _engine;

    /// <summary>Master volume 0..1 for music and effects (Volume=0..100 in Prehistorik2.cfg; the ESC overlay changes it live). Applied when the blocks are resampled into the device buffer.</summary>
    public static volatile float MasterVolume = Pre2Port.Game.Options.GameOptions.Current.Volume / 100f;

    public RaylibGameAudio(byte[] boot, string originalDir)
    {
        _engine = new AudioEngine(boot, originalDir);
        Raylib.SetAudioStreamBufferSizeDefault(StreamBufferFrames);   // must be set before the mixed stream is created
        Raylib.InitAudioDevice();
        _ready = Raylib.IsAudioDeviceReady();
        _cb = Process;
        if (!_ready)
        {
            Console.Error.WriteLine("audio: no audio device, running silent");
            return;
        }
        _clock.Start();
        Raylib.AttachAudioMixedProcessor(_cb);
    }

    /// <summary>Device callback: buffer = frames x 2 floats (stereo). Fills it with the resampled mono signal (added to the mix, which is otherwise empty).</summary>
    void Process(Span<float> buf)
    {
        int frames = buf.Length / 2;
        if (_step == 0)
        {
            _cbFrames += frames;
            double el = _clock.Elapsed.TotalMilliseconds;
            if (el >= CalibMs)
            {
                double rate = _cbFrames * 1000.0 / el, best = StdRates[0];
                foreach (int r in StdRates) if (Math.Abs(r - rate) < Math.Abs(best - rate)) best = r;
                _step = SourceRate / best;
            }
            return;                          // silence while calibrating
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            double step = _step;
            float vol = MasterVolume;
            for (int i = 0; i < frames; i++)
            {
                int k = (int)_pos;
                while (k + 1 >= _srcLen)     // need src[k] and src[k+1]
                {
                    if (k >= AudioMix.BlockLen * 4)   // drop consumed samples
                    {
                        int drop = AudioMix.BlockLen * 4;
                        Buffer.BlockCopy(_src, drop, _src, 0, _srcLen - drop);
                        _srcLen -= drop; _pos -= drop; k -= drop;
                    }
                    if (_srcLen + AudioMix.BlockLen > _src.Length) Array.Resize(ref _src, _src.Length * 2);
                    var blk = _engine.NextBlock();
                    if (blk == null || blk.Length < AudioMix.BlockLen) throw new InvalidOperationException("no audio block");
                    Array.Copy(blk, 0, _src, _srcLen, AudioMix.BlockLen);
                    _srcLen += AudioMix.BlockLen;
                }
                double f = _pos - k;
                float v = (float)((_src[k] * (1 - f) + _src[k + 1] * f - 128.0) / 128.0);   // unsigned 8 -> [-1,1)
                v *= vol;
                buf[2 * i] += v; buf[2 * i + 1] += v;
                _pos += step;
            }
            // slow producer: callback took longer than the audio it delivered
            double spent = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (spent > frames * step / SourceRate) Interlocked.Increment(ref _underruns);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _underruns);   // not enough data: leave silence
        }
    }

    public void LoadSfxBank(string file) => _engine.LoadSfxBank(file);
    public void LoadSong(string file) => _engine.LoadSong(file);
    public void PlaySfx(int id) => _engine.PlaySfx(id);

    public void Dispose()
    {
        if (!_ready) return;
        Raylib.DetachAudioMixedProcessor(_cb);
        Console.WriteLine($"audio: {Underruns} underruns in {_clock.Elapsed.TotalSeconds:F1} s");
        Raylib.CloseAudioDevice();
    }
}
