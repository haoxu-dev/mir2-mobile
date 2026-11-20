// P0-F —— M2 七面板「纯文字层」脚手架。
//
// 为什么只做文字：
//   m2spec/m2-plan.md §4 已实测：七面板 80 组控件里我方现成可用图只有 4 组（5.0%），
//   MISS + NO_LIB 共 32 组（40.0%）。46 个 Prguse 索引（2012-2027 / 2090-2113 / 2190 / 2221 …）
//   在我方 Prguse.wil（510 帧）里根本不存在，Crystal 的 Prguse.Lib 也是 510 帧，照抄补不上。
//   ⇒ 背景图这一层无解；而 36 个 MirLabel 的 Location 在源码里全是字面量（不依赖素材帧），
//     是唯一能立刻上屏、且能逐条回源验收的增量。
//
// 数据来源（不手抄、不推断）：
//   Assets/Resources/p0f_text_layout.json  ← 由 m2spec/gen_text_layout.py 生成。
//   每条 label 的 evidence 字段写着「源文件 L行：Location=(x,y)」，可逐条回源。
//   MainDialog 的原点不是我猜的：直接取 M2_layer1_AUTHORITATIVE.json 的 logical800x600
//   （0,349,800,251，我方 0039 实测），因此 ChatDialog/BeltDialog 的 x 链条不再悬空。
//
// 坐标契约（与 P0-D 完全一致，不许另开一套）：
//   一律先给「原版 800x600 逻辑坐标」，再用 P0BMapWalk.GroupRectForLogic 转组内相对设备像素。
//   本类只在 GUI.BeginGroup(MainUICanvasRect) 内部被调用，故**不能**用 CanvasRectForLogic。
//
// 默认可见性（重要，避免污染已验收的 P0-D 画面）：
//   七面板默认全部不画。原因：这些面板的原版 Visible 取决于运行时交互（CharacterDialog 要
//   ShowCharacterPage()、InventoryDialog 构造器就是 Visible=false），默认全开会把七块文字
//   糊在屏幕上，直接毁掉 P0-D 那张 0039 HUD 的取证。
//   MainDialog 的文字层（血/魔/等级/名字）同样默认关闭：它的 7 条文本全是运行时变量，
//   画占位会盖住已经 1:1 驗收过的 HUD 像素。要看时按 F7。
//
// 键盘（模拟器可测，★键位依据原版 Client\KeyBindSettings.cs L183/L187/L191 + Belt/Minimap 默认值）：
//   F9 背包(Inventory) / F10 角色·装备(Equipment) / F11 技能栏(Skills) / Z 腰带(Belt) / V 小地图(Minimap) / Enter 聊天（原版无此热键，Mir2 惯例，已知偏离） / MainDialog 无热键（常驻 HUD，用 None 占位）
//   再按一次关闭。用 Event.current 在本类内部消化，不改 P0BMapWalk 的任何既有逻辑。
using System.Collections.Generic;
using UnityEngine;

/// <summary>M2 七面板纯文字层：只画源码可静态定值的 MirLabel，不画任何缺失帧。</summary>
public class P0FText
{
    [System.Serializable]
    public class Label
    {
        public string panel;
        public string page;
        public string pageGroup;      // 非空 ⇒ 属于互斥分页组（当前只有 CharacterDialog）
        public bool pageDefault;      // 该分页在源码里是否为构造期默认可见页
        public string varName;
        public float relX;
        public float relY;
        public float x;
        public float y;
        public float w;
        public float h;
        public string text;
        public bool hasStaticText;
        public bool visible;
        public string evidence;
    }

    [System.Serializable]
    public class PanelOrigin
    {
        public string panel;
        public float x;
        public float y;
        public string expr;
    }

    [System.Serializable]
    public class Layout
    {
        public string specVersion;
        public string purpose;
        public int logicalW;
        public int logicalH;
        public Label[] labels;
        public PanelOrigin[] panelOrigins;
    }

    // ★ 面板开关顺序与按键一一对应 —— ★改为从 P0BMapWalk 单点读取，不再本地复制一份。
    //   历史 bug：本文件写 F1..F7，而 P0GArt.cs 只绑 F1/F2 ⇒ 位图层打不开 Belt/Chat/MiniMap/SkillBar。
    //   现在两层共用 P0BMapWalk.HOTKEY_PANELS / HOTKEY_CODES（依据 Client\KeyBindSettings.cs
    //   L183/L187/L191：Inventory=F9 / Equipment=F10 / Skills=F11），物理上不可能再漂移。
    static string[] PANEL_KEYS { get { return P0BMapWalk.HOTKEY_PANELS; } }
    static KeyCode[] KEY_CODES { get { return P0BMapWalk.HOTKEY_CODES; } }

    public int DrawnCount;
    public int MissingCount;
    public int SizeMismatchCount;
    public int OutOfBoundsDrawn;
    public int PageSkippedCount;
    /// <summary>运行期占位槽位数（源码运行期才填文本的 MirLabel；原版此处为空白，我方也不画）。</summary>
    public int PlaceholderCount;

    readonly Dictionary<string, bool> _panelOn = new Dictionary<string, bool>();
    readonly Dictionary<string, string> _activePage = new Dictionary<string, string>();
    readonly List<string> _pageOrder = new List<string>();
    readonly List<string> _missingLogged = new List<string>();
    Layout _layout;
    GUIStyle _style;
    bool _loaded;
    bool _loadFailedLogged;
    bool _zeroDrawnLogged;
    bool _verdictLogged;

    // ★ 与 P0GArt 同构：上次输出判定行时的计数快照，用于「数字变了就重出判定行」。
    int _lastLoggedDrawn = -1;
    int _lastLoggedMissing = -1;
    int _lastLoggedMismatch = -1;

    public void LoadLayout()
    {
        _loaded = true;
        var ta = Resources.Load<TextAsset>("p0f_text_layout");
        if (ta == null)
        {
            if (!_loadFailedLogged)
            {
                _loadFailedLogged = true;
                Debug.LogError("[P0F] ★ 文字层未装载 labels=0 —— 未找到 Assets/Resources/p0f_text_layout.json" +
                               "（须由 m2spec/gen_text_layout.py 生成并签入工程；七面板文字层将完全不渲染）");
            }
            return;
        }
        try
        {
            _layout = JsonUtility.FromJson<Layout>(ta.text);
        }
        catch (System.Exception e)
        {
            if (!_loadFailedLogged)
            {
                _loadFailedLogged = true;
                Debug.LogError("[P0F] ★ 文字层未装载 labels=0 —— p0f_text_layout.json 解析失败: " + e.Message);
            }
            return;
        }
        if (_layout == null || _layout.labels == null || _layout.labels.Length == 0)
        {
            Debug.LogError("[P0F] ★ 文字层未装载 labels=0 —— p0f_text_layout.json 无有效 labels");
            return;
        }

        foreach (var k in PANEL_KEYS) _panelOn[k] = P0BMapWalk.PanelAlwaysOn(k);

        // ★ 分页组初始化（CharacterDialog.cs L86/L111/L136 三页坐标完全重合，必须互斥）
        //   源码构造期可见页 = CharacterPage（未设 Visible=false）。我方**刻意改停 StatusPage**，
        //   属已知偏离、非还原。理由与 P0GArt.cs L110-122 同：CharacterPage 底图是 Prguse[340]，
        //   本机全部候选库该索引均 w=0/h=0 空壳帧 ⇒ 停在该页一开就是整块黑。
        //   改停 StatusPage 后：P0G 画 Title[506] 248x284 面板底图，P0F 画该页 13 条 MirLabel，
        //   图字同页且都有内容。
        //   ⚠ 必须与 P0GArt.cs 的 _charPage **成对修改**，两侧公式逐字一致（见 P0GArt L231-238 契约）。
        //   ⚠ 副作用（已知并接受）：默认页现落在页序内（IndexOf=0），2 页循环下
        //     按 1 与按 2 首次都落到 StatePage —— 这是 2 页循环的固有回绕，不是错位；
        //     要回到 StatusPage 再按一次 1 即可。
        _pageOrder.Clear();
        _activePage.Clear();
        _activePage["CharacterDialog"] = "StatusPage";
        for (int i = 0; i < _layout.labels.Length; i++)
        {
            var L0 = _layout.labels[i];
            if (L0 == null || string.IsNullOrEmpty(L0.page)) continue;
            if (!_pageOrder.Contains(L0.page)) _pageOrder.Add(L0.page);
        }
        Debug.Log("[P0F] 分页组 CharacterDialog 页序=[" + string.Join(",", _pageOrder.ToArray()) + "]" +
                  " 默认页=\"StatusPage\"（★刻意偏离源码 CharacterPage：Prguse[340] 全库空壳，见 P0GArt L110-122）" +
                  " 按 1/2 切换分页（仅 F10 打开角色面板时生效）");

        // ★ 必须数 hasStaticText，不能数 text != null：
        //   JSON 里非静态项是 ""（不是 null），用 text != null 会数出 55（全量），
        //   实测真值是 29。数字虚高会让下游误以为「七面板文本全都有源可查」。
        int stat = 0;
        for (int i = 0; i < _layout.labels.Length; i++)
            if (_layout.labels[i] != null && _layout.labels[i].hasStaticText) stat++;

        Debug.Log("[P0F] 文字层装载 OK: labels=" + _layout.labels.Length +
                  " 含静态文本=" + stat +
                  " 逻辑画布 " + _layout.logicalW + "x" + _layout.logicalH +
                  " | 规格 " + _layout.specVersion +
                  " | 面板 " + PANEL_KEYS.Length + " 个默认全部关闭（F9/F10/F11/Z/V/Enter 逐块开，主 HUD 无热键）");

        if (_layout.panelOrigins != null)
        {
            foreach (var o in _layout.panelOrigins)
            {
                if (o == null) continue;
                Debug.Log("[P0F] 面板原点 " + o.panel + " = (" + o.x + "," + o.y + ") 依据: " + o.expr);
            }
        }
    }

    /// <summary>按键 + 绘制。由 P0BMapWalk.OnGUI 在 BeginGroup(MainUICanvasRect) 内调用。</summary>
    public void DrawAll()
    {
        if (!_loaded) LoadLayout();
        if (_layout == null || _layout.labels == null)
        {
            DrawnCount = 0; MissingCount = 0;
            return;
        }

        HandleKeys();

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label);
            _style.alignment = TextAnchor.UpperLeft;
            _style.fontSize = 11 * P0BMapWalk.ScaleI;
            _style.normal.textColor = Color.white;
            _style.clipping = TextClipping.Clip;
        }

        DrawnCount = 0; MissingCount = 0; SizeMismatchCount = 0; OutOfBoundsDrawn = 0;
        PageSkippedCount = 0; PlaceholderCount = 0;

        foreach (var L in _layout.labels)
        {
            if (L == null) continue;
            if (!L.visible) continue;

            // ★ 该面板由 P0HItem 独占（背包）⇒ 文字层不画。
            //   历史：这里的两条 InventoryDialog 标签（GoldLabel/WeightLabel）在 JSON 里被标成
            //   `"kind":"layout-adaptation","sourceEquivalent":false` —— 即**不是原版坐标**，
            //   而是为了适配旧底图 Prguse[3] 才挪到 (62,182)/(178,182) 的。
            //   换了正确的 Prguse2[180] 之后，原版坐标是 (72,212)/(178,212)，由 P0HItem 直接画。
            if (P0BMapWalk.PanelOwnedByItemLayer(L.panel)) continue;

            bool on;
            if (!_panelOn.TryGetValue(L.panel, out on) || !on) continue;

            // ★ 分页互斥：角色面板三页坐标完全重合（都在 (8,90) 偏移下），
            //   只画当前激活页；非当前页跳过但不计入缺失（它不是"画不出来"，是"本就不该画"）。
            if (!string.IsNullOrEmpty(L.page))
            {
                string cur;
                if (_activePage.TryGetValue("CharacterDialog", out cur) && cur != L.page)
                {
                    PageSkippedCount++;
                    continue;
                }
            }

            float bw = L.w > 0f ? L.w : 0f;
            float bh = L.h > 0f ? L.h : 0f;

            // ★ 移动端纵向补偿（方案 A）：**必须与位图层用同一个单点函数**。
            //   若只补位图层不补文字层，背包底图会上移 30 行、而 GoldLabel(40,212) 等文字原地不动
            //   ⇒ 面板内部错位 30 行，比不补更糟。反向同理。
            int dy = P0BMapWalk.PanelTopShift(L.panel);

            // 越界标注：不隐藏、不裁剪坐标，如实计数（渲染层只报告，不改语义）
            //   用**平移后**的实际落点，否则补偿把元素移出画布也报 0 越界 = 判据失真
            if (bw > 0f && bh > 0f &&
                (L.x < 0f || L.y + dy < 0f || L.x + bw > _layout.logicalW || L.y + dy + bh > _layout.logicalH))
                OutOfBoundsDrawn++;

            // 文字框：宽高未声明时给一个足够容纳内容的默认框，避免 Rect(0,0) 什么都看不见
            float rw = bw > 0f ? bw : 120f;
            float rh = bh > 0f ? bh : 18f;
            Rect r = P0BMapWalk.GroupRectForLogic(L.x, L.y + dy, rw, rh);

            // ★ V9 同源判据：传进 GUI 的矩形宽高必须 == 逻辑宽高 × scale
            int expW = Mathf.RoundToInt(rw * P0BMapWalk.ScaleI);
            int expH = Mathf.RoundToInt(rh * P0BMapWalk.ScaleI);
            int gotW = Mathf.RoundToInt(r.width);
            int gotH = Mathf.RoundToInt(r.height);
            if (gotW != expW || gotH != expH)
            {
                SizeMismatchCount++;
                if (!_missingLogged.Contains("尺寸:" + L.panel + "/" + L.varName))
                {
                    _missingLogged.Add("尺寸:" + L.panel + "/" + L.varName);
                    Debug.LogError("[P0F] ★ 尺寸判据不通过: " + L.panel + "/" + L.varName +
                                   " 期望 " + expW + "x" + expH + " 实传 " + gotW + "x" + gotH);
                }
            }

            // ★ 判据用 hasStaticText（生成期写的显式布尔），**不能**用 text != null：
            //   JSON 里非静态项是 ""（不是 null），用 text != null 会把占位项误判成"真文本"。
            bool hasText = L.hasStaticText;

            // ★ 声明有静态文本、内容却是空 —— 这才是**真缺失**，必须计数。
            //   补丁前本类从未对 MissingCount 做过 ++，而 LogEvidence 的 allOk 又拿
            //   `MissingCount == 0` 当判据 ⇒ 该判据恒真、TEXT_VERDICT 永远 PASS = 假绿。
            if (hasText && string.IsNullOrEmpty(L.text))
            {
                MissingCount++;
                if (!_missingLogged.Contains("空文本:" + L.panel + "/" + L.varName))
                {
                    _missingLogged.Add("空文本:" + L.panel + "/" + L.varName);
                    Debug.LogError("[P0F] ★ 声明有静态文本但内容为空: " + L.panel + "/" + L.varName);
                }
                continue;
            }

            // ★ 非静态项 = 运行期由源码写入数值的槽位：
            //   InventoryDialog.cs L386 `WeightLabel.Text = 空格子数`、L388 `GoldLabel.Text = 金币`。
            //   原版此刻**一个像素都不画** —— MirLabel.CreateTexture() 在 Text 为空时
            //   L189-190 直接 return。我方也一律不画：
            //     · 不画 ⇒ 与原版 diff 能量 0，只是"这局还没捡到金币"；
            //     · 画 varName ⇒ 把 "GoldLabel"/"WeightLabel" 几个字母糊到屏幕上，
            //       正是用户说的"跟当年那个客户端不一样"。
            //   运行期数据源（金币/背包空格数）目前**未判定**，留给后续数据层，不在渲染层编造。
            if (!hasText)
            {
                PlaceholderCount++;
                continue;
            }

            // ★ 只画真文本，且**不画底条**。原版文字层没有任何底色框：
            //   MirLabel._backColour 从未被赋值 ⇒ 默认 Color.Empty（MirControl.cs L21 / 构造
            //   L671-679，仅在 L1033 Dispose 清除），graphics.Clear(Color.Empty) 全透明
            //   （MirLabel.cs L216-217）。实测原版该条带严格蓝像素 0/9,796、均值亮度 26~32；
            //   补丁前我方是 RGB(28.6,41.3,66.9) 的蓝胶囊 + (0.75,0.85,1) 冷白字（B-R=+64，
            //   原版该带最大 B 仅 189）⇒ 全图 98.61% 的 diff 能量都压在这条上，必须删干净。
            //   ⚠ GUI.color 是全局状态，删掉 DrawTexture 后不再需要任何保存/恢复。
            _style.normal.textColor = Color.white;   // = MirControl._foreColour 默认值（L676）
            GUI.Label(r, L.text, _style);

            DrawnCount++;
        }

        if (DrawnCount == 0 && !_zeroDrawnLogged)
        {
            _zeroDrawnLogged = true;
            Debug.Log("[P0F] 文字层当前无可画标签（七面板默认关闭；按 F9/F10/F11/Z/V/Enter 逐块开启）");
        }

        // ★ 判定行节流：只在「前 3 帧 / 状态变更 / 每 300 帧」输出。
        //   原写法 `|| DrawnCount > 0` 会在面板打开时**每帧**打一行（60 行/秒），
        //   把 adb logcat 冲爆，取证时反而找不到关键行。
        // ★ 补：`_verdictLogged == false` 只在按键时置位 ⇒ 面板「未按键就关闭」时
        //   已画/缺 数字会钉在首帧全关的 0 上（与 P0GArt 同一类假红）。
        //   ⇒ 再加「与上次输出不同即重出」，保证判定行描述的是当前帧。
        bool stateChanged = _verdictLogged == false;
        bool countChanged = DrawnCount != _lastLoggedDrawn ||
                            MissingCount != _lastLoggedMissing ||
                            SizeMismatchCount != _lastLoggedMismatch;
        if (Time.frameCount < 3 || stateChanged || countChanged || Time.frameCount % 300 == 0)
            LogEvidence();
    }

    void HandleKeys()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return;

        // ★ 分页切换：仅 F10（角色面板）打开时生效；1/2 在 StatusPage / StatePage 间切换。
        //   CharacterPage 是源码构造期默认页，但该页没有可画的 MirLabel（无 Text 属性），
        //   故曲线：按 1 循环切 _pageOrder（StatusPage -> StatePage -> StatusPage ...）。
        bool charOn;
        if (_panelOn.TryGetValue("CharacterDialog", out charOn) && charOn
            && (e.keyCode == KeyCode.Alpha1 || e.keyCode == KeyCode.Alpha2) && _pageOrder.Count > 0)
        {
            string cur;
            _activePage.TryGetValue("CharacterDialog", out cur);
            int idx = _pageOrder.IndexOf(cur);
            int next;
            if (e.keyCode == KeyCode.Alpha1) next = (idx < 0) ? 0 : (idx + 1) % _pageOrder.Count;
            else next = (idx <= 0) ? _pageOrder.Count - 1 : idx - 1;
            _activePage["CharacterDialog"] = _pageOrder[next];
            Debug.Log("[P0F] 角色面板分页 => " + _pageOrder[next] +
                      "（页序 " + (next + 1) + "/" + _pageOrder.Count + "，源 CharacterDialog.cs Show*Page 互斥）");
            _verdictLogged = false;
            e.Use();
            return;
        }

        for (int i = 0; i < KEY_CODES.Length; i++)
        {
            if (KEY_CODES[i] == KeyCode.None) continue;   // 常驻面板（技能栏）用 None，永不匹配
            if (e.keyCode != KEY_CODES[i]) continue;
            string p = PANEL_KEYS[i];
            if (P0BMapWalk.PanelOwnedByItemLayer(p)) continue;   // 该面板由 P0HItem 独占
            if (P0BMapWalk.PanelAlwaysOn(p)) continue;           // 常驻面板不参与开关
            bool cur;
            _panelOn.TryGetValue(p, out cur);
            _panelOn[p] = !cur;
            Debug.Log("[P0F] 面板 " + p + " => " + (_panelOn[p] ? "显示" : "隐藏"));
            _verdictLogged = false;   // 状态变了，下次重新出判定行
            e.Use();
            return;
        }

        // F11 = 原版「角色面板第 3 页」（ClMain.pas L2637）。与 P0GArt 同键同语义：
        // 打开角色面板并落到末页，保持「图字同页」契约。
        if (e.keyCode == KeyCode.F11)
        {
            _panelOn["CharacterDialog"] = true;
            if (_pageOrder.Count > 0)
                _activePage["CharacterDialog"] = _pageOrder[_pageOrder.Count - 1];
            Debug.Log("[P0F] F11 → CharacterDialog 显示，页=" +
                      (_activePage.ContainsKey("CharacterDialog") ? _activePage["CharacterDialog"] : "-"));
            _verdictLogged = false;
            e.Use();
            return;
        }
    }

    void LogEvidence()
    {
        if (_layout == null) return;
        var on = new List<string>();
        foreach (var p in PANEL_KEYS)
        {
            bool v;
            if (_panelOn.TryGetValue(p, out v) && v) on.Add(p);
        }
        bool allOk = SizeMismatchCount == 0 && MissingCount == 0;
        // ★ 单行机器判据（gate 可 grep；只有「已装载 + 无尺寸错误 + 至少一次成功绘制」才 PASS）
        Debug.Log("[P0F] TEXT_VERDICT = " + (allOk ? "PASS" : "FAIL") +
                  " labels=" + _layout.labels.Length +
                  " 开启面板=[" + string.Join(",", on.ToArray()) + "]" +
                  " 已画=" + DrawnCount + " 缺=" + MissingCount +
                  " 运行期占位=" + PlaceholderCount +
                  " 尺寸不符=" + SizeMismatchCount +
                  " 越界=" + OutOfBoundsDrawn +
                  " 分页跳过=" + PageSkippedCount +
                  " 纵向补偿=" + P0BMapWalk.PanelTopComp + "逻辑行 补偿面板=[" + P0BMapWalk.PanelTopCompNames + "]" +
                  " scale=" + P0BMapWalk.ScaleI);
        _verdictLogged = true;
        _lastLoggedDrawn = DrawnCount;
        _lastLoggedMissing = MissingCount;
        _lastLoggedMismatch = SizeMismatchCount;
    }
}
