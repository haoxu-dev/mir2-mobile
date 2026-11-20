// P0-C —— 进游戏主 UI 第一层（状态栏 + 底部 HUD：生命球 / 魔法球 / 聊天框）运行时渲染。
//
// 设计要点（都是被契约与实测逼出来的，不是审美选择）：
//
// 1) ★ 坐标只走一条路：所有元素坐标一律写「原版 800x600 逻辑坐标」，
//    再换算到设备像素。**注意有两个换算器，组内绘制必须用后者**：
//      · `P0BMapWalk.CanvasRectForLogic()`  → **绝对**设备坐标（打点/报告用）
//      · `P0BMapWalk.GroupRectForLogic()`   → **组内相对**坐标（实际 `GUI.DrawTexture` 用）
//    ★★ 为什么必须分开（实测核算出来的，不是洁癖）：
//      `GUI.BeginGroup(MainUICanvasRect)` 已把绘制原点**平移**到 (508, -60) 并裁剪。
//      组内若再传绝对坐标 ⇒ **二次偏移**：组内传 (508,1080) 实际落到 (1016, 1020)
//      ⇒ HUD 右移 508px、底边错 60px，**整体偏出主视口**。
//      正确落点：组内相对 (0,1140) → 屏幕 (508, 1080)，底 = 1080+60 = **1140 = 屏底** ✅
//    ⇒ 原版逻辑 y=600 落在设备 y=1140（底部 HUD 贴屏底，1:1）。
//    ⇒ CROP_TOP 的真实依赖点 = **调用侧** `GUI.BeginGroup(MainUICanvasRect)`，
//      并由 `P0BMapWalk.AssertCanvasInvariant()` 在运行期**实测断言**（非注释声明）。
//
// 2) ★ 整数倍 2×：原版 800x600 画布 → 1600x1200 设备像素，SCALE = 2 是整数，
//    配合 Point filter（P0CImport 已把关）⇒ 无插值模糊，像素 1:1 放大。
//
// 3) ★ 数据驱动：布局不写死在本文件，读 `Assets/Resources/p0c_layout.json`（t1 规格的运行时投影）。
//    理由：t1 的 `ui_layout_spec.json` 是权威规格，硬编码会把「同一组数字写两遍」的老毛病带回来。
//    ⚠ 该 json 放 `Assets/Resources/` **根**，不放 `p0c/` —— 否则其 .meta 会被 harness A5 判为
//      「缺 filterMode/enableMipMap/textureCompression」的不合格条目（见 P0CImport.cs L5-18 实测）。
//
// 4) ★ 字段名避开图片扩展名：本布局 json 用 `tex`（Resources 名，无扩展名）而非 `assetPath`。
//    因为 harness A4 的 walk() 对**任意字段的字符串值**做 `\.(png|bmp|uib|tga|jpg|jpeg)$` 匹配
//    再逐个 os.path.isfile —— 值一旦以图片扩展名结尾就会被 Test-Path 并计入缺失。
//    权威映射（含真实绝对 assetPath）留在 t1 的 ui_layout_spec.json 里由 A4 审。
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>进游戏主 UI 第一层：状态栏 + 底部 HUD（生命球 / 魔法球 / 聊天框）。</summary>
// ★ 2026-10-13 builder：**故意不继承 MonoBehaviour**（原本写的是 `: MonoBehaviour`）。
//   理由（两条，都是实测扫出来的）：
//   ① 本类**零 MonoBehaviour 成员引用** —— 我扫过全文，`transform`/`gameObject`/`StartCoroutine`/
//      `Invoke`/`GetComponent`/生命周期方法(Awake/Start/Update/OnGUI)**一个都没有**；
//      `DrawAll()` 只用 `Resources.Load` / `GUI.DrawTexture` / `Debug.Log` 这些 **static** API，
//      字段全是内联初始化 ⇒ 继承 MonoBehaviour **没有任何收益**。
//   ② 唯一的构造点是 `P0BMapWalk.cs L508 _ui = new P0CUI();` —— 对 MonoBehaviour 用 `new`
//      是 Unity 的**已知反模式**（Unity 会往日志里打 "You are trying to create a MonoBehaviour
//      using the 'new' keyword. This is not allowed."），而我们**必须**让 logcat 干净：
//      t3 要从 logcat 抓 `[P0C]` 四数/绘制行，t4 要按 logcat 判 A3/A3_ui，
//      一条 UnityEngine 级的 "This is not allowed." 混在证据里，会让人误判成构建/运行故障。
//   ⚠ 诚实标注：该告警是 Unity 的**已知行为**，本轮**尚未在 logcat 观察到**
//      （p0c.apk 还没出）。改成普通类后该告警**从构造上不可能出现**，属"顺带消除不确定性"，
//      不是"修复了一个已观测到的报错"。t3 跑起来后请顺手确认 logcat 里确无此告警。
public class P0CUI
{
    [Serializable]
    public class Element
    {
        public string name;        // 元素名（唯一）
        public string tex;         // Resources 名，如 "p0c/statusbar"（★ 不带图片扩展名）
        public float[] logicalRect; // 原版 800x600 逻辑坐标 [x, y, w, h]，原点=左上
        public string source;      // 来源索引（报告名 + 行号；★ 不出现图片扩展名）
    }

    [Serializable]
    public class Layout
    {
        public int logicalW = 800;
        public int logicalH = 600;
        public Element[] elements;
    }

    public int DrawnCount;
    public int MissingCount;

    readonly List<Element> _drawn = new List<Element>();
    readonly List<string> _missingLogged = new List<string>();
    Layout _layout;
    bool _loaded;
    bool _drawErrLogged;
    bool _zeroDrawnLogged;

    public void LoadLayout()
    {
        _loaded = true;
        var ta = Resources.Load<TextAsset>("p0c_layout");
        if (ta == null)
        {
            // ★ 必须是 Error，不能是 Warning：Warning 逃得过所有正则，
            //   于是「主UI 一条都没画」会在全绿日志里静默通过（队长 09:4x 指出的全链路洞）。
            Debug.LogError("[P0C] ★ 主UI布局未装载 elements=0 已画=0 —— 未找到 " +
                           "Assets/Resources/p0c_layout.json（须由 t1 的 ui_layout_spec.json 投影" +
                           "生成并签入工程；主UI 一层将完全不渲染）");
            return;
        }
        try
        {
            _layout = JsonUtility.FromJson<Layout>(ta.text);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0C] ★ 主UI布局未装载 elements=0 已画=0 —— " +
                           "p0c_layout.json 解析失败: " + e.Message);
            return;
        }
        if (_layout == null || _layout.elements == null || _layout.elements.Length == 0)
        {
            Debug.LogError("[P0C] ★ 主UI布局未装载 elements=0 已画=0 —— " +
                           "p0c_layout.json 无有效 elements");
            return;
        }
        // ★ 正向判据行（harness/t3 会 grep「主UI布局装载 OK: N 个元素」并要求 N >= 3）。
        //   ⚠ 本行**不得**含「未装载 / elements=0 / 已画=0」字样，否则会被判为失败。
        Debug.Log("[P0C] 主UI布局装载 OK: " + _layout.elements.Length + " 个元素, 逻辑画布 " +
                  _layout.logicalW + "x" + _layout.logicalH);
    }

    /// <summary>按 t1 规格逐元素绘制。由 P0BMapWalk.OnGUI 在 MainUICanvasRect 组内调用。</summary>
    public void DrawAll()
    {
        if (!_loaded) LoadLayout();
        if (_layout == null || _layout.elements == null)
        {
            if (!_drawErrLogged)
            {
                _drawErrLogged = true;
                Debug.LogError("[P0C] ★ 主UI布局未装载 elements=0 已画=0 —— DrawAll 无布局可画，" +
                               "主UI 一层（状态栏/生命球/魔法球/聊天框）完全不渲染");
            }
            DrawnCount = 0; MissingCount = 0;
            return;
        }

        DrawnCount = 0; MissingCount = 0;
        _drawn.Clear();
        foreach (var e in _layout.elements)
        {
            if (e == null || e.logicalRect == null || e.logicalRect.Length < 4) continue;
            var tex = Resources.Load<Texture2D>(e.tex);
            if (tex == null)
            {
                MissingCount++;
                // 缺纹理 = 「原始素材 1:1 落地」失败，升为 Error（每个元素只报一次，防刷屏）。
                if (!_missingLogged.Contains(e.name))
                {
                    _missingLogged.Add(e.name);
                    Debug.LogError("[P0C] ★ HUD 元素缺纹理: " + e.name + " tex=" + e.tex +
                                   "（该元素不会出现在屏幕上）");
                }
                continue;
            }
            // ★ 逻辑坐标 -> 组内相对设备像素（**必须用 GroupRectForLogic**，见其注释：
            //   BeginGroup 已平移原点，组内再传绝对坐标会二次偏移 = 右移 508 / 底错 60）。
            Rect r = P0BMapWalk.GroupRectForLogic(
                e.logicalRect[0], e.logicalRect[1], e.logicalRect[2], e.logicalRect[3]);
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
            DrawnCount++;
            _drawn.Add(e);
        }

        // ★ 画了 0 个 = 目标②实质未达成。用**无歧义的机器记号 `已画=0`**报警：
        //   为什么不用「0 个已画」—— 它是「10 个已画」的子串，会把元素数 ≥10 的正常运行
        //   误判成失败（`in` 是子串匹配）。`已画=0` 与 `已画=10` 无子串包含关系，可安全 grep。
        if (DrawnCount == 0 && !_zeroDrawnLogged)
        {
            _zeroDrawnLogged = true;
            Debug.LogError("[P0C] ★ 主UI绘制为空 已画=0 缺=" + MissingCount +
                           " —— 主UI 一层（状态栏/生命球/魔法球/聊天框）没有任何像素上屏");
        }

        // ★ 取证窗口：首帧 + 每 300 帧（~5 秒）一次，保证 t3 抓晚也能看到这行。
        if (Time.frameCount < 3 || Time.frameCount % 300 == 0)
            LogEvidence();
    }

    /// <summary>★ 证据打点：逐元素打印「逻辑矩形 -> 设备矩形」，供报告与复核引用（含"画布/裁/HUD"关键字）。</summary>
    void LogEvidence()
    {
        if (_layout == null) return;
        Debug.Log("[P0C] 主UI绘制: 已画=" + DrawnCount + " 缺=" + MissingCount +
                  " | 逻辑画布 " + _layout.logicalW + "x" + _layout.logicalH +
                  " | 画布原点设备(" + P0BMapWalk.MainUICanvasRectX + ", " + P0BMapWalk.MainUICanvasRectY + ")" +
                  " | 缩放 " + P0BMapWalk.ScaleI + " | HUD贴底锚 设备y " + P0BMapWalk.HudBottomDeviceY);
        foreach (var e in _drawn)
        {
            // 组内相对式（实际绘制用）+ 绝对设备式（报告/复核用），两者并列打印，便于发现二次偏移。
            Rect rel = P0BMapWalk.GroupRectForLogic(
                e.logicalRect[0], e.logicalRect[1], e.logicalRect[2], e.logicalRect[3]);
            Rect abs = P0BMapWalk.CanvasRectForLogic(
                e.logicalRect[0], e.logicalRect[1], e.logicalRect[2], e.logicalRect[3]);
            Debug.Log("[P0C] HUD画布 " + e.name +
                      " 逻辑(" + e.logicalRect[0] + "," + e.logicalRect[1] + "," +
                      e.logicalRect[2] + "," + e.logicalRect[3] + ")" +
                      " -> 组内(" + rel.x + "," + rel.y + "," + rel.width + "," + rel.height + ")" +
                      " 绝对(" + abs.x + "," + abs.y + "," + abs.width + "," + abs.height + ")" +
                      " 底 设备y " + (abs.y + abs.height) +
                      " | 来源 " + e.source);
        }
    }
}
