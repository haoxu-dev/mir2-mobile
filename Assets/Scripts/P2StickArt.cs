// 摇杆真美术 —— 离线 3D 渲染出来的两张图，运行时按「底盘一次 + 球头补丁一次」合成。
//
// 为什么要有这个文件（2026-11-02）：
//   用户原话「你那个摇杆，现在他妈的纯贴图，和那个王者荣耀的差距百倍」。
//   原来的 P2VirtualPad.MakePad() 是逐像素手画的径向渐变 + 一圈高光弧，
//   本质仍是 2D 涂鸦：没有法线、没有环境光、没有接触阴影，所以一定「像贴图」。
//
//   现在的做法是把物理渲染挪到**离线**（C:\ai\tmp\proto14.py）：
//     · 底盘 480x480：径向高度场（中心微拱 → 斜面抬起 → 外壁下落 → 收边）
//       + 四向凹槽**真的刻进高度场**（G_DEPTH=0.026）→ 数值求法线
//       + 半球环境光 + Blinn-Phong 高光 + 菲涅尔；
//     · 球头 104x104：真球面法线的琥珀金属球，双高光 + 边缘菲涅尔 + 底缘压暗；
//     · 接触阴影：光在左上 ⇒ 影落右下，解析函数 shadowAt() 在运行时算（见下）。
//   产物落成**原始 RGBA 字节**（.rgba），不是 PNG —— 见 P2Core.NEEDED 里的说明。
//
// ★ IMGUI 的硬约束（这个工程上踩过，别再踩）：
//   1) GUI.DrawTexture 是**不透明**刷一整块矩形，贴图 A 通道整条链被丢掉。
//      所以底盘图必须自己把「圆外」画成黑（黑底上就等于不存在），不能指望 alpha。
//   2) 每个元素**只能有一次 DrawTexture**。所以球头不能「阴影一层 + 球一层」，
//      必须把「底盘底色 × 接触阴影 + 球头」合成到同一张补丁里 —— 就是本文件做的事。
//      补丁的底色是**直接从底盘像素数组里取的**（不是猜的颜色），所以贴回去严丝合缝。
//
// ★ 坐标系：这里全部是 GUI 像素（左上原点、Y 向下），与 P2VirtualPad 一致，
//   实测 Screen.width=2616 时 GUI 空间 = 设备像素 1:1（旧环半径 190 在截图上是 178~184，吻合）。

using System;
using System.IO;
using Client.MirCore;
using UnityEngine;

public static class P2StickArt
{
    const string BaseRel = "Data/UI/pad_base.rgba";
    const string KnobRel = "Data/UI/pad_knob.rgba";

    public const int BaseSize = 480;      // 贴图边长
    public const int BaseRadius = 190;    // 盘面半径（像素，与 P2VirtualPad.StickBaseRadius 一致）
    public const int KnobSize = 104;
    public const int KnobRadius = 52;     // = StickRadius(118) * StickKnobRatio(0.44) ≈ 52

    // 补丁半径：球半径 52 + 48 = 100 像素。
    // ★ v16 必须给到 KnobRadius*1.92：新阴影公式的 cast 项在 dd=1.43r 处才回到 1.0，
    //   而 dd 是从偏移 (0.20r,0.24r) 的点量的 ⇒ 球心算起要 1.74r ≈ 90px 才完全消失。
    //   旧值 82 会在补丁右下方硬切一刀，露出一个方形亮角（比没有阴影还难看出戏）。
    const int PatchRadius = KnobRadius + 48;
    const int PatchSize = PatchRadius * 2;

    static bool _tried;
    static bool _ok;

    static Texture2D _baseTex;
    static byte[] _basePx;      // 上→下 行序的 RGBA，用于取补丁底色
    static byte[] _knobPx;      // 同上；A=0 表示圆外

    static Texture2D _patchTex;
    static byte[] _patchPx;
    static int _patchKey = int.MinValue;

    // 诊断：只打一次
    static string _diag;

    public static string Diag { get { return _diag; } }

    public static bool Ready { get { Ensure(); return _ok; } }

    static void Ensure()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            string root = PortableInit.RootPath;
            string bp = Path.Combine(root, BaseRel.Replace('/', Path.DirectorySeparatorChar));
            string kp = Path.Combine(root, KnobRel.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(bp) || !File.Exists(kp))
            {
                _diag = "★摇杆美术缺失 base=" + File.Exists(bp) + " knob=" + File.Exists(kp) + " root=" + root;
                return;
            }

            _basePx = File.ReadAllBytes(bp);
            _knobPx = File.ReadAllBytes(kp);
            if (_basePx.Length != BaseSize * BaseSize * 4)
            {
                _diag = "★pad_base 尺寸不对 " + _basePx.Length + " 应为 " + (BaseSize * BaseSize * 4);
                _basePx = null; return;
            }
            if (_knobPx.Length != KnobSize * KnobSize * 4)
            {
                _diag = "★pad_knob 尺寸不对 " + _knobPx.Length + " 应为 " + (KnobSize * KnobSize * 4);
                _knobPx = null; return;
            }

            // ★ 2026-11-11：盘外改成真透明。
            //   离线渲染出来的 pad_base.rgba 是 A 恒 255、盘外涂黑（实测 230400 个像素全是 255），
            //   以前靠"黑边就是纯黑"隐身。现在黑边有了会随场景变色的环境光底衬（P2Core.UpdateAmbient），
            //   480x480 的黑方块会直接盖在渐变上，比纯黑时更丑。
            //   所以在这里按 BaseRadius 做一次圆形 alpha 遮罩，绘制时改走 AlphaBlend。
            ApplyDiscAlpha(_basePx, BaseSize, BaseSize, BaseRadius);

            _baseTex = MakeTex(BaseSize, _basePx);
            _patchTex = new Texture2D(PatchSize, PatchSize, TextureFormat.RGBA32, false);
            _patchTex.wrapMode = TextureWrapMode.Clamp;
            _patchTex.filterMode = FilterMode.Bilinear;
            _patchPx = new byte[PatchSize * PatchSize * 4];
            _ok = true;
            _diag = "摇杆美术就绪 base=" + BaseSize + " knob=" + KnobSize + " patch=" + PatchSize;
        }
        catch (Exception e)
        {
            _ok = false;
            _diag = "★摇杆美术载入异常 " + e.Message;
        }
    }

    /// <summary>
    /// 把一张 A 恒 255 的方形贴图按圆形裁掉盘外部分（上→下行序，RGBA）。
    /// pad_base.rgba 的盘面半径是 BaseRadius=190（480 见方的中心），
    /// 盘外那圈本来是黑色，现在涂成 A=0，绘制时走 AlphaBlend 就自然融进任何底衬。
    /// 边缘留 1.5px 过渡带，否则放大到 480 设备像素会看到锯齿。
    /// </summary>
    static void ApplyDiscAlpha(byte[] rgba, int w, int h, float radius)
    {
        float cx = w * 0.5f, cy = h * 0.5f;
        float aa = 1.5f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                int i = (y * w + x) * 4;
                if (d <= radius - aa) continue;                    // 盘内原样
                if (d >= radius + aa) { rgba[i + 3] = 0; continue; } // 盘外全透明
                rgba[i + 3] = (byte)((1f - (d - (radius - aa)) / (2f * aa)) * 255f);
            }
    }

    /// <summary>上→下 的行序转成 Unity 的下→上，然后直接 LoadRawTextureData（与 P2Core.Upload 同法）。</summary>
    static Texture2D MakeTex(int n, byte[] topDown)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Bilinear;
        byte[] raw = new byte[topDown.Length];
        int row = n * 4;
        for (int y = 0; y < n; y++)
            Buffer.BlockCopy(topDown, y * row, raw, (n - 1 - y) * row, row);
        t.LoadRawTextureData(raw);
        t.Apply(false, false);
        return t;
    }

    // 主光的水平方向（与 proto16.py 的 LX/LY 同值：L=(-0.44,-0.52,0.73) 归一化后投影）
    const float LX = -0.6459f;
    const float LY = -0.7634f;

    /// <summary>
    /// 接触阴影乘数：1 = 不变，越小越暗。C# 侧与 proto16.py 的 shadow_at() 逐行对应。
    /// 光在左上（底盘/球头共用同一光位）⇒ 影子必须落右下，否则就是「球浮在盘上」。
    ///
    /// ★ v16：窗口从 1.16r 收到 1.06r。原来那圈太宽，把 socket 碗壁的暗环整个盖住了，
    ///   于是「球坐在窝里」的唯一线索被抹平，看起来就又像浮着。
    /// </summary>
    static float ShadowAt(float dx, float dy, float r)
    {
        float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
        float ca = Mathf.Clamp01((1.06f - d) / 0.20f);
        float contact = 0.07f + 0.93f * (1f - Mathf.Pow(ca, 1.4f));
        float ox = r * 0.20f, oy = r * 0.24f;
        float ddx = dx - ox, ddy = dy - oy;
        float dd = Mathf.Sqrt(ddx * ddx + ddy * ddy) / r;
        float q = Mathf.Clamp01((dd - 0.85f) / 0.58f);
        float cast = 0.40f + 0.60f * Mathf.Pow(q, 0.70f);
        return Mathf.Clamp01(contact * cast);
    }

    /// <summary>
    /// 球头反投到盘面上的琥珀环境色（0 = 无，越大越暖）。C# 侧与 proto16.py 的 bounce_at() 逐行对应。
    /// 真东西是两个物体共处一室：琥珀球的下缘被盘面照亮，又把暖光反投回接触阴影**外侧**，
    /// 形成一圈很窄的暖边。没有这一圈，球和盘就是两张图叠在一起。
    /// </summary>
    static float BounceAt(float dx, float dy, float r)
    {
        float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
        float ring = Mathf.Clamp01((d - 0.92f) / 0.10f) * Mathf.Clamp01((1.55f - d) / 0.55f);
        if (ring <= 0f) return 0f;
        float dn = d * r;
        if (dn < 1e-6f) return 0f;
        float dirw = Mathf.Clamp01(0.5f + 0.5f * (-(dx * LX + dy * LY)) / dn);
        return Mathf.Clamp01(ring * (0.35f + 0.65f * dirw) * 0.55f);
    }

    /// <summary>按偏移量生成一次「底盘底色 × 阴影 + 球头」的补丁。偏移量化到 2px，拖动时只重算几十次。</summary>
    static void BuildPatch(int ox, int oy)
    {
        int key = (ox << 16) ^ (oy & 0xFFFF);
        if (key == _patchKey && _patchTex != null) return;
        _patchKey = key;

        int bc = BaseSize / 2;          // 底盘中心（在底盘像素数组里）
        int x0 = bc + ox - PatchRadius;
        int y0 = bc + oy - PatchRadius;
        int kx = PatchRadius - KnobRadius;   // 球头左上角在补丁里的位置
        int ky = PatchRadius - KnobRadius;

        byte[] dst = _patchPx;
        int di = 0;
        for (int yy = 0; yy < PatchSize; yy++)
        {
            int by = y0 + yy;
            int ly = yy - ky;
            bool rowInKnob = ly >= 0 && ly < KnobSize;
            for (int xx = 0; xx < PatchSize; xx++)
            {
                int bx = x0 + xx;

                // ① 底色：直接抄底盘的真实像素（抄不到就纯黑 —— 那是盘外的黑边，本来就黑）
                byte br = 0, bg = 0, bb = 0;
                if ((uint)bx < (uint)BaseSize && (uint)by < (uint)BaseSize)
                {
                    int si = (by * BaseSize + bx) * 4;
                    bb = _basePx[si]; bg = _basePx[si + 1]; br = _basePx[si + 2];
                }

                // ② 阴影 + 琥珀反投光：以球心为原点
                float sdx = xx + 0.5f - PatchRadius - ox;
                float sdy = yy + 0.5f - PatchRadius - oy;
                float s = ShadowAt(sdx, sdy, KnobRadius);
                float fr = br * s * (1f / 255f), fg = bg * s * (1f / 255f), fb = bb * s * (1f / 255f);
                float bo = BounceAt(sdx, sdy, KnobRadius);
                if (bo > 0f)
                {
                    fr += bo * 46f / 255f;
                    fg += bo * 19f / 255f;
                    fb += bo * 3f / 255f;
                }

                // ③ 球头：按 alpha 混合（球头图圆外 A=0，所以边缘是抗锯齿过的）
                if (rowInKnob)
                {
                    int lx = xx - kx;
                    if (lx >= 0 && lx < KnobSize)
                    {
                        int ki = (ly * KnobSize + lx) * 4;
                        float a = _knobPx[ki + 3] * (1f / 255f);
                        if (a > 0f)
                        {
                            float kr = _knobPx[ki + 2] * (1f / 255f);
                            float kg = _knobPx[ki + 1] * (1f / 255f);
                            float kb = _knobPx[ki] * (1f / 255f);
                            fr = fr * (1f - a) + kr * a;
                            fg = fg * (1f - a) + kg * a;
                            fb = fb * (1f - a) + kb * a;
                        }
                    }
                }

                // ④ 圆外透明（2026-11-11，与 ApplyDiscAlpha 同一判据）：
                //   球头推到边缘时补丁会伸出盘外（offset 118 + 补丁半径 100 = 218 > 190），
                //   不裁的话盘外会露出一块 200x200 的黑方块压在环境光底衬上。
                //   判据用的是**底盘纹理坐标** (bx,by)，和底盘本身那道圆形遮罩完全对齐。
                byte pa = 255;
                {
                    float pdx = bx + 0.5f - bc, pdy = by + 0.5f - bc;
                    float pd = Mathf.Sqrt(pdx * pdx + pdy * pdy);
                    if (pd >= BaseRadius + 1.5f) pa = 0;
                    else if (pd > BaseRadius - 1.5f)
                        pa = (byte)((1f - (pd - (BaseRadius - 1.5f)) / 3f) * 255f);
                }

                dst[di] = (byte)(Mathf.Clamp01(fb) * 255f);
                dst[di + 1] = (byte)(Mathf.Clamp01(fg) * 255f);
                dst[di + 2] = (byte)(Mathf.Clamp01(fr) * 255f);
                dst[di + 3] = pa;
                di += 4;
            }
        }

        // 补丁同样是上→下行序，Unity 要下→上
        byte[] raw = new byte[dst.Length];
        int rowBytes = PatchSize * 4;
        for (int y = 0; y < PatchSize; y++)
            Buffer.BlockCopy(dst, y * rowBytes, raw, (PatchSize - 1 - y) * rowBytes, rowBytes);
        _patchTex.LoadRawTextureData(raw);
        _patchTex.Apply(false, false);
    }

    /// <summary>
    /// 画摇杆。offset 是球头相对底盘中心的位移（像素），静止时传 (0,0)。
    /// 返回 false 表示美术没就绪，调用方应退回手绘版本。
    /// </summary>
    public static bool Draw(Vector2 center, float offsetX, float offsetY)
    {
        Ensure();
        if (!_ok) return false;

        // 偏移量化到 2px：拖动时位置连续变化，不量化会每帧重建补丁（26k 像素 + 上传）
        int ox = Mathf.RoundToInt(offsetX * 0.5f) * 2;
        int oy = Mathf.RoundToInt(offsetY * 0.5f) * 2;

        BuildPatch(ox, oy);

        GUI.color = Color.white;
        // 底盘：480x480 原尺寸贴，盘面半径 190 与 StickBaseRadius 完全对齐
        // alphaBlend=true：盘外已经裁成 A=0（见 ApplyDiscAlpha），
        // 不混合的话 Unity 会当成不透明贴图，盘外那圈黑方块照样糊上去。
        GUI.DrawTexture(new Rect(center.x - BaseSize * 0.5f, center.y - BaseSize * 0.5f, BaseSize, BaseSize),
            _baseTex, ScaleMode.StretchToFill, true);
        // 球头补丁：只在球心位置贴一小块
        GUI.DrawTexture(new Rect(center.x + ox - PatchRadius, center.y + oy - PatchRadius,
                                 PatchSize, PatchSize),
            _patchTex, ScaleMode.StretchToFill, true);
        return true;
    }
}
