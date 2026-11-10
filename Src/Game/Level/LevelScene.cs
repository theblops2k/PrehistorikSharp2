using Pre2Port.Native;
using Pre2Port.Recovered;
using static Pre2Port.Native.LevelState;

namespace Pre2Port.Game.Level;

/// <summary>
/// Gameplay scene: loads a level over a copy of the boot image (<see cref="LevelLoad.ColdBoot"/>) and shows the scrolling tile
/// background + terrain with the level palette (<see cref="LevelRender"/>), the HUD and, when <c>player</c> is on, the playable
/// caveman: spawn at the level start point, walk (Left/Right), jump (Up), crouch (Down), terrain collision and the follow camera.
/// With <c>objects</c> on, the level objects too (spawners, AI, sprites). With <c>damage</c> on (needs the objects): contact with an enemy (8295 loop1: death/hurt/bump/stomp), energy, life loss, respawn at the checkpoint (4C69/4F6C) and game over = bounce + GAME OVER scene (host) or, without HoldEnd, a level 1 reboot. No pickups loop2 (bonus), platforms, pickups, fire/attack or foreground layer yet. Audio (level music + the ported effects) goes through <see cref="Audio"/>. One Update == one 70 Hz displayed frame.
/// Frame order (main loop spine): [previous frame's 26FA state half] -> player 5850 -> camera follow 5643 -> render sync, then Draw.
/// With <c>player</c> off the Update only does the render sync (the terrain/HUD first-frame verification scenarios).
/// </summary>
public sealed class LevelScene : IScene
{
    LevelState _s;
    readonly bool _player;
    readonly bool _objects;
    readonly bool _damage;
    readonly bool _bonus;
    readonly bool _platforms;
    readonly bool _attack;
    readonly byte[] _boot;
    readonly string _originalDir;

    /// <summary>
    /// Boot the level <paramref name="level"/> (0-based index, 0 == LEVEL1.SQZ) from <paramref name="originalDir"/>. <paramref name="objects"/> runs the
    /// level object system (spawners 0x8489 -> 12 live slots, their AI and sprites); it needs the player (they spawn around it).
    /// <paramref name="damage"/> adds the enemy contact / death / respawn pass (needs player and objects).
    /// </summary>
    public LevelScene(byte[] boot, string originalDir, int level, bool hud = true, bool player = true, bool objects = true, bool damage = false,
        bool bonus = false, bool platforms = false, bool attack = false, int modeCopy = 0, LevelCarry? carry = null)
    {
        _attack = attack && player && objects && bonus;               // fire = club attack 5F96 + weapon flight 6210 (the combat pass 88D7 comes with the pickups)
        _boot = boot;
        _originalDir = originalDir;
        _damage = damage && player && objects;
        _bonus = bonus && player && objects;                          // pickups: 581E / 88D7 / 60FE / loop2 / 8922 (needs the player and the objects)
        _platforms = platforms && player;                             // moving platforms 4907
        Level = level;
        _player = player;
        _objects = objects && player;
        _s = LevelLoad.ColdBoot(boot, originalDir, level, modeCopy, carry);
        _hud = hud ? HudAssets.Load(originalDir) : null;
    }

    readonly HudAssets? _hud;

    public int Level { get; }

    public enum EndKind { None, LevelEnd, GameOver, Warp, GameComplete }
    /// <summary>
    /// Level the host must load when <see cref="Outcome"/> is <see cref="EndKind.Warp"/>: the 4C69 end of level WITHOUT the tally (no BRAVO, no exit cutscene) -- the 30C6 curtain
    /// closes and 4F65 re-initialises: a main level entering its bonus level through the id 0x137 item ([0x6BE6] = 0xFF, table [0x2CF6 + level], 4C8F) or a bonus level ending (4CC1: level + 1).
    /// </summary>
    public int WarpTarget { get; private set; } = -1;
    /// <summary>When true a level end / game over is NOT handled inside the scene: <see cref="Outcome"/> is set and the host shows the end screen (<see cref="EndScene"/>).</summary>
    public bool HoldEnd { get; init; }
    /// <summary>
    /// When true the death bounce (509D, 60 frames, one per tick) is SHOWN, followed by the 30C6 fade; then the respawn (4F6C) or, at 0 lives, the game-over
    /// outcome (5063, needs <see cref="HoldEnd"/>). When false (verification against the Python reference) the bounce runs headless inside one tick.
    /// </summary>
    public bool AnimateDeath { get; init; }
    int _bounceLeft;
    bool _bounceGameOver, _gameOverAfterFade;
    /// <summary>
    /// The demo buffer DS:0x3F (asm DC1). With <see cref="DemoPlayback"/> off this live session is RECORDED into it (the DC1 0F0A record tail, RLE-packed input);
    /// with it on the level is a replay of the buffer ([0x2879] = 1: DC1 mode 1 feeds the six input flags, any key event or the 0x55AA sentinel sets [0x6BE5] = 1).
    /// </summary>
    public Pre2Port.Recovered.DemoInput.Store? DemoStore { get; init; }
    /// <summary>[0x2879] = 1: the level plays back <see cref="DemoStore"/> (the attract). A death or the level end also ends it (4F6F, 4F54 -> 506C).</summary>
    public bool DemoPlayback { get; init; }
    bool[]? _keysDown;

    /// <summary>[asm 1823 INT 9] any keyboard event (make OR break code) since the level start latches the scancode in [0x2874]; DC1 mode 1 turns it into [0x6BE5] = 1.</summary>
    void LatchKeyEvent(Input input)
    {
        var keys = Enum.GetValues<Input.Key>();
        if (_keysDown is null) { _keysDown = new bool[keys.Length]; foreach (var k in keys) _keysDown[(int)k] = input.IsKeyDown(k); return; }
        foreach (var k in keys)
        {
            bool down = input.IsKeyDown(k);
            if (down != _keysDown[(int)k] || input.IsKeyPressed(k)) _s.Wb(Pre2Port.Recovered.DemoInput.PendingKey, 1);
            _keysDown[(int)k] = down;
        }
    }

    /// <summary>Set by the 4C69 dispatcher when HoldEnd is on (the host replaces the scene from then on).</summary>
    public EndKind Outcome { get; private set; }
    /// <summary>Ticks updated so far.</summary>
    public int Ticks { get; private set; }
    public string SceneName => $"LEVEL{Level + 1}";
    /// <summary>The game memory (read-only use: verification and debugging).</summary>
    public LevelState State => _s;

    /// <summary>Snapshot of the state after the previous tick (== the frame drawn last): the "previous presentation state".</summary>
    LevelState? _prev;

    /// <summary>Presentation interpolation factor (0..1, default 1 = exact tick image). Display only; never changes the logic state.</summary>
    public double Alpha { get; set; } = 1.0;

    /// <summary>Interpolated presentation pose at <paramref name="alpha"/> (current pose when there is no previous tick or on a snap).</summary>
    public LevelRender.Pose PoseAt(double alpha)
        => _prev != null && Ticks > 1 && LevelRender.TryInterpolate(_prev, _s, alpha, out var p) ? p : LevelRender.Pose.Of(_s);

    public LevelState PrevState => _prev ?? _s;

    /// <summary>Pose of the previous tick (valid when <c>Ticks &gt; 1</c>).</summary>
    public LevelRender.Pose PrevPose => _prev != null ? LevelRender.Pose.Of(_prev) : LevelRender.Pose.Of(_s);

    /// <summary>Audio output (null = silent). Level music is started on the first tick ([asm 01AB]: song index [0x2D20 + level]); the effects the logic queued (<see cref="LevelState.Sfx"/>) are played after each tick.</summary>
    public Pre2Port.Game.Frontend.IGameAudio? Audio { get; init; }

    string? _requestedSong;

    public void Update(Input input)
    {
        if (Ticks == 0 && !DemoPlayback)
        {
            string levelSong = Pre2Port.Recovered.AudioFiles.LevelSong(_boot, Level);
            Audio?.LoadSong(levelSong);                                   // [01AB 02CC]; a no-op while [0x2879] = 1 (asm 02ED)
            _requestedSong = levelSong;                                   // [02E5-02EB] 02CC returns at once when the song's loaded flag ([0xABC + idx] bit 0) is set: LEVELA (index 9) starts on MONSTER (13), so the 7585 request is a no-op there
        }
        UpdateLogic(input);
        if (_s.SongRequest is int song)                                   // [asm 7585: 02CC(0xD)] boss music; loaded once per change (the request repeats every frame)
        {
            _s.SongRequest = null;
            if (!DemoPlayback && Pre2Port.Recovered.AudioFiles.SongFile(song) is string f && f != _requestedSong) { _requestedSong = f; Audio?.LoadSong(f); }
        }
        if (_s.SfxQueue.Count > 0)
        {
            if (Audio != null) foreach (int id in _s.SfxQueue) Audio.PlaySfx(id);
            _s.SfxQueue.Clear();
        }
    }

    // ---- cave curtain (30C6 fade-out + 3054 centre-out reveal) -------------------------------------------------------------------
    // The original blocks the whole game for the curtain: 30C6 = 9 steps (clear a 10-row band from the top and one from the bottom, then
    // 2 x 44CD = 2 vsyncs), 3054 = 10 steps (copy the 16 px strips at 160-16k and 160+16k of the new frame, then 2 vsyncs). The pan and the
    // arrival mini-pass in between take no vsync. So: 18 + 20 vsyncs, 1 tick = 3 vsyncs (70/3 Hz). The state change itself is done at the
    // trigger tick (nothing runs during the freeze, so it is unobservable); the scene then holds the logic and only draws the curtain.
    const int FadeVsyncs = 18, RevealVsyncs = 20, VsyncsPerTick = 3;
    bool _curtain;
    int _curtainTicks;
    LevelState? _curtainOld;

    void CurtainStart()
    {
        _curtainOld = new LevelState(_prev!.Data, _s.OriginalDir);        // the frame drawn last (state at the start of this tick, render-synced)
        _curtainOld.HudFrozenPreDeath = _s.HudFrozenPreDeath;               // the copy is a different LevelState: carry the frozen HUD (no 45AF during 30C6)
        _curtain = true;
        _curtainTicks = 0;
    }

    void Black(Framebuffer fb, int y0, int y1) => fb.FillRect(0, y0, fb.Width, y1 - y0, 0);

    void DrawCurtain(Framebuffer fb, double alpha)
    {
        int v = (int)(VsyncsPerTick * (_curtainTicks + Math.Clamp(alpha, 0.0, 1.0)));
        bool fade = v < FadeVsyncs;
        var s = fade ? _curtainOld! : _s;
        if (fb.Width > Screen.Width) LevelRender.DrawWide(s, fb, _hud, null, 1.0, _objects);
        else LevelRender.Draw(s, fb, _hud, null, 1.0, _objects);
        int rows = LevelRender.ViewportRows;
        if (fade)
        {
            int steps = Math.Min(9, v / 2 + 1);                           // 30C6: top band rows [0,10*steps), bottom band rows [176-10*steps,176)
            Black(fb, 0, Math.Min(10 * steps, rows));
            Black(fb, Math.Max(rows - 10 * steps, 0), rows);
        }
        else
        {
            int pairs = Math.Min(10, (v - FadeVsyncs) / 2 + 1);           // 3054: strip pairs copied so far
            int hw = fb.Width / 2, half = hw * pairs / 10;                // 320 px: 16 * pairs on each side of the centre
            fb.FillRect(0, 0, hw - half, rows, 0);
            fb.FillRect(hw + half, 0, fb.Width - hw - half, rows, 0);
        }
    }

    void UpdateLogic(Input input)
    {
        if (Ticks == 0 && DemoStore != null)
        {
            DemoStore.LoadInto(_s, DemoPlayback);                             // [asm 0214-0219] the buffer, the cursor and [0x2879] for this level; [0x2874] = 0
            if (DemoPlayback) LatchKeyEvent(input);                           // the baseline of the keyboard: events from here on abort the demo
        }
        if (_curtain)
        {
            _curtainTicks++;
            if (_gameOverAfterFade)                                       // [509D 512C 30C6] the death frame fades out, then 5063 goes on to the game-over scene
            {
                if (VsyncsPerTick * _curtainTicks < FadeVsyncs) return;
                _curtain = false; _curtainOld = null; _gameOverAfterFade = false;
                Outcome = EndKind.GameOver;
                return;
            }
            if (_warpAfterFade)                                           // [4C90 33C6 4F65] the level end without tally: 30C6 only (18 vsyncs), then the host loads the target
            {
                if (VsyncsPerTick * _curtainTicks < FadeVsyncs) return;
                _curtain = false; _curtainOld = null; _warpAfterFade = false;
                _s.Wb(Off.Level, _warpTarget);                            // [4C90] / [4CC4]
                WarpTarget = _warpTarget;
                Outcome = EndKind.Warp;
                return;
            }
            if (VsyncsPerTick * _curtainTicks < FadeVsyncs + RevealVsyncs) return;   // the game is blocked inside 30C6 / 3054
            _curtain = false;
            _curtainOld = null;
            Array.Copy(_s.Data, _prev!.Data, _s.Data.Length);             // no interpolation across the teleport
        }
        if (_warpPending)                                                 // 4C69 saw the end of level the frame before: the frame was drawn, now 30C6 closes the curtain over it
        {
            _warpPending = false;
            Array.Copy(_s.Data, _prev!.Data, _s.Data.Length);
            CurtainStart();
            _warpAfterFade = true;
            Ticks++;
            return;
        }
        if (_player && _bounceLeft > 0)
        {
            BounceTick();
            LevelCamera.SyncRenderState(_s);
            Ticks++;
            return;
        }
        if (_player)
        {
            if (_prev == null) _prev = new LevelState(_s.Data, _s.OriginalDir);
            else Array.Copy(_s.Data, _prev.Data, _s.Data.Length);
            FlushPostFrame();                                             // end of the frame just drawn
            if (_bonus) { Pickups.PopupRing(_s); Pickups.CombatPass(_s); }   // 021A 581E, 021D 88D7 (club / weapons vs the bonus cells)
            if (_objects) LevelObjects.Step(_s);                          // 6822: walker 684E + spawner pass 6913 (before the player, like the main loop)
            if (_attack) Pickups.Projectiles(_s);                         // 0223 6210: the thrown weapons 0x4F2E fly / expire
            if (_bonus) Pickups.Particles(_s);                            // 0226 60FE: the food / popup bursts
            if (_damage || _bonus) PlayerInteraction.DebrisPool(_s);      // 60DF: the hit/stomp/pickup popup effects expire
            if (_platforms) Platforms.Tick(_s);                           // 022C 4907: moving platforms + ride collision
            if (DemoStore != null && DemoPlayback)
            {
                LatchKeyEvent(input);
                Pre2Port.Recovered.DemoInput.Playback(_s);                    // DC1 mode 1: the flags come from the buffer, the keyboard is not read
                if (!_attack) _s.Wb(0x27E8, 0);                               // same gate as the live fire flag below
            }
            else
            {
                Player.SetKeys(_s, input.IsKeyDown(Input.Key.Left), input.IsKeyDown(Input.Key.Right),
                    input.IsKeyDown(Input.Key.Up), input.IsKeyDown(Input.Key.Down),
                    _attack && (input.IsKeyDown(Input.Key.Space) || input.IsKeyDown(Input.Key.Enter)));   // DC1: fire = Space (0x282D) / Enter (0x2810)
                if (DemoStore != null) { Pre2Port.Recovered.DemoInput.Record(_s); DemoStore.SaveFrom(_s); }   // DC1 0F0A record tail
            }
            Player.Step(_s);                                              // 5850
            bool early = _damage && PlayerInteraction.Loop1(_s);          // 8295 loop1: player vs enemies
            if (_bonus && !early) Pickups.Loop2(_s);                      // 8295 loop2: player vs pickups
            if (_bonus) Pickups.Project(_s);                              // 0235 8922: float items -> render slots
            int cave = CaveTrigger.Scan(_s);                              // 0238 52FE: cave / teleport entrance scan
            if (cave >= 0) CaveArrive(cave);                              // 5326: transition + arrival mini-pass, then the frame goes on
            if (TickEffects) Proximity.Step(_s);                          // 023B 53F6: proximity triggers (rising walls); after 52FE (and the cave arrival), before 5643
            LevelCamera.Follow(_s);                                       // 5643
            if (_damage) LevelState4C69();
            if (_bonus && Outcome == EndKind.None && _bounceLeft == 0 && !_gameOverAfterFade) Pickups.SpecialEvent(_s);   // 026A 67D7: BONUS letters reward / utensil set -> [0x6BE2] (after 4C69 ... 6772, before 4C30)
        }
        LevelCamera.SyncRenderState(_s);
        Ticks++;
        _postPending = _player && !_transitionDone;
        _transitionDone = false;
    }

    bool _transitionDone;

    /// <summary>Number of cave transitions taken (debug / verification).</summary>
    public int CaveTeleports { get; private set; }

    /// <summary>[asm 5326-53F2] cave transition for the table entry <paramref name="si"/> and the arrival mini-pass (4907, 8922, 6822, 26FA, 54AB, 3922; their VRAM plots are not drawn). The curtain frames are not drawn.</summary>
    void CaveArrive(int si)
    {
        CurtainStart();                                                   // 5332 30C6 fade-out over the old frame ... 53F2 3054 reveal (drawn in Draw)
        CaveTrigger.Teleport(_s, si);
        if (_platforms) Platforms.Tick(_s);                               // 53DD 4907
        if (_bonus) Pickups.Project(_s);                                  // 53E0 8922
        if (_objects) LevelObjects.Step(_s);                              // 53E3 6822
        Player.PostFrame(_s, _objects);                                   // 53E6 26FA ([0x6BD5]++, record mutation)
        CaveTrigger.StepFireflies(_s);                                    // 53EC 54AB
        CaveTrigger.StepScrollScript(_s);                                 // 53EF 3922
        CaveTeleports++;
    }

    /// <summary>
    /// [asm 4C69] the level-state dispatcher, after the end-of-frame half (26FA precedes 4C69 in the real frame, so that half runs first and is
    /// NOT repeated afterwards). Respawn armed -> 4F6C at the checkpoint (one tick, no animation); game over -> level 1 reboots.
    /// </summary>
    void LevelState4C69()
    {
        var kind = Respawn.Pending(_s);
        if (kind == Respawn.Kind.None) return;
        if (kind == Respawn.Kind.LevelEnd && !HoldEnd) throw new ObjectGapException("level end (4C69 [0x6BE6]) needs HoldEnd (the end screens)");
        if (kind == Respawn.Kind.GameComplete && !HoldEnd) throw new ObjectGapException("game complete (4C69 [0x6BE5] = 0xFF -> THE END 5034) needs HoldEnd (the end screens)");
        _postPending = false;
        LevelObjects.ParticleConsume(_s);                                 // 4B8E state half
        Player.PostFrame(_s, _objects);                                   // [0x6BD5]++, 26FA mutation, idle counter
        _transitionDone = true;
        if (kind == Respawn.Kind.LevelEnd)
        {
            var (tally, target) = LevelEndFlow();
            if (tally) Outcome = EndKind.LevelEnd;                        // the host shows the tally and loads level [0x2D8A] + 1
            else { _warpPending = true; _warpTarget = target; }           // 30C6 curtain over the frame just drawn, then the host loads `target`
        }
        else if (kind == Respawn.Kind.GameComplete) Outcome = EndKind.GameComplete;   // [4CB5 -> 5034] no death bounce, no curtain: the host shows THE END
        else if (AnimateDeath && (kind == Respawn.Kind.Respawn || (kind == Respawn.Kind.GameOver && HoldEnd)))
        {
            Respawn.BounceBegin(_s);                                      // [509D] scream, pose 0x21, kick; the 60 frames follow one per tick
            _bounceLeft = Respawn.BounceFrames;
            _bounceGameOver = kind == Respawn.Kind.GameOver;
        }
        else if (kind == Respawn.Kind.GameOver && HoldEnd) Outcome = EndKind.GameOver;   // the host shows the game-over screen and restarts level 1
        else if (kind == Respawn.Kind.Respawn) Respawn.Run(_s);
        else
        {
            _s = LevelLoad.ColdBoot(_boot, _originalDir, 0);              // 5063 game over: back to level 1 (lives 2, energy 3, score 0); no game-over screen
            _prev = null;
        }
    }

    bool _warpPending, _warpAfterFade;
    int _warpTarget;
    const int WarpTable = 0x2CF6;

    /// <summary>
    /// [asm 4C69 level-end part, 4C69-4CC8] what the end of the level does. Returns (tally, target): tally = the 4CCB exit cutscene + BRAVO + tally + next level [0x2D8A] + 1; otherwise the
    /// curtain 30C6 and 4F65 straight into level <c>target</c>.
    /// [0x6BE6] == 1 (normal end, 4CBA): a main level (&lt; 0xA) takes the tally, a bonus level (&gt;= 0xA) closes the curtain and loads level + 1.
    /// [0x6BE6] &gt; 1 (the id 0x137 item, 4C74): a main level warps into the bonus level cs-table [0x2CF6 + level] (XLAT, no +1, no tally); a bonus level looks its source level up in
    /// the table ([BX] == level, the first index), stores it in [0x2D8A] (4C89) and ends through 4CBA (= the tally of that main level, next = index + 1).
    /// </summary>
    (bool Tally, int Target) LevelEndFlow()
    {
        int mode = _s.Rb(0x6BE6), lvl = _s.Rb(Off.Level);
        if (mode > 1)                                                     // [4C72 JC not taken; 4C70 JZ not taken]
        {
            if (lvl < 0xA)                                                // [4C7A-4C7C]
            {
                int t = _s.Rb(WarpTable + lvl);                           // [4C8F] XLAT BX
                if (t >= 0x20) throw new ObjectGapException($"level {lvl} has no bonus level in the warp table [0x2CF6] ([0x2CF6 + {lvl}] = {t:X2}); the original would load garbage");
                return (false, t);                                        // [4C90-4C96] [0x2D8A] = t, 33C6 (= 30C6), JMP 5265 (= 4F65)
            }
            int i = 0;                                                    // [4C7E-4C83] unbounded scan of the DS bytes from 0x2CF6
            while (_s.Rb(WarpTable + i) != lvl)
                if (++i > 0xFFFF) throw new ObjectGapException($"bonus level {lvl:X2} is not in the warp table [0x2CF6..]");
            _s.Wb(Off.Level, i); lvl = i;                                 // [4C85-4C89]
        }
        if (lvl < 0xA) return (true, (lvl + 1) & 0xFF);                   // [4CBA-4CBF] -> 4CCB tally ... 4F5E INC
        return (false, (lvl + 1) & 0xFF);                                 // [4CC1-4CC8] 33C6, INC [0x2D8A], 4F65
    }

    /// <summary>
    /// [asm 50DE-512C] one frame of the shown death bounce. The frame drawn last is the one before this step (the original draws, then steps), so the
    /// 30C6 fade starts from the frame of the last tick; after the 60th step the respawn restore (4F6C) runs, or the game-over flag is armed (5063).
    /// </summary>
    void BounceTick()
    {
        Array.Copy(_s.Data, _prev!.Data, _s.Data.Length);
        _postPending = false;
        if (_bounceLeft == 1) CurtainStart();                             // [512C] 30C6 over the last bounce frame (then 3054 on the respawn)
        Respawn.BounceStep(_s, full: true);
        if (--_bounceLeft > 0) return;
        if (_bounceGameOver || _s.Rb(Pre2Port.Recovered.DemoInput.Mode) != 0) _gameOverAfterFade = true;   // [506C] after the fade: 25C7, song 0x11, 9B23 scene; a demo death too ([4F6F] [0x2879] != 0 -> 506C)
        else Respawn.Finish(_s);                                          // [4F6F-5033]
    }

    bool _postPending;

    /// <summary>The end-of-frame state half of the frame drawn last: 4B8E particle consume, then 26FA ([0x6BD5]++, record mutation),
    /// 54AB (firefly swarm + its two shared RNG draws, before the 6772/4C30 tail) and the timer ISR idle counter.</summary>
    void FlushPostFrame()
    {
        if (!_postPending) return;
        _postPending = false;
        if (_objects) LevelObjects.ParticleConsume(_s);
        Player.PostFrame(_s, _objects);
        CaveTrigger.StepFireflies(_s);                                    // 0253 54AB: after 26FA, before 4C69/6772/4C30
        CaveTrigger.StepScrollScript(_s);                                 // 0256 3922: after 54AB, before 4C69 (wind/snow + RNG; only LEVELG has a script)
        if (!TickEffects) return;
        LightFade.Step(_s);                                               // 0267 6772: light fade step (after 45AF/44FB; the DAC shown for the next frame)
        ScreenShake.Apply(_s);                                            // 026D 4C30: [0x6BF8] and (odd frames) player Y -= 3; runs after the frame was drawn and after 26FA ([0x6BD5] already bumped)
    }

    /// <summary>
    /// Run the main-loop calls 53F6 (proximity triggers), 6772 (light fade step) and 4C30 (screen shake apply). On by default; the headless verifiers whose
    /// Python reference generators do not run them ("Not run: ... shake, light fade, triggers") switch it off.
    /// 6772 and 4C30 follow 4C69 in the loop, i.e. after the frame was rendered, so they are part of the deferred post-frame half (FlushPostFrame): the frame
    /// drawn by a tick still shows the previous [0x6BF8] / DAC, and a tick that ends in a 4C69 transition (the original leaves the loop there) skips them.
    /// </summary>
    public bool TickEffects { get; init; } = true;

    /// <summary>
    /// Verification/debug teleport: finish the previous frame, move the player record to (<paramref name="x"/>, <paramref name="y"/>) with zero
    /// velocity and re-run the level-start camera snap (3af2), so far away spawners can be exercised without playing there.
    /// </summary>
    public void Warp(int x, int y)
    {
        FlushPostFrame();
        _s.Ww(Player.PX, x); _s.Ww(Player.PY, y);
        _s.Ww(Off.PlayerXvel, 0); _s.Ww(Off.PlayerYvel, 0);
        LevelCamera.Init(_s);
    }

    /// <summary>
    /// Verification stand-in for the (unported) attack handler 5F96: place the club record 0x4F0A (X, Y, sprite 1) for the next tick's combat pass 88D7.
    /// The previous frame is finished first, so the 26FA record mutation does not touch the injected record (as in the reference generator).
    /// </summary>
    public void PokeClub(int x, int y)
    {
        FlushPostFrame();
        _s.Ww(0x4F0A, x); _s.Ww(0x4F0C, y); _s.Ww(Player.Slot0Sprite, 1);
    }

    /// <summary>Verification: finish the previous frame (the end-of-frame state half) before a scripted poke, like the reference generators do between ticks.</summary>
    public void VerifyFlush() => FlushPostFrame();

    /// <summary>
    /// Verification: the STATE half of the cave teleport 5326-53D5 for the trigger table entry <paramref name="entry"/> (no curtain, no arrival mini-pass, no frame remainder), applied between
    /// ticks (work/gen_tree_ref.py "=tp"): the level-6 boss arena entry.
    /// </summary>
    public void VerifyTeleport(int entry)
    {
        FlushPostFrame();
        CaveTrigger.Teleport(_s, CaveTrigger.Table + entry * CaveTrigger.Stride);
        LevelCamera.SyncRenderState(_s);
    }

    /// <summary>Framebuffer width: <see cref="LevelRender.WideWidth"/> (320 + 16 per extra column from the screen proportion) when FULLSCREEN=WIDESCREEN, else 320.</summary>
    public int FbWidth => Settings.Current.WidescreenActive ? LevelRender.WideWidth : Screen.Width;

    public void Draw(Framebuffer fb) => Draw(fb, Alpha);

    /// <summary>The picture left on screen once the 30C6 curtain has closed (viewport black, HUD band intact): the frame the demo-end 9286 fade starts from.</summary>
    public void DrawClosed(Framebuffer fb)
    {
        Draw(fb, 1.0);
        Black(fb, 0, LevelRender.ViewportRows);
    }

    /// <summary>Draw at presentation factor <paramref name="alpha"/> (1 = the plain tick image; interpolation needs a previous drawn tick).</summary>
    public void Draw(Framebuffer fb, double alpha)
    {
        if (_curtain) { DrawCurtain(fb, alpha); return; }
        var prev = _player && Ticks > 1 ? _prev : null;
        if (fb.Width > Screen.Width) LevelRender.DrawWide(_s, fb, _hud, prev, alpha, _objects);
        else LevelRender.Draw(_s, fb, _hud, prev, alpha, _objects);
    }
}
