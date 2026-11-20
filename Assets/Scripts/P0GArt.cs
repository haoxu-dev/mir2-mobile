// P0-G —— M2 位图层（真实 UI 图第一次上屏）。
//
// 为什么开这一层，而不是继续改 P0-F：
//   P0-F 只画「文字」，因为当时判定七面板 80 组控件里我方现成可用图只有 4 组（5.0%）。
//   2026-10-17 的素材复核（m2-asset-revision-1017.md）订正了这条前提：
//     · Title.Lib（899 帧 / 796 非空）与 UI_32bit.Lib（474 帧）**我方已有**，
//       位置 `_t5ev\libC\genB_0901\`，此前被判「我方缺」是错的。
//   但**素材齐 ≠ 七面板都能画**：严格按「源码指定库 + 索引在界内 + 帧 w>2/h>2」重算，
//   七面板 50 个静态位图需求仍是 17 可用 / 33 缺失。真正能做到「底图 + 页签按钮」齐全的
//   只有 InventoryDialog 一个 ⇒ 本层先只交付它，不假装其余面板能画。
//
// 本层与 P0-F 的分工（不重叠、不互相覆盖像素）：
//   P0-G：InventoryDialog 的**底图与 4 个页签按钮**（实打实的 .Lib 解码像素）
//   P0-F：七面板的 **MirLabel 文字**（默认全关，F9/F10/F11/Z/V/Enter 逐块开）
//   ⇒ 视觉上 P0-G 必须先画（在 P0-F 之前），否则文字会被底图盖住。
//
// 数据来源（不手抄、不推断）：
//   Assets/Resources/p0g_art_layout.json  ← 由 m2spec/gen_m2_art.py 生成
//   Assets/Resources/p0g/*.png            ← 由同一脚本从 .Lib 逐帧 gzip 解出，未经重采样
//   每条记录带 srcLib / srcIndex / sourceLine，可逐条回到 InventoryDialog.cs 核对。
//
// ★ 三条「登记但不画」的纪律（都是源码事实，不是缺素材）：
//   1. AddButton：源码 L91 显式 `Visible = false` ⇒ 构造期不画。素材 Title[483] 其实存在（48x25）。
//   2. WeightBar：源码 L37 `DrawImage = false`，实际由 WeightBar_BeforeDraw(L396-424) 按负重百分比
//      在 Prguse[24] / UI_32bit[471] / UI_32bit[470] 之间换帧并裁切宽度 ⇒ 静态定不了图，不许硬画一帧。
//   3. CloseButton / DelItemButton / LockBar：Prguse2[360/366/307] 全部越界。
//      ★ 并且 Crystal 自己的 Prguse2.Lib 也只有 14 帧 —— 原版这条路径本来也取不到图，
//        所以我方不是「缺素材」，是「复刻了一条原版就画不出来的路径」。不许用别的库顶替（会画错）。
//
// 坐标契约（与 P0-D / P0-F 完全一致）：
//   JSON 里一律写「原版 800x600 逻辑坐标」，运行时用 P0BMapWalk.GroupRectForLogic 换算。
//   本类只在 GUI.BeginGroup(MainUICanvasRect) 内被调用，**不能**用 CanvasRectForLogic。
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>M2 位图层（P0-G）：InventoryDialog 底图 + 4 个页签按钮。</summary>
public class P0GArt
{
    [Serializable]
    public class Item
    {
        public string name;
        public string panel;
        public string kind;          // DRAW / HIDDEN / RUNTIME / OOB
        public string srcLib;
        public int srcIndex;
        public string sourceLine;
        public string page;          // 分页归属："" = 所有页都画；否则仅该页（源码 Show*Page 互斥）
        public float[] logicalRect;
        public string tex;
        public int[] pixelSize;
        public string md5;
        public int libFrames;
        public float alphaRatio;
        public string note;
    }

    [Serializable]
    public class Panel
    {
        public string panel;
        public float[] origin;
        public float[] size;
        public string originEvidence;
        public bool visibleDefault;
        public string visibleEvidence;
        public string sourceFile;
    }

    [Serializable]
    public class Counts
    {
        public int total;
        public int draw;
        public int skipped;
    }

    [Serializable]
    public class Layout
    {
        public string spec;
        public string specVersion;
        public string generator;
        public int logicalW;
        public int logicalH;
        public string purpose;
        public Panel[] panels;
        public Counts counts;
        public Item[] items;
    }

    // 面板开关：按面板分别记录，键位**直接读 P0BMapWalk.HOTKEY_*（单点定义）**，与 P0-F 逐字同源
    // ★ 必须逐面板独立：一个 bool 管所有面板会让「按 F1 也把背包打开」。
    readonly Dictionary<string, bool> _panelOn = new Dictionary<string, bool>();

    // 角色面板分页：位图与 P0-F 文字必须严格同页，否则出现「属性页的图 + 状态页的字」。
    //
    // ★ 为什么这里只有 2 页，而源码有 4 页（这是本轮实测后的刻意取舍）：
    //   P0-F 的页序不是写死的，而是从 p0f_text_layout.json 里「出现过 page 字段的页」派生，
    //   实测只有 ['StatusPage', 'StatePage'] —— 因为 CharacterPage 是装备立绘（无 MirLabel 文字）、
    //   SkillPage 是技能格（无静态文字），两页在文字层里根本没有条目。
    //   若这里用 4 页（CharacterPage/StatusPage/StatePage/SkillPage），按 1 键时：
    //     P0G 走 CharacterPage->StatusPage->StatePage->SkillPage（4 步循环）
    //     P0F 走 StatusPage->StatePage（2 步循环）
    //     ⇒ 从第 3 次按键起必然错位，图上写着属性页、字却是状态页。
    //   ⇒ 以「图字同页」为硬约束，这里**跟随 P0F 的 2 页**。
    //     CharacterPage / SkillPage 的位图仍已生成并登记在 JSON 里（不丢素材），
    //     等 P0-F 扩到 4 页时同步放开即可。此限制在交付文档 §7 明写。
    static readonly string[] CHAR_PAGES = { "StatusPage", "StatePage" };
    // ★ 默认页 = StatusPage。这是**刻意偏离源码，属已知偏离，不是还原**。
    //   源码 CharacterDialog.cs L40-46：CharacterPage 构造期未设 Visible=false ⇒ 源码默认可见页是 CharacterPage。
    //   我方不跟，因为 CharacterPage 的底图是 Libraries.Prguse idx 340（L662 `CharacterPage.Index = 340 + offSet;`），
    //   而 Prguse[340]（女号 341）在**本机全部候选库**里都是 w=0/h=0 空壳帧，实测：
    //     · _t5ev\libC\genB_verify\Prguse.Lib   md5 db5f31b9e40b… ver 3 count  510 usable 251 → idx 334..347 全 0x0
    //     · _t5ev\libC\genC_0901_1\Prguse.Lib   md5 cec03bc8ae5b… ver 3 count 1784 usable 144 → idx 334..347 全 0x0
    //   ⇒ 默认停在 CharacterPage 的表现是「F10 一开就是整块黑」，不是取图 bug，是素材客观不存在（系统性缺帧，非取错文件）。
    //   改停 StatusPage：Title[506] 实测 248x284 可画，PNG 已在 Assets\Resources\p0g\chr_page_status_506.png。
    //   ⚠ 必须与 P0FText.cs 的 _activePage["CharacterDialog"] **成对修改**：两侧页序/落页公式必须逐字一致
    //     （见下方 L231-238 契约），单改一边会让图与字不同页。
    string _charPage = "StatusPage";

    public int DrawnCount;
    public int MissingCount;
    public int SizeMismatchCount;
    public int SkippedCount;         // HIDDEN / RUNTIME / OOB / 非当前分页，登记但不画
    public int OwnedCount;           // kind=OWNED / 面板由他层独占渲染 —— 不是缺图，不画也不该算缺
    public int OutOfBoundsDrawn;
    public int PageSkippedCount;     // 因分页互斥而未画（不是缺图）

    readonly List<string> _missingLogged = new List<string>();
    readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();
    Layout _layout;
    bool _loaded;
    bool _loadFailedLogged;
    bool _dupLogged;
    bool _verdictLogged;

    // ★ 上一次输出判定行时的计数快照。用途：只要本次绘制结果与上次不同就重出判定行，
    //   让 `ART_VERDICT` 永远描述「当前这一帧」，而不是冻结在首帧的全关状态（见 DrawAll 末尾注释）。
    int _lastLoggedDrawn = -1;
    int _lastLoggedMissing = -1;
    int _lastLoggedMismatch = -1;

    public void LoadLayout()
    {
        _loaded = true;
        // ★ 常驻显示面板（技能栏）：原版没有开关键，本来就在屏幕上。
        //   从 HOTKEY_CODES 摘掉它的键之后必须在这里置真，否则它会直接消失。
        for (int i = 0; i < P0BMapWalk.HOTKEY_PANELS.Length; i++)
        {
            string p = P0BMapWalk.HOTKEY_PANELS[i];
            if (P0BMapWalk.PanelAlwaysOn(p)) _panelOn[p] = true;
        }
        var ta = Resources.Load<TextAsset>("p0g_art_layout");
        if (ta == null)
        {
            if (!_loadFailedLogged)
            {
                _loadFailedLogged = true;
                Debug.LogError("[P0G] ★ 位图层未装载 items=0 —— 未找到 Assets/Resources/p0g_art_layout.json" +
                               "（须由 m2spec/gen_m2_art.py 生成并签入工程；背包面板位图层将完全不渲染）");
            }
            return;
        }
        try
        {
            _layout = JsonUtility.FromJson<Layout>(ta.text);
        }
        catch (Exception e)
        {
            if (!_loadFailedLogged)
            {
                _loadFailedLogged = true;
                Debug.LogError("[P0G] ★ 位图层未装载 items=0 —— p0g_art_layout.json 解析失败: " + e.Message);
            }
            return;
        }
        if (_layout == null || _layout.items == null || _layout.items.Length == 0)
        {
            Debug.LogError("[P0G] ★ 位图层未装载 items=0 —— p0g_art_layout.json 无有效 items");
            return;
        }

        int dup = 0;
        var seen = new HashSet<string>();
        foreach (var it in _layout.items)
        {
            if (it == null || string.IsNullOrEmpty(it.name)) continue;
            if (!seen.Add(it.name)) dup++;
        }
        if (dup > 0)
        {
            Debug.LogError("[P0G] ★ items 里存在 " + dup + " 条重名记录（会让同一控件被画两次）");
            _dupLogged = true;
        }

        Debug.Log("[P0G] 位图层装载 OK: items=" + _layout.items.Length +
                  " 可画=" + _layout.counts.draw +
                  " 不画=" + _layout.counts.skipped +
                  " 逻辑画布 " + _layout.logicalW + "x" + _layout.logicalH +
                  " | 规格 " + _layout.spec + " " + _layout.specVersion +
                  " | 生成器 " + _layout.generator);

        foreach (var p in _layout.panels)
        {
            if (p == null) continue;
            Debug.Log("[P0G] 面板 " + p.panel + " 原点=(" + p.origin[0] + "," + p.origin[1] + ")" +
                      " 尺寸=" + p.size[0] + "x" + p.size[1] +
                      " 默认可见=" + p.visibleDefault +
                      " | 原点依据: " + p.originEvidence);
            Debug.Log("[P0G] 面板 " + p.panel + " 可见性依据: " + p.visibleEvidence);
        }

        // 登记但不画的项：逐条回显原因，防止下游误以为「漏画」
        // ★ PAGEVARIANT 是**可画但有页条件**的项，不算 skipped，故这里同样跳过。
        int hidden = 0, runtime = 0, oob = 0, pageVar = 0;
        foreach (var it in _layout.items)
        {
            if (it == null || it.kind == "DRAW") continue;
            if (it.kind == "PAGEVARIANT") { pageVar++; continue; }
            if (it.kind == "HIDDEN") hidden++;
            else if (it.kind == "RUNTIME") runtime++;
            else if (it.kind == "OOB") oob++;
            Debug.Log("[P0G] 登记不画 " + it.name + " [" + it.kind + "] " +
                      it.srcLib + "[" + it.srcIndex + "] " + it.sourceLine +
                      " 库帧数=" + it.libFrames + " | " + it.note);
        }
        Debug.Log("[P0G] 不画分类: 源码隐藏=" + hidden + " 运行时定图=" + runtime + " 索引越界=" + oob +
                  "（合计 " + (hidden + runtime + oob) + "，与 counts.skipped=" +
                  _layout.counts.skipped + " 应一致）");
        Debug.Log("[P0G] 分页变体(PAGEVARIANT)=" + pageVar +
                  " 项 —— 可画但只在所属分页显示；counts.draw=" + _layout.counts.draw +
                  " 含此 " + pageVar + " 项");
    }

    /// <summary>按键。由 P0BMapWalk 在 OnGUI 内调用。键位与 P0-F 逐字一致（两层共用）。</summary>
    public void HandleKeys()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return;

        // ① 面板开关：★改为遍历 P0BMapWalk.HOTKEY_PANELS/CODES（单点定义，与 P0-F 逐字同源）。
        //   替代原先写死的 `if (F1 || F2)` —— 那版只绑 2 个键，而文字层绑 7 个，
        //   ⇒ Belt/Chat/MiniMap/SkillBar 四个面板位图层永远打不开（art/text 脱同步）。
        for (int i = 0; i < P0BMapWalk.HOTKEY_CODES.Length; i++)
        {
            if (P0BMapWalk.HOTKEY_CODES[i] == KeyCode.None) continue;   // None 永不匹配，防 Future 误触发
            if (e.keyCode != P0BMapWalk.HOTKEY_CODES[i]) continue;
            string p = P0BMapWalk.HOTKEY_PANELS[i];
            if (P0BMapWalk.PanelOwnedByItemLayer(p)) continue;   // 该面板由 P0HItem 独占
            if (P0BMapWalk.PanelAlwaysOn(p)) continue;           // 常驻面板不参与开关
            bool cur;
            _panelOn.TryGetValue(p, out cur);
            _panelOn[p] = !cur;
            Debug.Log("[P0G] 面板位图层 " + p + " => " + (_panelOn[p] ? "显示" : "隐藏"));
            _verdictLogged = false;
            return;
        }

        // ①b F11 = 原版「角色面板第 3 页」（ClMain.pas L2637 `VK_F11: n_boState:=False; StatePage:=3; OpenMyStatus`）
        //     原版 F11 不是"技能栏"。这里把角色面板打开并落到末页，语义对齐
        //     （本工程角色面板只有 StatusPage/StatePage 两页，末页 = 原版第 3/5 页的位置）。
        if (e.keyCode == KeyCode.F11)
        {
            bool on;
            _panelOn.TryGetValue("CharacterDialog", out on);
            _panelOn["CharacterDialog"] = true;
            _charPage = CHAR_PAGES[CHAR_PAGES.Length - 1];
            Debug.Log("[P0G] F11 → CharacterDialog 显示，页=" + _charPage +
                      (on ? "（已开着，切末页）" : "（原版 F11 = 角色面板第 3 页）"));
            _verdictLogged = false;
            e.Use();
            return;
        }

        // ② 角色面板分页：1/2 与 P0-F 用同两个键，**且下标推导必须逐字相同**。
        //    P0-F 的公式（P0FText.HandleKeys L300-303）：
        //      idx = _pageOrder.IndexOf(cur)
        //      按 1: next = (idx < 0) ? 0 : (idx + 1) % Count
        //      按 2: next = (idx <= 0) ? Count - 1 : idx - 1
        //    ★ 必须逐字照抄，不许简化：
        //      · 现在两侧默认页都是 StatusPage，两侧 IndexOf 都返回 0，公式退化成对称的 2 页循环。
        //      · 但 (idx < 0) 与 (idx <= 0) 两个分支**保留不动**：一旦哪天把默认页改回不在页序里的
        //        CharacterPage（源码原状），缺了这两个分支就会立刻错位（简化式给 1，P0F 给 0）。
        //      · 另：_pageOrder 由 p0f_text_layout.json 里 page 字段**首次出现顺序**派生，
        //        CHAR_PAGES 是硬编码常量。两者顺序必须一致，否则 IndexOf 下标含义不同 → 图字不同页。
        //        实测 JSON 顺序 StatusPage -> StatePage，与 CHAR_PAGES 一致（P0F 启动日志「页序=[StatusPage,StatePage]」可验）。
        bool chrOn;
        if (e.keyCode == KeyCode.Alpha1 || e.keyCode == KeyCode.Alpha2)
        {
            if (!_panelOn.TryGetValue("CharacterDialog", out chrOn) || !chrOn) return;
            int idx = System.Array.IndexOf(CHAR_PAGES, _charPage);
            int next;
            if (e.keyCode == KeyCode.Alpha1) next = (idx < 0) ? 0 : (idx + 1) % CHAR_PAGES.Length;
            else next = (idx <= 0) ? CHAR_PAGES.Length - 1 : idx - 1;
            _charPage = CHAR_PAGES[next];
            Debug.Log("[P0G] 角色面板分页(位图层) => " + _charPage +
                      "（页序 " + (next + 1) + "/" + CHAR_PAGES.Length +
                      "，源 L608-L655 四页互斥；页序跟随 P0-F 以保图字同页）");
            _verdictLogged = false;
            return;
        }

        // ★ 故意**不**调用 e.Use()：P0-G 在 OnGUI 里先于 P0-F 执行，一旦 Use 就把事件吃掉，
        //   P0FText.HandleKeys 收不到同一个 KeyDown（实测回归 2026-10-17 16:50：
        //   [P0F] 开启面板=[] 永远不变，全部热键失效）。两层必须各自读同一次事件。
    }

    public void DrawAll()
    {
        if (!_loaded) LoadLayout();
        if (_layout == null || _layout.items == null) return;

        HandleKeys();

        DrawnCount = 0; MissingCount = 0; SizeMismatchCount = 0;
        SkippedCount = 0; OutOfBoundsDrawn = 0; PageSkippedCount = 0; OwnedCount = 0;

        foreach (var it in _layout.items)
        {
            if (it == null || it.logicalRect == null || it.logicalRect.Length < 4) continue;

            // ★ 该面板由 P0HItem 独占（背包）⇒ 位图层一项都不画。
            //   历史：这里曾画 prguse_r0003_336x270 当背包底图，但那张不是 1.76 的背包面板
            //   （原版是 Prguse2[180] 352x295），且没有 8x5 格线、不画任何物品。
            //   ★ 2026-10-27：JSON 里这 4 条已由 DRAW 改为 kind=OWNED（见 p0g_art_layout.json），
            //     它们是「已归属他层」而不是「缺图」，故单独计 OwnedCount，
            //     不混进 SkippedCount（那个数是给"登记不画"用的，混进去会让下游误判缺失量）。
            if (P0BMapWalk.PanelOwnedByItemLayer(it.panel) || it.kind == "OWNED")
            {
                OwnedCount++;
                continue;
            }

            // 面板关闭 ⇒ 该面板下所有项都不画（源码 Visible=false 的语义）
            bool on;
            if (!_panelOn.TryGetValue(it.panel, out on) || !on)
            {
                if (it.kind == "DRAW" || it.kind == "PAGEVARIANT") PageSkippedCount++;
                else SkippedCount++;
                continue;
            }

            // PAGEVARIANT：只有当前分页才画。
            // ★ 用 JSON 里**显式写的 page 字段**，不靠控件名后缀去猜 —— 名字是给人看的，
            //   page 是生成期从源码 Show*Page 抄下来的契约，两者不能混。
            if (it.kind == "PAGEVARIANT")
            {
                if (it.page != _charPage)
                {
                    PageSkippedCount++;
                    continue;
                }
            }
            else if (it.kind != "DRAW")
            {
                // HIDDEN / RUNTIME / OOB：登记不画，必须显式计入，防止被误读成漏画
                SkippedCount++;
                continue;
            }

            if (string.IsNullOrEmpty(it.tex))
            {
                MissingCount++;
                if (!_missingLogged.Contains(it.name))
                {
                    _missingLogged.Add(it.name);
                    Debug.LogError("[P0G] ★ 位图层 kind=" + it.kind + " 但没有 tex: " + it.name);
                }
                continue;
            }

            var tex = LoadTex(it.tex);
            if (tex == null)
            {
                MissingCount++;
                if (!_missingLogged.Contains(it.name))
                {
                    _missingLogged.Add(it.name);
                    Debug.LogError("[P0G] ★ 位图层缺纹理: " + it.name + " tex=" + it.tex +
                                   "（该元素不会出现在屏幕上）");
                }
                continue;
            }

            float lw = it.logicalRect[2];
            float lh = it.logicalRect[3];

            // ★ 移动端纵向补偿（方案 A，见 P0BMapWalk.TOP_COMP_PANELS 注释）。
            //   按**整个面板**平移：同一 panel 的所有元素拿到同一个 dy，所以面板内相对布局 1:1 不变。
            //   白名单外的面板（BeltDialog 452 / MainDialog 369）拿 0，继续贴底部 HUD 美术。
            int dy = P0BMapWalk.PanelTopShift(it.panel);

            // 越界标注用**平移后**的实际落点，否则补偿把元素移出画布也报 0 越界 = 判据失真
            if (lw > 0f && lh > 0f &&
                (it.logicalRect[0] < 0f || it.logicalRect[1] + dy < 0f ||
                 it.logicalRect[0] + lw > _layout.logicalW ||
                 it.logicalRect[1] + dy + lh > _layout.logicalH))
                OutOfBoundsDrawn++;

            Rect r = P0BMapWalk.GroupRectForLogic(it.logicalRect[0], it.logicalRect[1] + dy, lw, lh);

            // 像素画一律 Point 过滤：整数倍放大必须 NEAREST，否则边缘发糊（同 P0-D 口径）
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
            DrawnCount++;

            // ★ 尺寸判据：传入矩形宽高 == 逻辑宽高 × scale，且源纹理与记录一致
            int expW = Mathf.RoundToInt(lw * P0BMapWalk.ScaleI);
            int expH = Mathf.RoundToInt(lh * P0BMapWalk.ScaleI);
            int gotW = Mathf.RoundToInt(r.width);
            int gotH = Mathf.RoundToInt(r.height);
            bool texOk = it.pixelSize != null && it.pixelSize.Length >= 2 &&
                         tex.width == it.pixelSize[0] && tex.height == it.pixelSize[1];
            if (gotW != expW || gotH != expH || !texOk)
            {
                SizeMismatchCount++;
                if (!_missingLogged.Contains("尺寸:" + it.name))
                {
                    _missingLogged.Add("尺寸:" + it.name);
                    Debug.LogError("[P0G] ★ 尺寸判据不通过: " + it.name +
                                   " 期望 " + expW + "x" + expH + " 实传 " + gotW + "x" + gotH +
                                   " | 纹理 " + tex.width + "x" + tex.height +
                                   " 记录 " + (it.pixelSize != null && it.pixelSize.Length >= 2
                                       ? it.pixelSize[0] + "x" + it.pixelSize[1] : "?"));
                }
            }
        }

        // ★ 判定行输出条件（**历史 bug 的修复点，别改回去**）：
        //   旧写法 `Time.frameCount < 3 || _verdictLogged == false || frameCount % 300 == 0`
        //   配合 `_verdictLogged` 只在按键时被置 false ⇒ 实际只在「前 3 帧」与「每 300 帧」出，
        //   而前 3 帧**全部面板默认关闭**、DrawnCount 恒 0 ⇒ 判定行长期钉在 `已画=0`，
        //   取证时被误读成「素材丢了」，真因是**这行数字根本没在面板打开后重算过**（假红）。
        //   ⇒ 判据改为「本次绘制结果与上次输出时不同就出」：Drawn/Missing/SizeMismatch
        //     任一变化即重算，保证「看到 FAIL 时，它描述的就是当前这一帧」。
        //   `_verdictLogged` 保留语义 = 上一次已输出过，仅用于首帧必出。
        if (Time.frameCount < 3 || !_verdictLogged ||
            DrawnCount != _lastLoggedDrawn ||
            MissingCount != _lastLoggedMissing ||
            SizeMismatchCount != _lastLoggedMismatch ||
            Time.frameCount % 300 == 0)
            LogEvidence();
    }

    Texture2D LoadTex(string key)
    {
        Texture2D t;
        if (_texCache.TryGetValue(key, out t)) return t;
        t = Resources.Load<Texture2D>(key);
        _texCache[key] = t;
        return t;
    }

    void LogEvidence()
    {
        if (_layout == null) return;
        bool allOk = SizeMismatchCount == 0 && MissingCount == 0 && !_dupLogged;

        var on = new List<string>();
        foreach (var kv in _panelOn)
            if (kv.Value) on.Add(kv.Key);
        string state = on.Count == 0
            ? "VISIBLE=none"
            : "VISIBLE=[" + string.Join(",", on.ToArray()) + "]";

        // 分页只在角色面板开启时才有意义，否则打 none 免得误读
        bool chrOn;
        string page = (_panelOn.TryGetValue("CharacterDialog", out chrOn) && chrOn) ? _charPage : "-";

        Debug.Log("[P0G] ART_VERDICT = " + (allOk ? "PASS" : "FAIL") +
                  " " + state +
                  " page=" + page +
                  " items=" + _layout.items.Length +
                  " 可画=" + _layout.counts.draw +
                  " 已画=" + DrawnCount +
                  " 缺=" + MissingCount +
                  " 尺寸不符=" + SizeMismatchCount +
                  " 登记不画=" + SkippedCount +
                  " 归属他层=" + OwnedCount +
                  " 面板关/非当前页=" + PageSkippedCount +
                  " 越界=" + OutOfBoundsDrawn +
                  " 纵向补偿=" + P0BMapWalk.PanelTopComp + "逻辑行 补偿面板=[" + P0BMapWalk.PanelTopCompNames + "]" +
                  " scale=" + P0BMapWalk.ScaleI);
        _verdictLogged = true;
        _lastLoggedDrawn = DrawnCount;
        _lastLoggedMissing = MissingCount;
        _lastLoggedMismatch = SizeMismatchCount;
    }
}
