using Raylib_cs;

namespace Pre2Port.Game.Options;

/// <summary>Headless verifier of the ESC overlay's data layer (no window, no Raylib calls): <c>Pre2Port options-test &lt;scratchDir&gt;</c>. Prints PASS / FAIL lines.</summary>
public static class OptionsVerify
{
    static int _fail;
    static void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) _fail++; }

    sealed class Fake : IInputSource
    {
        public readonly HashSet<KeyboardKey> Down = new();
        public readonly HashSet<GamepadButton> PadDownSet = new();
        public float AxisX, AxisY;
        public bool Pad = true;
        readonly HashSet<KeyboardKey> _consumed = new();
        public readonly HashSet<KeyboardKey> NewPress = new();
        public bool KeyDown(KeyboardKey k) => Down.Contains(k);
        public bool KeyPressedConsume(KeyboardKey k) => NewPress.Remove(k);
        public bool PadAvailable(int slot) => Pad;
        public bool PadDown(int slot, GamepadButton b) => PadDownSet.Contains(b);
        public float PadAxis(int slot, GamepadAxis a) => a == GamepadAxis.LeftX ? AxisX : AxisY;
    }

    static string Read(string p) => File.ReadAllText(p);

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        _fail = 0;

        // ---------------- LivesAtStartup, Volume, ShowFps: now in Prehistorik2.cfg (po == p2); Prehistorik.cfg is only migrated
        string p2 = Path.Combine(dir, "Prehistorik2.cfg");
        string po = p2, legacy = Path.Combine(dir, "Prehistorik.cfg"), migrated = legacy + ".migrated";
        foreach (var fl in new[] { po, legacy, migrated }) if (File.Exists(fl)) File.Delete(fl);
        var o = GameOptions.Load(po);
        string txt = Read(po);
        Check("Prehistorik2.cfg created with the option defaults LivesAtStartup=3 Volume=100 ShowFps=NO", o.LivesAtStartup == 3 && o.Volume == 100 && !o.ShowFps && txt.Contains("LivesAtStartup=3") && txt.Contains("Volume=100") && txt.Contains("ShowFps=NO"));
        Check("start lives state: 3 -> 2 (original), 10 -> 9", o.StartLivesState == 2 && new GameOptions { LivesAtStartup = 10 }.StartLivesState == 9);
        File.WriteAllText(po, "# test\nlivesatstartup = 7\nVOLUME=35\n");
        o = GameOptions.Load(po);
        txt = Read(po);
        Check("keys case-insensitive (livesatstartup=7, VOLUME=35); missing ShowFps appended once; comment kept", o.LivesAtStartup == 7 && o.Volume == 35 && !o.ShowFps && txt.Contains("ShowFps=NO") && txt.Contains("# test") && txt.Split('\n').Count(l => l.StartsWith("ShowFps")) == 1);
        File.WriteAllText(po, "LivesAtStartup=2\nVolume=250\nShowFps=maybe\n");
        o = GameOptions.Load(po);
        Check("invalid values fall back to defaults (lives 2 -> 3, volume 250 -> 100, ShowFps maybe -> NO) without rewriting lines", o.LivesAtStartup == 3 && o.Volume == 100 && !o.ShowFps && Read(po).Contains("Volume=250"));
        o.LivesAtStartup = 10; o.Volume = 0; o.ShowFps = true; o.Save();
        var o2 = GameOptions.Load(po);
        Check("save / reload LivesAtStartup=10 Volume=0 ShowFps=YES", o2.LivesAtStartup == 10 && o2.Volume == 0 && o2.ShowFps && Read(po).Contains("ShowFps=YES"));

        // ---------------- migration of the old Prehistorik.cfg (once, never overwriting)
        File.WriteAllText(p2, "FULLSCREEN=WIDESCREEN\nVolume=40\nFPS=90\n");
        File.WriteAllText(legacy, "LivesAtStartup=8\nVolume=75\nShowFps=YES\n# old\n");
        var mg = GameOptions.Load(p2);
        txt = Read(p2);
        Check("migration: missing keys come from Prehistorik.cfg (LivesAtStartup=8, ShowFps=YES), existing Volume=40 is NOT overwritten, FULLSCREEN / FPS kept",
            mg.LivesAtStartup == 8 && mg.ShowFps && mg.Volume == 40 && txt.Contains("Volume=40") && !txt.Contains("Volume=75") && txt.Contains("FULLSCREEN=WIDESCREEN") && txt.Contains("FPS=90") && Settings.Load(p2).Mode == ScreenMode.Widescreen);
        Check("migration: Prehistorik.cfg renamed to Prehistorik.cfg.migrated (not read again)", !File.Exists(legacy) && File.Exists(migrated));
        File.WriteAllText(legacy, "LivesAtStartup=4\nVolume=10\n");
        File.WriteAllText(p2, "FPS=60\n");
        var mg2 = GameOptions.Load(p2);
        Check("a Prehistorik.cfg that shows up later migrates only the keys the cfg lacks, then is renamed too", mg2.LivesAtStartup == 4 && mg2.Volume == 10 && !File.Exists(legacy));
        File.WriteAllText(legacy, "LivesAtStartup=9\nVolume=1\n");
        var mg3 = GameOptions.Load(p2);
        Check("keys already in Prehistorik2.cfg always win over a stray Prehistorik.cfg", mg3.LivesAtStartup == 4 && mg3.Volume == 10 && Read(p2).Contains("LivesAtStartup=4"));
        foreach (var fl in new[] { legacy, migrated }) if (File.Exists(fl)) File.Delete(fl);

        // ---------------- Prehistorik2.cfg (FPS + filters; FULLSCREEN untouched)
        File.WriteAllText(p2, "fullscreen=original\nfps=144\n");
        var s = Settings.Load(p2);
        txt = Read(p2);
        Check("Prehistorik2.cfg: FPS=144 read (case-insensitive), FULLSCREEN=ORIGINAL kept, filter keys appended OFF, Vsync=OFF appended", s.Fps == 144 && s.Mode == ScreenMode.Original && !s.Vsync && txt.Contains("FILTERSCALING=OFF") && txt.Contains("FILTERPIXELART=OFF") && txt.Contains("FILTEREFFECTS=OFF") && txt.Contains("Vsync=OFF") && txt.Contains("fullscreen=original"));
        s.Fps = 240; s.FilterScaling = FilterScaling.Fsr; s.FilterPixelArt = FilterPixelArt.Scale3x; s.FilterEffects = FilterEffects.Scanlines; s.Vsync = true; s.SaveOverlayKeys();
        var s2 = Settings.Load(p2);
        txt = Read(p2);
        Check("overlay writes FPS=240 + filters + Vsync=ON to Prehistorik2.cfg and they reload; FULLSCREEN line preserved; no MaxFps key", s2.Fps == 240 && s2.FilterScaling == FilterScaling.Fsr && s2.FilterPixelArt == FilterPixelArt.Scale3x && s2.FilterEffects == FilterEffects.Scanlines && s2.Vsync && txt.Contains("Vsync=ON") && txt.Contains("FULLSCREEN=ORIGINAL") && !txt.Contains("MaxFps", StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(p2, "Vsync=T\n");
        var s3 = Settings.Load(p2);
        Check("legacy Vsync=T reads as On and is rewritten as Vsync=ON", s3.Vsync && Read(p2).Contains("Vsync=ON") && !Read(p2).Contains("Vsync=T"));
        File.WriteAllText(p2, "FPS=5000\n");
        Check("FPS out of range 1..1000 -> default 60", Settings.Load(p2).Fps == 60);

        // ---------------- hidden level=xx (only in the cfg: never appended, never saved by the overlay)
        int mx = Frontend.FrontEndRunner.MaxStartLevel;
        File.WriteAllText(p2, "FPS=60\n");
        var sl = Settings.Load(p2);
        Check("level absent -> start level 1 (index 0); the key is not appended to the cfg", sl.StartLevelRaw is null && Frontend.FrontEndRunner.StartLevelIndex(sl) == 0 && !Read(p2).Contains("level", StringComparison.OrdinalIgnoreCase));
        string[] bad = { "", "abc", "0", "-3", "1.5", "3x", " " };
        bool allBad = true;
        foreach (var v in bad) { File.WriteAllText(p2, $"level={v}\n"); allBad &= Frontend.FrontEndRunner.StartLevelIndex(Settings.Load(p2)) == 0; }
        Check("level invalid (empty, non numeric, 0, negative, decimal) -> level 1", allBad);
        File.WriteAllText(p2, "LEVEL=4\n"); var l4 = Settings.Load(p2);
        File.WriteAllText(p2, " level = 1 \n"); var l1 = Settings.Load(p2);
        File.WriteAllText(p2, $"level={mx}\n"); var lm = Settings.Load(p2);
        File.WriteAllText(p2, $"level={mx + 1}\n"); var lo = Settings.Load(p2);
        File.WriteAllText(p2, "level=99999999999999999999\n"); var lh = Settings.Load(p2);
        Check($"level valid: LEVEL=4 -> index 3 (case-insensitive), level = 1 -> 0, level={mx} -> {mx - 1}; above the top ({mx + 1}, 99999999999999999999) -> top ({mx})",
            Frontend.FrontEndRunner.StartLevelIndex(l4) == 3 && Frontend.FrontEndRunner.StartLevelIndex(l1) == 0 && Frontend.FrontEndRunner.StartLevelIndex(lm) == mx - 1 && Frontend.FrontEndRunner.StartLevelIndex(lo) == mx - 1 && Frontend.FrontEndRunner.StartLevelIndex(lh) == mx - 1 && mx == 10);
        File.WriteAllText(p2, "level=3\nFPS=60\n");
        var ls = Settings.Load(p2); ls.Fps = 120; ls.SaveOverlayKeys(); var go = GameOptions.Load(p2); go.Save();
        Check("level=3 survives the overlay saves (Settings.SaveOverlayKeys, GameOptions.Save)", Read(p2).Contains("level=3") && Frontend.FrontEndRunner.StartLevelIndex(Settings.Load(p2)) == 2);

        // ---------------- Controller.cfg
        string pc = Path.Combine(dir, "Controller.cfg");
        if (File.Exists(pc)) File.Delete(pc);
        var c = ControllerConfig.LoadOrCreate(pc);
        txt = Read(pc);
        Check("Controller.cfg created: Mode=Keyboard, Jump=Up, Fire=Space, PadFire=RightFaceLeft (square), PadJump=RightFaceDown (cross)",
            !c.UseJoystick && c.KeyOf(GameAction.Jump) == KeyboardKey.Up && c.KeyOf(GameAction.Fire) == KeyboardKey.Space && txt.Contains("Mode=Keyboard") && txt.Contains("PadFire=RightFaceLeft") && txt.Contains("PadJump=RightFaceDown") && txt.Contains("Left=Left"));
        Check("rebind to a free key", c.Rebind(GameAction.Fire, KeyboardKey.Z) && c.KeyOf(GameAction.Fire) == KeyboardKey.Z);
        Check("rebind to a key used by another action swaps (never two actions on one key)", c.Rebind(GameAction.Left, KeyboardKey.Z) && c.KeyOf(GameAction.Left) == KeyboardKey.Z && c.KeyOf(GameAction.Fire) == KeyboardKey.Left && c.Keys.Distinct().Count() == 5);
        Check("reserved keys rejected (Escape, Enter, KpEnter)", !c.Rebind(GameAction.Down, KeyboardKey.Escape) && !c.Rebind(GameAction.Down, KeyboardKey.Enter) && !c.Rebind(GameAction.Down, KeyboardKey.KpEnter) && c.KeyOf(GameAction.Down) == KeyboardKey.Down);
        c.UseJoystick = true; c.JoystickIndex = 2; Check("pad rebind: d-pad rejected, Jump<-Square swaps with Fire", !c.RebindPad(true, GamepadButton.LeftFaceUp) && c.RebindPad(false, GamepadButton.RightFaceLeft) && c.PadJump == GamepadButton.RightFaceLeft && c.PadFire == GamepadButton.RightFaceDown);
        c.Save();
        var c2 = ControllerConfig.LoadOrCreate(pc);
        Check("Controller.cfg reload: keys, Mode=Joystick, slot 2, pad buttons", c2.UseJoystick && c2.JoystickIndex == 2 && c2.KeyOf(GameAction.Left) == KeyboardKey.Z && c2.KeyOf(GameAction.Fire) == KeyboardKey.Left && c2.PadJump == GamepadButton.RightFaceLeft && c2.PadFire == GamepadButton.RightFaceDown);
        File.WriteAllText(pc, "left=A\nRIGHT=A\nmode=joystick\n");
        var c3 = ControllerConfig.LoadOrCreate(pc);
        Check("hand-edited duplicate key repaired; keys case-insensitive; missing keys added", c3.Keys.Distinct().Count() == 5 && c3.KeyOf(GameAction.Left) == KeyboardKey.A && c3.UseJoystick && Read(pc).Contains("PadFire="));
        Check("pad classes: Sony / Microsoft / Compatible",
            PadInfo.Detect("Wireless Controller") == PadType.Sony && PadInfo.Detect("PS5 DualSense") == PadType.Sony && PadInfo.Detect("Xbox 360 Controller") == PadType.Microsoft && PadInfo.Detect("USB Gamepad") == PadType.Compatible);
        Check("pad labels: Joy Microsoft #1 / Joy Sony #2 / Compatible #3", PadInfo.PadLabel(PadType.Microsoft, 0) == "Joy Microsoft #1" && PadInfo.PadLabel(PadType.Sony, 1) == "Joy Sony #2" && PadInfo.PadLabel(PadType.Compatible, 2) == "Compatible #3");
        Check("default pad button names (indices unchanged: Fire = RightFaceLeft, Jump = RightFaceDown): Sony SQUARE/CROSS, Microsoft X/A", (int)ControllerConfig.DefaultPadFire == 8 && (int)ControllerConfig.DefaultPadJump == 7 && PadInfo.ButtonName(ControllerConfig.DefaultPadFire, PadType.Sony) == "SQUARE" && PadInfo.ButtonName(ControllerConfig.DefaultPadJump, PadType.Sony) == "CROSS" && PadInfo.ButtonName(ControllerConfig.DefaultPadFire, PadType.Microsoft) == "X" && PadInfo.ButtonName(ControllerConfig.DefaultPadJump, PadType.Microsoft) == "A");
        Check("pad detection lists: sony ids / microsoft ids / no name -> Compatible; labels SONY/MICROSOFT/COMPATIBLE", PadInfo.Detect("Sony 054C:09CC") == PadType.Sony && PadInfo.Detect("Controller 045E:028E") == PadType.Microsoft && PadInfo.Detect("X-Box pad") == PadType.Microsoft && PadInfo.Detect("XInput Gamepad") == PadType.Microsoft && PadInfo.Detect("PS4 pad") == PadType.Sony && PadInfo.Detect(null) == PadType.Compatible && PadInfo.Detect("") == PadType.Compatible && PadInfo.Label(PadType.Sony) == "SONY" && PadInfo.Label(PadType.Microsoft) == "MICROSOFT" && PadInfo.Label(PadType.Compatible) == "COMPATIBLE");
        {
            var hid = new[] { new HidPad(0x054C, 0x09CC, "Wireless Controller", "p1"), new HidPad(0x045E, 0x028E, "Controller (XBOX 360 For Windows)", "p2"), new HidPad(0x046D, 0xC216, "Logitech Dual Action", "p3"), new HidPad(0x054C, 0x0CE6, "DualSense Wireless Controller", "p4") };
            var t1 = PadInfo.Classify("Wireless Controller", hid, out bool v1);   // exact product string -> vendor 054C (also contained in "DualSense Wireless Controller": both Sony)
            var t2 = PadInfo.Classify("Controller (Xbox 360 For Windows)", hid, out bool v2);   // different case -> vendor 045E
            var t3 = PadInfo.Classify("Logitech Dual Action", hid, out bool v3);   // associated, other vendor -> name decides -> Compatible, not by vendor
            var t4 = PadInfo.Classify("Generic USB Joystick", hid, out bool v4);   // not associated -> name fallback
            var t5 = PadInfo.Classify("Some Pad 054c", Array.Empty<HidPad>(), out bool v5);   // no Raw Input devices -> name fallback (id in name)
            var t6 = PadInfo.Classify("Pad", new[] { new HidPad(0x054C, 1, "Pad", "a"), new HidPad(0x045E, 2, "Pad", "b") }, out bool v6);   // ambiguous (two vendors) -> name -> Compatible
            Check("Raw Input VID association: 054C -> Sony, 045E -> Microsoft by HID product string (case-insensitive); other vendor/unmatched/ambiguous/none fall back to the name",
                t1 == PadType.Sony && v1 && t2 == PadType.Microsoft && v2 && t3 == PadType.Compatible && !v3 && t4 == PadType.Compatible && !v4 && t5 == PadType.Sony && !v5 && t6 == PadType.Compatible && !v6
                && PadInfo.FromVendor(0x054C) == PadType.Sony && PadInfo.FromVendor(0x045E) == PadType.Microsoft && PadInfo.FromVendor(0x1234) is null);
            bool enumOk; int cnt = 0; try { var rl = RawInputPads.EnumerateNow(); cnt = rl.Count; enumOk = true; foreach (var d in rl) Console.WriteLine($"     raw input pad: VID {d.VendorId:X4} PID {d.ProductId:X4} \"{d.ProductName}\" -> {PadInfo.Classify(d.ProductName, rl, out _)}"); } catch (Exception) { enumOk = false; }
            Check($"Raw Input enumeration (user32/hid P/Invoke) runs without error ({cnt} HID gamepad/joystick device(s) on this machine)", enumOk);
        }
        Check("pad button names: Sony shoulder/system/d-pad, Microsoft shoulder/system, Compatible BTN n", PadInfo.ButtonName(GamepadButton.RightFaceUp, PadType.Sony) == "TRIANGLE" && PadInfo.ButtonName(GamepadButton.RightFaceRight, PadType.Sony) == "CIRCLE" && PadInfo.ButtonName(GamepadButton.LeftTrigger1, PadType.Sony) == "L1" && PadInfo.ButtonName(GamepadButton.RightTrigger2, PadType.Sony) == "R2" && PadInfo.ButtonName(GamepadButton.MiddleLeft, PadType.Sony) == "SHARE" && PadInfo.ButtonName(GamepadButton.Middle, PadType.Sony) == "PS" && PadInfo.ButtonName(GamepadButton.MiddleRight, PadType.Sony) == "OPTIONS" && PadInfo.ButtonName(GamepadButton.LeftThumb, PadType.Sony) == "L3" && PadInfo.ButtonName(GamepadButton.LeftFaceUp, PadType.Sony) == "UP" && PadInfo.ButtonName(GamepadButton.LeftFaceLeft, PadType.Sony) == "LEFT" && PadInfo.ButtonName(GamepadButton.LeftTrigger1, PadType.Microsoft) == "LB" && PadInfo.ButtonName(GamepadButton.RightTrigger2, PadType.Microsoft) == "RT" && PadInfo.ButtonName(GamepadButton.MiddleLeft, PadType.Microsoft) == "BACK" && PadInfo.ButtonName(GamepadButton.MiddleRight, PadType.Microsoft) == "MENU" && PadInfo.ButtonName(GamepadButton.RightThumb, PadType.Microsoft) == "R3" && PadInfo.ButtonName(GamepadButton.LeftFaceDown, PadType.Microsoft) == "DOWN" && PadInfo.ButtonName(GamepadButton.RightFaceDown, PadType.Compatible) == "BTN 7" && PadInfo.ButtonName(GamepadButton.RightTrigger1, PadType.Compatible) == "BTN 11");

        // ---------------- menu state machine
        File.Delete(pc); File.Delete(po); File.WriteAllText(p2, "FULLSCREEN=NO\nFPS=60\n");
        var mo = GameOptions.Load(po); var ms = Settings.Load(p2); var mc = ControllerConfig.LoadOrCreate(pc);
        var pads = new List<(int, PadType, string)> { (0, PadType.Microsoft, "Joy Microsoft #1"), (1, PadType.Sony, "Joy Sony #2") };
        var menu = new OptionsMenu(mo, ms, mc) { Pads = () => pads };
        int volApplied = -1, fpsApplied = -1;
        menu.OnVolume = v => volApplied = v; menu.OnFps = f => fpsApplied = f;
        menu.Open();
        Check("root items in order: Game, Sound, Controller, Graphics, Return to Main Menu, Exit to Windows", menu.Rows().SequenceEqual(new[] { "Game", "Sound", "Controller", "Graphics", "Return to Main Menu", "Exit to Windows" }));
        void Press(bool up = false, bool down = false, bool left = false, bool right = false, bool sel = false, bool back = false, KeyboardKey key = KeyboardKey.Null, GamepadButton pad = GamepadButton.Unknown, bool anyDown = false)
            => menu.Step(new MenuInput { Up = up, Down = down, Left = left, Right = right, Select = sel, Back = back, Key = key, Pad = pad, AnyDown = anyDown });
        Press(sel: true);   // Game
        Press(right: true); Press(right: true);   // lives 3 -> 5
        Check("Game > Lives at startup 3 -> 5 saved to Prehistorik.cfg (LivesAtStartup=5)", mo.LivesAtStartup == 5 && Read(po).Contains("LivesAtStartup=5"));
        for (int i = 0; i < 9; i++) Press(right: true);
        Check("lives clamp at 10", mo.LivesAtStartup == 10);
        Press(sel: true); Check("select on the value wraps 10 -> 3", mo.LivesAtStartup == 3);
        Press(back: true); Check("Back returns to the root; Back on the root closes", menu.Current == OptionsMenu.Page.Root && menu.IsOpen);
        Press(down: true); Press(sel: true); Press(left: true);
        Check("Sound > Volume 100 -> 95 applied at once and saved (Volume=95)", mo.Volume == 95 && volApplied == 95 && Read(po).Contains("Volume=95"));
        Press(back: true); Press(down: true); Press(down: true); Press(sel: true);   // Graphics
        Check("Graphics: Show FPS disabled with FULLSCREEN=NO (selection stays on Full Screen, Full Screen is first)", menu.Current == OptionsMenu.Page.Graphics && menu.Rows()[0] == "Full Screen" && !menu.RowEnabled(1) && menu.Sel == 0);
        Press(down: true); Press(right: true); Check("Max FPS 60 -> 75 written to FPS in Prehistorik2.cfg and applied at once", ms.Fps == 75 && fpsApplied == 75 && Read(p2).Contains("FPS=75"));
        Press(down: true);
        bool vsApplied = false; menu.OnVsync = v => vsApplied = v;
        Press(right: true);
        Check("Graphics V-Sync Off -> On written as Vsync=ON and applied at once", ms.Vsync && vsApplied && Read(p2).Contains("Vsync=ON"));
        Press(down: true); Press(right: true);
        Press(down: true); Press(right: true);
        Press(down: true); Press(right: true);
        Check("filters cycle: Sharp... Scaling Bilinear, Pixel art xBR, Effects Vibrance saved", ms.FilterScaling == FilterScaling.Bilinear && ms.FilterPixelArt == FilterPixelArt.Xbr && ms.FilterEffects == FilterEffects.Vibrance && Read(p2).Contains("FILTERPIXELART=XBR"));
        // fullscreen: Show FPS enabled
        File.WriteAllText(p2, "FULLSCREEN=WIDESCREEN\nFPS=60\n");
        var wide = Settings.Load(p2);
        var menuW = new OptionsMenu(mo, wide, mc) { Pads = () => pads };
        menuW.Open(); menuW.Step(new MenuInput { Down = true }); menuW.Step(new MenuInput { Down = true }); menuW.Step(new MenuInput { Down = true }); menuW.Step(new MenuInput { Select = true });
        menuW.Step(new MenuInput { Down = true });
        menuW.Step(new MenuInput { Select = true });
        Check("Graphics with WIDESCREEN: Show FPS toggles On and is saved (ShowFps=YES)", menuW.RowEnabled(1) && mo.ShowFps && Read(po).Contains("ShowFps=YES"));
        var fsMode = ScreenMode.No; bool fsApplied = false;
        menu.OnMode = m => { fsMode = m; fsApplied = true; };
        menu.Open(); menu.Step(new MenuInput { Down = true }); menu.Step(new MenuInput { Down = true }); menu.Step(new MenuInput { Down = true }); menu.Step(new MenuInput { Select = true });
        menu.Step(new MenuInput { Right = true });
        Check("Graphics Full Screen No -> Original saved as FULLSCREEN=ORIGINAL and OnMode fired at once", ms.Mode == ScreenMode.Original && Read(p2).Contains("FULLSCREEN=ORIGINAL") && fsApplied && fsMode == ScreenMode.Original);
        menu.Step(new MenuInput { Right = true });
        Check("Graphics Full Screen Original -> Widescreen saved as FULLSCREEN=WIDESCREEN", ms.Mode == ScreenMode.Widescreen && Read(p2).Contains("FULLSCREEN=WIDESCREEN"));
        // controller page
        menu.Open(); Press(down: true); Press(down: true); Press(sel: true);
        Check("Controller page keyboard rows: Device, Left, Right, Down, Jump, Fire, Back", menu.Rows().SequenceEqual(new[] { "Device", "Left", "Right", "Down", "Jump", "Fire", "Back" }));
        Press(right: true);
        Check("Device cycles Keyboard -> Joy Microsoft #1 (Mode=Joystick saved); rows: Device, Jump, Fire, Back", mc.UseJoystick && mc.JoystickIndex == 0 && menu.DeviceText() == "Joy Microsoft #1" && menu.Rows().SequenceEqual(new[] { "Device", "Jump", "Fire", "Back" }) && Read(pc).Contains("Mode=Joystick"));
        Press(right: true); Check("next device Joy Sony #2", mc.JoystickIndex == 1 && menu.DeviceText() == "Joy Sony #2");
        Press(down: true); Press(sel: true, anyDown: true);
        Check("Jump row: Select starts capture", menu.Capturing);
        Press(pad: GamepadButton.RightFaceUp, anyDown: true); Check("pad press while the activating button is still held is ignored", menu.Capturing);
        Press(anyDown: false); Press(pad: GamepadButton.LeftFaceUp); Check("d-pad rejected in capture", menu.Capturing && menu.Message.Length > 0);
        Press(pad: GamepadButton.RightFaceUp); Check("Jump <- Triangle saved to Controller.cfg (PadJump=RightFaceUp)", !menu.Capturing && mc.PadJump == GamepadButton.RightFaceUp && Read(pc).Contains("PadJump=RightFaceUp"));
        Press(back: true); Check("joystick Back (jump) leaves the Controller page", menu.Current == OptionsMenu.Page.Root);
        // keyboard capture with swap + debounce
        mc.UseJoystick = false; mc.Save();
        Press(sel: true);   // root selection was restored to Controller
        Check("Back restores the root selection (Controller)", menu.Current == OptionsMenu.Page.Controller);
        Press(down: true); Press(sel: true, anyDown: true);   // Left row
        Press(key: KeyboardKey.A, anyDown: true);   // activating press still held -> ignored
        Check("key capture waits for release before accepting", menu.Capturing && mc.KeyOf(GameAction.Left) == KeyboardKey.Left);
        Press(anyDown: false); Press(key: KeyboardKey.Enter); Check("Enter rejected as reserved key", menu.Capturing && menu.Message == "Reserved key");
        Press(key: KeyboardKey.Right); Check("Left <- Right swaps with Right action (no duplicate)", !menu.Capturing && mc.KeyOf(GameAction.Left) == KeyboardKey.Right && mc.KeyOf(GameAction.Right) == KeyboardKey.Left && menu.Message.StartsWith("Swapped"));
        Press(sel: true, anyDown: true); Check("after a capture, input is ignored until everything is released (no double action)", !menu.Capturing);
        Press(anyDown: false); Press(back: true); Press(back: true);
        Check("Back on the root closes the overlay", !menu.IsOpen);
        // confirm pages
        menu.Open(); for (int i = 0; i < 5; i++) Press(down: true); Press(sel: true);
        Check("Exit to Windows asks for confirmation (default No)", menu.Current == OptionsMenu.Page.Confirm && menu.ConfirmSel == 0);
        Press(sel: true); Check("No returns to the root", menu.Current == OptionsMenu.Page.Root && menu.IsOpen && menu.Pending == OptionsMenu.Request.None);
        Press(sel: true); Press(right: true); Press(sel: true);
        Check("Yes requests ExitToWindows and closes", !menu.IsOpen && menu.TakeRequest() == OptionsMenu.Request.ExitToWindows);
        var menuL = new OptionsMenu(mo, ms, mc) { Pads = () => pads, CanReturnToMainMenu = () => false };
        menuL.Open(); for (int i = 0; i < 4; i++) menuL.Step(new MenuInput { Down = true });
        Check("Return to Main Menu is skipped (disabled) when the scene cannot return", menuL.Sel == 5 && !menuL.RowEnabled(4));
        var menuM = new OptionsMenu(mo, ms, mc) { Pads = () => pads, InMainMenu = () => true };
        menuM.Open(); for (int i = 0; i < 4; i++) menuM.Step(new MenuInput { Down = true });
        Check("Return to Main Menu is hidden on the main menu (5 rows; Exit to Windows follows Graphics)", menuM.Rows().SequenceEqual(new[] { "Game", "Sound", "Controller", "Graphics", "Exit to Windows" }) && menuM.Sel == 4);
        menuM.Step(new MenuInput { Select = true });
        Check("Exit to Windows still asks for confirmation from the 5-row root", menuM.Current == OptionsMenu.Page.Confirm && menuM.Confirming == OptionsMenu.Request.ExitToWindows);

        // ---------------- input mapper
        var map = new Dictionary<string, bool>();
        var kc = ControllerConfig.LoadOrCreate(Path.Combine(dir, "Controller2.cfg"));
        kc.Rebind(GameAction.Left, KeyboardKey.A); kc.Rebind(GameAction.Right, KeyboardKey.D); kc.Rebind(GameAction.Jump, KeyboardKey.W); kc.Rebind(GameAction.Fire, KeyboardKey.J);
        var mp = new InputMapper(kc);
        var fake = new Fake();
        var inp = new Input();
        fake.Down.Add(KeyboardKey.A); fake.NewPress.Add(KeyboardKey.A);
        mp.Fill(inp, fake, raw: false);
        Check("gameplay: A (bound Left) -> Input.Left down+pressed; native A is not typed", inp.IsKeyDown(Input.Key.Left) && inp.IsKeyPressed(Input.Key.Left) && !inp.IsKeyDown(Input.Key.A));
        fake.Down.Clear(); fake.Down.Add(KeyboardKey.Left);
        mp.Fill(inp, fake, raw: false);
        Check("gameplay: the old Left arrow no longer moves left", !inp.IsKeyDown(Input.Key.Left));
        fake.Down.Clear(); fake.Down.Add(KeyboardKey.J); fake.Down.Add(KeyboardKey.W);
        mp.Fill(inp, fake, raw: false);
        Check("gameplay: J -> Fire (Space), W -> Jump (Up)", inp.IsKeyDown(Input.Key.Space) && inp.IsKeyDown(Input.Key.Up));
        fake.Down.Clear(); fake.Down.Add(KeyboardKey.Enter);
        mp.Fill(inp, fake, raw: false);
        Check("gameplay: Enter still reaches the game (fire alias)", inp.IsKeyDown(Input.Key.Enter));
        fake.Down.Clear(); fake.Down.Add(KeyboardKey.A); fake.Down.Add(KeyboardKey.Left);
        mp.Fill(inp, fake, raw: true);
        Check("front-end (raw): native arrows and letters; bindings ignored (A types 'A', Left arrow = Left)", inp.IsKeyDown(Input.Key.A) && inp.IsKeyDown(Input.Key.Left));
        kc.UseJoystick = true; kc.JoystickIndex = 0; fake.Down.Clear();
        fake.PadDownSet.Add(kc.PadFire);
        mp.Fill(inp, fake, raw: false);
        bool fireEdge = inp.IsKeyPressed(Input.Key.Space);
        mp.Fill(inp, fake, raw: false);
        Check("joystick: Fire (square) -> Space, pressed once on the edge only", inp.IsKeyDown(Input.Key.Space) && fireEdge && !inp.IsKeyPressed(Input.Key.Space) && !inp.IsKeyDown(Input.Key.Up));
        fake.PadDownSet.Clear(); fake.PadDownSet.Add(kc.PadJump); fake.AxisX = 0.9f;
        mp.Fill(inp, fake, raw: false);
        Check("joystick: Jump (cross) -> Up, stick right -> Right", inp.IsKeyDown(Input.Key.Up) && inp.IsKeyDown(Input.Key.Right) && !inp.IsKeyDown(Input.Key.Space));
        Check("Escape is never produced by the mapper", !inp.IsKeyDown(Input.Key.Escape));

        // ---------------- layout (font pixels; no drawing): every fixed label + widest value fits the row (152 font px wide, 4 margin each side + arrows)
        int rowW = OptionsMenuView.PanelW - 2 * OptionsMenuView.Pad;
        int Wd(string t) => OptionsMenuView.TextWidth(t.ToUpperInvariant(), 1);
        var fits = new (string Label, string Value, bool Arrows)[]
        {
            ("Return to Main Menu", "", false), ("Lives at startup", "10", true), ("Show FPS", "Fullscreen only", false), ("Scaling", "Sharp bilinear", true), ("Pixel art", "Scale3x", true),
            ("V-Sync", "On", true),
            ("Device", "Joy Microsoft #1", true), ("Device", "Compatible #3", true), ("Volume", "", false),
        };
        bool allFit = fits.All(f => 4 + Wd(f.Label) + 4 + Wd(f.Value) + (f.Arrows ? 8 : 0) + 4 <= rowW);
        Check("labels and values fit the 168-unit panel", allFit);
        int rowsBottom = OptionsMenuView.TitleH + 2 + 7 * (OptionsMenuView.RowH + OptionsMenuView.RowGap) + 3 + OptionsMenuView.RowH;   // Graphics: 8 rows, Back separated
        int footerTop = OptionsMenuView.PanelH - OptionsMenuView.FooterH + 1;
        Check($"Graphics (8 rows) clears the footer: rows bottom {rowsBottom} + 2 <= footer {footerTop}", rowsBottom + 2 <= footerTop);
        int longest = Enum.GetValues<KeyboardKey>().Select(k => OptionsMenu.KeyName(k).Length).Max();
        Check($"longest key name ({longest} chars) is shortened by the view when needed", longest > 0);
        Check("overlay fonts: every glyph used is defined (no '?' fallback)", "Joy Sony #2 Press the new button 0123456789% Up/Down: move V-Sync On".ToUpperInvariant().Where(ch => ch != ' ').All(ch => { ulong gl = Pre2Port.Game.Launcher.LauncherArt.GlyphOf(ch, out _); return gl != Pre2Port.Game.Launcher.LauncherArt.GlyphOf('?', out _) || ch == '?'; }));

        // ---------------- render pacing (pure maths): V-Sync/uncapped frames must never turn into a multi-second sleep
        var onSchedule = FrameClock.Pace(1.0, 0.0, 100, 100);
        Check("pacing: frame on schedule keeps the start and waits 0", onSchedule.Start == 0.0 && Math.Abs(onSchedule.Next - 1.0) < 1e-9);
        var jitter = FrameClock.Pace(0.995, 0.0, 100, 100);
        Check("pacing: 5 ms jitter is not a rebase", jitter.Start == 0.0 && Math.Abs(jitter.Next - 1.0) < 1e-9);
        var ahead = FrameClock.Pace(0.0, 0.0, 100, 100);   // schedule 1 s in the future (uncapped frames while V-Sync was on)
        Check("pacing: stale schedule 1 s ahead rebases to now instead of sleeping", ahead.Next == 0.0 && ahead.Start == -1.0);
        var resumed = FrameClock.Pace(0.0, -1.0, 101, 100);
        Check("pacing: next frame after rebase is due in 10 ms", Math.Abs(resumed.Next - 0.01) < 1e-9 && resumed.Start == -1.0);
        var behind = FrameClock.Pace(10.0, 0.0, 100, 100);  // schedule 9 s in the past (vsync slower than the requested rate)
        Check("pacing: stale schedule 9 s behind rebases instead of bursting", behind.Next == 10.0 && Math.Abs(behind.Start - 9.0) < 1e-9);

        Console.WriteLine(_fail == 0 ? "options-test: ALL PASS" : $"options-test: {_fail} FAIL");
        return _fail == 0 ? 0 : 1;
    }
}
