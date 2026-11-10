using Pre2Port.Native;

namespace Pre2Port.Recovered;

/// <summary>Status-bar values the HUD draws (pre2/recovered/render_model.py HudState). <c>Score</c> is the DISPLAYED score (internal * 10).</summary>
public readonly record struct HudValues(long Score, int Lives, int Energy, int BonusMask);

/// <summary>
/// Gameplay HUD (EGA rows 176-199): port of <c>pre2/recovered/hud.py</c> (blit_hud_glyph 1030:473D, draw_hud 1030:45B8, draw_status_bar
/// 1030:4580, effective_bonus_mask 1030:4683) plus the value read of <c>pre2/views/render_state.py _hud_state</c>. Pure functions over four
/// EGA plane buffers; the chrome/font bytes come from <see cref="Pre2Port.Game.Level.HudAssets"/> (ALLFONTS.SQZ).
/// </summary>
public static class Hud
{
    public const int BarDi = 0x1B80;            // status-bar top-left offset within the page (row 176)
    public const int BarPlaneBytes = 0x398;     // 40 bytes/row x 23 rows, per plane
    public const int LivesDi = 0x1CED, ScoreDi = 0x1CF1, EnergyDi = 0x1D01;
    public const int MaxHearts = 3, ScoreDigits = 6;
    public const int HeartFull = 0x0A, HeartEmpty = 0x0B, BonusGlyph0 = 0x0C;
    public static readonly int[] BonusDi = { 0x1C91, 0x1BF2, 0x1CE3, 0x1C6C, 0x1C1D };   // B,O,N,U,S [0x6F86]
    public const int GlyphBase = 0xE60 + 0x7B0; // 0x1610 font offset of glyph 0
    public const int GlyphBytes = 0x60, GlyphRows = 0x0C;
    const int RowStride = 0x28;

    // DGROUP offsets of the live values (render_state.py: _LIVES/_ENERGY/_SCORE/_BONUS_MASK/_BONUS_FLASH/_FRAME_CTR_B)
    public const int OffLives = 0x27D8, OffEnergy = 0x27D6, OffScore = 0x6C0E, OffBonusMask = 0x6CA7, OffBonusFlash = 0x6C00, OffFrameCtr = 0x6BD5;

    /// <summary>[asm 4683] BONUS celebration: all five letters flash by frame parity (0x1F odd, 0 even); otherwise the collected mask.</summary>
    public static int EffectiveBonusMask(int collected, bool flashActive, int frameParity)
        => flashActive ? ((frameParity & 1) != 0 ? 0x1F : 0) : collected & 0xFF;

    /// <summary>The values the HUD draws, read from the game memory exactly like render_state._hud_state (without the death-bounce freeze cache).</summary>
    public static HudValues Read(LevelState s)
    {
        long score = ((long)s.Rw(OffScore) | ((long)s.Rw(OffScore + 2) << 16)) * 10;
        int bonus = EffectiveBonusMask(s.Rb(OffBonusMask), s.Rb(OffBonusFlash) != 0, s.Rb(OffFrameCtr));
        return new HudValues(score, s.Rb(OffLives), s.Rb(OffEnergy), bonus);
    }

    /// <summary>
    /// The values to DRAW (render_state._hud_state with the freeze cache). While a death/respawn is armed ([0x6BE4]==1) or game over is
    /// signalled ([0x6BE5]!=0; NOT the boss-hit flash [0x6BE4]==2) the original does not call 45AF, so the HUD keeps the last values drawn
    /// before the death (bounce, 30C6 fade, 9B23 scene). Otherwise the live values are drawn and cached. Restored automatically at the
    /// respawn (flag cleared -> live again); a new LevelState (cold boot) has an empty cache.
    /// </summary>
    public static HudValues ReadForDraw(LevelState s)
    {
        var live = Read(s);
        bool dying = s.Rb(0x6BE4) == 1 || s.Rb(0x6BE5) != 0;
        if (dying) return s.HudFrozenPreDeath is HudValues frozen ? frozen : live;
        s.HudFrozenPreDeath = live;
        return live;
    }

    /// <summary>[asm 473D] one 16x12 planar glyph (plane-major, 12 rows x 2 bytes per plane) from the font at <c>0x1610 + glyph*0x60</c> to <paramref name="di"/>.</summary>
    public static void BlitGlyph(byte[][] planes, int glyph, int di, byte[] font)
    {
        int src = GlyphBase + (glyph & 0xFF) * GlyphBytes;
        for (int p = 0; p < 4; p++)
        {
            int d = di & 0xFFFF;
            for (int row = 0; row < GlyphRows; row++)
            {
                planes[p][d] = font[src];
                planes[p][(d + 1) & 0xFFFF] = font[src + 1];
                src += 2;
                d = (d + RowStride) & 0xFFFF;
            }
        }
    }

    /// <summary>[asm 4580] static 320x23 chrome (4 planes x 0x398, plane-major) into <c>page + 0x1B80</c> (offsets wrap at 0xFFFF).</summary>
    public static void DrawStatusBar(byte[][] planes, int page, byte[] bar)
    {
        int di = (page + BarDi) & 0xFFFF;
        for (int p = 0; p < 4; p++)
            for (int i = 0; i < BarPlaneBytes; i++)
                planes[p][(di + i) & 0xFFFF] = bar[p * BarPlaneBytes + i];
    }

    /// <summary>[asm 45B8] lives digit, 6-digit score (+ fixed trailing 0), energy hearts, collected BONUS letters.</summary>
    public static void DrawHud(byte[][] planes, HudValues hud, byte[] font, int page)
    {
        BlitGlyph(planes, Math.Min(hud.Lives, 9) & 0xFF, (LivesDi + page) & 0xFFFF, font);
        int di = (ScoreDi + page) & 0xFFFF;
        long internalScore = (hud.Score / 10) % 1000000L;
        foreach (char ch in internalScore.ToString("D6"))
        {
            BlitGlyph(planes, ch - '0', di, font);
            di = (di + 2) & 0xFFFF;
        }
        BlitGlyph(planes, 0, di, font);
        di = (EnergyDi + page) & 0xFFFF;
        int full = Math.Min(Math.Max(hud.Energy, 0), MaxHearts);
        for (int i = 0; i < MaxHearts; i++)
        {
            BlitGlyph(planes, i < full ? HeartFull : HeartEmpty, di, font);
            di = (di + 2) & 0xFFFF;
        }
        for (int i = 0; i < 5; i++)
            if (((hud.BonusMask >> i) & 1) != 0)
                BlitGlyph(planes, (BonusGlyph0 + i) & 0xFF, (BonusDi[i] + page) & 0xFFFF, font);
    }
}
