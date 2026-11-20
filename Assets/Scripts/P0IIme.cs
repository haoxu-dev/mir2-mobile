// P0-I —— 原生输入法（IME）文字输入：屏幕底部的聊天输入面板。
//
// ============================ 为什么单开这一层 ============================
// 已有的 P0-D / P0-F / P0-G 全是「画」：位图与文字只上屏，一个字符都收不进来。
// 用户点名的两大缺口之一就是**输入**（原生输入法中文输入）。本层补的就是这个口子：
// 一个 IMGUI 原生控件 GUI.TextField，让系统输入法（含中文）能落进游戏。
//
// 本层不做的事（避免与既有层抢像素 / 抢事件）：
//   · 不画任何 .Lib 帧、不动底部 HUD（那是 P0-D 的 0039）、不画七面板文字（P0-F）；
//   · 默认 Visible=false，不打开时一个像素都不画、一个事件都不吃；
//   · 不修改 P0BMapWalk.cs，接线（字段 + 一行 DrawAll）由使用方自己加。
//
// ============================ 两条输入路径（互为兜底） ============================
// ① GUI.TextField —— 桌面 / 编辑器 / 部分机型：IMGUI 的文本编辑是**引擎原生实现**，
//    字符来自 Event.character，IME 合成完成后整字上屏（不是被拆开的按键序列）。
//    Android 上 legacy IMGUI 是否自动弹软键盘依赖机型/输入法，**不能只靠它**。
// ② TouchScreenKeyboard —— Android 显式唤起系统输入法：
//      TouchScreenKeyboard.Open("", Default, false, false, false, false, "", 200)
//    它返回对象的 .text 是 IME 合成后的**完整 Unicode 串**，所以本类一律「整串同步」，
//    **绝不**用 Input.inputString 逐字符拼接 —— 那样中文会被拆成拼音/半个码点。
//    收起：.active = false（关面板、或用户点了系统键盘的完成键）。
//
// 权威归属（关键，决定中文会不会被写坏）：
//   · 键盘 active  ⇒ 以键盘 .text 整串为权威（移动端 IME 路径）；
//   · 键盘不 active ⇒ 以 GUI.TextField 的返回值为权威（桌面物理键盘 / 输入法路径）。
//   两者不会同时写：同一帧只认一条路径，避免「TextField 追加 + 键盘覆盖」互相打架。
//
// 为什么 Open 的第一个参数写 ""、之后又把草稿塞回 _kb.text：
//   Open 的初始文本与键盘实际缓冲必须一致，否则第一次同步会把已有草稿清掉。
//   所以打开后读回 .text 作为基线；平台拒绝写文本时，以读回值为准（输入框同步回读回值），
//   保证「键盘串 == 输入框串」这条不变式永远成立。
//
// ============================ 坐标契约（与 P0-D/F/G 不同，必须看清） ============================
// P0BMapWalk.OnGUI() 开头就设了
//     GUI.matrix = 缩放(Screen.width/2616, Screen.height/1140)
// 即绘制空间是「2616×1140 虚拟设备像素」。而本面板的要求是**按真实 Screen 尺寸自适应**，
// 所以 DrawAll() 内部：
//     保存 GUI.matrix → 置为 Matrix4x4.identity → 用真实 Screen 像素布局 → finally 恢复。
// ⇒ 本类**必须**在 GUI.EndGroup() 之后被调用：
//   · 在 MainUICanvasRect(508,-60,1600,1200) 的 BeginGroup 里调用会被裁到 1600 宽，
//     横向铺不满（这是放置位置的硬约束，不是风格问题）；
//   · 面板贴屏幕底部，纵向与左右两侧的虚拟键按钮不重叠（见交付说明的插入位置）。
// 恢复矩阵是必须的：即使调用点在 OnGUI 最末尾，也不能把全局 GUI.matrix 留成单位矩阵，
// 否则同帧后续/下一帧的 2616 口径绘制会整体缩小。
//
// ============================ 尺寸自适应 ============================
// 基准取项目实测外屏 2616×1140（P0BMapWalk.SCREEN_W/H），但**不写死**：
//     s = clamp(min(Screen.width/2616, Screen.height/1140), 0.45, 2.0)
// 面板宽 = Screen.width * 0.94（横向铺满大部分），高按内容算并封顶 Screen.height*0.5，
// 历史行数由可用高度反推（1..12 行），字号/内边距/按钮宽一律乘 s。
// ⇒ 2616×1140 上 s=1.00；1920×1080 上 s≈0.73；800×600 上 s=0.45（下限，保证还能看清）。
//
// ============================ 自检（真机截图用） ============================
// 面板上有一行小字诊断（前缀 [P0I]），形如
//     [P0I] IME: tk=1 active=1 textLen=4 kbLen=4 focus=on res=2616x1140 s=1.00
// 逐项含义：
//   tk      = TouchScreenKeyboard.isSupported（机型是否支持系统键盘）
//   active  = 当前 TouchScreenKeyboard 对象是否 active（键盘有没有真起来）
//   textLen = 本层输入串长度（TextField/键盘写入后的值）
//   kbLen   = 键盘 .text 长度（-1 = 无键盘对象）；与 textLen 长期不等 ⇒ 同步断了
//   focus   = IMGUI 焦点是否在输入框上（GUI.GetNameOfFocusedControl）
// 打开/发送/收起/异常都会打一行 [P0I] 日志，logcat 可直接 grep。
//
// ============================ 编码与纪律 ============================
// · 本文件 UTF-8 **无 BOM**、**LF** 换行，与 P0FText.cs 一致（含中文注释，Windows 下勿转 CRLF）。
// · 所有日志前缀 [P0I]。
// · 只用公开 API：GUI.TextField / GUI.Button / GUI.FocusControl / GUI.SetNextControlName /
//   GUI.GetNameOfFocusedControl / TouchScreenKeyboard / Input.GetKeyDown(KeyCode.Return)。
//   不引用任何本项目里不存在的类；不碰任何已有文件。
// · DrawAll() 整个包在 try/catch 里：OnGUI 抛异常会把整帧 UI 打断（历史上 GUISkin 被剥
//   就出过这种事故），所以本层任何异常都只记一行 Error，绝不外抛。
using System.Collections.Generic;
using UnityEngine;

/// <summary>P0-I：原生输入法（IME）聊天输入面板。普通类，由 P0BMapWalk.OnGUI() 惰性创建并调用。</summary>
public class P0IIme
{
    // ================= 常量 =================
    /// <summary>IMGUI 控件名（GUI.SetNextControlName / FocusControl 的键，全局唯一）。</summary>
    public const string FIELD_CONTROL = "P0I_InputField";

    /// <summary>输入上限。必须与 TouchScreenKeyboard.Open 的第 8 参一致，否则键盘侧与输入框截断点不同。</summary>
    const int MaxInputChars = 200;

    /// <summary>历史缓冲上限（只是内存上限；屏幕上只画最近若干行）。</summary>
    const int MaxHistory = 200;

    /// <summary>历史区最多画几行（再多就挤压输入行，不划算）。</summary>
    const int MaxHistoryRows = 12;

    /// <summary>自适应基准：项目实测外屏 2616×1140（P0BMapWalk.SCREEN_W/SCREEN_H）。</summary>
    const float BASE_W = 2616f;
    const float BASE_H = 1140f;

    // ================= 可配置项（给接线方留的旋钮） =================
    /// <summary>开关热键。设为 KeyCode.None 可禁用（改由外部调用 Toggle()）。默认 F12，与 F9/F10/F11/Z/V/Enter 不冲突。</summary>
    public KeyCode ToggleKey = KeyCode.F12;

    /// <summary>点「发送」/按 Enter 后的回调（参数 = 已 Trim 的文本）。为空则只记日志，不抛。</summary>
    public System.Action<string> OnSend;

    // ================= 状态 =================
    bool _visible;
    string _input = string.Empty;
    readonly List<string> _history = new List<string>();

    // 软键盘（Android 才真正 Open；其余平台恒为 null，仅作兜底引用）
    TouchScreenKeyboard _kb;
    string _lastKbText = string.Empty;
    // 上次算出的键盘遮挡高度（px）；用于「只在变化时」打日志，避免每帧刷屏
    float _lastKbOccluded = -1f;
    string _lastKbSig = string.Empty;
    // Unity 自带输入浮层（EditText 窗口）是否已隐藏：1=on / 0=off / -1=尚未尝试。
    // 这是「面板下半截被白条盖死」那个缺陷的正对面证据，必须进日志。
    int _hideInputState = -1;

    // ---- Android 键盘高度探针（2026-10-22 加，缺陷：TouchScreenKeyboard.area 恒为 0）----
    // 背景：官方文档说 area 给「键盘占用的屏幕矩形」，实测本机 API 34 + Tuanjie 2022.3.62t16
    //   在系统输入法已弹起（dumpsys window 有 mInputMethodWindow）时再读仍是 (0,0,0,0)，
    //   ⇒ 依据 area 做的避让**一次都没生效**（截图 ime_fix.png 面板仍在键盘后）。
    // 修法：直接问安卓 ViewTreeObserver.RootView 的可见窗口矩形下限，
    //   visible.bottom - keyboardTop 就是键盘实际占的高度（含导航条），单位 px，与 GUI 同口径。
    //   每帧问 JNI 代价偏大 ⇒ 每 6 帧取一次，并缓存。
    AndroidJavaObject _unityPlayerActivity;
    int _actResolveFrame = -1;
    int _kbProbeFrame = -1;
    int _kbHeightCache;              // 上一次 JNI 探到的键盘高度（px），0 = 收起
    bool _kbProbeBroken;             // JNI 链路异常只报一次，之后永久退化到 area 路径
    bool _kbProbeLoggedZero;         // 「探针可用但读到 0」只报一次

    // 聚焦请求：Visible 置真时置位，DrawAll 里最多重试 3 帧
    bool _wantFocus;
    int _focusWaitFrames;
    // ★ 键盘被系统收起后的自动重聚焦（缺陷 #3）：
    //   _kbRefocusArmed  = 本面板曾在键盘上成功唤起过（否则不该主动弹键盘，比如刚打开还没聚焦）
    //   _kbRefocusFrame  = 下次允许重试的帧号（<0 表示还没排期）；节流避免与用户 BACK 对抗
    bool _kbRefocusArmed;
    int _kbRefocusFrame = -1;

    // 日志节流
    bool _firstKbLog;            // 键盘/平台相关的首次说明只打一次
    bool _threw;                 // DrawAll 异常只打一次，避免每帧刷屏
    bool _verdictLogged;
    int _lastVerdictFrame = -1;
    int _sendFrame = -1;         // Enter 去重：同一帧只发一次（OnGUI 会因事件多次回调）

    // 清焦点的请求：GUI.FocusControl 只允许在 OnGUI 内调用，直接调会抛，故置位后在 DrawAll 里处理。
    bool _wantBlur;

    // 程序化改 _input（发送/清空）的那一帧，禁止再用 TextField 的返回值覆盖，
    // 否则「发送后清空」会被同一帧的 edited（旧串）写回去 —— 表现为「发了但输入框没清」。
    int _suppressFieldFrame = -1;

    // 样式（GUI.skin 只能在 OnGUI 内取，故惰性构造；屏幕尺寸变了才重建）
    GUIStyle _field, _button, _title, _small, _hist;
    float _styleScale = -1f;

    // ---- 指针遮挡（2026-10-22 修：真机「清历史」点了没反应）----
    // 根因（device 实证）：P0BMapWalk.AttackBtn/Btn 用 raw `Event.current` + `e.Use()` 抢走
    // 自己那 508px 侧栏矩形内的 MouseDown，而 P0-I 面板画在它们**之后**。同一个 MouseDown
    // 被先 Use() 掉，后画的 GUI.Button 永远收不到 ⇒ 按钮看着在、点着没反应（静默死）。
    // 实测：histClearRect=(2427,718,96,26) 完全落在 AttackBtn=(2138,660,440,140) 内，
    //       点 (2475,731) 只出 `[P0H] 按钮攻击`、零条 [P0I]。
    // 解法：把面板矩形按真实像素记下来，下层 HUD 命中前先问一句，落在面板内的点击不归它。
    Rect _panelScreenRect = new Rect(0f, 0f, 0f, 0f);

    /// <summary>面板当前占用的屏幕矩形（真实像素）。未打开 / 未绘制过时宽高为 0。</summary>
    public Rect PanelScreenRect { get { return _panelScreenRect; } }

    /// <summary>
    /// 该屏幕点是否已被 P0-I 面板接管。下层 raw-event HUD（Btn/AttackBtn/ChatBtn）
    /// 命中前必须问这一句，否则会先 Use() 掉本该归面板的点击。
    /// 面板未打开 ⇒ 恒 false，行为与加此方法前完全一致（不改变已验收的 P0-D/H 手感）。
    /// </summary>
    public bool BlocksPointer(Vector2 screenPos)
    {
        if (!_visible) return false;
        Rect r = _panelScreenRect;
        if (r.width <= 0f || r.height <= 0f) return false;
        return r.Contains(screenPos);
    }

    // ---- P0-I 运行时自检（2026-10-22 加）----
    // 目的：把「包里有机码」升级成「运行时真的走到了」，此前所有 F12 实验失败都是黑盒。
    bool _enterLogged;                 // DrawAll 首次进入只打一次
    bool _closedLogged;                // 关闭态早退只打一次
    readonly HashSet<int> _seenKeys = new HashSet<int>();   // 见过的按键码只打一次，避免刷屏

    // ================= 对外只读统计（验收/截图对账用） =================
    public int SentCount { get; private set; }
    public string LastSent { get; private set; }
    public int HistoryCount { get { return _history.Count; } }
    public bool KeyboardActive { get { return IsKbActive(); } }
    public string InputText { get { return _input; } }

    /// <summary>可见性。默认 false（不挡已验收的 P0-D 画面）。置真时自动请求聚焦并唤起系统键盘。</summary>
    public bool Visible
    {
        get { return _visible; }
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value) RequestFocus();
            else CloseKeyboard();
            Debug.Log("[P0I] 面板 => " + (value ? "打开" : "关闭") +
                      "（键盘=" + (IsKbActive() ? "on" : "off") + " 历史=" + _history.Count + " 条）");
        }
    }

    /// <summary>切换可见性（打开时自动聚焦输入框）。</summary>
    public void Toggle() { Visible = !Visible; }

    /// <summary>请求聚焦输入框：DrawAll 里逐帧尝试 GUI.FocusControl，并唤起系统键盘（Android）。</summary>
    public void RequestFocus()
    {
        _wantFocus = true;
        _focusWaitFrames = 0;
        _verdictLogged = false;
    }

    /// <summary>清空输入框（不动历史）。</summary>
    public void ClearInput()
    {
        _input = string.Empty;
        _lastKbText = string.Empty;
        _suppressFieldFrame = Time.frameCount;   // 本帧 TextField 的旧返回值不得把清空结果写回去
        try { if (_kb != null && _kb.active) _kb.text = string.Empty; }
        catch (System.Exception) { }
    }

    /// <summary>清空消息历史（不动输入框）。</summary>
    public void ClearHistory()
    {
        int n = _history.Count;
        _history.Clear();
        Debug.Log("[P0I] 消息历史已清空（原有 " + n + " 条）");
    }

    /// <summary>发送当前输入（等价点「发送」）。空输入直接忽略。</summary>
    public void Send()
    {
        string t = _input == null ? string.Empty : _input.Trim();
        if (t.Length == 0)
        {
            Debug.Log("[P0I] 发送被忽略：输入为空");
            return;
        }
        _history.Add(t);
        if (_history.Count > MaxHistory)
            _history.RemoveRange(0, _history.Count - MaxHistory);

        SentCount++;
        LastSent = t;
        Debug.Log("[P0I] 发送 #" + SentCount + " len=" + t.Length + " 历史=" + _history.Count + " text=" + t);

        try { if (OnSend != null) OnSend(t); }
        catch (System.Exception ex) { Debug.LogError("[P0I] OnSend 回调异常（已吞）: " + ex.GetType().Name + ": " + ex.Message); }

        ClearInput();
        RequestFocus();          // 发完继续接着打
        _verdictLogged = false;
    }

    // ================= 主入口 =================
    /// <summary>在 OnGUI() 内调用。内部自行保存/恢复 GUI.matrix，异常不外抛。</summary>
    public void DrawAll()
    {
        try
        {
            DrawAllInner();
        }
        catch (System.Exception ex)
        {
            if (!_threw)
            {
                _threw = true;
                Debug.LogError("[P0I] DrawAll 异常（已吞掉，OnGUI 不会抛）: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    void DrawAllInner()
    {
        // ★ 生存性锚点：只要 OnGUI 跑起来，这条必定出现一次。若日志里连这条都没有，
        //   说明 OnGUI 根本没执行到本层（调用点问题），而不是本层逻辑问题。
        if (!_enterLogged)
        {
            _enterLogged = true;
            Debug.Log("[P0I] DrawAll 首次进入 res=" + Screen.width + "x" + Screen.height +
                      " ToggleKey=" + ToggleKey + " Visible=" + _visible + " frame=" + Time.frameCount);
        }

        HandleToggleKey();

        if (!_visible)
        {
            // 关闭状态：不画任何像素，只把可能残留的键盘/焦点收掉（收完 _kb 为 null，不会反复调）。
            if (!_closedLogged)
            {
                _closedLogged = true;
                Debug.Log("[P0I] 关闭态（未绘制面板）— 点左栏「聊天」按钮或按 " + ToggleKey + " 打开");
            }
            if (_kb != null) CloseKeyboard();
            HandleBlur();    // CloseKeyboard 只在 OnGUI 外置位，真正清焦点必须在这里做
            return;
        }

        // ★ 真实像素绘制：临时把矩阵置单位阵，finally 恢复。
        //   这一段是本层与 P0-D/F/G 唯一的口径差异，注释见文件头「坐标契约」。
        Matrix4x4 oldMatrix = GUI.matrix;
        Color oldColor = GUI.color;
        bool oldEnabled = GUI.enabled;
        try
        {
            GUI.matrix = Matrix4x4.identity;
            DrawPanel();
        }
        finally
        {
            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
            GUI.enabled = oldEnabled;
        }
    }

    // ================= 热键 =================
    void HandleToggleKey()
    {
        // ★ 按键探针：把「Unity 到底收不收得到按键」变成可证事实。
        //   每个不同的键码只打一行（HashSet 去重），不会刷屏。
        Event probe = Event.current;
        if (probe != null && (probe.type == EventType.KeyDown || probe.type == EventType.KeyUp))
        {
            int code = (int)probe.keyCode;
            if (_seenKeys.Add(code))
                Debug.Log("[P0I] 收到按键 type=" + probe.type + " keyCode=" + probe.keyCode +
                          "(" + code + ") 期望开关=" + ToggleKey);
        }

        if (ToggleKey == KeyCode.None) return;
        Event e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return;
        if (e.keyCode != ToggleKey) return;
        Toggle();
        e.Use();     // 消化掉，免得下面既有逻辑再吃一次同一个按键
    }

    // ================= 面板绘制 =================
    void DrawPanel()
    {
        float s = UiScale();
        EnsureStyles(s);

        // ---- 尺寸（全部真实像素） ----
        float margin = 10f * s;
        float pad = 14f * s;
        float gap = 10f * s;
        float fieldH = 46f * s;
        float lineH = 24f * s;
        float titleH = 26f * s;
        float diagH = 18f * s;

        float panelW = Mathf.Clamp(Screen.width * 0.94f, Screen.width * 0.5f, Screen.width - 2f * margin);
        float maxPanelH = Mathf.Clamp(Screen.height * 0.5f, 120f * s, Screen.height - 2f * margin);

        // 先把「1 行历史」的最小高度保底，再按可用高度反推能放几行。
        float fixedH = pad * 2f + titleH + diagH + gap * 2f + fieldH;
        if (maxPanelH < fixedH + lineH) maxPanelH = Mathf.Min(Screen.height - 2f * margin, fixedH + lineH);
        int rows = Mathf.Clamp(Mathf.FloorToInt((maxPanelH - fixedH) / lineH), 1, MaxHistoryRows);
        float panelH = fixedH + rows * lineH;
        if (panelH > maxPanelH)
        {
            panelH = maxPanelH;
            rows = Mathf.Clamp(Mathf.FloorToInt((panelH - fixedH) / lineH), 1, MaxHistoryRows);
        }

        float panelX = Mathf.Round((Screen.width - panelW) * 0.5f);

        // ★★ 缺陷修复：系统输入法遮挡面板。
        //   旧代码无条件把面板贴在屏幕底部（panelY = H - panelH - margin），
        //   安卓系统输入法弹出后占据屏幕下方一大块（实测 IME rect=[0,128][2616,1140]，
        //   即 y>128 全被盖住），输入框与「发送/清空」正好落在 y≈1070..1116，
        //   用户看不到自己打什么、也点不到发送 ⇒ 功能等价于坏掉。
        //   修法：读 TouchScreenKeyboard.area 拿到键盘真实占位（Unity 把 IME 矩形换算成
        //   「屏幕左下角为原点的 GUI 坐标」，高度即被遮挡的高度），把面板整体抬到键盘上沿之上；
        //   抬起后再夹一次顶部边界，保证不会顶出屏幕。键盘收起（area.height<=0）时行为与旧版一致。
        float kbOccluded = 0f;
        string kbSrc = "无";
        try
        {
            // ① 主路径：JNI 问安卓真实键盘高度（TouchScreenKeyboard.area 在本机恒 0，见字段注释）
            int kbPx = ProbeImeHeightPx();
            if (kbPx > 0)
            {
                kbOccluded = Mathf.Min(kbPx, Screen.height * 0.75f);
                kbSrc = "JNI";
            }
            else
            {
                // ② 兜底：Unity 官方口径（多数机型有效；本机恒 0 已实测）
                Rect kbArea = TouchScreenKeyboard.area;
                if (kbArea.height > 0f)
                {
                    kbOccluded = Mathf.Min(kbArea.height, Screen.height * 0.75f);
                    kbSrc = "area";
                }
            }
        }
        catch { kbOccluded = 0f; kbSrc = "异常"; }

        // ③ adjustResize 已生效时窗口自身会缩高，此时面板贴底即可，不应再叠一次键盘偏移。
        //   辨识办法：键盘开着，但 Screen.height 比「未弹出时的历史高度」矮了一截 ⇒ 系统已让位。
        //   为免依赖历史高度（旋转/切分辨率会变），直接判断：键盘出、屏幕高 < 0.75×物理高 ⇒ 已缩。
        bool resizedBySystem = false;
        if (kbOccluded > 0f)
        {
            int physH = GetPhysicalHeightPx();
            if (physH > 0 && Screen.height < physH * 0.85f)
            {
                resizedBySystem = true;
                kbOccluded = 0f;
                kbSrc += "+adjustResize(已缩窗)";
            }
        }

        float panelY = Mathf.Round(Screen.height - panelH - margin - kbOccluded);
        // 顶部夹紧：抬得太高会顶出屏幕（小屏 + 高键盘 + 多行历史时会发生）
        float minPanelY = margin;
        if (panelY < minPanelY) panelY = minPanelY;
        Rect panelRect = new Rect(panelX, panelY, panelW, panelH);
        _panelScreenRect = panelRect;    // 供 BlocksPointer 给下层 HUD 让位（真实像素口径）

        // ★ 判定行：把「避开没避开」变成可证事实，而不是靠人看截图。
        //   遮挡面上沿（自上而下的 GUI 坐标）= 屏高 - 遮挡高；面板底 <= 该值才算真避开。
        //   2026-10-23 真机教训：只按 IME 高度算，面板底 463 看着「避开」了 473 的键盘上沿，
        //   但 Unity 自带那层 EditText 浮层把上沿顶到 312 ⇒ 面板下半截照样被盖死。
        //   现已用 TouchScreenKeyboard.hideInput=true 拆掉那层，故判据改回「对 IME 上沿」，
        //   同时把 hideInput 的实际状态一起打进日志（它若没设上，这行会立刻显形）。
        float occludedTop = (kbOccluded > 0f) ? (Screen.height - kbOccluded) : Screen.height;
        float stillCovered = (panelY + panelH) - occludedTop;
        string kbSig = kbSrc + "@" + Mathf.RoundToInt(kbOccluded) + "@" + Screen.height + "@" + _hideInputState;
        if (kbSig != _lastKbSig)
        {
            _lastKbSig = kbSig;
            string verdict;
            if (kbOccluded > 0f)
                verdict = (stillCovered > 0.5f)
                    ? (" ⇒ 仍被遮挡 " + Mathf.RoundToInt(stillCovered) + " px")
                    : " ⇒ 已完全避开（面板底 " + Mathf.RoundToInt(panelY + panelH) + " ≤ 遮挡上沿 " + Mathf.RoundToInt(occludedTop) + "）";
            else if (resizedBySystem)
                verdict = " ⇒ 系统已缩窗(adjustResize)，贴底即安全";
            else
                verdict = " ⇒ 键盘收起，恢复底部贴边";
            Debug.Log("[P0I] 键盘避让 遮挡高=" + Mathf.RoundToInt(kbOccluded) +
                      " 来源=" + kbSrc +
                      " 遮挡上沿=" + Mathf.RoundToInt(occludedTop) +
                      " hideInput=" + (_hideInputState == 1 ? "on" : (_hideInputState == 0 ? "off" : "未探")) +
                      " 面板Y=" + Mathf.RoundToInt(panelY) +
                      " 面板H=" + Mathf.RoundToInt(panelH) +
                      " 面板底=" + Mathf.RoundToInt(panelY + panelH) +
                      " 屏高=" + Screen.height + verdict);
        }

        float innerX = panelX + pad;
        float innerW = panelW - pad * 2f;
        float y = panelY + pad;

        // 标题行右侧留出「清历史」按钮的位置。
        // 2026-10-22 修：此前 ClearHistory() 全仓 0 调用者 ⇒ P0IIme.cs:187 的日志永远打不出来（死代码），
        // 对账时表现为「发射点存在但设备侧恒 0 行」。这里给它接上真实入口，让该发射点变成可达可验证。
        float histBtnW = Mathf.Clamp(96f * s, 56f, innerW * 0.3f);
        Rect histClearRect = new Rect(innerX + innerW - histBtnW, y, histBtnW, titleH);
        Rect titleRect = new Rect(innerX, y, Mathf.Max(60f, innerW - histBtnW - 8f * s), titleH);
        y += titleH;
        Rect diagRect = new Rect(innerX, y, innerW, diagH);
        y += diagH + gap;
        float histH = rows * lineH;
        Rect histRect = new Rect(innerX, y, innerW, histH);
        y += histH + gap;
        Rect inputRow = new Rect(innerX, y, innerW, fieldH);

        // 输入行：右侧两个按钮，剩下的宽度给 TextField
        float btnW = Mathf.Clamp(112f * s, 64f, innerW * 0.3f);
        float btnGap = 8f * s;
        Rect clearRect = new Rect(inputRow.xMax - btnW, inputRow.y, btnW, fieldH);
        Rect sendRect = new Rect(clearRect.x - btnGap - btnW, inputRow.y, btnW, fieldH);
        Rect fieldRect = new Rect(inputRow.x, inputRow.y, Mathf.Max(60f, sendRect.x - btnGap - inputRow.x), fieldH);

        // ---- 1) 底板 + 边框 ----
        Color old = GUI.color;
        GUI.color = new Color(0.05f, 0.06f, 0.09f, 0.93f);
        GUI.DrawTexture(panelRect, Texture2D.whiteTexture);
        GUI.color = new Color(0.30f, 0.42f, 0.60f, 1f);
        float bw = Mathf.Max(1f, 2f * s);
        GUI.DrawTexture(new Rect(panelRect.x, panelRect.y, panelRect.width, bw), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(panelRect.x, panelRect.yMax - bw, panelRect.width, bw), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(panelRect.x, panelRect.y, bw, panelRect.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(panelRect.xMax - bw, panelRect.y, bw, panelRect.height), Texture2D.whiteTexture);
        GUI.color = old;

        // ---- 2) 标题 ----
        GUI.Label(titleRect, "聊天输入（点左栏「聊天」关闭 · 点输入框唤起系统输入法 · 回车发送）", _title);
        // 「清历史」只清消息历史，不动输入框（与「清空」区分开）。挂在标题行右侧。
        if (GUI.Button(histClearRect, "清历史", _button)) ClearHistory();

        // ---- 3) 历史（最新在底部，从下往上回看） ----
        DrawHistory(histRect, rows, lineH, s);

        // ---- 4) 输入行：原生 IMGUI TextField（IME 的落点）+ 发送/清空 ----
        //   SetNextControlName 必须在控件之前调用，FocusControl 才会认这个名字。
        GUI.SetNextControlName(FIELD_CONTROL);
        string edited = GUI.TextField(fieldRect, _input, MaxInputChars, _field);

        // 点输入框 ⇒ 唤起系统键盘（IMGUI 自己负责光标/选区，这里只负责把键盘叫起来）。
        HandleFieldClick(fieldRect);

        // ---- 5) 输入串归口（两条路径二选一，见文件头） ----
        //   ★ 必须在画按钮、处理 Enter 之前做：那两者会程序化清空 _input，
        //     若归口放在它们之后，本帧的 edited（旧串）会把刚清空的内容写回去。
        SyncInput(edited);

        // ---- 6) 按钮 ----
        if (GUI.Button(sendRect, "发送", _button)) Send();
        if (GUI.Button(clearRect, "清空", _button))
        {
            ClearInput();
            Debug.Log("[P0I] 输入框已清空");
            RequestFocus();
        }

        // ---- 7) 回车发送（只认公开的 KeyCode.Return；键盘 active 时交给系统键盘的完成键） ----
        HandleEnter();

        // ---- 8) 聚焦（放在控件之后：FocusControl 要控件先注册过才有意义） ----
        HandleFocus();
        HandleBlur();
        // ---- 8.5) 键盘被系统收起后的自动重聚焦（缺陷 #3）----
        HandleKbAutoRefocus();

        // ---- 9) 诊断行（最后画：带上的状态是本帧最新的） ----
        DrawDiagnostics(diagRect, s);

        // ---- 10) 判定行日志（节流） ----
        if (!_verdictLogged || Time.frameCount - _lastVerdictFrame >= 300)
            LogVerdict(s, panelW, panelH, rows, panelY, stillCovered);
    }

    /// <summary>
    /// 输入串归口：键盘 active 则以键盘为权威（Android IME 整串），否则以 TextField 返回值为权威。
    /// 两者同一帧只认一条，避免互相覆盖 —— 这是中文不被写坏的关键。
    /// </summary>
    void SyncInput(string edited)
    {
        if (IsKbActive())
        {
            SyncFromKeyboard();
            return;
        }
        // 本帧刚程序化改过 _input（发送/清空）⇒ 忽略 TextField 的旧返回值，防止内容被写回。
        if (_suppressFieldFrame == Time.frameCount) return;
        if (edited != _input) _input = Truncate(edited, MaxInputChars);
    }

    void DrawHistory(Rect histRect, int rows, float lineH, float s)
    {
        if (_history.Count == 0)
        {
            GUI.Label(new Rect(histRect.x, histRect.y, histRect.width, lineH),
                      "（还没有消息。输入后按 Enter 或点「发送」。）", _small);
            return;
        }

        int n = Mathf.Min(_history.Count, Mathf.Min(rows, MaxHistoryRows));
        int first = _history.Count - n;      // 只画最近 n 条；最旧的在这块的最上面
        Color old = GUI.color;
        for (int i = 0; i < n; i++)
        {
            Rect r = new Rect(histRect.x, histRect.y + i * lineH, histRect.width, lineH);
            if ((i & 1) == 1)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.05f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = old;
            }
            string t = _history[first + i];
            GUI.Label(r, "#" + (first + i + 1) + "  " + t, _hist);
        }
        GUI.color = old;
    }

    void DrawDiagnostics(Rect r, float s)
    {
        bool supported = KbSupported();
        bool active = IsKbActive();
        string foc = "off";
        try { foc = GUI.GetNameOfFocusedControl() == FIELD_CONTROL ? "on" : "off"; }
        catch (System.Exception) { foc = "?"; }

        string diag = "[P0I] IME: tk=" + (supported ? 1 : 0)
                    + " active=" + (active ? 1 : 0)
                    + " textLen=" + (_input == null ? 0 : _input.Length)
                    + " kbLen=" + KbTextLen()
                    + " focus=" + foc
                    + " res=" + Screen.width + "x" + Screen.height
                    + " s=" + s.ToString("F2");
        GUI.Label(r, diag, _small);
    }

    // ================= 输入串同步 =================
    void SyncFromKeyboard()
    {
        if (_kb == null) return;

        string kt = null;
        try { kt = _kb.text; }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[P0I] 读键盘 text 失败（已吞）: " + ex.Message);
            return;
        }
        if (kt == null) kt = string.Empty;
        kt = Truncate(kt, MaxInputChars);

        if (kt == _lastKbText) return;    // 键盘串没变 ⇒ 不动输入框，避免把 TextField 的光标位置打乱
        _lastKbText = kt;
        _input = kt;                      // ★ 整串替换：中文是 IME 合成后的完整 Unicode 串，绝不逐字符拼接
    }

    // ================= 键盘 =================
    bool KbSupported()
    {
        try { return TouchScreenKeyboard.isSupported; }
        catch (System.Exception) { return false; }
    }

    // ---- Android 键盘实际高度探针（见字段区注释）----
    // 取法说明（为什么不是 getWindowVisibleDisplayFrame）：
    //   adjustResize 生效后，Activity 的 DecoView 高度会缩，getWindowVisibleDisplayFrame 会返回
    //   「缩完后的可见区」，拿它减 Screen.height 恒为 0，探不出键盘。
    //   可靠口径是 WindowManager.getDefaultDisplay().getRealSize()（物理全高，不受软键盘影响）
    //   减去 visible frame 的 bottom ⇒ 键盘实际占据的像素高（含导航条），与 IMGUI 像素同口径。
#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject GetActivity()
    {
        if (_unityPlayerActivity != null) return _unityPlayerActivity;
        // 解析失败没必要每帧重试：隔 120 帧再说
        if (_actResolveFrame >= 0 && Time.frameCount - _actResolveFrame < 120) return null;
        _actResolveFrame = Time.frameCount;
        try
        {
            var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            _unityPlayerActivity = player.GetStatic<AndroidJavaObject>("currentActivity");
        }
        catch (System.Exception e)
        {
            if (!_kbProbeBroken)
            {
                _kbProbeBroken = true;
                Debug.LogWarning("[P0I] 取 currentActivity 失败，键盘高度走 TouchScreenKeyboard.area 兜底: " + e.Message);
            }
            _unityPlayerActivity = null;
        }
        return _unityPlayerActivity;
    }

    /// <summary>安卓真实键盘高度（px）。0 = 键盘收起；&lt;0 = 探针不可用。</summary>
    int ProbeImeHeightPx()
    {
        if (_kbProbeBroken) return -1;
        if (_kbProbeFrame >= 0 && Time.frameCount - _kbProbeFrame < 6) return _kbHeightCache;  // 6 帧取一次
        _kbProbeFrame = Time.frameCount;

        var act = GetActivity();
        if (act == null) return -1;

        AndroidJavaObject win = null, deco = null, disp = null, pt = null, frame = null, wm = null;
        try
        {
            // 物理全高（不受软键盘 / adjustResize 影响）
            wm = act.Call<AndroidJavaObject>("getWindowManager");
            win = act.Call<AndroidJavaObject>("getWindow");
            deco = win.Call<AndroidJavaObject>("getDecorView");
            disp = wm.Call<AndroidJavaObject>("getDefaultDisplay");

            var ptCls = new AndroidJavaClass("android.graphics.Point");
            pt = new AndroidJavaObject("android.graphics.Point");
            disp.Call("getRealSize", pt);
            int realH = pt.Get<int>("y");

            var rectCls = new AndroidJavaClass("android.graphics.Rect");
            frame = new AndroidJavaObject("android.graphics.Rect");
            deco.Call("getWindowVisibleDisplayFrame", frame);
            int visBottom = frame.Get<int>("bottom");

            int kbH = realH - visBottom;
            // 小于 12% 物理高的一律当噪声（状态栏/刘海差），视为键盘收起
            if (kbH < realH * 0.12f) kbH = 0;
            if (kbH < 0) kbH = 0;
            _kbHeightCache = kbH;

            if (kbH == 0 && !_kbProbeLoggedZero && IsKbActive())
            {
                _kbProbeLoggedZero = true;
                Debug.LogWarning("[P0I] 键盘 active 但 JNI 探到高度=0（realH=" + realH +
                                 " visBottom=" + visBottom + "）⇒ 该机型口径不同，靠 adjustResize 兜");
            }
            return _kbHeightCache;
        }
        catch (System.Exception e)
        {
            _kbProbeBroken = true;
            Debug.LogWarning("[P0I] JNI 键盘高度探针异常，改为仅靠 adjustResize（已吞）: " + e.Message);
            return -1;
        }
        finally
        {
            if (frame != null) frame.Dispose();
            if (pt != null) pt.Dispose();
            if (disp != null) disp.Dispose();
            if (deco != null) deco.Dispose();
            if (win != null) win.Dispose();
            if (wm != null) wm.Dispose();
        }
    }

    /// <summary>物理屏幕高（px），用于判断 adjustResize 是否已缩窗。&lt;0 = 未知。</summary>
    int GetPhysicalHeightPx()
    {
        var act = GetActivity();
        if (act == null) return -1;
        AndroidJavaObject wm = null, disp = null, pt = null;
        try
        {
            wm = act.Call<AndroidJavaObject>("getWindowManager");
            disp = wm.Call<AndroidJavaObject>("getDefaultDisplay");
            pt = new AndroidJavaObject("android.graphics.Point");
            disp.Call("getRealSize", pt);
            return pt.Get<int>("y");
        }
        catch (System.Exception) { return -1; }
        finally
        {
            if (pt != null) pt.Dispose();
            if (disp != null) disp.Dispose();
            if (wm != null) wm.Dispose();
        }
    }
#else
    int ProbeImeHeightPx() { return -1; }
    int GetPhysicalHeightPx() { return -1; }
#endif

    bool IsKbActive()
    {
        try { return _kb != null && _kb.active; }
        catch (System.Exception) { return false; }
    }

    /// <summary>键盘 .text 长度；-1 = 当前没有键盘对象（诊断行用）。</summary>
    int KbTextLen()
    {
        try
        {
            if (_kb == null) return -1;
            string t = _kb.text;
            return t == null ? 0 : t.Length;
        }
        catch (System.Exception) { return -1; }
    }

    void EnsureKeyboard()
    {
        if (IsKbActive()) return;
        if (_kb != null) CloseKeyboard();
        OpenKeyboard();
    }

    void OpenKeyboard()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (!TouchScreenKeyboard.isSupported)
            {
                if (!_firstKbLog)
                {
                    _firstKbLog = true;
                    Debug.Log("[P0I] 本机 TouchScreenKeyboard.isSupported=false —— 不唤起系统键盘，" +
                              "退回 GUI.TextField 原生输入（仍可接收 IMGUI 的字符事件）");
                }
                return;
            }

            // ★★ 缺陷修复（2026-10-23 真机取证）：Unity 在安卓上除了系统输入法，还会自己再弹一个
            //   EditText 浮层窗口来接 IME 文本，位置就贴在系统键盘正上方。
            //   本机实测：该窗口 w=2616 h=161，位于 y=312..473（系统键盘上沿 = 473，屏幕高 1140）。
            //   它是个不透明白条，会把「已经按键盘高度抬到 y=37..463」的 IMGUI 面板下半截整块盖掉
            //   ⇒ 输入框 / 发送 / 清空 全被压在白条底下（截图 now.png 实证）。
            //   UnityPlayer 为这件事专门留了 setHideInputField(boolean)，C# 侧入口即 TouchScreenKeyboard.hideInput。
            //   藏掉该浮层后，遮挡面上沿从 312 提到系统键盘上沿 473，面板按键盘高度上移即可整块露出。
            try
            {
                if (!TouchScreenKeyboard.hideInput)
                {
                    TouchScreenKeyboard.hideInput = true;
                    Debug.Log("[P0I] 已隐藏 Unity 自带输入浮层 hideInput=true（消掉键盘上方白条，面板不再被它盖住）");
                }
                _hideInputState = TouchScreenKeyboard.hideInput ? 1 : 0;
            }
            catch (System.Exception hex)
            {
                _hideInputState = -1;
                Debug.LogWarning("[P0I] 设置 hideInput 失败（已吞；退回「复合遮挡面」兜底判据）: " +
                                 hex.GetType().Name + ": " + hex.Message);
            }

            // 8 参重载：text / 键盘类型 / 自动纠错 / 多行 / 密码 / 警告 / 占位 / 字符上限。
            // 自动纠错必须 false：中文会被“纠”成乱码。单行：回车即完成。
            _kb = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default, false, false, false, false, "", MaxInputChars);
            if (_kb == null)
            {
                Debug.LogWarning("[P0I] TouchScreenKeyboard.Open 返回 null —— 只剩 GUI.TextField 一条路径");
                return;
            }

            // 把已有草稿塞进键盘缓冲，保证「键盘串 == 输入框串」的基线一致。
            if (_input != null && _input.Length > 0)
            {
                try { _kb.text = _input; }
                catch (System.Exception) { }
            }
            string back = string.Empty;
            try { back = _kb.text == null ? string.Empty : _kb.text; }
            catch (System.Exception) { }
            if (back != _input) _input = Truncate(back, MaxInputChars);  // 平台拒绝写文本 ⇒ 以键盘实际起点为准，两边不劈叉
            _lastKbText = _input;

            _kbRefocusArmed = true;      // ★ 键盘确实起来过 ⇒ 之后掉了才值得自动抢回
            _kbRefocusFrame = -1;

            if (!_firstKbLog)
            {
                _firstKbLog = true;
                Debug.Log("[P0I] 已唤起系统输入法：type=" + _kb.GetType().Name +
                          " limit=" + MaxInputChars + " 基线长度=" + _input.Length);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[P0I] TouchScreenKeyboard.Open 异常（已吞，不影响 OnGUI）: " +
                           ex.GetType().Name + ": " + ex.Message);
            _kb = null;
        }
#else
        // 桌面 / 编辑器：跳过 TouchScreenKeyboard（这些平台 isSupported=false，Open 也没有界面）。
        // GUI.TextField 直接吃物理键盘与系统输入法（Windows IME 经 Event.character 整字上屏）。
        if (!_firstKbLog)
        {
            _firstKbLog = true;
            Debug.Log("[P0I] 非 Android 平台（" + Application.platform +
                      "）跳过 TouchScreenKeyboard 唤起，输入走 GUI.TextField 原生 IME");
        }
#endif
    }

    /// <summary>收起系统键盘（面板隐藏 / 失焦 / 用户点完成键）。任何异常都吞掉。</summary>
    void CloseKeyboard()
    {
        try
        {
            if (_kb != null)
            {
                _kb.active = false;
                Debug.Log("[P0I] 收起系统输入法");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[P0I] 收起键盘异常（已吞）: " + ex.GetType().Name + ": " + ex.Message);
        }
        _kb = null;
        _lastKbText = string.Empty;
        // 主动收起键盘 ⇒ 解除自动重聚焦武装，避免「关面板后又被立刻抢回焦点」的来回打架
        _kbRefocusArmed = false;
        _kbRefocusFrame = -1;
        // 不能在 OnGUI 之外调 GUI.FocusControl（会抛 "GUI... called outside of OnGUI"），
        // 所以只置位，由 DrawAll 在 OnGUI 内清焦点。
        _wantBlur = true;
    }

    // ================= 事件 =================
    void HandleFieldClick(Rect fieldRect)
    {
        Event e = Event.current;
        if (e == null || e.type != EventType.MouseDown) return;
        if (!fieldRect.Contains(e.mousePosition)) return;
        EnsureKeyboard();     // ★ 不 e.Use()：TextField 还要靠这个 MouseDown 定位光标
    }

    void HandleFocus()
    {
        if (!_wantFocus) return;
        string cur = string.Empty;
        try { cur = GUI.GetNameOfFocusedControl(); }
        catch (System.Exception) { cur = string.Empty; }

        if (cur == FIELD_CONTROL)
        {
            _wantFocus = false;
            _focusWaitFrames = 0;
            return;
        }

        try { GUI.FocusControl(FIELD_CONTROL); }
        catch (System.Exception ex) { Debug.LogWarning("[P0I] FocusControl 失败（已吞）: " + ex.Message); }

        EnsureKeyboard();     // 打开面板就唤起键盘（不等焦点确认，Android 上焦点要下一帧才生效）

        _focusWaitFrames++;
        if (_focusWaitFrames > 3)
        {
            _wantFocus = false;
            Debug.LogWarning("[P0I] 聚焦重试 3 帧仍未拿到焦点（focus=" + cur + "）；TextField 仍可点选，键盘已尽力唤起");
        }
    }

    /// <summary>面板关闭时清 IMGUI 焦点。GUI.FocusControl 只能在 OnGUI 内调用，故在此处理 CloseKeyboard 置的位。</summary>
    void HandleBlur()
    {
        if (!_wantBlur) return;
        _wantBlur = false;
        try { GUI.FocusControl(string.Empty); }
        catch (System.Exception) { }
    }

    /// <summary>
    /// ★ 缺陷修复：系统把键盘收掉后（典型是用户按了 BACK 想「关掉键盘」），
    ///   面板还开着，但输入框不再有焦点、再打字也没反应 —— 用户必须再点一下输入框才能继续，
    ///   表现为「返回之后输入法不理我了」。
    ///   旧代码只在 Visible 由关→开时 RequestFocus()，这条路径完全没覆盖。
    ///   这里每帧兜底：面板可见 + 键盘已不 active + 没有待处理的聚焦/失焦请求 ⇒ 重新请求聚焦。
    ///   注意不能在 CloseKeyboard() 之后立刻抢回（那是面板关闭的主动收起，_visible 已为 false，天然不冲突），
    ///   也不能每帧无脑唤键盘（会跟用户的 BACK 打架），所以只在 _kbRetryAtFrame 到点后重试一次。
    /// </summary>
    void HandleKbAutoRefocus()
    {
        if (!_visible) { _kbRefocusArmed = false; return; }
        if (_wantFocus || _wantBlur) return;                 // 有明确请求时让别人处理
        if (!_kbRefocusArmed) return;                        // 只在「曾经唤起成功过」之后才兜底

        if (IsKbActive())
        {
            _kbRefocusFrame = -1;                            // 键盘回来了，重置
            return;
        }

        // 键盘掉了。给系统几帧喘息（IME 收起动画/窗口重排期间立刻重开会失败），
        // 然后每 30 帧最多重试一次，避免和用户的 BACK 死循环对抗。
        if (_kbRefocusFrame < 0) _kbRefocusFrame = Time.frameCount + 6;
        if (Time.frameCount < _kbRefocusFrame) return;
        _kbRefocusFrame = Time.frameCount + 30;

        Debug.Log("[P0I] 检测到系统输入法被收起（面板仍可见）⇒ 自动重新聚焦");
        RequestFocus();
    }

    void HandleEnter()
    {        Event e = Event.current;
        bool enter = false;
        if (e != null && e.type == EventType.KeyDown &&
            (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
        {
            enter = true;
            // 软键盘 active 时，回车是系统键盘的“完成”，IMGUI 不该再吃；也就不在这里发，避免双发。
            if (!IsKbActive()) e.Use();
        }
        else if (e == null && Input.GetKeyDown(KeyCode.Return))
        {
            enter = true;   // 兜底路径（正常 OnGUI 内 e 不为 null，这里只防极端情况）
        }

        if (!enter) return;
        if (_sendFrame == Time.frameCount) return;   // OnGUI 同一帧会因多类事件多次回调 ⇒ 去重
        _sendFrame = Time.frameCount;
        Send();
    }

    // ================= 样式 / 尺寸 =================
    float UiScale()
    {
        float sw = Mathf.Max(1f, Screen.width);
        float sh = Mathf.Max(1f, Screen.height);
        float s = Mathf.Min(sw / BASE_W, sh / BASE_H);
        return Mathf.Clamp(s, 0.45f, 2f);
    }

    void EnsureStyles(float s)
    {
        if (_field != null && Mathf.Abs(_styleScale - s) < 0.001f) return;
        _styleScale = s;

        int fField = Mathf.Max(11, Mathf.RoundToInt(26f * s));
        int fBtn = Mathf.Max(10, Mathf.RoundToInt(20f * s));
        int fTitle = Mathf.Max(9, Mathf.RoundToInt(18f * s));
        int fSmall = Mathf.Max(7, Mathf.RoundToInt(13f * s));
        int fHist = Mathf.Max(9, Mathf.RoundToInt(18f * s));

        _field = MakeStyle(2, fField);
        _button = MakeStyle(1, fBtn);
        _title = MakeStyle(0, fTitle);
        _small = MakeStyle(0, fSmall);
        _hist = MakeStyle(0, fHist);

        _title.alignment = TextAnchor.MiddleLeft;
        _title.normal.textColor = new Color(0.80f, 0.90f, 1f);
        _title.clipping = TextClipping.Clip;

        _small.alignment = TextAnchor.MiddleLeft;
        _small.normal.textColor = new Color(0.55f, 0.88f, 0.66f);   // 诊断行取绿色，真机截图里好认
        _small.clipping = TextClipping.Clip;

        _hist.alignment = TextAnchor.MiddleLeft;
        _hist.normal.textColor = Color.white;
        _hist.clipping = TextClipping.Clip;

        _field.alignment = TextAnchor.MiddleLeft;
        int px = Mathf.RoundToInt(8f * s);
        int py = Mathf.RoundToInt(4f * s);
        _field.padding = new RectOffset(px, px, py, py);
    }

    /// <summary>kind: 0=label 1=button 2=textField。GUI.skin 取不到时用空 GUIStyle 兜底（GUISkin 曾被剥过）。</summary>
    GUIStyle MakeStyle(int kind, int fontSize)
    {
        GUIStyle src = null;
        try
        {
            GUISkin skin = GUI.skin;
            if (skin != null)
            {
                if (kind == 2) src = skin.textField;
                else if (kind == 1) src = skin.button;
                else src = skin.label;
            }
        }
        catch (System.Exception) { src = null; }

        GUIStyle st;
        try { st = src != null ? new GUIStyle(src) : new GUIStyle(); }
        catch (System.Exception) { st = new GUIStyle(); }

        st.fontSize = fontSize;
        st.richText = false;
        st.wordWrap = false;
        return st;
    }

    static string Truncate(string v, int max)
    {
        if (v == null) return string.Empty;
        return v.Length <= max ? v : v.Substring(0, max);
    }

    // ================= 判定行 =================
    // ★ 2026-10-23 修：此前这里把 "PASS" 写死在字符串里，无论状态多坏都恒打 PASS
    //   ⇒ 判定行等于没有判定力（gate 拿它 grep 只会永远绿）。兄弟层 P0D/P0F/P0G 都是
    //   allOk ? "PASS" : "FAIL" 现算，本层对齐同一口径。
    //   allOk 的口径（全部满足才算 PASS）：
    //     ① 面板确实可见  ② 平台支持软键盘  ③ 面板没被键盘/浮层压住
    //     ④ 控件都拿到了真实矩形（面板内有正宽高）
    //   注意「键盘未唤起」「输入框为空」「还没发过消息」都是正常态，不参与判定，
    //   否则键盘一收起就误报 FAIL（本层面板在不打字时就是收起态）。
    //
    // ★★ 2026-10-23 再修（第二轮，两条判据被证明「恒真」，等于没有判定力）★★
    //   ① covered 恒等于 -margin，代数上不可能为正：
    //        panelY = H - panelH - margin - kbOccluded  （未触顶夹紧时）
    //        occludedTop = H - kbOccluded
    //        stillCovered = (panelY+panelH) - occludedTop = -margin  ⇒ 永远是 -10 / -4
    //      实测两个分辨率都印证：2616x1140 ⇒ covered=-10，900x480 ⇒ covered=-4（都= -margin）。
    //      ⇒ 它只在「顶部夹紧」生效时才可能为正，即**白条盖住面板这种真实故障它永远抓不到**。
    //      保留它当夹紧护栏（夹紧确实会导致越界），但不再把它当主判据。
    //   ② hideInput 只写进日志、没进判据：这层白条的根因就是 hideInput 没设上，
    //      却完全不参与 allOk ⇒ 白条复发时判定行照样 PASS。现补为硬判据。
    //   新增的两条**可判否**判据：
    //     ⑤ 面板整块在屏内（panelY>=0 且 panelY+panelH<=屏高）——小屏+高键盘+多行会真的越界
    //     ⑥ 键盘开着时 hideInput 必须确实为 on——白条根因的直接反证
    void LogVerdict(float s, float panelW, float panelH, int rows, float panelY, float stillCovered)
    {
        _verdictLogged = true;
        _lastVerdictFrame = Time.frameCount;
        bool laidOut = panelW > 0f && panelH > 0f && rows >= 1;
        bool notCovered = stillCovered <= 0.5f;
        bool onScreen = panelY >= -0.5f && (panelY + panelH) <= Screen.height + 0.5f;
        bool kbUp = IsKbActive();
        bool hdOk = !kbUp || _hideInputState == 1;   // 键盘没开时不要求
        bool allOk = _visible && KbSupported() && laidOut && notCovered && onScreen && hdOk;
        // 失败时把具体原因写进 reason=…，免得只有 FAIL 没有线索。
        string reason = allOk ? "-"
            : (!_visible ? "面板不可见"
            : (!KbSupported() ? "平台不支持软键盘"
            : (!laidOut ? "布局退化(面板/行数为0)"
            : (!onScreen ? ("面板越界 Y=" + Mathf.RoundToInt(panelY) + " H=" + Mathf.RoundToInt(panelH) + " 屏高=" + Screen.height)
            : (!hdOk ? "hideInput 未生效(白条风险)"
            : ("面板被遮挡 " + Mathf.RoundToInt(stillCovered) + "px"))))));
        Debug.Log("[P0I] IME_VERDICT = " + (allOk ? "PASS" : "FAIL") +
                  " reason=" + reason +
                  " visible=" + (_visible ? 1 : 0) +
                  " tk=" + (KbSupported() ? 1 : 0) +
                  " active=" + (kbUp ? 1 : 0) +
                  " textLen=" + (_input == null ? 0 : _input.Length) +
                  " kbLen=" + KbTextLen() +
                  " sent=" + SentCount +
                  " history=" + _history.Count +
                  " panel=" + Mathf.RoundToInt(panelW) + "x" + Mathf.RoundToInt(panelH) +
                  " rows=" + rows +
                  " panelY=" + Mathf.RoundToInt(panelY) +
                  " onScreen=" + (onScreen ? 1 : 0) +
                  " covered=" + Mathf.RoundToInt(stillCovered) +
                  " hideInput=" + (_hideInputState == 1 ? "on" : (_hideInputState == 0 ? "off" : "未探")) +
                  " res=" + Screen.width + "x" + Screen.height +
                  " s=" + s.ToString("F2"));
    }
}
