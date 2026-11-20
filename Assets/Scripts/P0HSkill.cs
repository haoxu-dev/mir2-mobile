// P0-H 技能层（V9）—— 火球术 / 雷电术 / 治愈术
// ============================================================================
// ★ 这一层解决用户指出的第二个"没联动"病灶：
//   之前只有平砍（空格），没有技能；HP/MP 球的蓝条是死的（只被怪打时回满）。
//   有了本层：按 1/2/3 ⇒ 扣蓝 → 球里蓝液面当场下降 → 怪掉血 → 打死给经验 → 经验条上涨
//   ⇒ HP / MP / 经验 / 等级 四样数据全部挂到**同一个真实循环**上。
//
// ★ 为什么不用 MonoBehaviour：P0BMapWalk 已经是唯一的 MonoBehaviour，
//   本层是被它 new 出来的纯 C# 对象，Tick(dt) 由它每帧调（与 P0HRuntime 同款）。
//
// ★ 为什么死亡要自己写：P0HRuntime.Kill() 是 private，而硬约束禁止改 P0HRuntime。
//   但 P0HEntity 的字段全 public ⇒ 自己置 dead/hp/act/frame 即可，
//   P0HRuntime.TickEntities() 的死亡收尾段（L430-441）会自动 DropLoot + 移除 + 掉落物。
//   经验则显式调 run.AddExp(...)（public）。
//
// ★ 数值口径照 Crystal 的 MagicInfo：Spell=耗蓝、Delay=冷却毫秒、Power=威力基数。
//   伤害 = MC*powerMul + Random(powerAddMin, powerAddMax)。
//   出生 MC=0（P0HRuntime L143），所以 1 级火球约 4~8 伤害 —— 与 1.76 一致：法师前期弱。
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

public class P0HSkill
{
    public const int SK_FIRE = 0, SK_THUNDER = 1, SK_HEAL = 2;
    public const int SK_COUNT = 3;

    // ---------------- 技能定义 ----------------
    class Def
    {
        public string name;
        public int spell;         // 耗蓝
        public int delayMs;       // 冷却（Crystal MagicInfo.Delay）
        public int powerMul;      // 威力倍率 × PlayerMC
        public int addMin, addMax;// 附加随机伤害
        public Color col;
        public bool heal;
        public string hotkey;
    }

    static readonly Def[] DEFS = new Def[]
    {
        new Def { name = "火球术", spell = 8,  delayMs = 800,  powerMul = 2, addMin = 4, addMax = 8,
                  col = new Color(1.00f, 0.55f, 0.15f), heal = false, hotkey = "1" },
        new Def { name = "雷电术", spell = 12, delayMs = 1200, powerMul = 2, addMin = 6, addMax = 12,
                  col = new Color(0.70f, 0.85f, 1.00f), heal = false, hotkey = "2" },
        new Def { name = "治愈术", spell = 10, delayMs = 1500, powerMul = 3, addMin = 6, addMax = 10,
                  col = new Color(0.35f, 1.00f, 0.45f), heal = true,  hotkey = "3" },
    };

    // ---------------- 技能条几何（逻辑 800x600 坐标） ----------------
    // 放在 HUD 面板（逻辑 y=349 起）**正上方**，不压静态层任何像素。
    const float BAR_X = 560f;
    const float BAR_Y = 300f;
    const float SLOT = 40f;
    const float GAP = 4f;
    const float BAR_W = SK_COUNT * SLOT + (SK_COUNT - 1) * GAP;   // 128

    // ---------------- 运行时状态 ----------------
    readonly float[] _cd = new float[SK_COUNT];      // 剩余冷却（秒）
    readonly int[] _castCount = new int[SK_COUNT];
    /// <summary>Cast() 被调用的总次数（含被拒的）。判据用它做分母，见 Cast() 头部注释。</summary>
    int _attempts = 0;
    public int DmgDone { get; private set; }
    public int Kills { get; private set; }
    public int Healed { get; private set; }
    public int CastOk { get; private set; }
    public int CastFail { get; private set; }
    public bool LastCastOk { get; private set; }
    public string LastSkill { get; private set; }

    // 特效
    float _fxT;          // 剩余特效时长
    int _fxIdx;
    Vector2 _fxPos;      // 特效中心（逻辑坐标）

    GUIStyle _key, _nm, _mp;
    static readonly Color SlotBg = new Color(0.06f, 0.06f, 0.08f, 0.72f);
    static readonly Color SlotEdge = new Color(0.62f, 0.55f, 0.38f, 1f);
    static readonly Color CdMask = new Color(0f, 0f, 0f, 0.62f);
    static readonly Color NoMpTint = new Color(0.45f, 0.35f, 0.35f, 0.55f);

    int _lastMp = -1;
    /// <summary>上次打证据日志的帧号。-1 = 还没打过 ⇒ 首帧必打一次（与 P0HHud 同口径）。</summary>
    int _evFrame = -1;
    /// <summary>触摸技能槽的命中计数（&gt;0 才打 SLOT_TOUCH 判据）。</summary>
    int _slotDown = 0;
    int _slotDownIdx = -1;

    // =====================================================================
    // 每帧推进（由 P0BMapWalk 在 _hRun.Tick 之后调用）
    // =====================================================================
    public void Tick(float dt, P0HRuntime run)
    {
        if (dt <= 0f) return;
        for (int i = 0; i < SK_COUNT; i++)
            if (_cd[i] > 0f) { _cd[i] -= dt; if (_cd[i] < 0f) _cd[i] = 0f; }

        if (_fxT > 0f) _fxT -= dt;

        if (run == null) return;
        if (run.PlayerMp != _lastMp)
        {
            _lastMp = run.PlayerMp;
            LogEvidence(run);
        }
        // ★ 2026-10-23 22:57 加：首帧 + 每 300 帧各打一次。
        //   原实现只靠「MP 变了」触发 ⇒ 没施过法 / 没受伤的设备 MP 恒定 ⇒ 一条日志都没有。
        //   与 P0HHud 的 LogEvidence 采用同一节流口径（首帧 + 每 300 帧），
        //   保证「功能没触发」和「日志抓漏」在 logcat 里可区分。
        else if (_evFrame < 0 || Time.frameCount - _evFrame >= 300)
        {
            _evFrame = Time.frameCount;
            LogEvidence(run);
        }
    }

    // =====================================================================
    // 施法。idx ∈ {0,1,2}；返回是否真的放出去了
    // =====================================================================
    public bool Cast(int idx, P0HRuntime run)
    {
        // ★ 2026-10-24 修判据（build-runner 设备实证，原文：「`_castCount[idx]++` 只在施放成功时
        //   执行，所以 TotalCasts() 恒等于 CastOk，(CastOk+CastFail)==TotalCasts() 就退化成 CastFail==0
        //   ⇒ 只要发生过一次失败（冷却中/魔法不足/无目标），这条就永远 FAIL」）。
        //   确实如此：`_castCount` 只在 L141 成功分支里 ++，拿它当"尝试总数"是错的。
        //   ⇒ 新增 _attempts 在**入口**无条件累加，判据改成 (CastOk+CastFail)==_attempts，
        //     这才是真不变量（每次调用要么进 CastOk 要么进 CastFail，一条都不能漏计）。
        _attempts++;

        if (run == null || idx < 0 || idx >= SK_COUNT)
        {
            CastFail++; LastCastOk = false;
            return false;
        }
        Def d = DEFS[idx];
        LastSkill = d.name;

        if (_cd[idx] > 0f)
        {
            run.EmitEvent(P0HRuntime.P0HEventKind.Cooldown, d.name + " 冷却中 " + _cd[idx].ToString("0.0") + "s");
            CastFail++; LastCastOk = false;
            return false;
        }
        if (run.PlayerMp < d.spell)
        {
            // ★ 原版文案（ClMain.pas:3430，GBK 原文）：
            //     DScreen.AddSysMsg(Format('需要 %d 魔法值才能释放 %s', [SpellSpend, pcm.Def.sMagicName]))
            //   走的是左上绿 AddSysMsg，不是聊天框。这里只拼文案，通道归 P0HNotify.Route。
            // ⚠ 原版还有 1 秒节流（L3427 g_MPLessHintTick > 1000），节流在 P0HNotify 里做，
            //   因为那里才是唯一上屏口。
            run.EmitEvent(P0HRuntime.P0HEventKind.ManaShort,
                          "需要 " + d.spell + " 魔法值才能释放 " + d.name, d.spell);
            CastFail++; LastCastOk = false;
            return false;
        }

        run.PlayerMp -= d.spell;
        _cd[idx] = d.delayMs / 1000f;
        _castCount[idx]++;
        _fxIdx = idx;
        _fxT = 0.32f;

        // ---------- 治愈：直接回血 ----------
        if (d.heal)
        {
            int h = run.PlayerMC * d.powerMul + Random.Range(d.addMin, d.addMax + 1);
            int before = run.PlayerHp;
            run.PlayerHp = Mathf.Min(run.PlayerHpMax, run.PlayerHp + h);
            int real = run.PlayerHp - before;
            Healed += real;
            _fxPos = new Vector2(400f, 300f);      // 相机跟随玩家 ⇒ 屏幕中心即自身
            run.EmitEvent(P0HRuntime.P0HEventKind.Heal, d.name + " 回复 " + real + " HP（剩 " + run.PlayerHp + "/" + run.PlayerHpMax + "）");
            CastOk++; LastCastOk = true;
            LogEvidence(run);
            return true;
        }

        // ---------- 攻击：找最近的活怪 ----------
        var tgt = FindTarget(run);
        if (tgt == null)
        {
            run.EmitEvent(P0HRuntime.P0HEventKind.SkillNoTarget, d.name + " 没有目标（8 格内无活怪）");
            CastFail++; LastCastOk = false;
            // 蓝已经扣了 —— 与原版一致：放空技能照样耗蓝
            LogEvidence(run);
            return false;
        }

        _fxPos = WorldToLogic(tgt.x, tgt.y, run);
        int dmg = Mathf.Max(1, run.PlayerMC * d.powerMul + Random.Range(d.addMin, d.addMax + 1));
        tgt.hp -= dmg;
        DmgDone += dmg;
        // ★ 技能伤害飘字（原版走 m_DamageList 的 Damage2 分支：字号同、32 帧就删）。
        run.EmitHitFx(tgt.x, tgt.y, dmg, true);

        if (tgt.hp <= 0)
        {
            tgt.hp = 0;
            tgt.dead = true;
            tgt.deadT = 0f;
            // 复刻 P0HRuntime.Play(e, DIE) 的可观测效果（Play 是 private）
            tgt.act = "Die"; tgt.frame = 0; tgt.actLen = 10; tgt.frameMs = 100; tgt.animT = 0f; tgt.actT = 0f;
            if (tgt.sr != null) tgt.sr.color = new Color(0.85f, 0.75f, 0.75f, 1f);
            Kills++;
            // 经验必须在这里给：死亡收尾段只 DropLoot，不 AddExp（AddExp 在 private Kill 里）
            run.AddExp(Mathf.Max(1, tgt.exp));
            run.EmitEvent(P0HRuntime.P0HEventKind.Kill, d.name + " 击杀 " + tgt.name + " -" + dmg + "（+" + tgt.exp + " EXP）", tgt.exp);
        }
        else
        {
            tgt.act = "Struck"; tgt.frame = 0; tgt.actLen = 2; tgt.frameMs = 100; tgt.animT = 0f; tgt.actT = 0f;
            run.EmitEvent(P0HRuntime.P0HEventKind.CombatHit, d.name + " 打 " + tgt.name + " -" + dmg + "（剩 " + tgt.hp + "/" + tgt.hpMax + "）");
        }

        CastOk++; LastCastOk = true;
        LogEvidence(run);
        return true;
    }

    /// <summary>找 8 格内最近的活怪（切比雪夫距离，与 P0HRuntime AI 的视野口径一致）。</summary>
    static P0HEntity FindTarget(P0HRuntime run)
    {
        if (run.Entities == null) return null;
        P0HEntity best = null;
        int bestD = int.MaxValue;
        for (int i = 0; i < run.Entities.Count; i++)
        {
            var e = run.Entities[i];
            if (e == null || e.dead || e.IsNpc) continue;
            int dd = Mathf.Max(Mathf.Abs(e.x - run.PlayerX), Mathf.Abs(e.y - run.PlayerY));
            if (dd > 8) continue;
            if (dd < bestD) { bestD = dd; best = e; }
        }
        return best;
    }

    /// <summary>
    /// 格坐标 → 逻辑屏幕坐标。
    /// ★ 相机每帧 Lerp 跟随玩家，玩家基本恒在屏幕中心（逻辑 400,300），
    ///   故用「相对玩家的格差 × 一格逻辑像素」近似，零相机依赖、不会因矩阵换算错位。
    ///   一格 = 48 逻辑像素（原版 Tiles 16x32，取横向 48 是 1.76 的等距标准）。
    /// </summary>
    static Vector2 WorldToLogic(int cx, int cy, P0HRuntime run)
    {
        const float CELL = 48f;
        float dx = (cx - run.PlayerX) * CELL;
        float dy = (cy - run.PlayerY) * CELL;
        return new Vector2(400f + dx, 300f + dy);
    }

    // =====================================================================
    // 绘制：技能条 + 特效（在 P0BMapWalk 的 BeginGroup 内调用）
    // =====================================================================
    /// <summary>
    /// 槽位命中判定用的回调：宿主注入「这点是不是别人的」（P0-I 面板优先级更高）。
    /// 为空则视为无人认领。★ 不注入也能跑，只是丢掉了「面板打开时让位」这一条。
    /// </summary>
    public System.Func<Vector2, bool> PointOwnedByOther;

    /// <summary>
    /// ★ 2026-10-24 加：技能栏的**触摸**入口。
    ///   为什么必须加（device 实证，不是推测）：本层原先只有 P0BMapWalk 里
    ///   `Input.GetKeyDown(KeyCode.Alpha1/2/3)` 一条施法路径，而同文件 L1210 已实测记录
    ///   「`adb shell input keyevent` 不进 Unity 传统 Input Manager」⇒ 真机上
    ///   **没有任何办法把技能放出去**，判据 [P0S] V9_VERDICT 恒为 FAIL（CastOk 恒 0）。
    ///   画着却点不动 = 用户最反感的那种"看着有 UI、其实没联动"。
    ///   ⇒ 与 ChatBtn/AttackBtn 同一套已验证姿势：raw Event.current + 命中矩形 + e.Use()。
    ///   命中矩形用 GroupRectForLogic（组内相对），与绘制严格同源，不会有点击偏移。
    /// </summary>
    void SlotClick(P0HRuntime run, int i, float sx, float sy)
    {
        var e = Event.current;
        if (e == null) return;
        if (e.type != EventType.MouseDown && e.type != EventType.MouseUp) return;

        Rect hit = P0BMapWalk.GroupRectForLogic(sx, sy, SLOT, SLOT);
        if (!hit.Contains(e.mousePosition)) return;
        if (PointOwnedByOther != null && PointOwnedByOther(e.mousePosition)) return;

        if (e.type == EventType.MouseDown)
        {
            _slotDown++;
            _slotDownIdx = i;
            bool ok = Cast(i, run);
            Debug.Log("[P0S] 触摸技能槽 idx=" + i + " 名=" + DEFS[i].name + " -> " +
                      (ok ? "施放成功" : ("未放出(冷却=" + _cd[i].ToString("F2") + "s 蓝=" + run.PlayerMp + "/" + run.PlayerMpMax + ")")));
        }
        e.Use();
    }

    // ★★★ 2026-10-24：真 1.76 客户端主界面**没有技能框**（实测 ref 全帧 800x600 里
    //   没有任何 128x40 的暗底亮字方阵；用户原话「你别自己瞎折腾…别自己瞎几把画」）。
    //   本层原来是自绘 3 个 40x40 方格 + 中文名 + 蓝耗数字，纯属自己发明的 UI。
    //   ⇒ 默认 HIDDEN。逻辑（施法/冷却/伤害）全部保留并照旧 Tick，只是不再上屏。
    //   需要调试时按数字键 1/2/3 仍可施法（键盘路径不受影响）。
    public bool Visible = false;

    public void DrawBar(P0HRuntime run)
    {
        if (run == null) return;
        EnsureStyles();

        DrawFx();          // ★ 特效属于"打出去的东西"，保留（真客户端打怪也有火球/闪电）

        if (!Visible) return;   // ← 默认不上屏：真客户端主界面不画技能框

        // 背板
        Rect barR = P0BMapWalk.GroupRectForLogic(BAR_X - 3f, BAR_Y - 3f, BAR_W + 6f, SLOT + 6f);
        GUI.color = new Color(0.03f, 0.03f, 0.05f, 0.55f);
        GUI.DrawTexture(barR, Texture2D.whiteTexture, ScaleMode.StretchToFill, true);

        for (int i = 0; i < SK_COUNT; i++)
        {
            Def d = DEFS[i];
            float sx = BAR_X + i * (SLOT + GAP);
            Rect r = P0BMapWalk.GroupRectForLogic(sx, BAR_Y, SLOT, SLOT);

            // 底
            GUI.color = SlotBg;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true);

            // 蓝不够 ⇒ 灰化
            bool noMp = run.PlayerMp < d.spell;
            if (noMp)
            {
                GUI.color = NoMpTint;
                GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
            }

            // 顶部技能色条（4 逻辑像素高）
            GUI.color = d.col;
            GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(sx, BAR_Y, SLOT, 4f),
                            Texture2D.whiteTexture, ScaleMode.StretchToFill, true);

            // 冷却：从下往上盖黑
            if (_cd[i] > 0f)
            {
                float frac = Mathf.Clamp01(_cd[i] / (d.delayMs / 1000f));
                float h = SLOT * frac;
                GUI.color = CdMask;
                GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(sx, BAR_Y + SLOT - h, SLOT, h),
                                Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
            }

            // 边框（4 条）
            GUI.color = SlotEdge;
            DrawEdge(sx, BAR_Y, SLOT, SLOT, 1f);

            // 技能名（槽内居中偏下）
            GUI.color = noMp ? new Color(0.75f, 0.65f, 0.65f, 1f) : Color.white;
            GUI.Label(P0BMapWalk.GroupRectForLogic(sx, BAR_Y + 12f, SLOT, 14f), d.name, _nm);

            // 耗蓝
            GUI.color = run.PlayerMp < d.spell ? new Color(1f, 0.4f, 0.4f) : new Color(0.55f, 0.75f, 1f);
            GUI.Label(P0BMapWalk.GroupRectForLogic(sx, BAR_Y + 24f, SLOT, 12f), d.spell + "", _mp);

            // 快捷键角标（2026-10-24 修：原 x=SOLT-12 只有 12 逻辑宽 = 24 物理 px，
            // 两位数角标必被切；且贴右边框 1 逻辑 px 看不出留白）
            GUI.color = new Color(1f, 0.95f, 0.55f, 1f);
            GUI.Label(P0BMapWalk.GroupRectForLogic(sx + SLOT - 16f, BAR_Y + 1f, 15f, 13f), d.hotkey, _key);

            // ★ 触摸命中：必须放在本槽**所有绘制之后**再判，
            //   否则 e.Use() 会把同一次事件吃掉，后面的槽位连绘制都受影响（IMGUI 事件流是全局的）。
            //   实际顺序上逐槽独立判命中，互不影响：命中矩形按 sx 各自算，天然不重叠。
            SlotClick(run, i, sx, BAR_Y);
        }
        GUI.color = Color.white;
    }

    void DrawEdge(float x, float y, float w, float h, float t)
    {
        GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(x, y, w, t), Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
        GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(x, y + h - t, w, t), Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
        GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(x, y, t, h), Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
        GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(x + w - t, y, t, h), Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
    }

    // ---------------- 特效：施法瞬间在两处各闪 0.32 秒 ----------------
    void DrawFx()
    {
        if (_fxT <= 0f) return;
        float a = Mathf.Clamp01(_fxT / 0.32f);          // 1 → 0
        Def d = DEFS[Mathf.Clamp(_fxIdx, 0, SK_COUNT - 1)];
        float r = 10f + 26f * (1f - a);                 // 扩散圈

        Color prev = GUI.color;
        // 中心光斑
        GUI.color = new Color(d.col.r, d.col.g, d.col.b, 0.45f * a);
        GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(_fxPos.x - r, _fxPos.y - r, r * 2f, r * 2f),
                        Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
        // 外圈
        GUI.color = new Color(d.col.r, d.col.g, d.col.b, 0.75f * a);
        DrawEdge(_fxPos.x - r - 2f, _fxPos.y - r - 2f, r * 2f + 4f, r * 2f + 4f, 2f);
        GUI.color = prev;
    }

    // ---------------- 样式 ----------------
    void EnsureStyles()
    {
        if (_nm != null) return;
        int s = P0BMapWalk.ScaleI;

        _nm = new GUIStyle(GUI.skin.label);
        _nm.alignment = TextAnchor.MiddleCenter;
        _nm.fontSize = 8 * s;
        _nm.normal.textColor = Color.white;
        _nm.clipping = TextClipping.Clip;

        _mp = new GUIStyle(_nm);
        _mp.fontSize = 8 * s;

        _key = new GUIStyle(_nm);
        _key.fontSize = 8 * s;
        _key.alignment = TextAnchor.UpperRight;
    }

    // ---------------- 判据日志 ----------------
    void LogEvidence(P0HRuntime run)
    {
        bool ok = run != null
                  && run.PlayerMp >= 0 && run.PlayerMp <= run.PlayerMpMax
                  && (CastOk + CastFail) == _attempts
                  && DEFS.Length == SK_COUNT;

        Debug.Log("[P0S] SKILL_LINK = " + (ok ? "PASS" : "FAIL") +
                  " MP=" + (run == null ? -1 : run.PlayerMp) + "/" + (run == null ? -1 : run.PlayerMpMax) +
                  " 冷却=" + CdText() +
                  " 施放=" + CastOk + "/" + (CastOk + CastFail) +
                  " 伤害=" + DmgDone + " 击杀=" + Kills + " 治疗=" + Healed +
                  " | 最近: " + (LastSkill == null ? "无" : LastSkill));

        // ★ 2026-10-23 22:56 改：原实现把 V9_VERDICT 包在 `if (CastOk>0 || CastFail>0)` 里，
        //   导致「一台没注入输入的设备」永远打不出这条判据 —— 验收方只能看到 SKILL_LINK，
        //   分不清是「技能没接上」还是「没人按过键」。那是**用沉默冒充通过**。
        //   现在无条件打：没施放过就如实写 施放成功=0，verdict 自然判 FAIL，
        //   装到设备上一眼就能看出是「功能没触发」而不是「日志抓漏」。
        //
        // ★ 2026-10-24 修（build-runner 设备实证）：原来的 ok 里用 TotalCasts() 当分母，
        //   而 _castCount 只在成功分支 ++ ⇒ 分母恒等于 CastOk ⇒ 判据退化成 `CastFail==0`，
        //   一次「魔法不足」就永久 FAIL。现改为 _attempts（入口无条件计数），
        //   语义是「每一次 Cast 调用都被如实记入成功或失败，一条不漏」——
        //   这才是这条判据本来想守的不变量。⇒ 允许失败，但不允许账目不平。
        bool verdict = ok && (CastOk > 0);
        Debug.Log("[P0S] V9_VERDICT = " + (verdict ? "PASS" : "FAIL") +
                  " 施放成功=" + CastOk + " 失败=" + CastFail + " 尝试=" + _attempts +
                  " 各技能(火/雷/愈)=" + CastPerSkill() +
                  " 造成伤害=" + DmgDone + " 技能击杀=" + Kills + " 治疗量=" + Healed +
                  " MP=" + (run == null ? -1 : run.PlayerMp) + "/" + (run == null ? -1 : run.PlayerMpMax) +
                  (CastOk + CastFail == 0 ? " (尚未施放过：按 1/2/3 或点技能栏)" : ""));

        // ★ 2026-10-24 加：技能栏的**触摸入口活着**这件事自带判据。
        //   V9_VERDICT 依赖「有人真的放出过技能」；这一条只证明触摸命中被走到过，
        //   与「技能能不能放」解耦，用来区分「没人点」和「点了没反应」。
        //   ⚠ 早前那句「设备上按键进不了 Unity，只有 tap 能进」已被 build-runner 实测证伪：
        //     真因是**全屏沉浸提示弹窗抢焦点**，`settings put secure immersive_mode_confirmations confirmed`
        //     之后 keyevent 8/9/10（1/2/3）与 141（F12）全部正常。⇒ 键盘与触摸两条通道都可用，
        //     触摸入口仍要保留（真机手点才是用户的真实路径）。
        if (_slotDown > 0)
        {
            Debug.Log("[P0S] SLOT_TOUCH = PASS 命中次数=" + _slotDown +
                      " 最近槽=" + _slotDownIdx + " 当前施放成功=" + CastOk);
        }
    }

    /// <summary>
    /// 各技能分别成功施放几次（「火球/雷电/治愈」顺序）。
    /// ★ 2026-10-24：原来它叫 TotalCasts() 并被判据当"尝试总数"用 —— 那是错的口径
    ///   （只数成功）。判据已改用 _attempts，这里退回它本来的意义：
    ///   **每槽成功次数**，用来证明三个技能都被真放过、不是只测了第一个。
    /// </summary>
    string CastPerSkill()
    {
        return _castCount[0] + "/" + _castCount[1] + "/" + _castCount[2];
    }

    string CdText()
    {
        return _cd[0].ToString("0.0") + "/" + _cd[1].ToString("0.0") + "/" + _cd[2].ToString("0.0");
    }
}
