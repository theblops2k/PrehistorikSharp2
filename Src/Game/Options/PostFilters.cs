using System.Numerics;
using Raylib_cs;

namespace Pre2Port.Game.Options;

/// <summary>
/// Presentation filters of the ESC overlay (Graphics), the same set and shaders as CoolSpotPort (Program.cs PostFs / ScalerFs), adapted to the Presenter's full-texture present:
/// scaling group Off / Bilinear / Sharp Bilinear / FSR 1.0 (EASU + RCAS), pixel-art group Off / xBR lv2 / Scale3x, effects group Off / Vibrance / Scanlines.
/// Priority as in CoolSpot: pixel art (xBR/Scale3x) over FSR; Sharp Bilinear and Vibrance share PostFs, the scaler shader (ScalerFs) also applies Vibrance. A shader that does not
/// compile reports once on the console and the filter has no effect (plain Point/Bilinear draw).
/// </summary>
public static class PostFilters
{
    public const float VibranceAmount = 0.6f;
    const string PostFs = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; uniform vec4 colDiffuse;
uniform vec2 texSize; uniform float sharpScale; uniform float vibrance;
out vec4 finalColor;
void main()
{
    vec2 uv = fragTexCoord;
    if (sharpScale > 0.0)
    {
        vec2 texel = uv * texSize;
        float s = max(sharpScale, 1.0);
        float rr = 0.5 - 0.5 / s;
        vec2 cd = fract(texel) - 0.5;
        vec2 f = (cd - clamp(cd, -rr, rr)) * s + 0.5;
        uv = (floor(texel) + f) / texSize;
    }
    vec4 c = texture(texture0, uv) * colDiffuse * fragColor;
    if (vibrance > 0.0)
    {
        float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
        float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
        c.rgb = clamp(mix(vec3(l), c.rgb, 1.0 + vibrance * (1.0 - (mx - mn))), 0.0, 1.0);
    }
    finalColor = c;
}";
    const string ScalerFs = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; uniform vec4 colDiffuse;
uniform vec2 texSize; uniform vec4 bounds; uniform vec2 outScale; uniform int mode; uniform float vibrance;
out vec4 finalColor;
vec3 T(ivec2 q)
{
    q = clamp(q, ivec2(bounds.xy), ivec2(bounds.zw) - 1);
    return texelFetch(texture0, ivec2(q.x, int(texSize.y) - 1 - q.y), 0).rgb;
}
// ---- xBR lv2 ----
float LUM(vec3 c) { return dot(c, vec3(14.352, 28.176, 5.472)); }
ivec2 rot(ivec2 o, int k) { for (int i = 0; i < k; i++) o = ivec2(o.y, -o.x); return o; }
float WD(float a, float b, float c, float d, float e, float f, float g, float h)
{ return abs(a - b) + abs(a - c) + abs(d - e) + abs(d - f) + 4.0 * abs(g - h); }
vec3 xbr(ivec2 ip, vec2 fp)
{
    const vec4 Ao = vec4(1.0, -1.0, -1.0, 1.0), Bo = vec4(1.0, 1.0, -1.0, -1.0), Co = vec4(1.5, 0.5, -0.5, 0.5);
    const vec4 Ax = vec4(1.0, -1.0, -1.0, 1.0), Bx = vec4(0.5, 2.0, -0.5, -2.0), Cx = vec4(1.0, 1.0, -0.5, 0.0);
    const vec4 Ay = vec4(1.0, -1.0, -1.0, 1.0), By = vec4(2.0, 0.5, -2.0, -0.5), Cy = vec4(2.0, 0.0, -1.0, 0.5);
    const float coef = 2.0;
    vec3 E = T(ip); float e = LUM(E);
    vec3 res = E;
    for (int k = 0; k < 4; k++)
    {
        vec3 Fc = T(ip + rot(ivec2(1, 0), k)), Hc = T(ip + rot(ivec2(0, 1), k));
        float b = LUM(T(ip + rot(ivec2(0, -1), k))), c = LUM(T(ip + rot(ivec2(1, -1), k))), d = LUM(T(ip + rot(ivec2(-1, 0), k)));
        float f = LUM(Fc), g = LUM(T(ip + rot(ivec2(-1, 1), k))), h = LUM(Hc), i = LUM(T(ip + rot(ivec2(1, 1), k)));
        float i4 = LUM(T(ip + rot(ivec2(2, 1), k))), i5 = LUM(T(ip + rot(ivec2(1, 2), k)));
        float h5 = LUM(T(ip + rot(ivec2(0, 2), k))), f4 = LUM(T(ip + rot(ivec2(2, 0), k)));
        bool fx      = Ao[k] * fp.y + Bo[k] * fp.x > Co[k];
        bool fx_left = Ax[k] * fp.y + Bx[k] * fp.x > Cx[k];
        bool fx_up   = Ay[k] * fp.y + By[k] * fp.x > Cy[k];
        bool lv1 = (e != f) && (e != h);
        bool lv2l = (e != g) && (d != g);
        bool lv2u = (e != c) && (b != c);
        bool edr = (WD(e, c, g, i, h5, f4, h, f) < WD(h, d, i5, e, i4, b, f, i)) && lv1;
        bool edr_left = (coef * abs(f - g) <= abs(h - c)) && lv2l;
        bool edr_up = (abs(f - g) >= coef * abs(h - c)) && lv2u;
        bool nc = edr && (fx || (edr_left && fx_left) || (edr_up && fx_up));
        bool px = abs(e - f) <= abs(e - h);
        if (nc) res = px ? Fc : Hc;
    }
    return res;
}
// ---- Scale3x (AdvMAME3x) ----
bool eq(vec3 a, vec3 b) { return all(equal(a, b)); }
vec3 scale3x(ivec2 ip, vec2 fp)
{
    vec3 A = T(ip + ivec2(-1, -1)), B = T(ip + ivec2(0, -1)), C = T(ip + ivec2(1, -1));
    vec3 D = T(ip + ivec2(-1, 0)),  E = T(ip),                F = T(ip + ivec2(1, 0));
    vec3 G = T(ip + ivec2(-1, 1)),  H = T(ip + ivec2(0, 1)), I = T(ip + ivec2(1, 1));
    bool bd = eq(B, D), bf = eq(B, F), dh = eq(D, H), hf = eq(H, F);
    bool c0 = bd && !bf && !dh, c2 = bf && !bd && !hf, c6 = dh && !bd && !hf, c8 = hf && !dh && !bf;
    ivec2 s = ivec2(min(floor(fp * 3.0), vec2(2.0)));
    int n = s.y * 3 + s.x;
    if (n == 0) return c0 ? D : E;
    if (n == 1) return ((c0 && !eq(E, C)) || (c2 && !eq(E, A))) ? B : E;
    if (n == 2) return c2 ? F : E;
    if (n == 3) return ((c0 && !eq(E, G)) || (c6 && !eq(E, A))) ? D : E;
    if (n == 4) return E;
    if (n == 5) return ((c2 && !eq(E, I)) || (c8 && !eq(E, C))) ? F : E;
    if (n == 6) return c6 ? D : E;
    if (n == 7) return ((c6 && !eq(E, I)) || (c8 && !eq(E, G))) ? H : E;
    return c8 ? F : E;
}
// ---- FSR 1.0: EASU + RCAS ----
float Lm(vec3 c) { return c.b * 0.5 + (c.r * 0.5 + c.g); }
void easuTap(inout vec3 aC, inout float aW, vec2 off, vec2 dir, vec2 len, float lob, float clp, vec3 c)
{
    vec2 v = vec2(off.x * dir.x + off.y * dir.y, off.x * (-dir.y) + off.y * dir.x) * len;
    float d2 = min(v.x * v.x + v.y * v.y, clp);
    float wB = 0.4 * d2 - 1.0, wA = lob * d2 - 1.0;
    wB *= wB; wA *= wA;
    wB = 1.5625 * wB - 0.5625;
    float w = wB * wA;
    aC += c * w; aW += w;
}
void easuSet(inout vec2 dir, inout float len, float w, float lA, float lB, float lC, float lD, float lE)
{
    float lenX = max(abs(lD - lC), abs(lC - lB)), dirX = lD - lB;
    dir.x += dirX * w;
    lenX = clamp(abs(dirX) / max(lenX, 1e-6), 0.0, 1.0); lenX *= lenX; len += lenX * w;
    float lenY = max(abs(lE - lC), abs(lC - lA)), dirY = lE - lA;
    dir.y += dirY * w;
    lenY = clamp(abs(dirY) / max(lenY, 1e-6), 0.0, 1.0); lenY *= lenY; len += lenY * w;
}
vec3 easu(vec2 p)
{
    vec2 pp = p - 0.5; vec2 fl = floor(pp); pp -= fl; ivec2 q = ivec2(fl);
    vec3 b = T(q + ivec2(0, -1)), c = T(q + ivec2(1, -1)), e = T(q + ivec2(-1, 0)), f = T(q), g = T(q + ivec2(1, 0)), h = T(q + ivec2(2, 0));
    vec3 i = T(q + ivec2(-1, 1)), j = T(q + ivec2(0, 1)), k = T(q + ivec2(1, 1)), l = T(q + ivec2(2, 1)), n = T(q + ivec2(0, 2)), o = T(q + ivec2(1, 2));
    float bL = Lm(b), cL = Lm(c), eL = Lm(e), fL = Lm(f), gL = Lm(g), hL = Lm(h), iL = Lm(i), jL = Lm(j), kL = Lm(k), lL = Lm(l), nL = Lm(n), oL = Lm(o);
    vec2 dir = vec2(0.0); float len = 0.0;
    easuSet(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
    easuSet(dir, len, pp.x * (1.0 - pp.y), cL, fL, gL, hL, kL);
    easuSet(dir, len, (1.0 - pp.x) * pp.y, fL, iL, jL, kL, nL);
    easuSet(dir, len, pp.x * pp.y, gL, jL, kL, lL, oL);
    vec2 dir2 = dir * dir; float dirR = dir2.x + dir2.y;
    bool zro = dirR < (1.0 / 32768.0);
    dirR = inversesqrt(max(dirR, 1e-9)); dirR = zro ? 1.0 : dirR; dir.x = zro ? 1.0 : dir.x; dir *= vec2(dirR);
    len = len * 0.5; len *= len;
    float stretch = (dir.x * dir.x + dir.y * dir.y) / max(abs(dir.x), abs(dir.y));
    vec2 len2 = vec2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
    float lob = 0.5 + ((0.25 - 0.04) - 0.5) * len, clp = 1.0 / lob;
    vec3 min4 = min(min(f, g), min(j, k)), max4 = max(max(f, g), max(j, k));
    vec3 aC = vec3(0.0); float aW = 0.0;
    easuTap(aC, aW, vec2( 0.0, -1.0) - pp, dir, len2, lob, clp, b);
    easuTap(aC, aW, vec2( 1.0, -1.0) - pp, dir, len2, lob, clp, c);
    easuTap(aC, aW, vec2(-1.0,  1.0) - pp, dir, len2, lob, clp, i);
    easuTap(aC, aW, vec2( 0.0,  1.0) - pp, dir, len2, lob, clp, j);
    easuTap(aC, aW, vec2( 0.0,  0.0) - pp, dir, len2, lob, clp, f);
    easuTap(aC, aW, vec2(-1.0,  0.0) - pp, dir, len2, lob, clp, e);
    easuTap(aC, aW, vec2( 1.0,  1.0) - pp, dir, len2, lob, clp, k);
    easuTap(aC, aW, vec2( 2.0,  1.0) - pp, dir, len2, lob, clp, l);
    easuTap(aC, aW, vec2( 2.0,  0.0) - pp, dir, len2, lob, clp, h);
    easuTap(aC, aW, vec2( 1.0,  0.0) - pp, dir, len2, lob, clp, g);
    easuTap(aC, aW, vec2( 1.0,  2.0) - pp, dir, len2, lob, clp, o);
    easuTap(aC, aW, vec2( 0.0,  2.0) - pp, dir, len2, lob, clp, n);
    return min(max4, max(min4, aC / aW));
}
vec3 fsr(vec2 p)
{
    vec2 s = 1.0 / outScale;
    vec3 e = easu(p), b = easu(p + vec2(0.0, -s.y)), d = easu(p + vec2(-s.x, 0.0)), f = easu(p + vec2(s.x, 0.0)), h = easu(p + vec2(0.0, s.y));
    vec3 mn4 = min(min(b, d), min(f, h)), mx4 = max(max(b, d), max(f, h));
    vec3 hitMin = min(mn4, e) / (4.0 * max(mx4, vec3(1e-4)));
    vec3 hitMax = (vec3(1.0) - max(mx4, e)) / min(4.0 * mn4 - vec3(4.0), vec3(-1e-4));
    vec3 lobeRGB = max(-hitMin, hitMax);
    float lobe = max(-0.1875, min(min(lobeRGB.r, min(lobeRGB.g, lobeRGB.b)), 0.0)) * exp2(-0.2);
    return clamp((lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0), 0.0, 1.0);
}
void main()
{
    vec2 p = vec2(fragTexCoord.x * texSize.x, (1.0 - fragTexCoord.y) * texSize.y);
    p = clamp(p, bounds.xy, bounds.zw - 0.001);
    ivec2 ip = ivec2(floor(p)); vec2 fp = p - floor(p);
    vec3 rgb;
    if (mode == 1) rgb = xbr(ip, fp);
    else if (mode == 2) rgb = scale3x(ip, fp);
    else rgb = fsr(p);
    vec4 c = vec4(rgb, 1.0) * colDiffuse * fragColor;
    if (vibrance > 0.0)
    {
        float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
        float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
        c.rgb = clamp(mix(vec3(l), c.rgb, 1.0 + vibrance * (1.0 - (mx - mn))), 0.0, 1.0);
    }
    finalColor = c;
}";

    static Shader _scaler; static bool _scalerTried, _scalerOk; static int _slTexSize, _slBounds, _slOutScale, _slMode, _slVib;
    static Shader _post; static bool _postTried, _postOk; static int _locTexSize, _locSharp, _locVib;

    /// <summary>True when any filter is selected (the Presenter uses the plain path otherwise).</summary>
    public static bool Any(Settings s) => s.FilterScaling != FilterScaling.Off || s.FilterPixelArt != FilterPixelArt.Off || s.FilterEffects != FilterEffects.Off;

    /// <summary>Draws <paramref name="tex"/> (full texture) into <paramref name="d"/> with the filters of <paramref name="gc"/>. Must be inside BeginDrawing.</summary>
    public static unsafe void Present(Texture2D tex, Rectangle d, Color tint, Settings gc)
    {
        var srcR = new Rectangle(0, 0, tex.Width, tex.Height);
        bool sharp = gc.FilterScaling == FilterScaling.SharpBilinear, vib = gc.FilterEffects == FilterEffects.Vibrance;
        int pm = gc.FilterPixelArt == FilterPixelArt.Xbr ? 1 : gc.FilterPixelArt == FilterPixelArt.Scale3x ? 2 : gc.FilterScaling == FilterScaling.Fsr ? 3 : 0;
        if (pm > 0 && !_scalerTried)
        {
            _scalerTried = true; _scaler = Raylib.LoadShaderFromMemory(null, ScalerFs); _scalerOk = Raylib.IsShaderValid(_scaler);
            if (!_scalerOk) Console.WriteLine("ERROR: ScalerFs (XBR/Scale3x/FSR) did not compile; scaling filters have no effect");
            else { _slTexSize = Raylib.GetShaderLocation(_scaler, "texSize"); _slBounds = Raylib.GetShaderLocation(_scaler, "bounds"); _slOutScale = Raylib.GetShaderLocation(_scaler, "outScale"); _slMode = Raylib.GetShaderLocation(_scaler, "mode"); _slVib = Raylib.GetShaderLocation(_scaler, "vibrance"); }
        }
        if (pm > 0 && _scalerOk)
        {
            Raylib.SetTextureFilter(tex, TextureFilter.Point);
            float* ts = stackalloc float[2]; ts[0] = tex.Width; ts[1] = tex.Height;
            float* bd = stackalloc float[4]; bd[0] = 0; bd[1] = 0; bd[2] = tex.Width; bd[3] = tex.Height;
            float* os = stackalloc float[2]; os[0] = d.Width / tex.Width; os[1] = d.Height / tex.Height;
            int md = pm; float vb2 = vib ? VibranceAmount : 0f;
            Raylib.SetShaderValue(_scaler, _slTexSize, ts, ShaderUniformDataType.Vec2);
            Raylib.SetShaderValue(_scaler, _slBounds, bd, ShaderUniformDataType.Vec4);
            Raylib.SetShaderValue(_scaler, _slOutScale, os, ShaderUniformDataType.Vec2);
            Raylib.SetShaderValue(_scaler, _slMode, &md, ShaderUniformDataType.Int);
            Raylib.SetShaderValue(_scaler, _slVib, &vb2, ShaderUniformDataType.Float);
            Raylib.BeginShaderMode(_scaler);
            Raylib.DrawTexturePro(tex, srcR, d, Vector2.Zero, 0f, tint);
            Raylib.EndShaderMode();
            Scanlines(gc, d, tex.Height);
            return;
        }
        Raylib.SetTextureFilter(tex, gc.FilterScaling == FilterScaling.Bilinear || sharp ? TextureFilter.Bilinear : TextureFilter.Point);
        bool useShader = sharp || vib;
        if (useShader && !_postTried)
        {
            _postTried = true;
            _post = Raylib.LoadShaderFromMemory(null, PostFs); _postOk = Raylib.IsShaderValid(_post);
            if (!_postOk) Console.WriteLine("ERROR: PostFs (Sharp Bilinear / Vibrance) did not compile; those filters have no effect");
            else { _locTexSize = Raylib.GetShaderLocation(_post, "texSize"); _locSharp = Raylib.GetShaderLocation(_post, "sharpScale"); _locVib = Raylib.GetShaderLocation(_post, "vibrance"); }
        }
        if (useShader && _postOk)
        {
            float* ts = stackalloc float[2]; ts[0] = tex.Width; ts[1] = tex.Height;
            float sc = sharp ? d.Width / tex.Width : 0f, vb = vib ? VibranceAmount : 0f;
            Raylib.SetShaderValue(_post, _locTexSize, ts, ShaderUniformDataType.Vec2);
            Raylib.SetShaderValue(_post, _locSharp, &sc, ShaderUniformDataType.Float);
            Raylib.SetShaderValue(_post, _locVib, &vb, ShaderUniformDataType.Float);
            Raylib.BeginShaderMode(_post);
            Raylib.DrawTexturePro(tex, srcR, d, Vector2.Zero, 0f, tint);
            Raylib.EndShaderMode();
        }
        else Raylib.DrawTexturePro(tex, srcR, d, Vector2.Zero, 0f, tint);
        Scanlines(gc, d, tex.Height);
    }

    static void Scanlines(Settings gc, Rectangle d, int srcH)
    {
        if (gc.FilterEffects != FilterEffects.Scanlines) return;
        float rh = d.Height / srcH;
        for (int y = 0; y < srcH; y++)
        {
            int y0 = (int)Math.Round(y * rh + rh * 0.6f), y1 = (int)Math.Round((y + 1) * rh);
            if (y1 > y0) Raylib.DrawRectangle((int)d.X, (int)d.Y + y0, (int)d.Width, y1 - y0, new Color(0, 0, 0, 110));
        }
    }
}
