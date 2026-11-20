// ============================================================================
//  P0HNotify.cs —— 1.76 客户端「系统提示 / 滚动公告 / 伤害飘字」独立通道层
// ============================================================================
//  真值来源：work\truth\openmir2_client_channels.md（逐行取证，行号可复查）
//    · 左上绿   DrawScrn.pas:1374-1396  x=20 y=30 每条 +16  clGreen+黑十字描边  3000ms  上限 10
//    · 左下红   DrawScrn.pas:1398-1421  x=20 y=350 每条 -16  clRed（含「灵气」→ clLime）3000ms 上限 10
//    · 右下红   DrawScrn.pas:1422-1432  y=330 右对齐 x=800-宽-14 每条 -16  clRed      4000ms  上限 10
//    · 顶部滚动 DrawScrn.pas:1305-1350  宋体 11 粗；背景 palette[152]=(107,107,90) @alpha150，28 高，
//                                       全宽 800；文本从 x=800 往左滚，滚出（offset ≥ 宽+800）即删
//    · 中央     DrawScrn.pas:1440-1513  {$IFDEF OPENCENTERMAG} —— M2.inc 未定义 ⇒ **不编译**，只留空方法
//    · 伤害飘字 DrawScrn.pas:1196-1261  每帧 offset+1、y 上移 1.4；>32（技能）/ >38（普攻）删
//    · 描边     uGameEngine.pas:846-857  DrawBoldText = **十字四邻域**（四角不画）
//    · 字体     uCommon.pas:10-12        DefaultFontName='宋体' 9
//
//  ★ 为什么必须单独一层（用户最反感的病根）：
//    原版这些提示**从来不走聊天框**；我们此前把所有自造事件全灌进聊天框，
//    表现就是「砍一刀弹一行系统提示」。本类负责把它们送回各自该在的位置。
//
//  ★ 绘制契约（违反必错位，与 P0HChat / P0HHud 同一条）：
//     必须在 P0BMapWalk.OnGUI 的 GUI.BeginGroup(MainUICanvasRect) 之内调用，
//     坐标一律走 P0BMapWalk.GroupRectForLogic（组内相对）。
//     绝不能用 CanvasRectForLogic（那是绝对设备坐标，组内会二次偏移 508,-60）。
//
//  ★ 纵向位移：本机画布从顶部裁 CROP_TOP=60 设备 px = 逻辑 30 行（y ∈ [0,30) 不存在）。
//    原版左上绿首发 y=30 正好压在可见首行；顶部滚动条原版 y=6..26 **整条被裁掉**，
//    故本类把滚动条下移到 y=30..58（这是唯一一处"故意不照抄原值"的地方，原因在裁切）。
//    左上绿 y=30 与左下红 y=350、右下红 y=330 均落在可见区，按原值。
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

public class P0HNotify
{
    // ---------------------------------------------------------------- 几何（逻辑 800x600）
    const float TL_X = 20f, TL_Y = 30f, TL_STEP = 16f, TL_LIFE = 3f;
    const float BL_X = 20f, BL_Y = 350f, BL_STEP = -16f, BL_LIFE = 3f;
    const float BR_RIGHT = 800f - 14f, BR_Y = 330f, BR_STEP = -16f, BR_LIFE = 4f;
    const float AD_Y = 30f, AD_H = 28f, AD_START_X = 800f, AD_GAP = 82f;
    const int TL_MAX = 10, BL_MAX = 10, BR_MAX = 10;

    /// <summary>
    /// 经验提示阈值 = 原版 `g_MaxExpFilter` 默认值 2000（MShare.pas:1373）。
    /// `g_MaxExpFilter` 与 `g_ShowWinExpMode` 都会从 ini 读（MShare.pas:3560 / ClMain.pas:15039），
    /// 本项目固定用默认值，不做设置项。
    /// </summary>
    const int EXP_FILTER = 2000;

    const float TL_FONT = 9f;    // 宋体 9  = 左上绿 / 左下红 / 右下红
    const float AD_FONT = 11f;   // 宋体 11 粗 = 顶部滚动
    const float DMG_FONT = 14f;  // 伤害数字：原版是位图 170+d / 190+d，字号未确认 ⇒ 14 逻辑 px 兜底

    /// <summary>HUD 底栏顶边（逻辑 y≈349）。飘字落到它下面会被底栏盖住 ⇒ 直接不画。</summary>
    const float HUD_TOP_LOGIC = 345f;

    /// <summary>
    /// 原版左上绿的 clGreen = RGB(0,128,0)。
    /// ★ 不能直接用 Unity 的 Color.green —— 那是 (0,255,0)，比原版亮一倍，会看成荧光绿。
    /// </summary>
    static readonly Color CL_GREEN = new Color(0f, 128f / 255f, 0f, 1f);

    /// <summary>原版 clRed = RGB(255,0,0)，与 Unity Color.red 一致（保留常量只为口径统一）。</summary>
    static readonly Color CL_RED = new Color(1f, 0f, 0f, 1f);

    /// <summary>原版 clLime = RGB(0,255,0)，仅左下红通道「灵气」特判会用到。</summary>
    static readonly Color CL_LIME = new Color(0f, 1f, 0f, 1f);

    // ---------------------------------------------------------------- 数据
    class ToastLine
    {
        public string text;
        public float  age;
    }

    class AdLine
    {
        public string text;
        public Color  fg;
        public float  offset;   // 距右边界已滚出的逻辑 px
        public float  width;    // 文本逻辑宽（首次绘制时算）
    }

    class FloatNum
    {
        public int    cx, cy;   // 受击对象所在格（每帧重投影 ⇒ 相机 Lerp 时数字跟着怪走）
        public string text;
        public float  jitter;   // 同格叠加时横向错开（逻辑 px）
        public float  age;      // 秒
        public bool   skill;    // true = 技能伤害（原版删得更早：>32 帧）
    }

    readonly List<ToastLine>    _tl  = new List<ToastLine>();
    readonly List<ToastLine>    _bl  = new List<ToastLine>();
    readonly List<ToastLine>    _br  = new List<ToastLine>();
    readonly List<AdLine>   _ads = new List<AdLine>();
    readonly List<AdLine>   _adQueue = new List<AdLine>();
    readonly List<FloatNum> _dmg = new List<FloatNum>();

    Font     _font;
    GUIStyle _toast, _ad, _dmgStyle;
    bool     _styleLogged, _adBgLogged;

    // ---------------------------------------------------------------- 生命周期 / 事件分流

    int _pumpedBase = -1;

    /// <summary>
    /// 经验行回调。★ 1.76 原版这里是**进聊天框**的：
    ///   ClMain.pas:10142-10155
    ///     if g_ShowWinExpMode then AddSysMsgBottom('经验值 +%d')
    ///     else AddChatBoardString('%d经验值增加', clWhite, clRed)
    ///   g_ShowWinExpMode 默认 False（MShare.pas:2074）⇒ 走 else，白字红底进聊天。
    /// 宿主把本回调接到 P0HChat.Append(..., ChatType.System) 即与原版一致；
    /// 未接线时退回左下红，避免经验完全看不见。
    /// </summary>
    public System.Action<int> OnExpGain;

    /// <summary>
    /// 「魔法值不够」提示的节流计时（秒，用 Time.time 打点）。
    /// ★ 原版 ClMain.pas:3427-3432：
    ///     else if GetTickCount - g_MPLessHintTick > 1000 then
    ///       begin g_MPLessHintTick := GetTickCount;
    ///         DScreen.AddSysMsg(Format('需要 %d 魔法值才能释放 %s', ...));
    ///   即 **1 秒内只提示一次**。按住技能键不会把左上绿刷爆。
    /// </summary>
    float _lastManaHintT = -99f;

    /// <summary>
    /// 建层后调用一次：把「建层之前」的旧事件（出生装/刷怪期）当基线丢掉，并记下基线号。
    /// ★ 这一步以前在 P0HChat.Init 里做（那里会 Clear 队列）；现在队列的唯一消费者是
    ///   本类（P0HChat 只装真人说话），基线清理必须跟着消费者走，否则两边互相踩。
    /// </summary>
    public void Init(P0HRuntime run)
    {
        int dropped = 0;
        if (run != null)
        {
            dropped = run.EventQueue.Count;
            run.EventQueue.Clear();
            _pumpedBase = run.EventsEmitted;
        }
        Debug.Log("[P0HINT] 提示层就绪 丢弃历史事件=" + dropped + " 基线=" + _pumpedBase +
                  " 通道=左上绿/左下红/右下红/顶部滚动(中央未编译)/伤害飘字");
    }

    /// <summary>
    /// 消费 P0HRuntime.EventQueue 并按**原版通道**分流（这是本任务的核心）：
    ///   战斗流水（打中/打空/被挡/被打）→ 什么都**不上**（伤害数字已由 HitQueue 画在头顶）
    ///   经验 → 左下红；升级/拾取/施法失败 → 左上绿；击杀 → 只记日志（经验走 ExpGain）
    ///   真人说话（Route 直接上屏）与传送（单机适配）→ 只进 logcat
    /// 真值：work\truth\openmir2_client_channels.md 第 3 / 7 节。
    /// ★ 整个工程里必须**只有本方法**排空 EventQueue，多一个消费者就会丢事件或双上屏。
    /// </summary>
    public void Pump(P0HRuntime run)
    {
        if (run == null) return;
        int guard = 0;
        while (run.EventQueue.Count > 0 && guard++ < 64)
        {
            P0HRuntime.P0HEvent e = run.EventQueue.Dequeue();
            Route(run, e);
        }
        _pumpedBase = run.EventsEmitted;
    }

    void Route(P0HRuntime run, P0HRuntime.P0HEvent e)
    {
        string s = e.text ?? "";
        switch (e.kind)
        {
            // ---------- 战斗流水：原版一律不上屏 ----------
            // DrawScrn.pas:1196-1261 只在受击者头顶画数字；ClMain.pas 收到 SM_STRUCK 也不加聊天行。
            case P0HRuntime.P0HEventKind.CombatHit:
            case P0HRuntime.P0HEventKind.CombatMiss:
            case P0HRuntime.P0HEventKind.CombatBlocked:
            case P0HRuntime.P0HEventKind.PlayerHurt:
                // 只记日志，绝不 Append 到聊天框、也不加提示行（伤害数字由 HitQueue 负责）
                break;

            case P0HRuntime.P0HEventKind.Kill:
                // 击杀的「伤害 / 经验」分别由飘字和 ExpGain 两条走；这里只留日志。
                // （ExpGain 由 P0HRuntime.AddExp 内部发，击杀与技能击杀都会经过它。）
                break;

            // ---------- 经验：**默认什么都不显示** ----------
            // ClMain.pas:10142-10156 原文两条分支，各自外面还套一层过滤：
            //   if g_ShowWinExpMode then
            //     if not g_gcGeneral[3] or (exp > g_MaxExpFilter) then AddSysMsgBottom('经验值 +%d')
            //   else
            //     if not g_gcGeneral[3] or (exp > g_MaxExpFilter) then
            //        AddChatBoardString('%d经验值增加', clWhite, clRed)
            // 默认值（MShare.pas:302 / 1373）：
            //   g_gcGeneral[3] = True（数组第 4 个元素，ShowExpFilter 默认开）
            //   g_MaxExpFilter = 2000
            // ⇒ 合并后判据 = exp > 2000。打小怪加几十点经验时**原版一个字都不显示**，
            //   只有单次经验超过 2000（打 BOSS / 吃经验丹）才吐一行「N经验值增加」。
            //   这就是用户要的"聊天框干净"的原版依据，不是我们自作主张。
            case P0HRuntime.P0HEventKind.ExpGain:
                {
                    int amt = e.amount;
                    if (amt <= 0) amt = ParseTailInt(s);
                    if (amt <= EXP_FILTER) break;          // 默认 2000 以下静默
                    if (OnExpGain != null) OnExpGain(amt); // 白字红底进聊天框
                    else Bottom(amt + "经验值增加");
                }
                break;

            // ---------- 左下红：阵亡（原版不弹；单机适配，见真值文档第 7 节）----------
            case P0HRuntime.P0HEventKind.PlayerDeath:
                Bottom("你已阵亡，已原地复活");
                break;

            // ---------- 左上绿 ----------
            case P0HRuntime.P0HEventKind.LevelUp:
                Top("您的等级已升级！");                        // ClMain.pas:10212 原文
                break;
            case P0HRuntime.P0HEventKind.PickupGold:
                {
                    int g = e.amount;
                    if (g <= 0) g = ParseTailInt(s);
                    Top(g > 0 ? ("获得 " + g + "金币") : s);        // ClMain.pas:10820 口径
                }
                break;
            case P0HRuntime.P0HEventKind.PickupItem:
                Top(s.Length > 0 ? s : "拾取物品");             // ClFunc.pas:1998「X 被发现」
                break;
            case P0HRuntime.P0HEventKind.Cooldown:
                // ★ 原版客户端**没有**「冷却中」这条提示。
                //   全工程 .pas 里唯一含「冷却」的字符串是 FState.pas:10203 的鼠标悬停
                //   「特殊技能冷却 -N秒」，那是物品/技能格的 tooltip，不是施法反馈。
                //   ⇒ 冷却期间再按技能，原版是**完全静默**（根本不发 CM_SPELL）。
                //   这里只留日志，绝不上屏。
                break;

            case P0HRuntime.P0HEventKind.ManaShort:
                // 原版文案已在 P0HSkill 拼好（"需要 N 魔法值才能释放 火球术"），
                // 走左上绿 AddSysMsg，1 秒节流（ClMain.pas:3427）。
                if (Time.time - _lastManaHintT >= 1f)
                {
                    _lastManaHintT = Time.time;
                    Top(s);
                }
                break;

            case P0HRuntime.P0HEventKind.Heal:
                // ★ 原版治愈术**不回显**「回复 N HP」。全工程 .pas 里没有这条文案
                //   （grep '回复' 只命中 MShare.pas 的物品属性「魔法回复 +N」）。
                //   血量变化看 HUD 血球即可 ⇒ 只留日志。
                break;

            case P0HRuntime.P0HEventKind.SkillNoTarget:
                // ★ 原版没有「没有目标」提示。ClMain.pas:3380 那条是
                //   「目标太远了，施展魔法失败！！！」—— 距离超限才提示，
                //   而"没锁定目标"时客户端直接把技能当范围技丢出去（g_MagicTarget=nil 分支）。
                //   本移植没有范围技，找怪失败只能静默 ⇒ 只留日志。
                break;

            // ---------- Info：只进 logcat ----------
            default:
                // 「说话: xxx」已由 P0HChat.Send → Route() 直接上屏，这里再上就是双行（实测过）。
                // 「传送到 (x,y)」原版无此提示，属单机适配，只留日志。
                Debug.Log("[P0HINT] info " + s);
                break;
        }
        Debug.Log("[P0HINT] kind=" + e.kind + " text=" + s);
    }

    /// <summary>从文本尾部抠出数字（"经验值 +12" / "获得 35金币" 这类），拿不到返回 0。</summary>
    static int ParseTailInt(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int i = s.Length - 1;
        while (i >= 0 && !char.IsDigit(s[i])) i--;
        int end = i;
        while (i >= 0 && char.IsDigit(s[i])) i--;
        if (end <= i) return 0;
        int v;
        return int.TryParse(s.Substring(i + 1, end - i), out v) ? v : 0;
    }

    // ---------------------------------------------------------------- 对外 API

    // ---- 别名：真值文档 work\truth\openmir2_client_channels.md 第 7 节用的是
    //      Top / Bottom / BottomRight / Float 这套名字，这里两套都留，避免文档与代码对不上。
    public void Top(string msg)         { Toast(msg); }
    public void Bottom(string msg)      { ToastBottom(msg); }
    public void BottomRight(string msg) { ToastRight(msg); }
    public void Float(int cx, int cy, int amount, bool skill) { Damage(cx, cy, amount, skill); }

    /// <summary>左上绿（AddSysMsg）：升级 / 金币 / 施法失败 / 拾取被发现。</summary>
    public void Toast(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        if (_tl.Count >= TL_MAX) _tl.RemoveAt(0);
        _tl.Add(new ToastLine { text = msg, age = 0f });
        Debug.Log("[P0HINT] channel=Top text=" + msg);
    }

    /// <summary>左下红（AddSysMsgBottom）：受击 / 危险类。</summary>
    public void ToastBottom(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        if (_bl.Count >= BL_MAX) _bl.RemoveAt(0);
        _bl.Add(new ToastLine { text = msg, age = 0f });
        Debug.Log("[P0HINT] channel=Bottom text=" + msg);
    }

    /// <summary>右下红（AddSysMsgBottom2）。</summary>
    public void ToastRight(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        if (_br.Count >= BR_MAX) _br.RemoveAt(0);
        _br.Add(new ToastLine { text = msg, age = 0f });
        Debug.Log("[P0HINT] channel=BottomRight text=" + msg);
    }

    /// <summary>
    /// 中央消息（AddSysMsgCenter）。★ 1.76 客户端编译配置里没有 OPENCENTERMAG，
    /// 整套 DrawScreenCenter / AddSysMsgCenter 都不参与编译 ⇒ 这里**故意什么都不做**。
    /// 保留方法只为「通道齐全、将来开宏就能用」，不许在这里偷偷画字。
    /// </summary>
    public void Center(string msg, int seconds)
    {
        if (string.IsNullOrEmpty(msg)) return;
        Debug.Log("[P0HINT] channel=Center(disabled) text=" + msg);
    }

    /// <summary>顶部滚动公告（SM_SYSMESSAGE2 → m_adList）。文本从右边进、往左滚。</summary>
    public void Ad(string text, Color fg)
    {
        if (string.IsNullOrEmpty(text)) return;
        var a = new AdLine { text = text, fg = fg, offset = 0f, width = -1f };
        // 原版：前一条滚过「自身宽度 + 82」后，下一条才出场（DrawScrn.pas:1331）
        if (_ads.Count == 0) _ads.Add(a);
        else _adQueue.Add(a);
        Debug.Log("[P0HINT] channel=Ad text=" + text);
    }

    /// <summary>
    /// 伤害飘字。cx/cy = 受击对象所在格（不是像素）。
    /// 原版由 SM_STRUCK 触发，在**受击者头顶**画伤害数字，既不进聊天也不进提示层。
    /// </summary>
    public void Damage(int cx, int cy, int dmg, bool skill)
    {
        if (dmg <= 0) return;
        while (_dmg.Count >= 48) _dmg.RemoveAt(0);
        _dmg.Add(new FloatNum
        {
            cx = cx, cy = cy, text = dmg.ToString(), skill = skill,
            jitter = Random.Range(-6f, 6f), age = 0f
        });
        Debug.Log("[P0HINT] channel=Float cell(" + cx + "," + cy + ") -" + dmg +
                  (skill ? " 技能" : " 普攻") + " 在屏=" + _dmg.Count);
    }

    /// <summary>消费 P0HRuntime.HitQueue（谁被打、掉多少、是不是技能）。每帧调一次即可。</summary>
    public void Drain(P0HRuntime run)
    {
        if (run == null) return;
        int guard = 0;
        while (run.HitQueue.Count > 0 && guard++ < 32)
        {
            P0HRuntime.HitFx h = run.HitQueue.Dequeue();
            Damage(h.x, h.y, h.dmg, h.skill);
        }
    }

    /// <summary>每帧推进（寿命 / 滚动 / 飘字上升）。由 P0BMapWalk.Update 调**一次**。</summary>
    public void Tick(float dt)
    {
        if (dt <= 0f) return;

        // 原版只检查最老一条（GetTickCount - Objects[0] >= 3000 ⇒ Delete(0)），寿命相同，
        // 所以按队头删就等价于"整批到期"。
        Expire(_tl, TL_LIFE, dt);
        Expire(_bl, BL_LIFE, dt);
        Expire(_br, BR_LIFE, dt);

        // ---- 顶部滚动 ----
        // 原版 offset 每帧 +1（60fps ⇒ 60 逻辑 px/秒）；这里乘 dt 保持不同帧率下速度一致。
        for (int i = _ads.Count - 1; i >= 0; i--)
        {
            var a = _ads[i];
            if (a.width < 0f) continue;                 // 还没测量过 ⇒ 等这一帧画完再滚
            a.offset += 60f * dt;
            if (a.offset >= a.width + AD_START_X) _ads.RemoveAt(i);
        }
        // 前一条滚出「自身宽 + 82」后，下一条出场
        while (_adQueue.Count > 0)
        {
            if (_ads.Count == 0) { _ads.Add(_adQueue[0]); _adQueue.RemoveAt(0); continue; }
            var last = _ads[_ads.Count - 1];
            if (last.width < 0f) break;
            if (last.offset < last.width + AD_GAP) break;
            _ads.Add(_adQueue[0]);
            _adQueue.RemoveAt(0);
        }

        // ---- 伤害飘字 ----
        for (int i = _dmg.Count - 1; i >= 0; i--)
        {
            var f = _dmg[i];
            f.age += dt;
            float frames = f.age * 60f;                 // 折算成原版的"帧数"
            if (frames > (f.skill ? 32f : 38f)) _dmg.RemoveAt(i);
        }
    }

    static void Expire(List<ToastLine> list, float life, float dt)
    {
        for (int i = 0; i < list.Count; i++) list[i].age += dt;
        while (list.Count > 0 && list[0].age >= life) list.RemoveAt(0);
    }

    /// <summary>清空全部通道（切场景/重开时用）。</summary>
    public void Clear()
    {
        _tl.Clear(); _bl.Clear(); _br.Clear(); _ads.Clear(); _adQueue.Clear(); _dmg.Clear();
    }

    // ---------------------------------------------------------------- 绘制
    /// <summary>
    /// 世界层（伤害飘字）。★ 原版顺序是 `DScreen.DrawScreen`（地图+头顶飘字）在
    /// `g_DWinMan.DirectPaint`（HUD/聊天框）**之前** ⇒ 本方法必须在 HUD 之前调，
    /// 否则伤害数字会盖到血球/聊天框上（原版是会被底栏压住的）。
    /// </summary>
    public void DrawWorldLayer()
    {
        EnsureStyles();
        if (_toast == null || _font == null) return;    // 字体没起来就绝不画方块
        GUI.color = Color.white;
        DrawFloats();
        GUI.color = Color.white;
    }

    /// <summary>提示层（顶部滚动 + 左上绿 + 左下红 + 右下红）。必须在 HUD/聊天框之后调。</summary>
    public void DrawAll()
    {
        EnsureStyles();
        if (_toast == null || _font == null) return;

        GUI.color = Color.white;
        DrawAds();
        // ★ 唯一一处「因手机裁剪而做的避让」：原版左上绿首发 y=30、顶部广告背景 y=0..28，
        //   两者不重叠；本机把广告整体下移到 30..58 后就会压住第一行绿字
        //   ⇒ 广告在场时左上绿起点临时下移到 62（广告是滚动的，滚完即恢复 y=30）。
        float tlY = (_ads.Count > 0 || _adQueue.Count > 0) ? (AD_Y + AD_H + 4f) : TL_Y;
        DrawToastList(_tl, TL_X, tlY, TL_STEP, CL_GREEN, false);
        DrawToastList(_bl, BL_X, BL_Y, BL_STEP, CL_RED, true);
        DrawToastRight();
        GUI.color = Color.white;
    }

    // ---- 顶部滚动 ----
    void DrawAds()
    {
        if (_ads.Count == 0) return;

        // 背景：原版 FillRectAlpha(IntRectBDS(0,0,800,28), GetRGB(152), 150)
        //   GetRGB(152) = palette[152] = (107,107,90)；alpha 150/255
        GUI.color = new Color(107f / 255f, 107f / 255f, 90f / 255f, 150f / 255f);
        GUI.DrawTexture(GR(0f, AD_Y, 800f, AD_H), Texture2D.whiteTexture);
        GUI.color = Color.white;

        if (!_adBgLogged)
        {
            _adBgLogged = true;
            Debug.Log("[P0T] 顶部滚动条首次绘制 逻辑rect=(0," + AD_Y + ",800," + AD_H +
                      ") 底色=(107,107,90,150)");
        }

        for (int i = 0; i < _ads.Count; i++)
        {
            var a = _ads[i];
            if (a.width < 0f) a.width = Width(_ad, a.text);
            float x = AD_START_X - a.offset;
            // 完全在屏外的整条跳过（省 drawcall）；部分露出的照画，由 BeginGroup 自己裁边
            if (x > 800f || x + a.width < 0f) continue;
            Bold(_ad, GR(x, AD_Y, a.width + 2f, AD_H), a.text, a.fg);
        }
    }

    // ---- 三条固定位置通道 ----
    void DrawToastList(List<ToastLine> list, float x, float y, float step, Color col, bool limeIfLingqi)
    {
        for (int i = 0; i < list.Count; i++)
        {
            string s = list[i].text;
            float ly = y + step * i;
            if (ly < 0f || ly > 596f) continue;
            Color c = col;
            // 原版：左下红通道里文本含「灵气」用 clLime=(0,255,0)（DrawScrn.pas:1411-1417）。
            // ★ 「灵气」是**后期资料片**才有的资源，纯 1.76 永远不会出现 ⇒ 这个分支在 1.76 下
            //   是死代码，保留只为「照抄原版判定」，不许往左上/左下塞任何带「灵气」的文案。
            if (limeIfLingqi && s.IndexOf("灵气") >= 0) c = CL_LIME;
            Bold(_toast, GR(x, ly, Width(_toast, s) + 2f, 12f), s, c);
        }
    }

    void DrawToastRight()
    {
        for (int i = 0; i < _br.Count; i++)
        {
            string s = _br[i].text;
            float w = Width(_toast, s);
            float x = BR_RIGHT - w;                                    // 右对齐：800 - 宽 - 14
            float ly = BR_Y + BR_STEP * i;
            if (ly < 0f || ly > 596f) continue;
            Bold(_toast, GR(x, ly, w + 2f, 12f), s, Color.red);
        }
    }

    // ---- 伤害飘字 ----
    void DrawFloats()
    {
        if (_dmgStyle == null || _dmg.Count == 0) return;
        for (int i = 0; i < _dmg.Count; i++)
        {
            var f = _dmg[i];
            Vector2 basePos = CellToLogic(f.cx, f.cy);
            if (basePos.x < -9000f) continue;                   // 相机背后
            float h = Height(_dmgStyle, f.text);
            float w = Width(_dmgStyle, f.text);
            float k = f.age * 60f;                              // 原版 offset（帧）
            // 原版 DrawScrn.pas:1217 `m_nSayX + offset ...`：数字**同时向右**漂（1 逻辑 px/帧），
            // 不是只往上飞。之前漏了这一项，飘字会笔直上升，跟原版斜飘对不上。
            float lx = basePos.x + k + f.jitter - w * 0.5f;
            float ly = basePos.y - 10f - k * 1.4f - h;          // 原版：sayY-10-offset*1.4-字高
            if (ly > HUD_TOP_LOGIC) continue;                   // 落到底栏后面 ⇒ 不画
            Bold(_dmgStyle, GR(lx, ly, w + 2f, h + 2f), f.text, Color.white);
        }
    }

    /// <summary>十字四邻域黑描边 + 本体色（uGameEngine.pas:846-857 的 DrawBoldText）。</summary>
    void Bold(GUIStyle st, Rect r, string s, Color body)
    {
        if (st == null) return;
        const float dx = 2f;      // 组内是设备 px：1 逻辑 px = 2 设备 px
        st.normal.textColor = Color.black;
        GUI.Label(new Rect(r.x - dx, r.y, r.width, r.height), s, st);
        GUI.Label(new Rect(r.x + dx, r.y, r.width, r.height), s, st);
        GUI.Label(new Rect(r.x, r.y - dx, r.width, r.height), s, st);
        GUI.Label(new Rect(r.x, r.y + dx, r.width, r.height), s, st);
        st.normal.textColor = body;
        GUI.Label(r, s, st);
        st.normal.textColor = Color.white;
    }

    // ---------------------------------------------------------------- 坐标换算
    static Rect GR(float lx, float ly, float lw, float lh)
    {
        return P0BMapWalk.GroupRectForLogic(lx, ly, lw, lh);
    }

    float Width(GUIStyle st, string s)
    {
        if (st == null || string.IsNullOrEmpty(s)) return 0f;
        return st.CalcSize(new GUIContent(s)).x / P0BMapWalk.ScaleI;
    }

    float Height(GUIStyle st, string s)
    {
        if (st == null || string.IsNullOrEmpty(s)) return 0f;
        return st.CalcSize(new GUIContent(s)).y / P0BMapWalk.ScaleI;
    }

    /// <summary>
    /// 格坐标 → 逻辑画布坐标（受击者头顶上方一点）。
    /// 相机每帧 Lerp，所以这里**每帧重算**，飘字才会跟着怪走（原版 Objects[] 存的是屏幕偏移）。
    /// WorldToScreenPoint 的 y 原点在屏幕左下，逻辑画布 y 原点在顶部 ⇒ 必须翻一次。
    /// </summary>
    static Vector2 CellToLogic(int cx, int cy)
    {
        var cam = Camera.main;
        if (cam == null) return new Vector2(400f, 300f);

        Vector3 w = P0BMapWalk.WorldPos(cx, cy) + new Vector3(0f, 0.85f, 0f);   // 0.85 单位 ≈ 到头顶
        Vector3 sp = cam.WorldToScreenPoint(w);
        if (sp.z < 0f) return new Vector2(-10000f, -10000f);      // 在相机背后 ⇒ 不画

        float sw = Screen.width  > 0 ? Screen.width  : 1;
        float sh = Screen.height > 0 ? Screen.height : 1;
        float devX = sp.x * (P0BMapWalk.ScreenWidthDevice  / sw);   // 屏幕 px → 设备 px（GUI.matrix 口径）
        float devY = sp.y * (P0BMapWalk.ScreenHeightDevice / sh);   // 仍是"左下为原点"
        float topY = P0BMapWalk.ScreenHeightDevice - devY;          // 翻成"上为原点"
        return new Vector2(
            (devX - P0BMapWalk.MainUICanvasRectX) / P0BMapWalk.ScaleI,
            (topY + P0BMapWalk.CropTopDevice)     / P0BMapWalk.ScaleI);
    }

    // ---------------------------------------------------------------- 样式
    void EnsureStyles()
    {
        if (_toast != null && _ad != null && _dmgStyle != null) return;

        if (_font == null)
        {
            _font = Resources.Load<Font>("chatfont");
            if (_font == null)
                _font = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Noto Sans CJK SC", "Noto Sans SC", "Droid Sans Fallback" }, 28);
            if (_font == null)
            {
                Debug.LogError("[P0T] ★字体全部加载失败 —— 拒绝绘制（否则整屏方块）");
                return;
            }
            Debug.Log("[P0T] 字体就绪 name=" + _font.name + " dynamic=" + _font.dynamic);
        }

        _toast    = MakeStyle(TL_FONT,  FontStyle.Normal, TextAnchor.UpperLeft);
        _ad       = MakeStyle(AD_FONT,  FontStyle.Bold,   TextAnchor.MiddleLeft);
        _dmgStyle = MakeStyle(DMG_FONT, FontStyle.Bold,   TextAnchor.UpperLeft);

        if (!_styleLogged)
        {
            _styleLogged = true;
            Debug.Log("[P0T] 样式就绪 左上/左下/右下=" + Mathf.RoundToInt(TL_FONT * P0BMapWalk.ScaleI) +
                      "px 顶部滚动=" + Mathf.RoundToInt(AD_FONT * P0BMapWalk.ScaleI) +
                      "px 飘字=" + Mathf.RoundToInt(DMG_FONT * P0BMapWalk.ScaleI) + "px");
        }
    }

    GUIStyle MakeStyle(float logicSize, FontStyle fs, TextAnchor anchor)
    {
        var s = new GUIStyle(GUI.skin.label);
        s.font = _font;
        s.fontSize = Mathf.Max(8, Mathf.RoundToInt(logicSize * P0BMapWalk.ScaleI));
        s.fontStyle = fs;
        s.alignment = anchor;
        s.wordWrap = false;
        s.richText = false;
        s.clipping = TextClipping.Clip;
        s.padding = new RectOffset(0, 0, 0, 0);
        s.margin = new RectOffset(0, 0, 0, 0);
        s.normal.textColor = Color.white;
        return s;
    }
}

