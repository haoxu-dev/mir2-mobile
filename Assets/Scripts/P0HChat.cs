// ============================================================================
//  P0HChat.cs —— 底部聊天框（记录区 + 输入条 + 滚动条），1:1 复刻 2003 原版 1.76
// ============================================================================
//  用户 2026-10-25 原话：
//    「第一张图片就上面那个，没问题。就是它又有聊天框里边的记录，这种的，你了解清楚
//      他们整一套是怎么去运行的，然后把它 1:1 的搬过来，要它真正的有联动，各种情况。」
//
//  四条硬指标（缺一条不算完成）：
//    ★ 2026-10-26 用户定调后重划职责（此前"9 行假记录"已作废）：
//    ① 冷启动**空框** —— 2003 原版 1.76 首次进游戏就是空聊天板（没有任何预置行）。
//       旧的 9 行是从参考截图 OCR 抄进去的假数据，不属于"真联动"，已删除。
//    ② 能输入：点底部输入条弹系统输入法，中文上屏；回车后变「角色名: 内容」
//    ③ 能翻：滚轮 / PageUp / PageDown / Home / End / 拖拽；滑块跟随；
//              翻旧账时新消息不把视图拽回底部
//    ④ 真联动：**只有真人说话与系统类消息**进聊天框。
//       砍怪 / 被打 / 打空 / 被挡 一律不进聊天（原版只画头顶飘字）；
//       升级 / 金币 / 魔法不足 / 冷却 / 无目标 走左上绿（P0HNotify.Top）；
//       经验走左下红。分流唯一入口 = P0HNotify.Pump。
//
//  ★★ 绘制契约（违反必错位，详见 P0BMapWalk.cs L382-407 的实测核算）：
//    本类的 DrawAll() **必须**在 P0BMapWalk.OnGUI 的
//        GUI.BeginGroup(MainUICanvasRect)  ... GUI.EndGroup()
//    之内被调用，且坐标一律走 P0BMapWalk.GroupRectForLogic(lx,ly,lw,lh)。
//    绝不能用 CanvasRectForLogic（那是绝对设备坐标，组内会二次偏移 508,-60）。
//
//  ★★ 几何全部来自**客户端源码 + 真底图 Prguse3[291] 像素实测**（不再拿参考截图猜）：
//    源码 FState.pas:9037-9046（聊天文字）
//        x = 208
//        y = SCREENHEIGHT - 130 + (i - ChatBoardTop) * 12   ⇒ 470 + i*12
//        VIEWCHATLINE = 9 行  ⇒ 470..577 共 108 px = 9×12，与白板高度**完全吻合**
//    真底图实测（prguse1_800x251.png = Prguse3[291]，板内 y = 逻辑 y − 349）：
//        白板 y 470..577 连续纯白 387 px，x 208..594 无缺口；
//        595..597 是 3D 凸边（192/148/239 灰阶）⇒ **白板右边界 = 594**；
//        输入白条 y 581..592，x 208..594（387 px），上下各 1 px 分隔线。
//    ⇒ TEXT_W / IN_W 一律 387（此前 374 是按旧 1.85 皮量出来的，偏窄 13 px）。
//    ⇒ 滚动条是 DBChatDirectPaint 后加的，**没烤进底板**，画在白板内侧 x 584..594。
//
//  ★ 字号校准（本轮 PIL 逐字号实测 + 参考图反推）：
//    SimHei 下「N 个全角字的总步进 == N × fontSize」。参考图 8 个全角字实测宽
//    110~111 逻辑 px ⇒ 全角步进 ≈ 13.9 ⇒ fontSize = 14 × ScaleI（ScaleI=2 → 设备 28px）。
//    ⚠ 若改成 10×ScaleI 会每行窄 28%、折行点全错 —— 这是上一版交接里的错误值。
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

/// <summary>聊天行类型（语义取自 MIR2 源码 ChatDialog 的 TextType 枚举，颜色按原版取值）。</summary>
public enum ChatType
{
    Normal,          // 普通白底黑字
    Hint,            // 系统提示：深绿
    Announcement,    // 公告：深蓝
    LineMessage,     // 线路消息
    Shout,           // 喊话：金底
    Shout2,
    Shout3,
    System,          // 系统：红
    System2,         // 系统 2：暗红
    Group,           // 组队：棕
    WhisperOut,      // 私聊发出：矢车菊蓝
    WhisperIn,       // 私聊收到：深蓝
    Guild,           // 行会：绿
    LevelUp,         // 升级：淡紫底 + 蓝字
    Relationship,    // 关系：粉
    Mentor           // 师徒：紫
}

public class P0HChat
{
    // ------------------------------------------------------------------ 几何
    // 逻辑 800x600 坐标（唯一口径）。数值来源见文件头注释。
    const float TEXT_X = 208f;      // 源码 FState.pas:9043 TextOut(208, ...)
    const float TEXT_Y = 470f;      // 源码 :9043 SCREENHEIGHT - 130 = 470
    const float TEXT_W = 387f;      // 真底图白板 x 208..594
    const float LINE_H = 12f;       // 源码 :9043 (i - ChatBoardTop) * 12
    const int   ROWS   = 9;         // 源码 VIEWCHATLINE = 9 ⇒ 470..577

    const float IN_X = 208f, IN_Y = 581f, IN_W = 387f, IN_H = 12f;
    // 右侧 16x100 竖条（源码 L8883-8884）由 P0HHud 画，本层不设常量（单一属主）

    const int MAX_LINES  = 200;
    const int MAX_INPUT  = 200;
    const string FIELD_CONTROL = "P0HChatField";

    /// <summary>折行估算上限（逻辑 px）。全角字宽 ≈ fontSize 逻辑 px = 14。</summary>
    const float WRAP_W = TEXT_W - 6f;
    const float FULL_W = 14f;    // 全角步进（逻辑 px），由 fontSize/ScaleI 得来
    const float HALF_W = 7f;     // 半角步进

    // ------------------------------------------------------------------ 数据
    class Line
    {
        public string   text;       // 完整行（含前缀）
        public ChatType type;
        public int      prefixLen;  // 前缀字符数，0 = 无前缀
        public float    textWidth = -1f;   // 逻辑 px，首次绘制时用 GUIStyle.CalcSize 算
        public float    bgStart    = -1f;  // 逻辑 px，相对 TEXT_X（金底起点）
    }

    readonly List<Line> _lines = new List<Line>();
    int    _start;                  // 顶部可见行索引 0..maxStart
    string _input = "";
    bool   _justSent;               // 发送后吞掉一帧 TextField 回吐的旧串

    TouchScreenKeyboard _kb;
    bool   _kbLogged;

    Font      _font;
    Texture2D _goldTex, _sbTex;
    GUIStyle  _style, _inputStyle;

    bool _wantFocus, _wantBlur;
    int  _focusFrames, _focusRetry;
    bool _hadFocus;
    bool _firstDrawLogged;
    bool _kbPrevActive;
    float _kbOpenAt;               // 本次唤起键盘的时刻（真值探针的宽限期起点）
    int  _kbHiddenProbes;          // 连续"active=true 但可见高=0"的帧数（防弹出动画误判）
    bool _suppressAutoOpen;        // 系统侧收键盘后置位：禁止程序自己把键盘抢回来，等用户再点

    // 键盘遮挡探针（与 P0IIme 同口径：本机 adjustNothing ⇒ Screen.height 恒 1140，推不出键盘）
    int  _kbProbeFrame = -1;
    int  _kbHcache;
    bool _kbProbeBroken;
    int  _kbProbeMode;             // 0=未探测 1=WindowInsets(API30+，权威) 2=可见区域差(旧法)
    AndroidJavaObject _act;   // 仅安卓用（非安卓不调用探针，静态类型引用不会有运行时开销）

    // 滚轮去重（OnGUI 一帧会被调用多次，不同 event type）
    int _wheelFrame = -1;

    // 拖拽滚动
    bool  _dragging;
    float _dragLastY;
    float _dragAccum;              // 拖拽位移余量（单帧位移常小于一行，必须累加才滚得动）
    Vector2 _pressPos;
    bool  _pressMoved;

    string _lastEvent = "";
    P0HRuntime _run;
    int _pumpedBase = -1;           // 已消费到的 EventsEmitted 基线（仅用于日志/自检）

    /// <summary>发送回调（宿主接线到 OnChatSend）。</summary>
    public System.Action<string> OnSend;

    /// <summary>
    /// 传送回调（宿主接线到 P0BMapWalk 的真实格坐标/相机刷新）。
    /// P0HChat 只拿到 _run（实体层），拿不到贴图层的 px/py/path/burst，
    /// 所以 @MOVE 必须由宿主执行真正的位移，这里只负责解析与文案。
    /// </summary>
    /// <summary>返回 false 表示目标格不可站（宿主按真 .map 的 bit15 判定）。</summary>
    public System.Func<int, int, bool> TeleportTo;

    /// <summary>
    /// 提示层（P0HNotify）。★ 原版的系统提示 / 伤害飘字**不走聊天框**
    /// （DrawScrn.pas:1374-1432 是独立绘制通道），Pump 在这里做分流：
    ///   升级 / 金币 / 施法失败 / 拾取 → 左上绿；战斗流水 → 只画飘字，聊天框一条都不加。
    /// 允许为 null（此时退回旧行为：全部上聊天框），但宿主必须接线。
    /// </summary>
    public P0HNotify Notify;

    public int  LineCount { get { return _lines.Count; } }
    public int  ScrollStart { get { return _start; } }
    public int  MaxStart { get { return Mathf.Max(0, _lines.Count - ROWS); } }
    public string InputText { get { return _input; } }

    /// <summary>
    /// 组内矩形。**非静态**：键盘弹起时整体上抬 _liftLogic（逻辑 px）。
    /// ★ 上抬是必需的 —— 本机键盘上沿在设备 y≈473，聊天区原本在 876..1140 会被整块盖死，
    ///   看不到自己打的字。抬起来后输入条正好落在键盘上沿之上。
    /// </summary>
    Rect GR(float lx, float ly, float lw, float lh)
    {
        return P0BMapWalk.GroupRectForLogic(lx, ly - _liftLogic, lw, lh);
    }

    /// <summary>键盘把聊天区整体上抬的逻辑 px（0 = 键盘收起）。</summary>
    float _liftLogic;

    // ------------------------------------------------------------------ 生命周期
    public void Init(P0HRuntime run)
    {
        _run = run;
        _lastEvent = (run != null) ? run.LastEvent : "";
        // ★ 2026-10-26：这里**不再清事件队列**。聊天框已不再是队列的消费者
        //   （队列由 P0HNotify.Pump 独家排空并按原版通道分流），基线清理跟着消费者走，
        //   见 P0HNotify.Init。此处若继续 Clear，会把提示层还没接手的事件悄悄吃掉。
    }

    /// <summary>
    /// 冷启动：**空框**。
    /// ★ 1.76 原版（WEMADE 2003 客户端）进游戏时 ChatStrs 是空表，一条预置行都没有；
    ///   旧版这里塞的 9 行是从参考截图 OCR 抄出来的假聊天，属于"看起来像"而不是"真联动"，
    ///   2026-10-26 按用户要求整段删除。现在框里出现的每一行都必须是真实发生过的事件。
    /// </summary>
    public void Seed()
    {
        _lines.Clear();
        _start = 0;
        Debug.Log("[P0CHAT] Seed 完成 行数=0（1.76 原版冷启动即空框，不预置任何假记录）");
    }

    /// <summary>
    /// 预置行直接入表，**不折行** —— 参考帧就是 9 行，一折行就变 10+ 行、颜色与金底逐行对不上。
    /// 超出 TEXT_W 的部分由 GUIStyle.clipping=Clip 裁掉（原版同样会裁）。
    /// </summary>
    void Add(Line l)
    {
        _lines.Add(l);
    }

    /// <summary>
    /// 按逻辑宽度估算折行（纯算术，不依赖 GUIStyle ⇒ 可在 Awake/Seed 阶段调用）。
    /// 全角 14 px / 半角 7 px；整除不进位，保证不出现"半行空"。
    /// 参考图 9 行都在 26 个全角字以内 ⇒ Seed 内容不会被折开，与参考帧 1:1。
    /// </summary>
    static List<string> WrapByEstimate(string s, int prefixLen)
    {
        var outp = new List<string>();
        if (string.IsNullOrEmpty(s)) { outp.Add(""); return outp; }

        int i = 0;
        while (i < s.Length)
        {
            float w = 0f;
            int j = i;
            while (j < s.Length)
            {
                float cw = IsFullWidth(s[j]) ? FULL_W : HALF_W;
                if (w + cw > WRAP_W && j > i) break;
                w += cw;
                j++;
            }
            outp.Add(s.Substring(i, j - i));
            i = j;
        }
        return outp;
    }

    static bool IsFullWidth(char c)
    {
        return c >= 0x1100 &&
               (c <= 0x115F || c == 0x2329 || c == 0x232A ||
                (c >= 0x2E80 && c <= 0xA4CF) ||
                (c >= 0xAC00 && c <= 0xD7A3) ||
                (c >= 0xF900 && c <= 0xFAFF) ||
                (c >= 0xFE30 && c <= 0xFE6F) ||
                (c >= 0xFF00 && c <= 0xFF60) ||
                (c >= 0xFFE0 && c <= 0xFFE6));
    }

    // ------------------------------------------------------------------ 追加 / 滚动
    /// <summary>
    /// 追加一条。★ 只有"追加前已经贴底"才贴底 ⇒ 用户翻旧账时新消息不会把视图拽回去。
    /// </summary>
    public void Append(string text, ChatType type)
    {
        Append(text, type, 0);
    }

    public void Append(string text, ChatType type, int prefixLen)
    {
        if (text == null) text = "";
        bool stick = (_start >= MaxStart);

        var segs = WrapByEstimate(text, prefixLen);
        for (int k = 0; k < segs.Count; k++)
        {
            var n = new Line
            {
                text = segs[k],
                type = type,
                // 前缀只落在第一段，且只在没被折行时才算（折行会破坏前缀→底色的对应）
                prefixLen = (k == 0 && segs.Count == 1) ? prefixLen : 0
            };
            if (k == 0 && segs.Count == 1 && prefixLen > 0)
                n.textWidth = -1f;   // 绘制时现算 bgStart
            _lines.Add(n);
        }

        // ★★ 2026-10-26：裁头必须保持"用户看的那几行不动"。
        //   旧写法 `if (_start>0) _start--` 只在贴底时正确；日志实测出现过
        //   "行数=200 start=0/191"——用户翻着旧账，被裁头一路把 start 顶到 0，
        //   表现就是"聊着聊着突然跳回最上面"。现在按删除条数等量回退 _start。
        int removed = 0;
        while (_lines.Count > MAX_LINES)
        {
            _lines.RemoveAt(0);
            removed++;
        }
        if (removed > 0)
            _start = stick ? MaxStart : Mathf.Max(0, _start - removed);

        if (stick) _start = MaxStart;
        _start = Mathf.Clamp(_start, 0, MaxStart);

        // 验收用日志（不上屏）：每条追加都留痕，含类型与最终文本。
        //   前缀语义 / 底色 / 静默丢弃只能靠日志核对，截图看不出"该不该出这一行"。
        if (_lines.Count > 0)
        {
            var tail2 = _lines[_lines.Count - 1];
            Debug.Log("[P0CHAT] 追加 type=" + type + " 行数=" + _lines.Count +
                      " start=" + _start + "/" + MaxStart + " text=" + tail2.text);
        }
    }

    /// <summary>玩家自己说的话：原版格式「角色名: 内容」。</summary>
    public void AppendPlayer(string name, string text)
    {
        if (string.IsNullOrEmpty(name)) name = "无名";
        Append(name + ": " + (text == null ? "" : text), ChatType.Normal);
    }

    void ScrollBy(int delta)
    {
        int before = _start;
        _start = Mathf.Clamp(_start + delta, 0, MaxStart);
        if (_start != before)
            Debug.Log("[P0CHAT] 滚动 start=" + _start + "/" + MaxStart);
    }

    // ------------------------------------------------------------------ 真联动
    /// <summary>
    /// 事件队列的转发口。**唯一消费者是 P0HNotify.Pump**（它按原版通道分流）。
    /// ★ 2026-10-26：这里不再自带"全部塞进聊天框"的兜底分支，也不再有 LastEvent 兜底。
    ///   原因：原版战斗流水（打中/打空/被挡/被打）**根本不进聊天**（DrawScrn.pas:1196-1261
    ///   只画头顶飘字），系统提示走独立通道（DrawScrn.pas:1374-1432）。
    ///   留着兜底 = 只要宿主漏接一次 Notify，聊天框立刻又开始喷「打 鹿 -4」。
    /// </summary>
    public void Pump(P0HRuntime run)
    {
        if (run == null) return;
        if (Notify != null) { Notify.Pump(run); _lastEvent = run.LastEvent ?? ""; _pumpedBase = run.EventsEmitted; return; }
        Debug.LogError("[P0CHAT] Notify 未接线 ⇒ 事件无法分流（宿主必须 new P0HNotify 并赋给 Notify）");
    }

    // ------------------------------------------------------------------ 输入
    /// <summary>宿主「聊天」按钮 / 触摸聊天区时调用：聚焦输入条并弹系统输入法。</summary>
    public void FocusInput()
    {
        // ★★ 2026-10-25 真机 bug：键盘被系统收起后，GUI 控件往往**仍保持焦点**，
        //   _hadFocus 一直是 true ⇒ DrawInput 的「焦点上升沿」再也不会触发，
        //   表现就是「第二次点输入条不弹键盘」。所以聚焦请求必须**强制重置**状态机。
        CloseKeyboard();
        _hadFocus = false;
        _wantBlur = false;
        _justSent = false;
        _kbPrevActive = false;
        _kbHiddenProbes = 0;
        _suppressAutoOpen = false;   // 用户主动点了输入条 ⇒ 解除"别抢键盘"抑制
        _wantFocus = true;
        _focusFrames = 0;
        _focusRetry = 0;
        Debug.Log("[P0CHAT] 请求聚焦输入条 (强制重置焦点态)");
    }

    void Send()
    {
        string t = (_input == null) ? "" : _input.Trim();
        if (t.Length == 0) { CloseKeyboard(); return; }

        _input = "";
        _justSent = true;
        CloseKeyboard();
        _wantBlur = true;

        // ★★ 2026-10-25：按原版 ChatDialog + PlayerObject.ProcessChat 的真实前缀语义分发。
        //   源码：Client/MirScenes/Dialogs/MainDialogs.cs L716-760（回车发送）、L1096-1123（前缀入口）
        //         Server/MirObjects/PlayerObject.cs L1962-2158（服务端按首字符路由并改写文本）
        //   ⚠ 本地显示**只由这里负责**，宿主回调不再 AppendPlayer，否则会出双行。
        Route(t);

        // 回调宿主：只做日志 / 事件（不再负责上屏）
        try { if (OnSend != null) OnSend(t); }
        catch (System.Exception ex) { Debug.LogError("[P0CHAT] OnSend 异常: " + ex.Message); }
        Debug.Log("[P0CHAT] 发送 len=" + t.Length + " text=" + t);
    }

    /// <summary>
    /// 原版聊天前缀路由（单机复刻：没有服务器，但**判定与文案照源码**）。
    ///   普通文本      → "名字: 内容"            Normal(白底黑字)   ← 服务端 L2104-2106 附近的默认分支
    ///   !内容         → "(!)名字:内容"          Shout(黄底黑字)    ← PlayerObject L2050-2113
    ///   /名字 内容    → "/名字 内容" 私聊发出     WhisperOut(矢车菊蓝) ← L1962-1999
    ///   @命令         → 客户端本地命令           ← MainDialogs L726-734 / L2152+
    ///   !!内容        → 组队：无队伍 ⇒ 静默丢弃   ← L2001-2003 `if (GroupMembers == null) return;`
    ///   !~内容        → 行会：无行会 ⇒ 静默丢弃   ← L2014-2016
    ///   ★ 1.76 没有「师徒」「夫妻」系统（那是后续资料片的 !# 和 :) 前缀）⇒ 不再解析，
    ///     落到普通说话分支（原版 1.76 客户端也不认识这两个前缀）。
    /// </summary>
    void Route(string t)
    {
        string name = (_run != null && !string.IsNullOrEmpty(_run.PlayerName)) ? _run.PlayerName : "无名";

        // ---- 组队 / 行会：1.76 有这两个系统，单机没有对应关系 ⇒ 原版就是静默 return ----
        if (t.StartsWith("!!") || t.StartsWith("!~"))
        {
            Debug.Log("[P0CHAT] 前缀无接收方，按原版静默丢弃: " + t.Substring(0, 2));
            return;
        }

        // ---- 私聊 ----
        if (t.StartsWith("/"))
        {
            string body = t.Substring(1);
            int sp = body.IndexOf(' ');
            string who = (sp > 0) ? body.Substring(0, sp) : body;
            if (string.IsNullOrEmpty(who)) { Debug.Log("[P0CHAT] 私聊缺目标名，丢弃"); return; }
            _lastPM = "/" + who;
            Append("/" + body, ChatType.WhisperOut);          // 原版 L1998：本地显示 "/{message}"
            // 原版 L1980：找不到目标 ⇒ CouldNotFind = "无法找到 {0}。"（Server.MirForms
            //   Localization\Chinese.json L72 的官方中文原文）。单机世界里没有第二个玩家。
            Append("无法找到 " + who + "。", ChatType.System);
            return;
        }

        // ---- GM 公告：@!内容 → "(*){名字}:{内容}"，Announcement(蓝底白字) ----
        //   源码 Server/MirObjects/PlayerObject.cs L2140-2150：`if (!IsGM) return;` 之后
        //   改写为 `(*)Name:msg` 并全服广播。单机没有服务器，本地玩家就是这台机的管理员，
        //   所以这里按「本地 GM」放行 —— 这是单机适配，不是伪造服务器。
        if (t.StartsWith("@!"))
        {
            string msg = t.Substring(2);
            if (msg.Length == 0) { Debug.Log("[P0CHAT] 公告内容为空，丢弃"); return; }
            Append("(*)" + name + ":" + msg, ChatType.Announcement);
            return;
        }

        // ---- 客户端本地命令（原版就在客户端处理，不发服务器）----
        if (t.StartsWith("@"))
        {
            string body = t.Substring(1).Trim();
            // 拆 token：原版服务端就是 `parts = data.Message.Split(' ')`（PlayerObject.cs L1965 附近），
            // 只看 parts[0] 当命令名；这里同样只认空格分隔，不猜别的分隔符。
            var parts = body.Split(new char[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            string cmd = (parts.Length > 0) ? parts[0].ToUpper() : "";

            // ---- 客户端本地选项（不需要服务器）----
            if (cmd == "LEVELEFFECT") { _optLevelEffect = !_optLevelEffect; Append("升级特效: " + (_optLevelEffect ? "开" : "关"), ChatType.Hint); return; }
            if (cmd == "TARGETDEAD")  { _optTargetDead  = !_optTargetDead;  Append("目标死亡提示: " + (_optTargetDead ? "开" : "关"), ChatType.Hint); return; }

            // ================= 真 GM 命令（单机：本地玩家就是管理员）=================
            //   全部照 Server/MirObjects/PlayerObject.cs 的命令分支实现，文案取
            //   Server.MirForms/Localization/Chinese.json 的官方中文原文。

            // @LEVEL [n] —— L2296-2309：设置等级 + LevelUp()，文案
            //   "{LevelUp} {old} -> {new}."（LevelUp = 恭喜！你升级了，生命值和魔法值已恢复。）
            if (cmd == "LEVEL")
            {
                if (_run == null) { Append("无法提升玩家等级。", ChatType.System); return; }
                int lv = _run.PlayerLevel;
                if (parts.Length >= 2 && !int.TryParse(parts[1], out lv))
                {
                    Append("无法提升玩家等级。", ChatType.System);   // CouldNotLevelPlayer
                    return;
                }
                if (lv <= 0) { Append("无法提升玩家等级。", ChatType.System); return; }
                _run.AdminSetLevel(lv);   // 内部 EmitEvent("★升级！...") ⇒ 聊天框按 LevelUp 上屏
                return;
            }

            // @GIVEGOLD n —— L3090-3095：GainGold(count)；成功由服务端广播
            //   "GM {gm} 已给予玩家 {player} 金币 {count}"（PlayerGivenGoldByGM）
            if (cmd == "GIVEGOLD")
            {
                if (_run == null) return;
                int n;
                if (parts.Length < 2 || !int.TryParse(parts[1], out n) || n <= 0)
                {
                    Append("单机版用法: @GIVEGOLD 数量", ChatType.System);
                    return;
                }
                _run.AdminGiveGold(n);
                Append("GM " + name + " 已给予玩家 " + name + " 金币 " + n, ChatType.Announcement);
                return;
            }

            // @MOVE [x y] —— L2845-2870：给了坐标就 Teleport(CurrentMap, (x,y))；
            //   没给坐标（或解析失败）就 TeleportRandom(200, 0)。单机只做坐标传送。
            if (cmd == "MOVE")
            {
                int x, y;
                if (parts.Length < 3 || !int.TryParse(parts[1], out x) || !int.TryParse(parts[2], out y))
                {
                    Append("单机版用法: @MOVE X Y", ChatType.System);
                    return;
                }
                if (TeleportTo == null)
                {
                    Append("单机版无法传送（宿主未接线）", ChatType.System);
                    return;
                }
                if (!TeleportTo(x, y))
                {
                    // 原版传送失败只有两种：地图禁定位 / 冷却中（10 秒）。
                    //   单机没有冷却，失败只可能是目标格被 .map bit15 判为阻挡。
                    Append("无法传送到 (" + x + "," + y + ")：那块地被挡住了", ChatType.System);
                    return;
                }
                return;
            }

            // @DIE —— L3454-3457：Die()。旁人看到的是
            //   KilledByGM = "{0} 被管理员（GM）彻底击杀：{1}。"
            if (cmd == "DIE")
            {
                if (_run == null) return;
                _run.AdminDie();   // 内部 EmitEvent("★玩家阵亡...") ⇒ System 红字
                return;
            }

            // 其余 @ 命令在单机没有服务器可以执行 —— 说清楚，不假装成功。
            Append("单机版不支持命令: @" + t.Substring(1), ChatType.System);
            return;
        }

        // ---- 喊话：!内容 → "(!)名字:内容"，Shout(黄底黑字) ----
        //   ⚠ 下面三个门槛取自 Crystal 服务端 PlayerObject.cs L2050-2113（2020 版）：
        //     ① 冷却 10 秒  ② 等级 ≥ 8  ③ 成功后 ShoutTime = now + 10000
        //   **未从 2003 原版 1.76 客户端/服务端证实**（1.76 时代喊话门槛未找到源码原文），
        //     本轮按现有值保留，仅为单机防刷屏；不当作"已验收的 1.76 行为"。
        if (t.StartsWith("!"))
        {
            string msg = t.Substring(1);
            if (msg.Length == 0) { Debug.Log("[P0CHAT] 喊话内容为空，丢弃"); return; }

            float now = Time.realtimeSinceStartup;
            // 两句提示都用 Server.MirForms\Localization\Chinese.json 的官方中文原文：
            //   L76 CannotShoutForSeconds = "你还需要等待 {0} 秒才能喊话。"
            //   L77 RequiredLevel8ForShout  = "你的等级需要达到 8 级才能使用喊话。"
            if (now < _shoutReadyAt)
            {
                int left = Mathf.CeilToInt(_shoutReadyAt - now);
                Append("你还需要等待 " + left + " 秒才能喊话。", ChatType.System);
                return;
            }

            int lv = (_run != null) ? _run.PlayerLevel : 1;
            if (lv < 8)
            {
                Append("你的等级需要达到 8 级才能使用喊话。", ChatType.System);
                return;
            }

            _shoutReadyAt = now + 10f;
            Append("(!)" + name + ":" + msg, ChatType.Shout);
            return;
        }

        // ---- 普通说话：默认分支 ----
        Append(name + ": " + t, ChatType.Normal);
    }

    string _lastPM = "";
    bool _optLevelEffect = true;
    bool _optTargetDead = true;
    float _shoutReadyAt;          // 喊话冷却到点时刻（Time.realtimeSinceStartup 口径）

    void OpenKeyboard()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // ★★ 2026-10-25：判"要不要重新唤起"必须用屏幕真值（KbVisibleOnScreen），
        //   不能用 `IsKbActive()`。系统侧收起键盘后 Unity 的 active 仍为 true，
        //   老写法会把第二次点击直接 return 掉，用户看到的就是"点了没反应"。
        if (KbVisibleOnScreen()) return;
        if (_kb != null)
        {
            // 残留引用（active=true 但已不可见）⇒ 清掉再开，避免拿到同一个僵尸对象。
            try { _kb.active = false; } catch (System.Exception) { }
            _kb = null;
        }
        try
        {
            if (!TouchScreenKeyboard.isSupported)
            {
                if (!_kbLogged)
                {
                    _kbLogged = true;
                    Debug.Log("[P0CHAT] TouchScreenKeyboard.isSupported=false，退回 GUI.TextField 原生输入");
                }
                return;
            }
            // 与 P0IIme 已验证通路一致：hideInput=true 消掉键盘上方那块不透明 EditText 浮层。
            try { TouchScreenKeyboard.hideInput = true; } catch (System.Exception) { }

            // 8 参：text / 类型 / 自动纠错(false，中文会被纠成乱码) / 多行 / 密码 / 警告 / 占位 / 上限
            _kb = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default,
                                           false, false, false, false, "", MAX_INPUT);
            if (_kb == null)
            {
                Debug.LogWarning("[P0CHAT] TouchScreenKeyboard.Open 返回 null —— 只剩 TextField 一条路径");
                return;
            }
            if (_input != null && _input.Length > 0)
            {
                try { _kb.text = _input; } catch (System.Exception) { }
            }
            _kbOpenAt = Time.realtimeSinceStartup;
            _kbHiddenProbes = 0;
            _suppressAutoOpen = false;   // 新唤起 ⇒ 重新开始真值探测窗口
            Debug.Log("[P0CHAT] 已唤起系统输入法 type=" + _kb.GetType().Name +
                      " limit=" + MAX_INPUT + " 基线=" + (_input == null ? 0 : _input.Length));
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[P0CHAT] 唤起键盘异常（已吞）: " + ex.GetType().Name + ": " + ex.Message);
            _kb = null;
        }
#else
        if (!_kbLogged)
        {
            _kbLogged = true;
            Debug.Log("[P0CHAT] 非 Android 平台（" + Application.platform + "）跳过 TouchScreenKeyboard");
        }
#endif
    }

    void CloseKeyboard()
    {
        try
        {
            if (_kb != null && _kb.active) { _kb.active = false; Debug.Log("[P0CHAT] 收起系统输入法"); }
        }
        catch (System.Exception) { }
        _kb = null;
    }

    bool IsKbActive()
    {
        try { return _kb != null && _kb.active; }
        catch (System.Exception) { return false; }
    }

    /// <summary>
    /// 键盘**真的**露在屏幕上吗（系统真值，不是 Unity 的 active）。
    /// ★★ 2026-10-25 真机实证：按系统返回键/下滑收键盘后，Unity 的
    ///    `TouchScreenKeyboard.active` 仍然是 true（它只在 Done/Canceled 时才翻 false）。
    ///    只信 active ⇒ 聊天框永远以为"键盘还在"，第二次点输入条被
    ///    `OpenKeyboard()` 开头的 `if (IsKbActive()) return;` 直接吞掉，零日志零反应。
    ///    真值只有一条：可见区域底边与真实屏高之差 = 键盘高度（>0 才叫露着）。
    ///    探针拿不到（非安卓/异常）时退回 Unity 口径，不制造新的误判。
    /// </summary>
    bool KbVisibleOnScreen()
    {
        if (_kb == null) return false;
        int h = ProbeImeHeightPx();
        if (h < 0) return IsKbActive();      // 探针不可用 ⇒ 退回 Unity 口径
        // ★ 探针不可信（非 API30+ 的旧算法）时，只信 Unity 的 active：
        //   旧算法在 Gboard 下实测会误报 0，绝不能让它否掉一个活着的键盘。
        if (_kbProbeMode != 1) return IsKbActive();
        return h > 0;
    }

    // ------------------------------------------------------------------ 命中测试
    /// <summary>
    /// 该屏幕点是否归聊天框所有（走绝对式：Event.mousePosition 在组外是屏幕坐标）。
    /// 与 P0BMapWalk.ImeOwnsPoint 的既有规则同构，保证下层 HUD 按钮让位。
    /// </summary>
    public bool BlocksPointer(Vector2 p)
    {
        Rect abs = P0BMapWalk.CanvasRectForLogic(208f, 468f, 390f, 125f);
        abs.y -= KbLiftDevice;
        return abs.Contains(p);
    }

    /// <summary>当前键盘把内容整体上抬了多少设备 px（0 = 键盘收起）。</summary>
    float KbLiftDevice { get { return _liftLogic * P0BMapWalk.ScaleI; } }

    /// <summary>当前键盘把内容整体上抬了多少设备 px（0 = 键盘收起）。</summary>
    void UpdateLift()
    {
        _liftLogic = 0f;
        if (!IsKbActive()) { _kbPrevActive = false; return; }

        int kbH = ProbeImeHeightPx();
        if (kbH <= 0) return;                      // 探针不可用 ⇒ 不退化成"瞎抬"

        // 键盘上沿（设备 px）
        float kbTop = Screen.height - kbH;
        // 输入条底边（设备 px，未抬）= -CROP_TOP + (IN_Y + IN_H) * SCALE
        //   CROP_TOP 由 BeginGroup 施加，组内不需要重复计算 ⇒ 这里用相对换算：
        //   组内 y = ly * SCALE，落到设备 = 组内 y - CROP_TOP。
        float inBottomDev = -P0BMapWalk.CropTopDevice + (IN_Y + IN_H) * P0BMapWalk.ScaleI;
        float need = inBottomDev - (kbTop - 8f);   // 键盘上沿再留 8px 余量
        if (need <= 0f) return;

        _liftLogic = Mathf.Ceil(need / P0BMapWalk.ScaleI);

        // 不许把聊天块顶出屏幕顶部
        float blockTopDev = -P0BMapWalk.CropTopDevice + TEXT_Y * P0BMapWalk.ScaleI;
        float maxLift = (blockTopDev - 4f) / P0BMapWalk.ScaleI;
        if (_liftLogic > maxLift) _liftLogic = Mathf.Max(0f, maxLift);

        if (!_kbPrevActive)
        {
            _kbPrevActive = true;
            Debug.Log("[P0CHAT] 键盘避让 键盘高=" + kbH + " 上沿=" + Mathf.RoundToInt(kbTop) +
                      " 上抬逻辑=" + Mathf.RoundToInt(_liftLogic) +
                      " 设备=" + Mathf.RoundToInt(KbLiftDevice));
        }
    }

    /// <summary>
    /// 安卓真实键盘高度（设备 px）。0 = 收起；&lt;0 = 探针不可用。
    /// 口径与 P0IIme 一致：getRealSize() − getWindowVisibleDisplayFrame().bottom。
    /// 本机 Manifest 是 adjustNothing（Screen.height 恒 1140，靠 Screen.height 推不出键盘）。
    /// </summary>
    int ProbeImeHeightPx()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_kbProbeBroken) return -1;
        if (_kbProbeFrame >= 0 && Time.frameCount - _kbProbeFrame < 6) return _kbHcache;
        _kbProbeFrame = Time.frameCount;

        if (_act == null)
        {
            try
            {
                var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                _act = player.GetStatic<AndroidJavaObject>("currentActivity");
            }
            catch (System.Exception) { _act = null; }
        }
        if (_act == null) { _kbProbeBroken = true; return -1; }

        // ---- 首选：WindowInsets.Type.ime()（API 30+）。这是系统给的权威值，
        //      不受 adjustNothing / 输入法自绘高度 / 手势条影响，Gboard/ADB 键盘都准。
        if (_kbProbeMode == 0)
        {
            int sdk = 0;
            try { sdk = new AndroidJavaClass("android.os.Build$VERSION").GetStatic<int>("SDK_INT"); }
            catch (System.Exception) { sdk = 0; }
            _kbProbeMode = (sdk >= 30) ? 1 : 2;
        }

        AndroidJavaObject wm = null, deco = null, disp = null, pt = null, frame = null, wi = null, ins = null;
        try
        {
            wm = _act.Call<AndroidJavaObject>("getWindowManager");
            deco = _act.Call<AndroidJavaObject>("getWindow").Call<AndroidJavaObject>("getDecorView");

            if (_kbProbeMode == 1)
            {
                wi = deco.Call<AndroidJavaObject>("getRootWindowInsets");
                if (wi != null)
                {
                    int imeType = new AndroidJavaClass("android.view.WindowInsets$Type").CallStatic<int>("ime");
                    bool vis = wi.Call<bool>("isVisible", imeType);
                    int hh = 0;
                    if (vis)
                    {
                        ins = wi.Call<AndroidJavaObject>("getInsets", imeType);
                        if (ins != null) hh = ins.Get<int>("bottom");
                    }
                    if (hh < 0) hh = 0;
                    _kbHcache = hh;
                    return hh;
                }
                // insets 拿不到（极少数窗口状态）⇒ 本帧退回旧法，下次再试新法
            }

            disp = wm.Call<AndroidJavaObject>("getDefaultDisplay");
            pt = new AndroidJavaObject("android.graphics.Point");
            disp.Call("getRealSize", pt);
            int realH = pt.Get<int>("y");
            frame = new AndroidJavaObject("android.graphics.Rect");
            deco.Call("getWindowVisibleDisplayFrame", frame);
            int visBottom = frame.Get<int>("bottom");
            int h = realH - visBottom;
            if (h < realH * 0.12f) h = 0;   // 状态栏/导航条噪声
            if (h < 0) h = 0;
            _kbHcache = h;
            return h;
        }
        catch (System.Exception)
        {
            _kbProbeBroken = true;
            return -1;
        }
        finally
        {
            if (ins != null) ins.Dispose();
            if (wi != null) wi.Dispose();
            if (frame != null) frame.Dispose();
            if (pt != null) pt.Dispose();
            if (disp != null) disp.Dispose();
            if (deco != null) deco.Dispose();
            if (wm != null) wm.Dispose();
        }
#else
        return -1;
#endif
    }

    // ------------------------------------------------------------------ 绘制
    public void DrawAll(P0HRuntime run)
    {
        EnsureStyle();
        if (_style == null || _font == null) return;   // 字体都没起来就绝不画方块

        Event e = Event.current;

        UpdateLift();                                  // 键盘避让：本帧整体上抬量
        GUI.color = Color.white;

        // ★ 键盘弹起时必须自己补一块白底：原版的白色底板是烤在 0039 底图里的，
        //   底图不跟着抬，光抬文字会变成白字浮在地图上。抬多少就补多大一块纯白。
        if (_liftLogic > 0f)
        {
            GUI.color = Color.white;
            // 键盘上抬时白板要跟着补满（覆盖 468..580 整块聊天区，左右各留 2 px 边）
            GUI.DrawTexture(GR(206f, 468f, 391f, 113f), Texture2D.whiteTexture);
        }
        float fs = 14f * P0BMapWalk.ScaleI;            // 设备字号（校准值，见文件头）

        HandleScrollAndDrag(e);

        int n = _lines.Count;
        int maxStart = MaxStart;
        _start = Mathf.Clamp(_start, 0, maxStart);

        // ---- 1) 逐行：底色 → 文字 ----
        // ★ 原版 ChatDialog.ReceiveChat（MainDialogs.cs L781-852）是「底色 + 字色」成对出现的：
        //   公告/线路=蓝底白字，喊话=黄底黑字，Shout2=绿底白字，Shout3=紫底白字，
        //   系统=红底白字，System2=暗红底白字，升级=淡紫底蓝字，
        //   普通/私聊/行会/组队/师徒=白底彩字，关系=透明底粉字。
        //   底色只铺到文字实际宽度（原版 MirLabel.AutoSize），不铺满整行。
        for (int i = 0; i < ROWS; i++)
        {
            int idx = _start + i;
            if (idx >= n) break;
            Line ln = _lines[idx];
            float y = TEXT_Y + i * LINE_H;

            Color bg = BackColorOf(ln.type);
            bool needBg = bg.a > 0.001f && !(bg.r > 0.999f && bg.g > 0.999f && bg.b > 0.999f);

            if (needBg || ln.prefixLen > 0)
            {
                if (ln.textWidth < 0f)
                {
                    ln.textWidth = _style.CalcSize(new GUIContent(ln.text)).x / P0BMapWalk.ScaleI;
                    if (ln.prefixLen > 0)
                        ln.bgStart = _style.CalcSize(
                            new GUIContent(ln.text.Substring(0, ln.prefixLen))).x / P0BMapWalk.ScaleI;
                    else
                        ln.bgStart = 0f;
                }
            }

            if (needBg)
            {
                float bx = TEXT_X + Mathf.Max(0f, ln.bgStart);
                float bw = Mathf.Max(0f, ln.textWidth - Mathf.Max(0f, ln.bgStart));
                if (bw > 0.5f)
                {
                    if (ln.type == ChatType.Shout && _goldTex != null)
                    {
                        GUI.color = Color.white;   // 黄底保留原版金/石纹贴图（参考帧已验收）
                        GUI.DrawTexture(GR(bx, y, bw, LINE_H), _goldTex, ScaleMode.StretchToFill);
                    }
                    else
                    {
                        GUI.color = bg;
                        GUI.DrawTexture(GR(bx, y, bw, LINE_H), Texture2D.whiteTexture);
                    }
                }
            }

            if (ln.prefixLen > 0 && ln.bgStart > 0.5f)
            {
                // 前缀（黑，无底）+ 后半段（喊话=深棕，其余按类型字色）
                GUI.color = Color.white;
                _style.normal.textColor = Color.black;
                GUI.Label(GR(TEXT_X, y, ln.bgStart + 1f, LINE_H),
                          ln.text.Substring(0, ln.prefixLen), _style);

                _style.normal.textColor = (ln.type == ChatType.Shout)
                                        ? new Color(0.30f, 0.10f, 0.02f, 1f)
                                        : ColorOf(ln.type);
                GUI.Label(GR(TEXT_X + ln.bgStart, y, TEXT_W - ln.bgStart, LINE_H),
                          ln.text.Substring(ln.prefixLen), _style);
            }
            else
            {
                GUI.color = Color.white;
                _style.normal.textColor = ColorOf(ln.type);
                GUI.Label(GR(TEXT_X, y, TEXT_W, LINE_H), ln.text, _style);
            }
        }

        GUI.color = Color.white;

        // ---- 2) 滚动条（底板没烤石纹，必须画）----
        DrawScrollBar(n, maxStart);

        // ---- 3) 输入条 ----
        DrawInput(e);

        if (!_firstDrawLogged)
        {
            _firstDrawLogged = true;
            Debug.Log("[P0CHAT] 首次绘制 lines=" + n + " start=" + _start + "/" + maxStart +
                      " font=" + (_font != null ? _font.name : "null") + " fontSize=" + (int)fs +
                      " 输入条=" + IN_X + "," + IN_Y + "," + IN_W + "x" + IN_H);
        }
    }

    /// <summary>
    /// 字色 —— 逐项对应 .NET System.Drawing 原版命名色的 RGB 真值（MainDialogs.cs L781-852）。
    /// ★ 用 0-255 除以 255 的精确值，不用近似色名，否则和原版截图对不上。
    /// </summary>
    static Color ColorOf(ChatType t)
    {
        switch (t)
        {
            case ChatType.Normal:        return new Color(0f, 0f, 0f, 1f);              // Black
            case ChatType.Hint:          return new Color(0f, 100 / 255f, 0f, 1f);      // DarkGreen (0,100,0)
            case ChatType.Announcement:  return new Color(1f, 1f, 1f, 1f);              // White on Blue
            case ChatType.LineMessage:   return new Color(1f, 1f, 1f, 1f);              // White on Blue
            case ChatType.Shout:         return new Color(0.30f, 0.10f, 0.02f, 1f);     // 金底上的近黑（参考帧实测）
            case ChatType.Shout2:        return new Color(1f, 1f, 1f, 1f);              // White on Green
            case ChatType.Shout3:        return new Color(1f, 1f, 1f, 1f);              // White on Purple
            case ChatType.System:        return new Color(1f, 1f, 1f, 1f);              // White on Red
            case ChatType.System2:       return new Color(1f, 1f, 1f, 1f);              // White on DarkRed
            case ChatType.Group:         return new Color(165 / 255f, 42 / 255f, 42 / 255f, 1f);    // Brown
            case ChatType.WhisperOut:    return new Color(100 / 255f, 149 / 255f, 237 / 255f, 1f);  // CornflowerBlue
            case ChatType.WhisperIn:     return new Color(0f, 0f, 139 / 255f, 1f);      // DarkBlue
            case ChatType.Guild:         return new Color(0f, 128 / 255f, 0f, 1f);      // Green
            case ChatType.LevelUp:       return new Color(0f, 0f, 1f, 1f);              // Blue
            case ChatType.Relationship:  return new Color(1f, 105 / 255f, 180 / 255f, 1f);  // HotPink
            case ChatType.Mentor:        return new Color(128 / 255f, 0f, 128 / 255f, 1f);  // Purple
            default:                     return Color.black;
        }
    }

    /// <summary>
    /// 底色 —— 与 ColorOf 成对，取自同一段原版 switch。
    /// α=0 表示"透明/不铺底"（关系聊天），白色表示"底板本来就白，不用另外画"。
    /// </summary>
    static Color BackColorOf(ChatType t)
    {
        switch (t)
        {
            case ChatType.Normal:        return Color.white;
            case ChatType.Hint:          return Color.white;
            case ChatType.Announcement:  return new Color(0f, 0f, 1f, 1f);              // Blue
            case ChatType.LineMessage:   return new Color(0f, 0f, 1f, 1f);              // Blue
            case ChatType.Shout:         return new Color(1f, 1f, 0f, 1f);              // Yellow（用金/石纹贴图铺）
            case ChatType.Shout2:        return new Color(0f, 128 / 255f, 0f, 1f);      // Green
            case ChatType.Shout3:        return new Color(128 / 255f, 0f, 128 / 255f, 1f);  // Purple
            case ChatType.System:        return new Color(1f, 0f, 0f, 1f);              // Red
            case ChatType.System2:       return new Color(139 / 255f, 0f, 0f, 1f);      // DarkRed
            case ChatType.Group:         return Color.white;
            case ChatType.WhisperOut:    return Color.white;
            case ChatType.WhisperIn:     return Color.white;
            case ChatType.Guild:         return Color.white;
            case ChatType.LevelUp:       return new Color(225 / 255f, 185 / 255f, 250 / 255f, 1f);  // (225,185,250)
            case ChatType.Relationship:  return new Color(1f, 1f, 1f, 0f);              // Transparent
            case ChatType.Mentor:        return Color.white;
            default:                     return Color.white;
        }
    }

    // =====================================================================
    // 聊天框右侧那根 16x100 竖条 —— **不在这里画**
    //   源码 FState.pas:8883-8884：
    //     d := g_opui.Images[20]; //聊天框 拉伸条
    //     dsurface.Draw(SCREENWIDTH - 207, SCREENHEIGHT - 127, d.ClientRect, d, True)  ⇒ (593, 473)
    //   ★ 关键：源码是 **无条件** 画的，而且画的是静态原图，不是按滚动位置动的滑块。
    //     ⇒ 属主归 P0HHud（静态件单一属主），本层一律不画，避免叠两层。
    //     1.76 原版确实没有可见的动态滚动条，翻页靠滚轮 / PageUp / PageDown。
    //   （_sbTex 保留字段只为兼容旧引用，不再参与绘制。）
    // =====================================================================
    void DrawScrollBar(int n, int maxStart)
    {
        // 空实现 —— 见上：原版这根条是静态原图，由 P0HHud.DrawAll 的 DrawStatic 画一次。
    }

    void DrawInput(Event e)
    {
        // 空输入不画占位提示 —— 原版输入区就是空的（参考帧 y 581..592 纯白，零墨迹）
        if (_inputStyle == null) return;

        // ★★ 2026-10-26 真机根因修复：系统返回键收键盘后，IMGUI 的焦点**仍留在 TextField 上**，
        //   于是"焦点上升沿"永远不再出现（_hadFocus 一直 true），第二次点输入条零反应。
        //   实测日志：连按 3 轮，第 1 次能弹、第 2/3 次连 `请求聚焦输入条` 都不打。
        //   ⇒ 点输入条必须**由命中测试直接发起聚焦请求**，不能依赖焦点变化这条间接通路。
        //   命中区给上下各留 6 逻辑px 容错（手指点不准 12px 高的条很正常）。
        if (e != null && e.type == EventType.MouseDown)
        {
            // ★★★ 2026-10-26 真机根因（这次是真的）：命中矩形单位错了。
            //   e.mousePosition 在 BeginGroup 内是**组内设备坐标**（= 逻辑 * SCALE，SCALE=2），
            //   而这里原来直接拿逻辑值 (208,581,374,12) 当组内矩形比 ⇒ 只有左上 1/4 区域，
            //   实际输入条在组内 (416,1162)-(1164,1186)，手指点上去永远判不中。
            //   历史现象「第一次能弹键盘」其实是点到下方 TextField 自己抢了焦点，不是命中生效。
            //   ⇒ 一律走 GroupRectForLogic 换算（GR 自带键盘上抬与 SCALE），容错给 ±8 逻辑 px。
            Rect hit = GR(IN_X - 8f, IN_Y - 8f, IN_W + 16f, IN_H + 16f);
            if (hit.Contains(e.mousePosition))
            {
                // 键盘不在 ⇒ 强制重置并重新唤起；键盘已经在 ⇒ 只续期聚焦请求，不来回开关闪屏。
                if (!KbVisibleOnScreen() || !IsKbActive()) FocusInput();
                else
                {
                    _wantBlur = false;
                    _suppressAutoOpen = false;
                    _wantFocus = true;
                    _focusFrames = 0;
                    _focusRetry = 0;
                }
                Debug.Log("[P0CHAT] 点中输入条 p=" + e.mousePosition.ToString("F0") +
                          " hit=" + hit + " kb=" + KbVisibleOnScreen());
                e.Use();
            }
        }

        // ---- 焦点维护 ----
        string focused = "";
        try { focused = GUI.GetNameOfFocusedControl(); } catch (System.Exception) { }
        bool hasFocus = (focused == FIELD_CONTROL);

        if (_wantBlur)
        {
            try { GUI.FocusControl(""); } catch (System.Exception) { }
            _wantBlur = false;
            hasFocus = false;
        }

        // ★★ 2026-10-25 真机 bug 修复：原来只有「焦点上升沿」一条路弹键盘。
        //   键盘被系统收起（返回键/发送后）时 GUI 焦点可能仍在控件上，
        //   上升沿永远不再触发 ⇒ 第二次点输入条毫无反应。
        //   现在补一条：只要用户在请求聚焦、焦点已在、而键盘确实不在 ⇒ 重新唤起。
        if (hasFocus && !_hadFocus) { _hadFocus = true; OpenKeyboard(); }
        else if (hasFocus && _wantFocus && !_suppressAutoOpen && !KbVisibleOnScreen())
        {
            _hadFocus = true;
            OpenKeyboard();
        }
        else if (!hasFocus && _hadFocus)
        {
            _hadFocus = false;
            if (!_wantFocus) CloseKeyboard();
        }

        // ---- 回车发送（在 TextField 之前拦，避免被它吞掉）----
        if (e != null && e.type == EventType.KeyDown && hasFocus &&
            (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
        {
            e.Use();
            Send();
            return;
        }

        // ---- 系统键盘：先同步文本，再判 Done / Canceled ----
        // ★★ 2026-10-25 真机实测的次序 bug：原来写成 `if (IsKbActive()) { if (status==Done) ... }`，
        //    但用户按 Gboard 的「完成 / 回车」时，Unity 会先把 keyboard.active 置 false、
        //    status 才变 Done ⇒ 外层守卫把内层判断永久挡死，表现是「打字上屏了，按回车却不发送」。
        //    正确顺序：只要 _kb 还在引用上，就读文本 + 读状态，与 active 无关。
        if (_kb != null)
        {
            string kt = null;
            try { kt = _kb.text; } catch (System.Exception) { }
            if (kt != null && !_justSent) _input = kt;

            TouchScreenKeyboard.Status st = TouchScreenKeyboard.Status.Visible;
            try { st = _kb.status; } catch (System.Exception) { }
            if (st == TouchScreenKeyboard.Status.Done) { Send(); return; }
            if (st == TouchScreenKeyboard.Status.Canceled) { CloseKeyboard(); return; }

            // 用户按系统键盘的「收起」键（不是完成）⇒ active 变 false 而 status 仍 Visible。
            // 必须在这里断引用，否则 _kb 永远残留，之后再点输入条不会再弹键盘。
            bool act = false;
            try { act = _kb.active; } catch (System.Exception) { }
            if (!act)
            {
                // 断引用前先把键盘里的字取回来，避免"字打了一半被吞"。
                if (kt != null && kt.Length > 0) _input = kt;
                Debug.Log("[P0CHAT] 系统键盘被收起（active=false）⇒ 释放引用，可再次唤起");
                _kb = null;
                _hadFocus = false;
                _kbHiddenProbes = 0;
            }
            // ★★ 2026-10-25 真机：active 仍为 true，但系统把键盘收回去了（返回键/下滑）。
            //   这一支不处理的话，输入框会一直挂在焦点上、_hadFocus 永远 true，
            //   以后再点输入条既没有焦点上升沿、又被 IsKbActive() 挡掉 ⇒ 彻底失灵。
            //   处置：断引用 + 主动丢焦点 ⇒ 用户下一次点击重新产生上升沿。
            //   ⚠ 判定必须"稳"：键盘有弹出动画（实测唤起后 1.1 秒探针仍读到 0），
            //      单帧 0 就判收起 = 自己把刚弹出的键盘收掉。故要求连续多次 0 且已过 3 秒。
            else if (!_wantFocus)
            {
                if (KbVisibleOnScreen()) _kbHiddenProbes = 0;
                else _kbHiddenProbes++;

                // ★★ 2026-10-26 真机事故：旧探针（可见区域差）在 Gboard 下会读到 0，
                //   这里就会把**还在屏幕上**的键盘主动 active=false 关掉，连带吞掉已输入的字。
                //   现在只做"非破坏式断引用"：绝不主动关键盘；而且必须先确认探针可信
                //   （WindowInsets 权威口径），否则一律不判。
                bool probeTrusted = (_kbProbeMode == 1);
                if (probeTrusted && _kbHiddenProbes >= 8 &&
                    Time.realtimeSinceStartup - _kbOpenAt > 4.0f)
                {
                    // 断引用前先把键盘里的字取回来，避免用户白打。
                    try { if (_kb.text != null) _input = _kb.text; } catch (System.Exception) { }
                    Debug.Log("[P0CHAT] 系统键盘已被系统侧收起（active=true 但连续" + _kbHiddenProbes +
                              "次权威 inset=0）⇒ 仅释放引用（不主动关键盘）+ 丢焦点");
                    _kb = null;
                    _hadFocus = false;
                    _wantFocus = false;
                    _suppressAutoOpen = true;   // 别把键盘又抢回来：等用户再点一次输入条
                    _wantBlur = true;           // 下一帧 FocusControl("")，让点击能重新触发上升沿
                    _kbHiddenProbes = 0;
                }
            }
        }

        GUI.SetNextControlName(FIELD_CONTROL);
        string before = _input;
        string nv = GUI.TextField(GR(IN_X, IN_Y, IN_W, IN_H), _input, MAX_INPUT, _inputStyle);

        if (_justSent)
        {
            // 发送后一帧：TextField 可能回吐旧串，丢弃
            _justSent = false;
            _input = "";
        }
        else if (_kb == null && (nv != before || nv != _input))
        {
            // 无系统键盘（桌面 / 键盘已收）⇒ TextField 自己就是权威
            _input = nv;
        }

        // ---- 聚焦请求（必须在控件之后：FocusControl 要控件先注册过才有意义）----
        if (_wantFocus)
        {
            _focusFrames++;
            try { GUI.FocusControl(FIELD_CONTROL); } catch (System.Exception) { }
            if (GUI.GetNameOfFocusedControl() == FIELD_CONTROL || _focusFrames > 3)
                _wantFocus = false;
            _focusRetry++;
            // ★ 焦点拿不到也要保证键盘起来：部分机器上 IMGUI 焦点与系统输入法脱钩，
            //   _wantFocus 到期后不会再有上升沿 ⇒ 这里兜一层显式唤起。
            if (!IsKbActive() && _focusRetry >= 2) OpenKeyboard();
        }
    }

    // ------------------------------------------------------------------ 滚动交互
    void HandleScrollAndDrag(Event e)
    {
        // 组内坐标（BeginGroup 已平移 Event.mousePosition）
        float sx = P0BMapWalk.ScaleI, sy = P0BMapWalk.ScaleI;
        // ★ 键盘弹起时整块上抬 ⇒ 命中区必须跟着抬，否则手指点在真字上却判"不在框内"
        Rect area = new Rect(TEXT_X * sx, (TEXT_Y - _liftLogic) * sy, TEXT_W * sx, (ROWS * LINE_H) * sy);

        Vector2 mp = (e != null) ? e.mousePosition : Vector2.zero;
        bool inArea = area.Contains(mp);

        // ---- 滚轮（一帧只认一次：OnGUI 每帧会被多种 event 调用）----
        float wheel = Input.mouseScrollDelta.y;
        if (Mathf.Abs(wheel) > 0.01f && inArea && _wheelFrame != Time.frameCount)
        {
            _wheelFrame = Time.frameCount;
            ScrollBy(wheel > 0f ? -3 : 3);
        }

        // ---- 键盘翻页 ----
        if (Input.GetKeyDown(KeyCode.PageUp))   ScrollBy(-ROWS);
        if (Input.GetKeyDown(KeyCode.PageDown)) ScrollBy(ROWS);
        if (Input.GetKeyDown(KeyCode.Home))     { if (_start != 0) { _start = 0; Debug.Log("[P0CHAT] Home -> 0"); } }
        if (Input.GetKeyDown(KeyCode.End))      { int m = MaxStart; if (_start != m) { _start = m; Debug.Log("[P0CHAT] End -> " + m); } }
        // ★ 上下箭头故意不接滚动：会和方向键走路冲突。

        // ---- 拖拽 / 点行（方便 adb shell input swipe 验收；点行=原版「点名字自动填 /名字 」）----
        if (e == null) return;
        if (e.type == EventType.MouseDown && inArea && !_dragging)
        {
            _dragging = true;
            _dragLastY = mp.y;
            _dragAccum = 0f;
            _pressPos = mp;
            _pressMoved = false;
        }
        else if (e.type == EventType.MouseDrag && _dragging)
        {
            if (Mathf.Abs(mp.y - _pressPos.y) > 6f || Mathf.Abs(mp.x - _pressPos.x) > 6f)
                _pressMoved = true;
            float dy = mp.y - _dragLastY;
            _dragLastY = mp.y;
            _dragAccum += dy;
            float oneLine = LINE_H * sy;
            while (_dragAccum >= oneLine)  { ScrollBy(-1); _dragAccum -= oneLine; }
            while (_dragAccum <= -oneLine) { ScrollBy(1);  _dragAccum += oneLine; }
            e.Use();
        }
        else if ((e.type == EventType.MouseUp || e.type == EventType.Ignore) && _dragging)
        {
            _dragging = false;
            // 没拖动 ⇒ 当成点击：原版 ChatDialog 点某行会把「/名字 」预填进输入框
            //   （MainDialogs.cs L970-984），这里同语义。
            if (!_pressMoved && e.type == EventType.MouseUp) ClickLineAt(mp);
        }
    }

    /// <summary>
    /// 点某行 ⇒ 预填「/名字 」并弹键盘（原版 ChatDialog 的 temp.Click 行为）。
    /// 名字取法同原版 L974-977：先按 ':' 和 ' ' 切，取第一段，再削掉喊话/公告前缀。
    /// </summary>
    void ClickLineAt(Vector2 groupPos)
    {
        float sy = P0BMapWalk.ScaleI;
        float top = (TEXT_Y - _liftLogic) * sy;
        int row = Mathf.FloorToInt((groupPos.y - top) / (LINE_H * sy));
        if (row < 0 || row >= ROWS) return;
        int idx = _start + row;
        if (idx < 0 || idx >= _lines.Count) return;

        string text = _lines[idx].text ?? "";
        int cut = text.IndexOf(':');
        int sp = text.IndexOf(' ');
        if (cut < 0 || (sp >= 0 && sp < cut)) cut = sp;
        string name = (cut > 0) ? text.Substring(0, cut) : "";
        name = name.Replace("(!)", "").Replace("(*)", "").Trim();
        if (name.Length == 0) return;

        FocusInput();
        _input = "/" + name + " ";
        Debug.Log("[P0CHAT] 点行预填私聊 行=" + idx + " 目标=" + name);
    }

    // ------------------------------------------------------------------ 样式 / 素材
    void EnsureStyle()
    {
        if (_font == null)
        {
            _font = Resources.Load<Font>("chatfont");
            if (_font == null)
            {
                _font = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Noto Sans CJK SC", "Noto Sans SC", "Droid Sans Fallback" }, 28);
            }
            if (_font == null)
            {
                Debug.LogError("[P0CHAT] ★字体全部加载失败 —— 拒绝绘制（否则整屏方块）");
                return;
            }
            Debug.Log("[P0CHAT] 字体就绪 name=" + _font.name + " dynamic=" + _font.dynamic);
        }

        float fs = 14f * P0BMapWalk.ScaleI;

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label);
            _style.font = _font;
            _style.fontSize = (int)fs;
            _style.alignment = TextAnchor.MiddleLeft;
            _style.wordWrap = false;
            _style.clipping = TextClipping.Clip;
            _style.padding = new RectOffset(0, 0, 0, 0);
            _style.margin = new RectOffset(0, 0, 0, 0);
            _style.richText = false;
            _style.normal.textColor = Color.black;
        }

        if (_inputStyle == null)
        {
            _inputStyle = new GUIStyle(GUI.skin.textField);
            _inputStyle.font = _font;
            _inputStyle.fontSize = (int)fs;
            _inputStyle.alignment = TextAnchor.MiddleLeft;
            _inputStyle.wordWrap = false;
            _inputStyle.clipping = TextClipping.Clip;
            // GUIStyle 没有 background 字段（只有 normal.background 这类状态图），写错这个
            // 直接 CS1061 编不过 —— 这是实测踩出来的。
            _inputStyle.normal.background = null;    // 白底已烤进底板，别再画一层
            _inputStyle.focused.background = null;
            _inputStyle.hover.background = null;
            _inputStyle.active.background = null;
            _inputStyle.border = new RectOffset(0, 0, 0, 0);
            _inputStyle.padding = new RectOffset(0, 0, 0, 0);
            _inputStyle.margin = new RectOffset(0, 0, 0, 0);
            _inputStyle.normal.textColor = Color.black;
            _inputStyle.focused.textColor = Color.black;
            _inputStyle.hover.textColor = Color.black;
            _inputStyle.active.textColor = Color.black;
        }

        if (_goldTex == null) _goldTex = MakeGoldTexture();
        if (_sbTex == null) _sbTex = Resources.Load<Texture2D>("p0c/0003_20x158_vv");
        if (_sbTex == null && !_sbLogged)
        {
            _sbLogged = true;
            Debug.LogWarning("[P0CHAT] 滚动条素材 p0c/0003_20x158_vv 缺失 —— 轨道不画");
        }
    }

    bool _sbLogged;

    /// <summary>
    /// 金底渐变 1x24：顶 (255,255,170) → 中 (232,224,118) → 底 (208,198,70)。
    /// 取自参考帧 y 506~516 的行均值 (199,191,91)→(197,190,68) 与亮部 (255,255,239)。
    /// ★ Texture2D 的 y=0 是**底部**。
    /// </summary>
    static Texture2D MakeGoldTexture()
    {
        var top = new Color(255f / 255f, 255f / 255f, 170f / 255f, 1f);
        var mid = new Color(232f / 255f, 224f / 255f, 118f / 255f, 1f);
        var bot = new Color(208f / 255f, 198f / 255f, 70f / 255f, 1f);

        var t = new Texture2D(1, 24, TextureFormat.RGBA32, false);
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < 24; y++)
        {
            float f = y / 23f;                      // 0 = 底, 1 = 顶
            Color c = (f >= 0.5f)
                ? Color.Lerp(mid, top, (f - 0.5f) * 2f)
                : Color.Lerp(bot, mid, f * 2f);
            t.SetPixel(0, y, c);
        }
        t.Apply();
        t.name = "P0HChatGold";
        return t;
    }
}
