// 虚拟按键图标 —— 离线生成的白描图标 + 原版 MagIcon 技能图，运行时合进按键贴图。
//
// 为什么要烘进贴图而不是再画一次 DrawTexture：
//   IMGUI 的 GUI.DrawTexture 是**不透明**刷矩形（A 通道整条丢），
//   一个按键画两层（底 + 图标）时，图标那层的方角会把下层圆角切掉。
//   P2VirtualPad.MakePad 已经把「底 + 高光 + 棱」烘成一张图了，图标同理。
//
// 素材来源（两份，都在 StreamingAssets/Data/UI/，进 P2Core.NEEDED）：
//   ic_<name>.rgba  256x256 RGBA，RGB 全白、A = 覆盖率。由 <本机>/tmp\mk_pad_icons.py
//                   用有符号距离场画出来（剑/宝箱/药瓶/气泡/背包/铠甲/星星/叉/箭头），
//                   边缘天然抗锯齿，缩到 r=48 的技能键上也不会有锯齿。
//   mag\<帧号>.rgba  原版 MagIcon.Lib 的**偶数帧**（= 未按下态，Index = Icon*2，
//                   见 Client/MirScenes/Dialogs/MainDialogs.cs:1837）。
//                   由 <本机>/tmp\mk_magicon_rgba.py 从 Data_HOLD/MagIcon.Lib 导出。
//                   ★ 这是正版素材，不是自己画的 —— 用户要求「照着正版弄」。
//
// ★ 采样方式：白色图标用**双线性**（SDF 生成，天然平滑）；
//   技能图标用**最近邻**（32x30 的像素画，放大到 ~90px 时双线性会糊成一团，
//   最近邻反而保持原版的像素感，和游戏里的技能栏看起来是同一套东西）。

using System;
using System.Collections.Generic;
using System.IO;
using Client.MirCore;      // PortableInit（素材根目录）
using UnityEngine;

public static class P2IconArt
{
    const string IconDir = "Data/UI";
    const string MagDir = "Data/UI/mag";
    const int IconSize = 256;      // ic_*.rgba 边长
    const int MagW = 32, MagH = 30; // MagIcon.Lib 单帧尺寸（实测 132 帧里 93 张是 32x30）

    static readonly Dictionary<string, float[]> _masks = new Dictionary<string, float[]>();
    static readonly Dictionary<int, Color32[]> _mags = new Dictionary<int, Color32[]>();

    static bool _rootOk;
    static string _root;

    static string Root
    {
        get
        {
            if (!_rootOk)
            {
                _rootOk = true;
                try { _root = PortableInit.RootPath; }
                catch { _root = null; }
            }
            return _root;
        }
    }

    /// <summary>白色图标的覆盖率遮罩（256x256，0..1）。读不到返回 null（调用方跳过图标）。</summary>
    public static float[] Mask(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        float[] m;
        if (_masks.TryGetValue(name, out m)) return m;

        string root = Root;
        if (root == null) { _masks[name] = null; return null; }

        string p = Path.Combine(root, IconDir.Replace('/', Path.DirectorySeparatorChar), "ic_" + name + ".rgba");
        if (!File.Exists(p)) { _masks[name] = null; return null; }

        try
        {
            byte[] raw = File.ReadAllBytes(p);
            if (raw.Length != IconSize * IconSize * 4)
            {
                if (P2VirtualPad.Log != null)
                    P2VirtualPad.Log("★图标 " + name + " 尺寸不对 " + raw.Length);
                _masks[name] = null; return null;
            }
            var a = new float[IconSize * IconSize];
            for (int i = 0; i < a.Length; i++) a[i] = raw[i * 4 + 3] * (1f / 255f);
            _masks[name] = a;
            return a;
        }
        catch (Exception e)
        {
            if (P2VirtualPad.Log != null) P2VirtualPad.Log("★图标 " + name + " 载入异常 " + e.Message);
            _masks[name] = null;
            return null;
        }
    }

    /// <summary>
    /// 技能图标（原版 MagIcon 的未按下帧）32x30 RGBA，**行序保持文件的 上→下**。
    ///
    /// ★ 这里**不要翻行**：整个 MakePad 都在「屏幕坐标」里工作（y=0 = 上），
    ///   到写 Texture2D 之前才统一翻一次（Unity 的 SetPixels32 第 0 行是**下**）。
    ///   翻两次 = 图标上下颠倒 —— 圆形按键看不出，剑/药瓶一眼就看出来了。
    /// </summary>
    public static Color32[] Mag(int frame)
    {
        Color32[] c;
        if (_mags.TryGetValue(frame, out c)) return c;

        string root = Root;
        if (root == null) { _mags[frame] = null; return null; }

        string p = Path.Combine(root, MagDir.Replace('/', Path.DirectorySeparatorChar),
                                frame.ToString("000") + ".rgba");
        if (!File.Exists(p)) { _mags[frame] = null; return null; }

        try
        {
            byte[] raw = File.ReadAllBytes(p);
            if (raw.Length != MagW * MagH * 4) { _mags[frame] = null; return null; }
            c = new Color32[MagW * MagH];
            for (int i = 0; i < c.Length; i++)
                c[i] = new Color32(raw[i * 4], raw[i * 4 + 1], raw[i * 4 + 2], raw[i * 4 + 3]);
            _mags[frame] = c;
            return c;
        }
        catch
        {
            _mags[frame] = null;
            return null;
        }
    }

    static float SampleMask(float[] m, float u, float v)
    {
        // u,v ∈ [0,1]；越界取 0
        float x = u * IconSize - 0.5f;
        float y = v * IconSize - 0.5f;
        if (x < -1f || y < -1f || x > IconSize || y > IconSize) return 0f;
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        int x1 = Mathf.Clamp(x0 + 1, 0, IconSize - 1);
        int y1 = Mathf.Clamp(y0 + 1, 0, IconSize - 1);
        x0 = Mathf.Clamp(x0, 0, IconSize - 1);
        y0 = Mathf.Clamp(y0, 0, IconSize - 1);
        float a = m[y0 * IconSize + x0], b = m[y0 * IconSize + x1];
        float c = m[y1 * IconSize + x0], d = m[y1 * IconSize + x1];
        return (a * (1f - fx) + b * fx) * (1f - fy) + (c * (1f - fx) + d * fx) * fy;
    }

    /// <summary>
    /// 把白色图标合成进按键像素数组（就地修改）。px 是**上→下**行序（MakePad 的生成序，
    /// 最后由 SetPixels32 交给 Unity 时 Unity 会自己处理，MakePad 里一直是这个序）。
    /// </summary>
    public static void BlendMask(Color32[] px, int n, float[] mask, float scale, float strength)
    {
        if (px == null || mask == null || scale <= 0f) return;
        float r = n * 0.5f;
        float inv = 1f / (r * scale);
        const float SHADOW_OFF = 0.055f;
        // ★ 3x3 超采样：按键贴图只有 128/192 见方，而图标最终会缩放贴到半径
        //   48~100 的圆上（小键甚至是缩小的），单点采样会让细笔画（剑刃、瓶口）
        //   出现断点或阶梯。每个像素取 9 个点平均，代价是 9 倍采样次数，
        //   但这些贴图有缓存、只在第一次画时生成，实机代价可忽略。
        for (int y = 0; y < n; y++)
        {
            float dy = y + 0.5f - r;
            float v = (dy * inv + 1f) * 0.5f;
            if (v < -0.01f || v > 1.01f) continue;
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - r;
                float u = (dx * inv + 1f) * 0.5f;
                if (u < -0.01f || u > 1.01f) continue;

                float a = 0f, sh = 0f;
                for (int sy2 = 0; sy2 < 3; sy2++)
                    for (int sx2 = 0; sx2 < 3; sx2++)
                    {
                        float ou = u + (sx2 - 1) * 0.34f / (r * scale);
                        float ov = v + (sy2 - 1) * 0.34f / (r * scale);
                        a  += SampleMask(mask, ou, ov);
                        // 暗影（沿 Y 下移一点再采）—— 这一步**不看 a**，
                        // 否则字形外沿那圈柔和过渡就没有影子，图标看着是贴上去的
                        sh += SampleMask(mask, ou, ov - SHADOW_OFF);
                    }
                a *= (1f / 9f);
                sh *= (1f / 9f) * 0.45f;
                if (a <= 0.004f && sh <= 0.004f) continue;

                int i = y * n + x;
                Color32 c0 = px[i];
                float rr = c0.r * (1f - sh) * (1f - a * strength) + 255f * a * strength;
                float gg = c0.g * (1f - sh) * (1f - a * strength) + 255f * a * strength;
                float bb = c0.b * (1f - sh) * (1f - a * strength) + 255f * a * strength;
                px[i] = new Color32(ClampByte(rr), ClampByte(gg), ClampByte(bb), 255);
            }
        }
    }

    /// <summary>
    /// 把原版技能图标合成进按键像素数组：**等比放大到铺满整个圆，按圆裁切**。
    ///
    /// ★ 2026-11-05 改（离线评审定案）：
    ///   MagIcon.Lib 每一帧都是 30x32 的**整块不透明**小方块（深色底 + 那圈黑边框），
    ///   实测 alpha 只有最右 3 列是 0，其余 90% 全实心 —— 它不是带透明的白描图标。
    ///   上一版按「贴纸」把它居中贴进圆里，屏上就是一个浅色圆里嵌一块方砖。
    ///   现在照原神/王者技能键的做法：暗盘打底，原画**盖满圆**（左右溢出部分自然把
    ///   黑边框挤到圆外），只用圆遮罩裁边，最后 MakePad 再压一道亮环把圆收住。
    ///
    /// ★ 采样用 **bicubic（Catmull-Rom）**：32x30 放大到 128 是 4.3 倍。离线把
    ///   最近邻/双线性/bicubic 三版并排给评审看过，结论 bicubic &gt; 双线性 &gt; 最近邻，
    ///   最近邻的马赛克方块在这尺寸下很明显。参数公式在 <本机>/tmp\castmock.py。
    /// </summary>
    public static void BlendMag(Color32[] px, int n, Color32[] mag, float scale, float strength)
    {
        if (px == null || mag == null || scale <= 0f) return;
        float r = n * 0.5f;
        float half = r * scale;               // 图标在贴图里占的半边长（像素）
        float cx = r, cy = r;
        float k = (half * 2f) / MagH;         // 30px → 屏幕上占多少像素
        float ucx = (MagW - 1) * 0.5f;        // 图标中心在自身像素坐标里的位置
        float ucy = (MagH - 1) * 0.5f;

        int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - half));
        int x1 = Mathf.Min(n - 1, Mathf.CeilToInt(cx + half));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - half));
        int y1 = Mathf.Min(n - 1, Mathf.CeilToInt(cy + half));

        for (int y = y0; y <= y1; y++)
        {
            // 贴图数组的行序与 Mag() 返回的数组都是**上→下**，两边一致，直接对应
            float sy = (y + 0.5f - cy) / k + ucy;
            for (int x = x0; x <= x1; x++)
            {
                float sx = (x + 0.5f - cx) / k + ucx;
                float sr, sg, sb, sa;
                SampleMagBicubic(mag, sx, sy, out sr, out sg, out sb, out sa);
                float a = sa * strength;
                if (a <= 0.004f) continue;
                int i = y * n + x;
                Color32 c0 = px[i];
                px[i] = new Color32(
                    ClampByte(c0.r * (1f - a) + sr * a),
                    ClampByte(c0.g * (1f - a) + sg * a),
                    ClampByte(c0.b * (1f - a) + sb * a),
                    255);
            }
        }
    }

    /// <summary>
    /// Catmull-Rom 双三次采样（可分离，先横后竖）。输出 0..255。
    /// 权重：w0=-0.5t³+t²-0.5t  w1=1.5t³-2.5t²+1  w2=-1.5t³+2t²+0.5t  w3=0.5t³-0.5t²
    /// 越界按边缘像素夹取（Clamp），不做透明处理 —— 技能帧本身就是整块不透明的，
    /// 边缘夹取不会像「越界取 0」那样在圆边拉出一圈黑。
    /// </summary>
    static void SampleMagBicubic(Color32[] m, float sx, float sy,
                                 out float or_, out float og, out float ob, out float oa)
    {
        int ix = Mathf.FloorToInt(sx), iy = Mathf.FloorToInt(sy);
        float tx = sx - ix, ty = sy - iy;

        float w0 = -0.5f * tx * tx * tx + tx * tx - 0.5f * tx;
        float w1 = 1.5f * tx * tx * tx - 2.5f * tx * tx + 1f;
        float w2 = -1.5f * tx * tx * tx + 2f * tx * tx + 0.5f * tx;
        float w3 = 0.5f * tx * tx * tx - 0.5f * tx * tx;

        float v0 = -0.5f * ty * ty * ty + ty * ty - 0.5f * ty;
        float v1 = 1.5f * ty * ty * ty - 2.5f * ty * ty + 1f;
        float v2 = -1.5f * ty * ty * ty + 2f * ty * ty + 0.5f * ty;
        float v3 = 0.5f * ty * ty * ty - 0.5f * ty * ty;

        float ar = 0f, ag = 0f, ab = 0f, aa = 0f;
        for (int j = 0; j < 4; j++)
        {
            float wy = (j == 0 ? v0 : j == 1 ? v1 : j == 2 ? v2 : v3);
            if (wy == 0f) continue;
            int yy = Mathf.Clamp(iy - 1 + j, 0, MagH - 1);
            float rr = 0f, gg = 0f, bb = 0f, al = 0f;
            for (int i = 0; i < 4; i++)
            {
                float wx = (i == 0 ? w0 : i == 1 ? w1 : i == 2 ? w2 : w3);
                if (wx == 0f) continue;
                int xx = Mathf.Clamp(ix - 1 + i, 0, MagW - 1);
                Color32 c = m[yy * MagW + xx];
                rr += c.r * wx; gg += c.g * wx; bb += c.b * wx; al += c.a * wx;
            }
            ar += rr * wy; ag += gg * wy; ab += bb * wy; aa += al * wy;
        }
        or_ = ar; og = ag; ob = ab; oa = aa * (1f / 255f);
    }

    static byte ClampByte(float v)
    {
        if (v <= 0f) return 0;
        if (v >= 255f) return 255;
        return (byte)(v + 0.5f);
    }

    /// <summary>素材自检（写进启动日志，方便远程确认图标到底有没有下发）。</summary>
    public static string Diag()
    {
        string root = Root;
        if (root == null) return "图标：无根目录";
        string d = Path.Combine(root, IconDir.Replace('/', Path.DirectorySeparatorChar));
        string md = Path.Combine(root, MagDir.Replace('/', Path.DirectorySeparatorChar));
        int ic = 0;
        string[] names = { "attack", "pick", "pot", "npc", "bag", "eqp", "skill", "close", "page" };
        for (int i = 0; i < names.Length; i++)
            if (File.Exists(Path.Combine(d, "ic_" + names[i] + ".rgba"))) ic++;
        int mg = 0;
        if (Directory.Exists(md))
        {
            try { mg = Directory.GetFiles(md, "*.rgba").Length; } catch { mg = -1; }
        }
        return "图标：" + ic + "/" + names.Length + " 白描 + 技能 " + mg + " 张";
    }
}
