// P0-H —— 实体层：怪物 / NPC 的刷出、渲染、朝向、寻路、战斗状态机、死亡、掉落、拾取
//
// ============================ 数据来源（全部可回溯，无一处拍脑袋） ============================
// 刷怪点   : Assets/Resources/p0h_world.json 的 `spawns`  ← Mirserver\Mir200\Envir\MonGen.txt（85 行 map0）
// 怪物属性 : 同上 `monsters`                    ← Mud2\DB\Monster.DB（3 层解析，见 _p0h\db\FINDINGS.md）
// NPC 清单 : 同上 `npcs`                         ← Mirserver\Mir200\Envir\MerChant.txt（23 条 map0）
// 外观帧   : Assets/Resources/p0h/mon_index.txt + mon_atlas_*.png  ← Mon*.wzl 按 (appr,act,dir,frame) 索引
// 锚点语义 : mon_layout.json `anchor` = "topLeft = worldPoint + (ox, oy)"
//            ← Crystal `MonsterObject.cs:4309` BodyLibrary.Draw(..., offSet:true)
//               + `MLibrary.cs:665` `if (offSet) point.Offset(mi.X, mi.Y);`
//
// ============================ 几何口径（与 P0BMapWalk 逐字一致，不新造数字） ============================
// P0BMapWalk.World(x,y) = Vector3((x-y)*HW*0.01, (x+y)*HH*0.01, 0)；HW=48 HH=32
//   ⇒ 1 格 = 0.48 世界单位横 / 0.32 世界单位纵；世界单位 * 100 = 设备像素。
// Sprite 以 worldPoint 为**左上角**，故 pivot 用 (0,1)，尺寸 scale = (w/100, h/100)。
// 绘制深度沿用 P0BMapWalk 的 `x+y` 口径，槽位夹在 smTiles(-100+(x+y)) 与 player(30000) 之间：
//   monster sortingOrder = 1000 + (x+y)，NPC = 1000 + (x+y)（同层按 y 稳定，靠 order+层级错开）。
//
// ⚠ 本文件禁止 `using System;`（会与 UnityEngine.Object 撞名，见 P0BMapWalk 文件头 CS0104 说明）。
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class P0HWorld
{
    // ======================= 常量 =======================
    const int HW = 48, HH = 32;
    const float WPX = 0.01f;             // 世界单位换算系数（与 P0BMapWalk 同口径）
    const int SORT_BASE = 1000;          // 实体排序基准（夹在 smTiles 与 player 之间）

    // ======================= 数据 =======================
    public class MonDef
    {
        public string name;
        public int raceimg, ai, appr, level, hp, ac, mac, dc, dcMax, mc, sc;
        public int accuracy, agility, attackspeed, movespeed, exp;
    }

    public class NpcDef
    {
        public string label, script;
        public int x, y, appr, dir;
    }

    /// <summary>
    /// 一行掉落（Crystal `DropInfo`，MonsterInfo.cs:328-345）：
    ///   `1/N 金币 100`  -> c=N, gold=100
    ///   `1/N 乌木剑`    -> c=N, looks=物品在 DnItems 里的帧号
    /// </summary>
    public class DropRow
    {
        public int c;                    // 1/c 的概率（AttemptDrop 里 rate = c/DropRate，DropRate=1）
        public int gold;                 // >0 表示金币
        public int looks;                // 物品地面帧号
        public string item;              // 物品名（无则空）
    }

    public class Sprite
    {
        public Texture2D tex;            // 所属图集页
        public Rect rect;                // 图集内像素矩形（y 为 Unity 左下原点，取帧时还原）
        public int page;                 // 图集页号
        public int rawY;                 // 原始 PNG 行号（自顶向下），翻转 + 越界校验都用它
        public int ox, oy;               // 锚点偏移（相对帧左上角）
        public bool flipX;               // dir 1/2 是镜像（与原客户端同法）
        public bool ok;
    }

    // 键 = appr|act|dir|frame
    readonly Dictionary<string, Sprite> mSprites = new Dictionary<string, Sprite>();
    readonly Dictionary<string, MonDef> mMonDefs = new Dictionary<string, MonDef>();
    readonly List<NpcDef> mNpcDefs = new List<NpcDef>();
    readonly Dictionary<int, NpcDef> mNpcByPos = new Dictionary<int, NpcDef>();
    readonly List<Dictionary<string, object>> mSpawns = new List<Dictionary<string, object>>();

    readonly List<Texture2D> mPages = new List<Texture2D>();
    // 物品图集（地面掉落用）：DnItems.wil 解码出来的帧，键 = Looks
    readonly Dictionary<int, Sprite> mItemSpr = new Dictionary<int, Sprite>();
    readonly List<Texture2D> mItemPages = new List<Texture2D>();
    // 掉落表：怪物名 -> 行（c=1/N 的 N；gold>0 或 looks=地面物品帧号）
    readonly Dictionary<string, List<DropRow>> mDrops = new Dictionary<string, List<DropRow>>();
    readonly Dictionary<int, Texture2D> mNpcAtlas = new Dictionary<int, Texture2D>();
    // NPC 索引：appr -> dir -> frame -> Sprite
    readonly Dictionary<int, Dictionary<int, Dictionary<int, Sprite>>> mNpcSpr =
        new Dictionary<int, Dictionary<int, Dictionary<int, Sprite>>>();

    public bool Ready { get; private set; }
    public int SpriteCount { get { return mSprites.Count; } }
    public int SPawnCount { get { return mSpawns.Count; } }
    public int NpcCount { get { return mNpcDefs.Count; } }

    // ======================= 加载 =======================
    /// <summary>
    /// 加载全部数据。任何一项缺失都打 LogError 并置 Ready=false（拒绝静默降级画空气）。
    /// </summary>
    public bool Load()
    {
        bool ok = true;

        // ---- ① 世界契约（唯一权威源） ----
        TextAsset world = Resources.Load<TextAsset>("p0h_world");
        if (world == null) { Debug.LogError("[P0H] ★素材缺失 p0h_world.json"); return false; }
        if (!ParseWorld(world.text)) ok = false;

        // ---- ② 怪物帧索引 ----
        TextAsset mi = Resources.Load<TextAsset>("p0h/mon_index");
        if (mi == null) { Debug.LogError("[P0H] ★素材缺失 p0h/mon_index.txt"); ok = false; }
        else ParseMonIndex(mi.text);

        // ---- ③ 怪物图集页（按目录下实际存在的 mon_atlas_N 逐页载入）----
        for (int p = 0; p < 8; p++)
        {
            Texture2D t = Resources.Load<Texture2D>("p0h/mon_atlas_" + p);
            if (t == null) break;
            mPages.Add(t);
        }
        if (mPages.Count == 0) { Debug.LogError("[P0H] ★素材缺失 p0h/mon_atlas_*"); ok = false; }

        // ---- ④ NPC 索引 + 图集（每个 appr 一张） ----
        TextAsset ni = Resources.Load<TextAsset>("p0h/npc_index");
        if (ni == null) { Debug.LogError("[P0H] ★素材缺失 p0h/npc_index.txt"); ok = false; }
        else ParseNpcIndex(ni.text);

        // ---- ⑤ 地面物品图集（DnItems.wil 解出来的，键 = 物品 Looks；金币用 112-116）----
        for (int p = 0; p < 4; p++)
        {
            Texture2D t = Resources.Load<Texture2D>("p0h/item_atlas_" + p);
            if (t == null) break;
            mItemPages.Add(t);
        }
        if (mItemPages.Count == 0) { Debug.LogError("[P0H] ★素材缺失 p0h/item_atlas_*"); ok = false; }
        TextAsset ii = Resources.Load<TextAsset>("p0h/item_index");
        if (ii == null) { Debug.LogError("[P0H] ★素材缺失 p0h/item_index.txt"); ok = false; }
        else ParseItemIndex(ii.text);

        // ---- ⑥ 掉落表（真实 Envir/MonItems）----
        TextAsset dr = Resources.Load<TextAsset>("p0h_drops");
        if (dr == null) { Debug.LogError("[P0H] ★素材缺失 p0h_drops.json"); ok = false; }
        else ParseDrops(dr.text);

        // 绑定帧到图集纹理
        BindTextures();

        Ready = ok;
        Debug.Log("[P0H] 数据加载 " + (ok ? "OK" : "★失败") +
                  " | 怪物帧 " + mSprites.Count +
                  " 图集页 " + mPages.Count +
                  " NPC帧 " + CountNpcSprites() + " NPC图集 " + mNpcAtlas.Count +
                  " 物品帧 " + mItemSpr.Count + " 物品页 " + mItemPages.Count +
                  " 掉落表 " + mDrops.Count +
                  " | 怪物种类 " + mMonDefs.Count + " NPC " + mNpcDefs.Count + " 刷怪点 " + mSpawns.Count);
        return ok;
    }

    int CountNpcSprites()
    {
        int n = 0;
        foreach (var a in mNpcSpr.Values) foreach (var b in a.Values) n += b.Count;
        return n;
    }

    /// <summary>
    /// 把已解析的 Sprite.tex 从 null 绑到真实纹理（索引先于纹理解析，故分两步）。
    /// ★ 同时做**尺寸校验**：索引里的 x/y/w/h 以源 PNG 坐标为准，若纹理导入时被降采样
    ///   （例：maxTextureSize 太小把 64×4096 压成 32×2048），坐标就整体超界，
    ///   Sprite.Create 会抛 ArgumentException 并把整个 Bootstrap 打断。
    ///   这里把超界的帧标 ok=false，取帧时返回 null（该帧不画），保证**永不抛**。
    /// </summary>
    void BindTextures()
    {
        int clipped = 0;
        for (int p = 0; p < mPages.Count; p++)
            foreach (var s in mSprites.Values)
            {
                if (s.page != p || s.tex != null) continue;
                s.tex = mPages[p];
                if (!Fits(s, mPages[p])) { s.ok = false; clipped++; }
            }

        foreach (var kv in mNpcAtlas)
        {
            Texture2D t = kv.Value;
            Dictionary<int, Dictionary<int, Sprite>> bydir;
            if (!mNpcSpr.TryGetValue(kv.Key, out bydir)) continue;
            foreach (var d in bydir.Values)
                foreach (var s in d.Values)
                {
                    s.tex = t;
                    if (!Fits(s, t)) { s.ok = false; clipped++; }
                }
        }

        foreach (var s in mItemSpr.Values)
        {
            if (s.page < 0 || s.page >= mItemPages.Count) { s.ok = false; clipped++; continue; }
            s.tex = mItemPages[s.page];
            if (!Fits(s, s.tex)) { s.ok = false; clipped++; }
        }

        if (clipped > 0)
            Debug.LogError("[P0H] ★" + clipped + " 帧超出纹理范围（纹理被降采样？检查 P0HImport maxTextureSize）——已跳过不画");
    }

    /// <summary>
    /// 帧矩形（源 PNG 坐标）是否完整落在纹理内。
    /// ★ 只判 x 与 rawY；**绝不能判 rect.y** —— 此刻 rect.y 还是 -1 哨兵（等取帧时按纹理高换算），
    ///   判它会把每一帧都当成越界（实测一次干掉全部 2708 帧）。
    /// </summary>
    static bool Fits(Sprite s, Texture2D t)
    {
        if (t == null) return false;
        if (s.rect.x < 0f) return false;
        if (s.rect.x + s.rect.width > t.width) return false;
        if (s.rawY + s.rect.height > t.height) return false;
        return true;
    }

    // ======================= 解析 =======================
    bool ParseWorld(string json)
    {
        try
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null) { Debug.LogError("[P0H] ★p0h_world.json 根不是对象"); return false; }

            object o;
            // ---- monsters ----
            if (root.TryGetValue("monsters", out o))
            {
                var ms = o as Dictionary<string, object>;
                if (ms != null)
                    foreach (var kv in ms)
                    {
                        var d = kv.Value as Dictionary<string, object>;
                        if (d == null) continue;
                        mMonDefs[kv.Key] = new MonDef
                        {
                            name = kv.Key,
                            raceimg = I(d, "raceimg"), ai = I(d, "ai"), appr = I(d, "appr"),
                            level = I(d, "level"), hp = I(d, "hp"), ac = I(d, "ac"), mac = I(d, "mac"),
                            dc = I(d, "dc"), dcMax = I(d, "dcmax"), mc = I(d, "mc"), sc = I(d, "sc"),
                            accuracy = I(d, "accuracy"), agility = I(d, "agility"),
                            attackspeed = I(d, "attackspeed"), movespeed = I(d, "movespeed"),
                            exp = I(d, "exp")
                        };
                    }
            }
            // ---- spawns ----
            if (root.TryGetValue("spawns", out o))
            {
                var ss = o as List<object>;
                if (ss != null)
                    foreach (var e in ss)
                    {
                        var d = e as Dictionary<string, object>;
                        if (d != null) mSpawns.Add(d);
                    }
            }
            // ---- npcs ----
            if (root.TryGetValue("npcs", out o))
            {
                var ns = o as List<object>;
                if (ns != null)
                    foreach (var e in ns)
                    {
                        var d = e as Dictionary<string, object>;
                        if (d == null) continue;
                        var nd = new NpcDef
                        {
                            label = S(d, "label"), script = S(d, "script"),
                            x = I(d, "x"), y = I(d, "y"), appr = I(d, "appr"), dir = I(d, "dir")
                        };
                        mNpcDefs.Add(nd);
                        mNpcByPos[Key2(nd.x, nd.y)] = nd;
                    }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[P0H] ★p0h_world.json 解析异常: " + ex.Message);
            return false;
        }
        if (mMonDefs.Count == 0) { Debug.LogError("[P0H] ★monsters 为空"); return false; }
        if (mSpawns.Count == 0) { Debug.LogError("[P0H] ★spawns 为空"); return false; }
        return true;
    }

    static int I(Dictionary<string, object> d, string k)
    {
        object o;
        if (!d.TryGetValue(k, out o) || o == null) return 0;
        if (o is double) return (int)System.Math.Round((double)o);
        if (o is long) return (int)(long)o;
        int r; if (int.TryParse(o.ToString(), out r)) return r;
        return 0;
    }

    static string S(Dictionary<string, object> d, string k)
    {
        object o;
        return (d.TryGetValue(k, out o) && o != null) ? o.ToString() : "";
    }

    /// <summary>
    /// mon_index.txt：首行 `count=N`，其后每行 11 列
    /// `&lt;appr&gt; &lt;act&gt; &lt;dir&gt; &lt;frame&gt; &lt;page&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; &lt;ox&gt; &lt;oy&gt;`
    /// （x,y 为图集像素坐标；w,h 裁边后尺寸；ox,oy 相对帧左上角的锚点偏移）
    /// </summary>
    void ParseMonIndex(string text)
    {
        var lines = text.Split('\n');
        int head = -1, bad = 0;
        foreach (var raw in lines)
        {
            string s = raw.Trim();
            if (s.Length == 0 || s[0] == '#') continue;
            if (s.StartsWith("count="))
            {
                int.TryParse(s.Substring(6), out head);
                continue;
            }
            var t = s.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 11) { bad++; continue; }
            int appr, dir, frame, page, x, y, w, h, ox, oy;
            string act = t[1];
            if (!int.TryParse(t[0], out appr) || !int.TryParse(t[2], out dir) ||
                !int.TryParse(t[3], out frame) || !int.TryParse(t[4], out page) ||
                !int.TryParse(t[5], out x) || !int.TryParse(t[6], out y) ||
                !int.TryParse(t[7], out w) || !int.TryParse(t[8], out h) ||
                !int.TryParse(t[9], out ox) || !int.TryParse(t[10], out oy)) { bad++; continue; }

            string key = Key4(appr, act, dir, frame);
            mSprites[key] = new Sprite
            {
                page = page,
                // ★ 图集 y 是**自顶向下**的 PNG 行号，Unity 纹理 y 是**自底向上** ⇒ 必须翻转。
                //   翻转需要纹理高度，而纹理会话在此之后才载入，故这里 rect.y 先置 -1 作哨兵，
                //   首次 MonSprite() 时按 `tex.height - y - h` 一次性换算并回写（见 MonSprite）。
                rect = new Rect(x, -1f, w, h),
                rawY = y,
                ox = ox, oy = oy,
                flipX = (dir == 1 || dir == 2),
                ok = true
            };
        }
        if (head >= 0 && mSprites.Count != head)
            Debug.LogWarning("[P0H] ★mon_index 行数 " + mSprites.Count + " ≠ 声明 " + head);
        if (bad > 0) Debug.LogWarning("[P0H] ★mon_index 有 " + bad + " 行解析失败");
        Debug.Log("[P0H] 怪物索引解析 " + mSprites.Count + "/" + head + " 行 => " +
                  (head > 0 && mSprites.Count == head ? "OK" : "★异常"));
    }

    // 索引里的原始 y 已存进 Sprite.rawY（PNG 行号，自顶向下；
    // Unity 纹理自底向上，故取帧时需按页高翻转，见 MonSprite/NpcSprite）。

    /// <summary>
    /// npc_index.txt：首行 `count=N`，其后每行 9 列
    /// `&lt;appr&gt; &lt;dir&gt; &lt;frame&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; &lt;ox&gt; &lt;oy&gt;`
    /// （**没有 page 列**：每个 appr 一张独立图集 npc_atlas_&lt;appr&gt;.png）
    /// </summary>
    void ParseNpcIndex(string text)
    {
        var lines = text.Split('\n');
        int head = -1, n = 0, bad = 0;
        foreach (var raw in lines)
        {
            string s = raw.Trim();
            if (s.Length == 0 || s[0] == '#') continue;
            if (s.StartsWith("count=")) { int.TryParse(s.Substring(6), out head); continue; }
            var t = s.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 9) { bad++; continue; }
            int appr, dir, frame, x, y, w, h, ox, oy;
            if (!int.TryParse(t[0], out appr) || !int.TryParse(t[1], out dir) ||
                !int.TryParse(t[2], out frame) || !int.TryParse(t[3], out x) ||
                !int.TryParse(t[4], out y) || !int.TryParse(t[5], out w) ||
                !int.TryParse(t[6], out h) || !int.TryParse(t[7], out ox) ||
                !int.TryParse(t[8], out oy)) { bad++; continue; }

            Dictionary<int, Dictionary<int, Sprite>> bydir;
            if (!mNpcSpr.TryGetValue(appr, out bydir))
            {
                bydir = new Dictionary<int, Dictionary<int, Sprite>>();
                mNpcSpr[appr] = bydir;
            }
            Dictionary<int, Sprite> byf;
            if (!bydir.TryGetValue(dir, out byf))
            {
                byf = new Dictionary<int, Sprite>();
                bydir[dir] = byf;
            }
            byf[frame] = new Sprite
            {
                rect = new Rect(x, -1f, w, h),
                rawY = y,
                ox = ox, oy = oy,
                flipX = (dir == 2),
                ok = true
            };
            n++;
        }
        // 载入每个 appr 的图集纹理
        foreach (int appr in mNpcSpr.Keys)
        {
            Texture2D t = Resources.Load<Texture2D>("p0h/npc_atlas_" + appr);
            if (t == null) { Debug.LogError("[P0H] ★素材缺失 p0h/npc_atlas_" + appr); continue; }
            mNpcAtlas[appr] = t;
        }
        if (head >= 0 && n != head)
            Debug.LogWarning("[P0H] ★npc_index 行数 " + n + " ≠ 声明 " + head);
        if (bad > 0) Debug.LogWarning("[P0H] ★npc_index 有 " + bad + " 行解析失败");
        Debug.Log("[P0H] NPC 索引解析 " + n + "/" + head + " 行, appr " + mNpcSpr.Count +
                  " 个 => " + (head > 0 && n == head ? "OK" : "★异常"));
    }

    // ======================= 取帧 =======================
    static string Key4(int appr, string act, int dir, int frame)
    {
        return appr + "|" + act + "|" + dir + "|" + frame;
    }

    static int Key2(int x, int y) { return x * 100000 + y; }

    /// <summary>
    /// 取怪物帧。拿到后 rect 已按图集纹理高度换算成 Unity 坐标（左下原点）。
    /// </summary>
    public Sprite MonSprite(int appr, string act, int dir, int frame)
    {
        Sprite s;
        if (!mSprites.TryGetValue(Key4(appr, act, dir, frame), out s)) return null;
        if (!s.ok) return null;                               // 越界帧：不画也不抛
        if (s.tex == null && s.page < mPages.Count) s.tex = mPages[s.page];
        if (s.tex == null) return null;
        if (s.rect.y < 0f)
            s.rect = new Rect(s.rect.x, s.tex.height - s.rawY - s.rect.height,
                              s.rect.width, s.rect.height);
        return s;
    }

    public Sprite NpcSprite(int appr, int dir, int frame)
    {
        Dictionary<int, Dictionary<int, Sprite>> bydir;
        if (!mNpcSpr.TryGetValue(appr, out bydir)) return null;
        Dictionary<int, Sprite> byf;
        if (!bydir.TryGetValue(dir, out byf)) return null;
        Sprite s;
        if (!byf.TryGetValue(frame, out s)) return null;
        if (!s.ok) return null;                               // 越界帧：不画也不抛
        if (s.tex == null && mNpcAtlas.ContainsKey(appr)) s.tex = mNpcAtlas[appr];
        if (s.tex == null) return null;
        if (s.rect.y < 0f)
            s.rect = new Rect(s.rect.x, s.tex.height - s.rawY - s.rect.height,
                              s.rect.width, s.rect.height);
        return s;
    }

    /// <summary>
    /// item_index.txt：首行 `count=N`，其后每行 6 列
    /// `&lt;looks&gt; &lt;page&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt;`（矩形已裁到 bbox，
    /// 原客户端是**居中在 96×64 格子里**画，见 ItemObject.cs:78-79，
    /// 故这里不存锚点，由调用方按格子居中）。
    /// </summary>
    void ParseItemIndex(string text)
    {
        var lines = text.Split('\n');
        int head = -1, n = 0, bad = 0;
        foreach (var raw in lines)
        {
            string s = raw.Trim();
            if (s.Length == 0) continue;
            if (s.StartsWith("count="))
            {
                int.TryParse(s.Substring(6).Trim(), out head);
                continue;
            }
            string[] t = s.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 6) { bad++; continue; }
            int looks, page, x, y, w, h;
            if (!int.TryParse(t[0], out looks) || !int.TryParse(t[1], out page) ||
                !int.TryParse(t[2], out x) || !int.TryParse(t[3], out y) ||
                !int.TryParse(t[4], out w) || !int.TryParse(t[5], out h)) { bad++; continue; }
            mItemSpr[looks] = new Sprite
            {
                rect = new Rect(x, -1f, w, h),
                rawY = y, page = page, ox = 0, oy = 0, flipX = false, ok = true
            };
            n++;
        }
        if (head >= 0 && n != head)
            Debug.LogWarning("[P0H] ★item_index 行数 " + n + " ≠ 声明 " + head);
        if (bad > 0) Debug.LogWarning("[P0H] ★item_index 有 " + bad + " 行解析失败");
        Debug.Log("[P0H] 物品索引解析 " + n + "/" + head + " 行 => " +
                  (head > 0 && n == head ? "OK" : "★异常"));
    }

    /// <summary>
    /// p0h_drops.json：`{ "tables": { "鸡": [ {c,gold,looks,item}, ... ] } }`
    /// 口径 = Crystal MonsterInfo.cs:328-345 DropInfo.FromLine + :510-524 AttemptDrop。
    /// </summary>
    void ParseDrops(string json)
    {
        try
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null) { Debug.LogError("[P0H] ★p0h_drops.json 根不是对象"); return; }
            object o;
            if (!root.TryGetValue("tables", out o)) { Debug.LogError("[P0H] ★p0h_drops.json 缺 tables"); return; }
            var ts = o as Dictionary<string, object>;
            if (ts == null) { Debug.LogError("[P0H] ★p0h_drops.tables 不是对象"); return; }
            int rows = 0;
            foreach (var kv in ts)
            {
                var arr = kv.Value as List<object>;
                var lst = new List<DropRow>();
                if (arr != null)
                    foreach (var e in arr)
                    {
                        var d = e as Dictionary<string, object>;
                        if (d == null) continue;
                        var r = new DropRow
                        {
                            c = Mathf.Max(1, I(d, "c")), gold = I(d, "gold"),
                            looks = I(d, "looks"), item = S(d, "item")
                        };
                        if (r.gold <= 0 && r.looks <= 0) continue;
                        lst.Add(r);
                        rows++;
                    }
                mDrops[kv.Key] = lst;
            }
            Debug.Log("[P0H] 掉落表解析 " + mDrops.Count + " 张 " + rows + " 行 => " +
                      (mDrops.Count > 0 ? "OK" : "★异常"));
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[P0H] ★p0h_drops.json 解析异常: " + ex.Message);
        }
    }

    /// <summary>怪物名的掉落表（弓箭护卫这类没表的怪返回空表 = 不掉落，不臆造）。</summary>
    public IList<DropRow> DropsOf(string monName)
    {
        List<DropRow> l;
        return mDrops.TryGetValue(monName, out l) ? l : System.Array.Empty<DropRow>();
    }

    /// <summary>
    /// 地面物品帧（键 = 物品 Looks，即 ItemObject.cs:32 的 FloorItems/DnItems 索引）。
    /// 缺帧返回 null（不画也不抛）。
    /// </summary>
    public Sprite ItemSprite(int looks)
    {
        Sprite s;
        if (!mItemSpr.TryGetValue(looks, out s)) return null;
        if (!s.ok) return null;
        if (s.page < 0 || s.page >= mItemPages.Count) return null;
        if (s.tex == null) s.tex = mItemPages[s.page];
        if (s.tex == null) return null;
        if (s.rect.y < 0f)
            s.rect = new Rect(s.rect.x, s.tex.height - s.rawY - s.rect.height,
                              s.rect.width, s.rect.height);
        return s;
    }

    public int ItemSpriteCount { get { return mItemSpr.Count; } }

    public MonDef GetMon(string name)
    {
        MonDef d;
        return mMonDefs.TryGetValue(name, out d) ? d : null;
    }

    public IList<NpcDef> Npcs { get { return mNpcDefs; } }
    public IList<Dictionary<string, object>> Spawns { get { return mSpawns; } }

    public NpcDef NpcAt(int x, int y)
    {
        NpcDef d;
        return mNpcByPos.TryGetValue(Key2(x, y), out d) ? d : null;
    }
}

// =====================================================================================
// MiniJson —— 极简 JSON 解析器（Unity 内置 JsonUtility 不支持 Dictionary/任意结构，
//   而 p0h_world.json 的 monsters 是「名字 -> 对象」的字典，故必须自带一份。
//   只支持本工程用到的子集：object / array / string / number / true / false / null。
//   ⚠ 不引外部 DLL（工程无包管理器依赖，加依赖会把构建链搞脆）。
// =====================================================================================
public static class MiniJson
{
    public static object Parse(string json)
    {
        int i = 0;
        object v = ParseValue(json, ref i);
        return v;
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
    }

    static object ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) return null;
        char c = s[i];
        if (c == '{') return ParseObject(s, ref i);
        if (c == '[') return ParseArray(s, ref i);
        if (c == '"') return ParseString(s, ref i);
        if (c == 't') { i += 4; return true; }
        if (c == 'f') { i += 5; return false; }
        if (c == 'n') { i += 4; return null; }
        return ParseNumber(s, ref i);
    }

    static Dictionary<string, object> ParseObject(string s, ref int i)
    {
        var d = new Dictionary<string, object>();
        i++;                                  // '{'
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return d; }
        while (i < s.Length)
        {
            SkipWs(s, ref i);
            string k = ParseString(s, ref i);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ':') i++;
            d[k] = ParseValue(s, ref i);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == '}') { i++; break; }
            break;
        }
        return d;
    }

    static List<object> ParseArray(string s, ref int i)
    {
        var a = new List<object>();
        i++;                                  // '['
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return a; }
        while (i < s.Length)
        {
            a.Add(ParseValue(s, ref i));
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == ']') { i++; break; }
            break;
        }
        return a;
    }

    static string ParseString(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length || s[i] != '"') return "";
        i++;
        var sb = new System.Text.StringBuilder();
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') break;
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) break;
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 <= s.Length)
                    {
                        int cp;
                        if (int.TryParse(s.Substring(i, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out cp))
                            sb.Append((char)cp);
                        i += 4;
                    }
                    break;
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();
    }

    static object ParseNumber(string s, ref int i)
    {
        int st = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
                                s[i] == '-' || s[i] == '+')) i++;
        string t = s.Substring(st, i - st);
        double d;
        if (double.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        return 0.0;
    }
}

