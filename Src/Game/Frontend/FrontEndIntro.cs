namespace Pre2Port.Game.Frontend;

/// <summary>
/// Intro state machine OLDIES -> TITUS -> PRESENT (front_end.py _native_front_end_frames). One displayed frame per
/// 70 Hz tick. A skip during TITUS or PRESENT ends the intro (the caller would continue with the menu).
/// </summary>
public sealed class FrontEndIntro : IScene
{
    public double LogicHz => 70.0;   // VGA retrace
    readonly FrontEndAssets _assets;
    readonly bool _skippable, _presentWaitsKey;
    readonly IFrontEndAudio _audio;
    IntroStage? _stage;          // stage to update next tick
    IntroStage? _drawn;          // stage whose frame was selected this tick
    int _state;                  // 0 OLDIES, 1 TITUS, 2 PRESENT, 3 done

    public FrontEndIntro(FrontEndAssets assets, bool skippable, IFrontEndAudio? audio = null, bool presentWaitsKey = true)
    {
        _assets = assets;
        _skippable = skippable;
        _presentWaitsKey = presentWaitsKey;
        _audio = audio ?? new NullFrontEndAudio();
        _stage = new OldiesScene(assets);
    }

    /// <summary>True once the last frame has been shown (menu would follow).</summary>
    public bool Done => _state == 3;
    /// <summary>True if this tick displayed a frame.</summary>
    public bool HasFrame => _drawn is not null;
    public int FrameCount { get; private set; }
    public string SceneName => _drawn?.Name ?? "";
    public int PaletteEntries => _drawn?.PaletteEntries ?? 256;

    public void Update(Input input)
    {
        _drawn = null;
        if (_stage is null) return;
        var s = _stage;
        s.Update(input);
        _drawn = s;
        FrameCount++;
        if (s.Result == StageResult.Running) return;
        if (s.Result == StageResult.Skipped) { _stage = null; _state = 3; return; }
        switch (_state)
        {
            case 0:
                _audio.LoadSfxBank("SAMPLE.SQZ");              // [asm 07c9] audio seam, not played
                _stage = new TitusScene(_assets, _skippable); _state = 1; break;
            case 1:
                _audio.LoadSong("PRESENTA.TRK");               // [asm 02cc] audio seam, not played
                _stage = new PresentScene(_assets, _skippable, _presentWaitsKey); _state = 2; break;
            default:
                _stage = null; _state = 3; break;
        }
    }

    public void Draw(Framebuffer fb) => _drawn?.Draw(fb);
}
