// P1-Map —— 真瓦片渲染器：直接吃原版 .Lib 的数据，按原版两套口径画。
//
// 素材：Assets/Resources/p1_atlas2/map0.bytes（由 build_minilib.py 从原版 .Lib 抽出）
//   格式：magic "M2TL" + int32 count + count*32 字节索引 + 原样 gzip 数据
//   索引每条：int16 lib, int16 idx, int32 offset, int32 length,
//             int16 w, int16 h, int16 ox, int16 oy, int16 flags, int16 rsvd, int32, int32
//
// ★ 原版有两套绘制口径，必须都实现，否则树和石头会消失：
//
//   ① DrawFloor（GameScene.cs:10657-10760）—— 只画「平地」：
//        drawX = (x - ux + OffSetX) * 48 - OffSetX
//        drawY = (y - uy + OffSetY) * 32
//        Back   只在 y%2==0 && x%2==0 && y<=endY 画
//        Middle/Front 只在 尺寸==48x32 或 96x64 时画（其余跳过）
//
//   ② DrawObjects（GameScene.cs:10787-10935）—— 画「竖起来的」树/石头/建筑：
//        drawY = (y - uy + OffSetY + 1) * 32          ← 注意 +1，底边对齐
//        Middle/Front 只在尺寸**不是** 48x32/96x64 时画
//        Front 摆放：lib.Draw(index, drawX, drawY - s.Height)   ← 向上顶
//        特例 fileIndex==28 且 GetOffSet != Empty 时改用带偏移的重载
//        Middle 用 DrawUp（同样向上顶，且用 DrawUpBlend 的场合先不管）
//
// 内存：不整库解压。索引常驻（4435*32=142KB），像素按需 gzip 解压进 LRU，
//       上限 24MB —— 一屏真正画的图约 200 张、1~2MB，远低于上限。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Client.MirCore;
using Client.MirScenes;
using UnityEngine;

public class P1Map
{
    public const int CellW = 48;
    public const int CellH = 32;

    public int ScreenW = 960;
    public int ScreenH = 540;
    int OffSetX, OffSetY;
    public int ViewRangeX { get; private set; }
    public int ViewRangeY { get; private set; }
    public int OffsetX { get { return OffSetX; } }
    public int OffsetY { get { return OffSetY; } }

    struct Entry
    {
        public int off, len, w, h, ox, oy;
        public int page;      // 解码后放进 LRU 的槽号，-1 = 还没解
    }

    sealed class Tile
    {
        public Color32[] px;
        public int w, h, ox, oy;
        public int lastUse;
    }

    readonly Dictionary<long, Entry> _idx = new Dictionary<long, Entry>(8192);
    readonly Dictionary<long, Tile> _cache = new Dictionary<long, Tile>(1024);
    readonly List<long> _lru = new List<long>(1024);
    byte[] _blob;

    Color32[] _screen;
    Texture2D _tex;
    int _frame;
    int _cacheBytes;
    const int CacheBudget = 24 * 1024 * 1024;

    public int TileCount { get { return _idx.Count; } }
    public int CachedTiles { get { return _cache.Count; } }
    public int CachedMB { get { return _cacheBytes / 1048576; } }
    public Texture2D Texture { get { return _tex; } }
    public int DrawCalls { get; private set; }
    public int Misses { get; private set; }

    static long Key(int lib, int idx) { return ((long)lib << 32) | (uint)idx; }

    public string Load(string resDir)
    {
        var ta = Resources.Load<TextAsset>(resDir + "/map0");   // 磁盘名 map0.tiles
        if (ta == null) return "找不到 Resources/" + resDir + "/map0.tiles";

        _blob = ta.bytes;
        if (_blob.Length < 8 || _blob[0] != 'M' || _blob[1] != '2' || _blob[2] != 'T' || _blob[3] != 'L')
            return "map0.tiles 头不对";

        int count = BitConverter.ToInt32(_blob, 4);
        if (count <= 0 || 8 + count * 32 > _blob.Length) return "map0.tiles 索引区越界";

        for (int i = 0; i < count; i++)
        {
            int p = 8 + i * 32;
            short lib = BitConverter.ToInt16(_blob, p);
            short idx = BitConverter.ToInt16(_blob, p + 2);
            var e = new Entry
            {
                off = BitConverter.ToInt32(_blob, p + 4),
                len = BitConverter.ToInt32(_blob, p + 8),
                w = BitConverter.ToInt16(_blob, p + 12),
                h = BitConverter.ToInt16(_blob, p + 14),
                ox = BitConverter.ToInt16(_blob, p + 16),
                oy = BitConverter.ToInt16(_blob, p + 18),
                page = -1,
            };
            if (e.off < 0 || e.len <= 0 || e.off + e.len > _blob.Length) continue;
            _idx[Key(lib, idx)] = e;
        }
        if (_idx.Count == 0) return "索引为空";

        OffSetX = ScreenW / 2 / CellW;
        OffSetY = ScreenH / 2 / CellH - 1;
        ViewRangeX = OffSetX + 6;
        ViewRangeY = OffSetY + 6;

        _screen = new Color32[ScreenW * ScreenH];
        _tex = new Texture2D(ScreenW, ScreenH, TextureFormat.RGBA32, false);
        _tex.filterMode = FilterMode.Point;
        _tex.wrapMode = TextureWrapMode.Clamp;
        return null;
    }

    /// <summary>取一张图（按需 gzip 解压 + LRU 缓存）。data 是 BGRA，直接就是 Color32。</summary>
    Tile Get(int lib, int idx)
    {
        long k = Key(lib, idx);
        Entry e;
        if (!_idx.TryGetValue(k, out e)) { Misses++; return null; }

        Tile t;
        if (_cache.TryGetValue(k, out t)) { t.lastUse = _frame; return t; }

        try
        {
            using (var ms = new MemoryStream(_blob, e.off, e.len, false))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            {
                int need = e.w * e.h * 4;
                var raw = new byte[need];
                int got = 0;
                while (got < need)
                {
                    int n = gz.Read(raw, got, need - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got != need) { Misses++; return null; }

                var px = new Color32[e.w * e.h];
                for (int i = 0; i < px.Length; i++)
                {
                    // ★ 原版纹理是 SlimDX Format.A8R8G8B8，小端内存里是 B,G,R,A。
                    //   Color32 的字段序是 R,G,B,A —— 所以必须把 0 和 2 对调。
                    //   （第一版写成了 raw[0]→R，实机截图整体发青，就是这里反了。）
                    px[i] = new Color32(raw[i * 4 + 2], raw[i * 4 + 1], raw[i * 4 + 0], raw[i * 4 + 3]);
                }

                t = new Tile { px = px, w = e.w, h = e.h, ox = e.ox, oy = e.oy, lastUse = _frame };
                _cache[k] = t;
                _lru.Add(k);
                _cacheBytes += need;
                EvictIfNeeded();
                return t;
            }
        }
        catch (Exception)
        {
            Misses++;
            return null;
        }
    }

    void EvictIfNeeded()
    {
        if (_cacheBytes <= CacheBudget) return;
        // 从最久未用的开始丢一半
        _lru.Sort((a, b) =>
        {
            Tile ta, tb;
            _cache.TryGetValue(a, out ta);
            _cache.TryGetValue(b, out tb);
            if (ta == null) return -1;
            if (tb == null) return 1;
            return ta.lastUse.CompareTo(tb.lastUse);
        });
        int drop = _lru.Count / 2;
        for (int i = 0; i < drop; i++)
        {
            Tile t;
            if (_cache.TryGetValue(_lru[i], out t))
            {
                _cacheBytes -= t.px.Length * 4;
                _cache.Remove(_lru[i]);
            }
        }
        _lru.RemoveRange(0, drop);
    }

    // ── 角色图库编号（与 build 侧 append_charlib.py 约定一致）──
    public const int LibArmour = 1000;   // 1000 + Armour
    public const int LibHair = 1200;
    public const int LibWeapon = 1400;

    /// <summary>画一个角色。参数口径照抄 PlayerObject.DrawBody() / GameScene.DrawObjects()。</summary>
    /// <param name="armour">衣服编号（CArmour 序号），0 = 默认布衣</param>
    /// <param name="dir">朝向 0..7（0=Up 顺时针）</param>
    /// <param name="frameIdx">本动作内的帧序号</param>
    public void DrawPlayer(int ux, int uy, int px, int py,
                           int offMoveX, int offMoveY,
                           int armour, int dir, bool walking, int frameIdx)
    {
        if (_screen == null) return;

        // 帧表（Frames.cs:157-158）：Standing new Frame(0, 4, 0, 500) / Walking new Frame(32, 6, 0, 100)
        //
        // ★ 方向步长实测是 8，不是帧表里写的 Count(=4/6)。
        //   本工程 CArmour 图库（新版 8 方向素材）每 4(或6) 帧一段、段末按 8 对齐补空槽，
        //   所以 dir 之间真实间隔 = 8。用 Count 当步长会取到空槽（实测 dir1..3 全空）。
        //   判定依据：8 个方向左右镜像中线误差 0~3px（probe_stride.py）；
        //   Walking 的 base 也实测为 64，不是帧表写的 32（32 段是另一组动作，镜像误差 6px 不合格）。
        const int DirStride = 8;
        int start = walking ? 64 : 0;
        int count = walking ? 6 : 4;
        int frame = start + DirStride * dir + (frameIdx % count);

        // DrawLocation = (Movement - User.Movement + OffSet) * Cell，严格按 PlayerObject.cs:848
        //   ★ 注意这里没有 -OffSetX（只有地面 DrawFloor 才有），因为这是原版两处代码的真实差别
        int dx = (px - ux + OffSetX) * CellW + offMoveX;
        int dy = (py - uy + OffSetY) * CellH + offMoveY;

        // DrawBody：BodyLibrary.Draw(DrawFrame, DrawLocation, colour, true)
        //   useOffSet=true → point.Offset(mi.X, mi.Y)（MLibrary.cs:735-745）
        var t = Get(LibArmour + armour, frame);
        if (t != null) Draw(t, dx + t.ox, dy + t.oy);
    }

    /// <summary>合成一屏。等价于原版 DrawFloor() + DrawObjects() 两次调用。</summary>
    public void Render(MapControl map, int ux, int uy, int mapW, int mapH,
                       int offMoveX = 0, int offMoveY = 0)
    {
        if (_screen == null) return;
        _frame++;
        DrawCalls = 0;

        // 原版：DXManager.Device.Clear(ClearFlags.Target, Color.Empty, ...) 是纯黑
        var black = new Color32(0, 0, 0, 255);
        for (int i = 0; i < _screen.Length; i++) _screen[i] = black;

        int startX = ux - ViewRangeX, endX = ux + ViewRangeX;
        int startY = uy - ViewRangeY, endY = uy + ViewRangeY;
        int endYExtended = endY + 5;

        // ── ① DrawFloor ──
        for (int y = startY; y <= endYExtended; y++)
        {
            if (y <= 0) continue;
            if (y >= mapH) break;
            int drawY = (y - uy + OffSetY) * CellH + offMoveY;

            for (int x = startX; x <= endX; x++)
            {
                if (x < 0) continue;
                if (x >= mapW) break;
                int drawX = (x - ux + OffSetX) * CellW - OffSetX + offMoveX;

                Bridge.GetCell(map, x, y, out short bi, out int bimg,
                                            out short mi, out int mimg,
                                            out short fi, out int fimg);

                if (y % 2 == 0 && x % 2 == 0 && y <= endY && bimg != 0 && bi != -1)
                {
                    int index = (bimg & 0x1FFFFFFF) - 1;
                    var t = Get(bi, index);
                    if (t != null) Draw(t, drawX, drawY);
                }

                int midIdx = mimg - 1;
                if (midIdx >= 0 && mi != -1)
                {
                    var t = Get(mi, midIdx);
                    if (t != null && IsFlat(t)) Draw(t, drawX, drawY);
                }

                int frontIdx = (fimg & 0x7FFF) - 1;
                if (frontIdx != -1 && fi != -1 && fi != 200)
                {
                    var t = Get(fi, frontIdx);
                    if (t != null && IsFlat(t)) Draw(t, drawX, drawY);
                }
            }
        }

        // ── ② DrawObjects（树/石头/建筑，向上顶到脚下）──
        //    注意 +1：原版这里比 DrawFloor 的 drawY 多一格，让物体底边落在格子下沿
        int objEndY = uy + ViewRangeY + 25;
        for (int y = startY; y <= objEndY; y++)
        {
            if (y <= 0) continue;
            if (y >= mapH) break;
            int drawY = (y - uy + OffSetY + 1) * CellH + offMoveY;

            for (int x = startX; x <= endX; x++)
            {
                if (x < 0) continue;
                if (x >= mapW) break;
                int drawX = (x - ux + OffSetX) * CellW - OffSetX + offMoveX;

                Bridge.GetCell(map, x, y, out short bi, out int bimg,
                                            out short mi, out int mimg,
                                            out short fi, out int fimg);

                // Middle 的高物体
                int midIdx = mimg - 1;
                if (midIdx > 0 && mi != -1)
                {
                    var t = Get(mi, midIdx);
                    if (t != null && !IsFlat(t)) Draw(t, drawX, drawY - t.h);
                }

                // Front 的高物体
                int frontIdx = (fimg & 0x7FFF) - 1;
                if (frontIdx >= 0 && fi != -1 && fi != 200)
                {
                    var t = Get(fi, frontIdx);
                    if (t != null && !IsFlat(t))
                    {
                        if (fi == 28 && t.ox != 0)
                            Draw(t, drawX + t.ox, drawY - CellH + t.oy);
                        else
                            Draw(t, drawX, drawY - t.h);
                    }
                }
            }
        }

        // ★ 这里不把像素传给 GPU —— 上传是独立的一步（Flush）。
        //   原版是「画一层、设备立即落到后备缓冲」，我们只有一个 CPU 缓冲，
        //   必须等所有层（含 DrawPlayer 画的角色）都画完再一次性上传。
        //   第一版在 Render 末尾就上传，导致后画的角色只留在内存里、屏幕上没有（实机已复现）。
    }

    /// <summary>把 CPU 画面缓冲上传到纹理。所有绘制（地图 + 角色）结束后调用一次。</summary>
    public void Flush()
    {
        if (_tex == null || _screen == null) return;
        _tex.SetPixels32(_screen);
        _tex.Apply(false, false);
    }

    static bool IsFlat(Tile t)
    {
        return (t.w == CellW && t.h == CellH) || (t.w == CellW * 2 && t.h == CellH * 2);
    }

    /// <summary>纯 alpha 混合贴一张图（等价于 DXManager.Draw 在默认混合态下的行为）。</summary>
    void Draw(Tile t, int dx, int dy)
    {
        if (dx >= ScreenW || dy >= ScreenH || dx + t.w <= 0 || dy + t.h <= 0) return;

        int x0 = dx < 0 ? -dx : 0;
        int y0 = dy < 0 ? -dy : 0;
        int x1 = t.w; if (dx + x1 > ScreenW) x1 = ScreenW - dx;
        int y1 = t.h; if (dy + y1 > ScreenH) y1 = ScreenH - dy;
        if (x1 <= x0 || y1 <= y0) return;

        DrawCalls++;
        var src = t.px;

        for (int yy = y0; yy < y1; yy++)
        {
            int srow = yy * t.w;
            // 屏幕缓冲自下而上（Unity 纹理约定）；(dy+yy) 是自上而下的屏幕行号
            int drow = (ScreenH - 1 - (dy + yy)) * ScreenW + dx;
            for (int xx = x0; xx < x1; xx++)
            {
                Color32 s = src[srow + xx];
                if (s.a == 0) continue;
                if (s.a == 255) { _screen[drow + xx] = s; continue; }

                Color32 d = _screen[drow + xx];
                int ia = 255 - s.a;
                _screen[drow + xx] = new Color32(
                    (byte)((s.b * s.a + d.b * ia) / 255),
                    (byte)((s.g * s.a + d.g * ia) / 255),
                    (byte)((s.r * s.a + d.r * ia) / 255),
                    255);
            }
        }
    }
}
