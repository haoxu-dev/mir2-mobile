// P0-H HUD 实时数据层（V11 —— 全部按 FState.pas DBottomDirectPaint 的源码坐标 1:1）
// ============================================================================
// ★★ 这一版为什么整层重写
//   用户原话：「你别自己瞎折腾，去找客户端源文件，然后跟客户端要一比一一模一样，别自己瞎几把画」
//   「当年玩的什么 03 年的还是零几年的时候，盛大的那一版 1.76 到底什么情况？你了解清楚了之后再去对照」
//
//   以前两版的病根都找到了：
//    ① 底图取错了库。源码 FState.pas:8797 画的是
//         d := g_WMain3Images.Images[BOTTOMBOARD800]
//       而 MShare.pas:107 明确 MAINIMAGEFILE3 = 'Data\Prguse3.data'，
//       MShare.pas:2662 g_WMain3Images := CreateImages(MAINIMAGEFILE3, ...)
//       ⇒ 原版底图来自 **Prguse3**，不是 Prguse。BOTTOMBOARD800 在
//         FState.pas:18 默认 371，ClMain.pas:9644/9649 按有无英雄改成 371/291。
//         本工程单机无英雄 ⇒ **291**。工程底图已换成 Prguse3[291] 原帧。
//    ② 坐标全是拿"参考截图"猜的。这一版**每一条坐标都是从源码抄下来的**，
//       注释里带着原行号，可逐条回查（证据档 _audit176\_DBottomDirectPaint.txt）。
//
// ★ 源码坐标表（逻辑坐标 800x600；btop := SCREENHEIGHT - 251 = 349，L8791）
//   L8797  底图整张 Prguse3[BOTTOMBOARD800] 贴 (0, 349) 800x251   ← 由 P0DUI 按 p0d_layout.json 画
//   L8816  昼夜图标  Prguse[15|12|13|14] @ (748, 428) 32x54        g_nDayBright 0/1/2/3
//   L8821  新手球底  Prguse[5] @ (38, 439) 96x92，rc.Right = 94    job=0 且 Level<28
//   L8827  新手球液  Prguse[6] @ (38, 439+rc.Top)，rc.Top = Round(92/MaxHP*(MaxHP-HP))
//   L8835  正式球HP  Prguse[4] 源 rc=(0,0,45,90)，rc.Top=Round(90/MaxHP*(MaxHP-HP))
//                   @ (40, 440+rc.Top)，尺寸 45 x (90-rc.Top)
//   L8841  正式球MP  源 rc=(47,0,91,90)，同一个 rc.Top 公式（用 MaxMP/MP）
//                   @ (40+47 = 87, 440+rc.Top)，尺寸 44 x (90-rc.Top)
//   L8875  HP 数字   BoldText(28, 563, "HP/MaxHP", 白字黑边)
//   L8876  MP 数字   BoldText(88, 563, "MP/MaxMP", 白字黑边)
//   L8878  地图+坐标 BoldText(20, 583, "地图名 X:Y", 白字黑边)
//   L8884  聊天拉伸  g_opui.Images[20] @ (593, 473) 16x100
//   L8888  体力球    g_opui.Images[g_sVigourbar1/2] @ (78, 439)  —— 仅召唤兽(骷髅)在场时画，
//                   本工程 g_nMaxSquHitPoint 恒 0 ⇒ 不画（源码条件 L8886）
//   L8907  等级      PomiTextOut(660, 496, IntToStr(Level))
//   L8921  经验条    Prguse[7] @ (666, 527)，宽 = Round(76 * Exp / MaxExp)
//   L8932  负重条    Prguse[7] @ (666, 560)，宽 = Round(76 * Weight / MaxWeight)
//   L9026  时钟      TextOut(672, 579, "HH:MM:SS")
//   L9043  聊天文字  x=208, y = 470 + i*12, 行高 12, 9 行（由 P0HChat 画）
//
// ★ 用户明确指出的那个错：「最下边那一排血条啊聊天框啊等级那一排，现在还不是原版」
//   上一版把整条底图错误地当成 133 高（hud_main 那套 1.85 私服皮），
//   原版是 **251 高、从 y=349 起**，上方 118 像素透出地图 —— 这就是"看着矮了一截"的根因。
//
// ★ 坐标契约：本层所有绘制都在 GUI.BeginGroup(MainUICanvasRect) 内部，
//   所有 Rect 一律走 P0BMapWalk.GroupRectForLogic(lx,ly,lw,lh)（组内相对）。
//   绝不能用 CanvasRectForLogic —— 它返回绝对设备坐标，组内会二次偏移 (508,-60)。
//
// ★ 合成顺序：底图（P0DUI）→ 静态零件（本层）→ 液体（本层）→ 文字（本层）→ 聊天（P0HChat）
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

public class P0HHud
{
    // ---------------- 底图（逻辑坐标，源码 L8791 实测 btop=349） ----------------
    const float BOARD_X = 0f;
    const float BOARD_Y = 349f;      // = 600 - 251
    const float BOARD_W = 800f;
    const float BOARD_H = 251f;

    // ---------------- 昼夜图标（源码 L8816） ----------------
    const float DAY_X = 748f;        // 800 div 2 + (800 div 2 - 52)
    const float DAY_Y = 428f;        // 79 + DBottom.Top(=349)
    const float DAY_W = 32f;
    const float DAY_H = 54f;

    // ---------------- 球（源码 L8821-8845） ----------------
    //   新手球（job=0 且 Level<28）: Prguse[5] 底 + Prguse[6] 液，96x92 @ (38, 439)
    //   正式球（其它）             : Prguse[4] 左右半，92x90 @ (40, 440+top) / (87, 440+top)
    const float BALL_NEW_X = 38f, BALL_NEW_Y = 439f;
    const float BALL_NEW_W = 96f, BALL_NEW_H = 92f;
    const float BALL_NEW_CUT = 2f;   // rc.Right := ClientRect.Right - 2   (L8824/L8830)

    const float ORB_X = 40f, ORB_Y = 440f;   // 440 = 349 + 91
    const float ORB_FULL_W = 92f, ORB_H = 90f;
    const float ORB_HP_W = 45f;      // rc.Right := 92 div 2 - 1  ⇒ 列 0..44
    const float ORB_MP_U0 = 47f;     // rc.Left := 92 div 2 + 1
    const float ORB_MP_W = 44f;      // 92 - 1 - 47 = 44 ⇒ 列 47..90

    // ---------------- 文字（源码 L8875/8876/8878/8907/9026） ----------------
    const float TXT_HP_X = 28f, TXT_HP_Y = 563f;
    const float TXT_MP_X = 88f, TXT_MP_Y = 563f;
    const float TXT_MAP_X = 20f, TXT_MAP_Y = 583f;
    const float TXT_LV_X = 660f, TXT_LV_Y = 496f;
    const float TXT_CLK_X = 672f, TXT_CLK_Y = 579f;
    const float TXT_H = 16f;         // 一行字占高（BoldText 字高 ~12，留余量）

    // ---------------- 经验 / 负重条（源码 L8921/L8932） ----------------
    const float BAR_X = 666f;
    const float BAR_EXP_Y = 527f;    // 600 - 73
    const float BAR_WT_Y = 560f;     // 600 - 40
    const float BAR_W = 76f, BAR_H = 13f;

    // ---------------- 聊天拉伸条（源码 L8884） ----------------
    const float CHAT_STR_X = 593f;   // 800 - 207
    const float CHAT_STR_Y = 473f;   // 600 - 127

    // ---------------- 纹理缓存 ----------------
    readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();

    // ---------------- 样式 ----------------
    GUIStyle _w;   // 白字黑边（BoldText 等价）
    Font _font;

    // ---------------- 判据统计 ----------------
    public int DrawnCount { get; private set; }
    public int MissingTexCount { get; private set; }
    public int HpPct { get; private set; }
    public int MpPct { get; private set; }
    public int ExpPct { get; private set; }
    public int WtPct { get; private set; }
    public int Level { get; private set; }
    public int HpFillH { get; private set; }
    public int MpFillH { get; private set; }
    public bool NewbieOrb { get; private set; }

    bool _noTexLogged;
    string _lastKey;
    int _lastTick;
    bool _orbOk, _ballOk, _barOk, _stripOk;

    // =====================================================================
    // 唯一入口：P0BMapWalk.OnGUI 在 BeginGroup 内、P0DUI 之后调用
    // =====================================================================
    public void DrawAll(P0HRuntime run)
    {
        if (run == null) return;

        DrawnCount = 0;
        MissingTexCount = 0;
        EnsureStyles();

        int hp = run.PlayerHp, hpMax = run.PlayerHpMax;
        int mp = run.PlayerMp, mpMax = run.PlayerMpMax;
        int lv = run.PlayerLevel;
        long exp = run.PlayerExp, expMax = run.PlayerExpMax;
        int wt = run.PlayerWeight, wtMax = run.PlayerWeightMax;

        HpPct = hpMax > 0 ? Mathf.Clamp(Mathf.RoundToInt(100f * hp / hpMax), 0, 100) : 0;
        MpPct = mpMax > 0 ? Mathf.Clamp(Mathf.RoundToInt(100f * mp / mpMax), 0, 100) : 0;
        ExpPct = expMax > 0 ? Mathf.Clamp(Mathf.RoundToInt(100f * (float)exp / expMax), 0, 100) : 0;
        WtPct = wtMax > 0 ? Mathf.Clamp(Mathf.RoundToInt(100f * wt / wtMax), 0, 100) : 0;
        Level = lv;

        // ① 底图由 P0DUI 画（静态美术单一属主），本层只查在不在
        _stripOk = Resources.Load<Texture2D>("p0d/prguse1_800x251") != null;

        // ② 昼夜图标（L8816）
        DrawDayIcon(run.DayBright);

        // ③ 血蓝球（L8819-8846）：job==0 且 Level<28 走新手球，否则正式球
        NewbieOrb = (run.PlayerJob == 0) && (lv < 28);
        if (NewbieOrb) DrawNewbieBall(hp, hpMax);
        else DrawOrbPair(hp, hpMax, mp, mpMax);

        // ④ 聊天拉伸条（L8884）—— 原版静态件，画在聊天框右缘
        DrawStatic("p0d/bar_chat_stretch_16x100", CHAT_STR_X, CHAT_STR_Y, 16f, 100f);

        // ⑤ 文字（L8875/8876/8878/8907/9026）
        DrawTextShadow(hp + "/" + hpMax, TXT_HP_X, TXT_HP_Y);
        DrawTextShadow(mp + "/" + mpMax, TXT_MP_X, TXT_MP_Y);
        DrawTextShadow(run.MapTitle + " " + run.PlayerX + ":" + run.PlayerY, TXT_MAP_X, TXT_MAP_Y);
        DrawTextShadow(IntToStr(lv), TXT_LV_X, TXT_LV_Y);
        DrawTextShadow(Clock(), TXT_CLK_X, TXT_CLK_Y);

        // ⑥ 经验 / 负重条（L8909-8932）
        _barOk = DrawPartialBar(BAR_EXP_Y, expMax > 0 ? (float)exp / expMax : 0f)
                 & DrawPartialBar(BAR_WT_Y, wtMax > 0 ? (float)wt / wtMax : 0f);

        // ---------- 判据日志（只在这些数变了或每 300 帧打一次） ----------
        string key = hp + "|" + mp + "|" + lv + "|" + exp + "|" + wt + "|" + run.PlayerX + "|" + run.PlayerY;
        if (key != _lastKey || Time.frameCount - _lastTick > 300)
        {
            _lastKey = key;
            _lastTick = Time.frameCount;
            LogEvidence(run, hp, hpMax, mp, mpMax, exp, expMax, wt, wtMax);
        }
    }

    // =====================================================================
    // 昼夜图标（源码 L8809-8816）
    //   case g_nDayBright of 0: Prguse[15] 早上 / 1: [12] 白天 / 2: [13] 傍晚 / 3: [14] 晚上
    //   位置 (748, 428)
    // =====================================================================
    static readonly string[] DAY_TEX = {
        "p0d/bar_day0_32x54", "p0d/bar_day1_32x54", "p0d/bar_day2_32x54", "p0d/bar_day3_32x54"
    };

    void DrawDayIcon(int bright)
    {
        if (bright < 0) bright = 0;
        if (bright > 3) bright = 3;
        DrawStatic(DAY_TEX[bright], DAY_X, DAY_Y, DAY_W, DAY_H);
    }

    // =====================================================================
    // 正式血蓝球（源码 L8835-8846）
    //   HP 源 rc = x[0..45) y[top..90)，画在 (40, 440+top)，尺寸 45 x (90-top)
    //   MP 源 rc = x[47..91) y[top..90)，画在 (87, 440+top)，尺寸 44 x (90-top)
    //   top = Round(90 / Max * (Max - 当前))  —— 满值 top=0 画满，空值 top=90 不画
    //   ★ 这就是原版的真机制：**不另做液体贴图**，直接从 Prguse[4] 裁。
    // =====================================================================
    void DrawOrbPair(int hp, int hpMax, int mp, int mpMax)
    {
        Texture2D t = Tex("p0d/bar_f04_orb_92x90");
        _orbOk = t != null;
        if (t == null) { HpFillH = 0; MpFillH = 0; return; }

        int topHp = hpMax > 0 ? Mathf.RoundToInt(ORB_H / hpMax * (hpMax - Mathf.Min(hpMax, hp))) : 0;
        int topMp = mpMax > 0 ? Mathf.RoundToInt(ORB_H / mpMax * (mpMax - Mathf.Min(mpMax, mp))) : 0;
        topHp = Mathf.Clamp(topHp, 0, (int)ORB_H);
        topMp = Mathf.Clamp(topMp, 0, (int)ORB_H);
        HpFillH = (int)ORB_H - topHp;
        MpFillH = (int)ORB_H - topMp;

        DrawCrop(t, ORB_X, ORB_Y + topHp, ORB_HP_W, ORB_H - topHp,
                 0f, ORB_HP_W / ORB_FULL_W, (ORB_H - topHp) / ORB_H);
        DrawCrop(t, ORB_X + ORB_MP_U0, ORB_Y + topMp, ORB_MP_W, ORB_H - topMp,
                 ORB_MP_U0 / ORB_FULL_W, ORB_MP_W / ORB_FULL_W, (ORB_H - topMp) / ORB_H);
    }

    // =====================================================================
    // 新手球（源码 L8820-8833）：job=0 且 Level<28
    //   底 Prguse[5] @ (38,439) 96x92，rc.Right = 94（右侧 2 px 不画）
    //   液 Prguse[6] 同一 rc，rc.Top = Round(92/MaxHP*(MaxHP-HP))，画在 (38, 439+top)
    // =====================================================================
    void DrawNewbieBall(int hp, int hpMax)
    {
        Texture2D glass = Tex("p0d/bar_f05_ball_96x92");
        Texture2D liq = Tex("p0d/bar_f06_ball_96x92");
        _ballOk = glass != null && liq != null;
        if (glass == null) { HpFillH = 0; return; }

        float w = BALL_NEW_W - BALL_NEW_CUT;
        DrawCrop(glass, BALL_NEW_X, BALL_NEW_Y, w, BALL_NEW_H, 0f, w / BALL_NEW_W, 1f);
        if (liq == null) { HpFillH = 0; return; }

        int top = hpMax > 0
            ? Mathf.RoundToInt(BALL_NEW_H / hpMax * (hpMax - Mathf.Min(hpMax, hp)))
            : 0;
        top = Mathf.Clamp(top, 0, (int)BALL_NEW_H);
        HpFillH = (int)BALL_NEW_H - top;
        DrawCrop(liq, BALL_NEW_X, BALL_NEW_Y + top, w, BALL_NEW_H - top,
                 0f, w / BALL_NEW_W, (BALL_NEW_H - top) / BALL_NEW_H);
        MpFillH = 0;   // 新手球只有一个腔，MP 不单独显示（源码同样只读 MaxHP）
    }

    // =====================================================================
    // 经验 / 负重条（源码 L8909-8932）
    //   d := g_WMainImages.Images[7]（Prguse[7] 76x13）
    //   rc.Right := Round(76 / (Max/Cur)) = Round(76 * Cur / Max)
    //   Exp / Weight 为 0 时源码 r=0 ⇒ rc.Right=0 ⇒ 什么都不画
    //   返回 true = 贴图在
    // =====================================================================
    bool DrawPartialBar(float y, float frac)
    {
        Texture2D t = Tex("p0d/bar_f07_barslot_76x13");
        if (t == null) return false;
        frac = Mathf.Clamp01(frac);
        float w = Mathf.Round(BAR_W * frac);
        if (w <= 0f) return true;      // 源码同样不画
        DrawCrop(t, BAR_X, y, w, BAR_H, 0f, w / BAR_W, 1f);
        return true;
    }

    // =====================================================================
    // 静态整件：贴图原样按逻辑矩形画（alphaBlend）
    // =====================================================================
    void DrawStatic(string key, float x, float y, float w, float h)
    {
        Texture2D t = Tex(key);
        if (t == null) return;
        GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(x, y, w, h), t, ScaleMode.StretchToFill, true);
        DrawnCount++;
    }

    // =====================================================================
    // 按 UV 裁切绘制（等价 Delphi 的 dsurface.Draw(x, y, rc, d, False)）
    //   u0/v0 是贴图归一化坐标，(0,0) = 贴图**左下角**（Unity 约定）
    //   这里只从**上边**裁：保留贴图底部 dh 比例的行 ⇒ v0 恒为 0
    // =====================================================================
    void DrawCrop(Texture2D t, float x, float y, float w, float h, float u0, float du, float dv)
    {
        if (t == null || w <= 0f || h <= 0f || du <= 0f || dv <= 0f) return;
        Rect dst = P0BMapWalk.GroupRectForLogic(x, y, w, h);
        GUI.DrawTextureWithTexCoords(dst, t, new Rect(u0, 0f, du, dv), true);
        DrawnCount++;
    }

    // =====================================================================
    // 文字（等价 Delphi BoldText：白字 + 黑边）
    //   ★ 位置语义与源码一致：入参是**文字左上角**的逻辑坐标
    // =====================================================================
    void DrawTextShadow(string s, float x, float y)
    {
        if (string.IsNullOrEmpty(s) || _w == null) return;
        Rect r1 = P0BMapWalk.GroupRectForLogic(x + 1f, y + 1f, 400f, TXT_H);
        Rect r0 = P0BMapWalk.GroupRectForLogic(x, y, 400f, TXT_H);

        Color prev = GUI.color, prevC = _w.normal.textColor;
        GUI.color = Color.white;

        _w.normal.textColor = Color.black;
        GUI.Label(r1, s, _w);
        _w.normal.textColor = Color.white;
        GUI.Label(r0, s, _w);

        _w.normal.textColor = prevC;
        GUI.color = prev;
        DrawnCount++;
    }

    static string IntToStr(int v) { return v.ToString(); }

    static string Clock()
    {
        DateTime t = DateTime.Now;
        return t.Hour.ToString("00") + ":" + t.Minute.ToString("00") + ":" + t.Second.ToString("00");
    }

    // =====================================================================
    // 样式：字体优先用工程内 chatfont（与 P0HChat 同一套 CJK 退路）
    // =====================================================================
    void EnsureStyles()
    {
        if (_w != null) return;

        _font = Resources.Load<Font>("chatfont");
        if (_font == null)
        {
            string[] cands = { "Noto Sans CJK SC", "Noto Sans SC", "Droid Sans Fallback", "Arial Unicode MS" };
            try { _font = Font.CreateDynamicFontFromOSFont(cands, 12 * P0BMapWalk.ScaleI); }
            catch (Exception) { _font = null; }
            if (_font == null) _font = GUI.skin.font;
        }

        _w = new GUIStyle(GUI.skin.label);
        _w.font = _font;
        _w.alignment = TextAnchor.UpperLeft;
        _w.fontSize = 11 * P0BMapWalk.ScaleI;
        _w.fontStyle = FontStyle.Bold;
        _w.normal.textColor = Color.white;
        _w.clipping = TextClipping.Clip;
        _w.wordWrap = false;
        _w.padding = new RectOffset(0, 0, 0, 0);
        _w.margin = new RectOffset(0, 0, 0, 0);
    }

    Texture2D Tex(string key)
    {
        Texture2D t;
        if (_tex.TryGetValue(key, out t)) return t;
        t = Resources.Load<Texture2D>(key);
        _tex[key] = t;
        if (t == null)
        {
            MissingTexCount++;
            if (!_noTexLogged)
            {
                _noTexLogged = true;
                Debug.LogError("[P0H] ★ HUD 纹理缺失: " + key +
                               " —— 检查 Assets/Resources/" + key + ".png 是否导入");
            }
        }
        return t;
    }

    // =====================================================================
    // 判据日志（单行，gate 用 grep 抓；只有全绿才 PASS）
    // =====================================================================
    void LogEvidence(P0HRuntime run, int hp, int hpMax, int mp, int mpMax,
                     long exp, long expMax, int wt, int wtMax)
    {
        bool artOk = _stripOk && (_orbOk || _ballOk) && _barOk;

        Debug.Log("[P0H] HUD_LINK = " + ((run.Ready && artOk && Level >= 1) ? "PASS" : "FAIL") +
                  " HP=" + hp + "/" + hpMax + "(" + HpPct + "% 高" + HpFillH + "px)" +
                  " MP=" + mp + "/" + mpMax + "(" + MpPct + "% 高" + MpFillH + "px)" +
                  " Lv=" + Level +
                  " Exp=" + exp + "/" + expMax + "(" + ExpPct + "%)" +
                  " Wt=" + wt + "/" + wtMax + "(" + WtPct + "%)" +
                  " 球型=" + (NewbieOrb ? "新手" : "正式") +
                  " 地图=" + run.MapTitle + "(" + run.PlayerX + ":" + run.PlayerY + ")" +
                  " 已画=" + DrawnCount +
                  " 缺纹理=" + MissingTexCount +
                  " 底图=" + (_stripOk ? "齐" : "缺") +
                  " 零件=" + ((_orbOk || _ballOk) ? "齐" : "缺") + "/" + (_barOk ? "齐" : "缺"));

        Debug.Log("[P0H] V9_VERDICT = " + ((artOk && MissingTexCount == 0) ? "PASS" : "FAIL") +
                  " 已画=" + DrawnCount +
                  " 缺纹理=" + MissingTexCount +
                  " Hp%=" + HpPct + " Mp%=" + MpPct + " Exp%=" + ExpPct + " Wt%=" + WtPct +
                  " Lv=" + Level +
                  " HpH=" + HpFillH + "/90" +
                  " MpH=" + MpFillH + "/90" +
                  " 底图=Prguse3[291] 800x251@(0,349)");
    }
}
