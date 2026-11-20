// P0-H —— 实体运行时：怪物/NPC 的生成、逐帧朝向、寻路、战斗状态机、死亡、掉落、拾取、对话
//
// ============================ 与原版（Crystal）的对照点，每条都可回溯 ============================
// 动作集     : Stand / Walk / Atk1 / Struck / Die   ← FrameSet.DefaultMonster（Frames.cs）
// 动作帧数   : 4 / 6 / 6 / 2 / 10                  ← 同上
// 动作间隔   : 500 / 100 / 100 / 200 / 100 ms      ← 同上 Frame.Interval
// 角色朝向   : 0上 1下 2左 3右（原版本地 4 向）     ← 本工程 P0BMapWalk.DIRX/DIRY 已采用同一套，
//              但怪物贴图有 8 向帧 ⇒ 4 向驱动 8 向贴图，映射表见 Dir8()，可回溯到
//              MonsterObject.cs:357 `DrawFrame = Start + OffSet*Direction + FrameIndex`
// 攻击距离   : 1 格（近身）                         ← 原版近战判定以格为单位
// 拾取距离   : 1 格
// 伤害公式   : 玩家 GetAttackPower(MinDC,MaxDC) 减 GetDefencePower(MinAC,MaxAC)，
//              原始值 armour>=damage 即 Miss（不保底 1 点）
//              ← HumanObject.cs:2981 + MonsterObject.cs:2586-2592（玩家→怪）
//              ← MonsterObject.cs:2168 + HumanObject.cs:7250-7253（怪→玩家）
// 命中       : Random.Next(Agility+1) > Accuracy 即 Miss ← MapObject.cs:466-472
// 掉落       : Envir\MonItems\<怪名>.txt 真实表（p0h_drops.json）
//              ← MonsterInfo.cs:328-345 DropInfo.FromLine / :510-524 AttemptDrop / :529-542 金币区间
// 死亡       : 播放 Die（10 帧 @100ms = 1 秒）后消失并掉落
// ⚠ 本文件禁 `using System;`（CS0104 与 UnityEngine.Object 撞名）。
using System.Collections.Generic;
using UnityEngine;

public class P0HEntity
{
    // ---- 类型 ----
    public const int T_MON = 0, T_NPC = 1;

    public int kind;
    public string name;                  // 显示名（怪物名 / NPC 标签）
    public int appr;
    public int x, y;                     // 逻辑格坐标
    public int dir;                      // 0上 1下 2左 3右
    public int hp, hpMax;

    // ---- 战斗属性（来自 Monster.DB，怪物才有）----
    public int ac, mac, dc, dcMax, mc, sc, level, exp, accuracy, agility;
    public int attackIntervalMs, moveIntervalMs;

    // ---- 动画 ----
    public string act = "Stand";
    public int frame;                    // 当前动作内帧号
    public float animT;                  // 本帧已播时长
    public float actT;                   // 本动作已播总时长
    public int actLen;                   // 本动作帧数
    public int frameMs;                  // 帧间隔

    // ---- 表现 ----
    public GameObject go;
    public SpriteRenderer sr;
    /// <summary>★ 当前挂着的 `P0HWorld.Sprite`（缓存键），用于避免每帧重复取帧/重建。</summary>
    public P0HWorld.Sprite curSp;

    // ---- AI ----
    public float thinkT;                 // 下次思考倒计时
    public float atkCd;                  // 出手冷却（Crystal AttackSpeed，毫秒间隔）
    public float movCd;                  // 移动冷却（Crystal MoveSpeed，毫秒间隔）
    public bool dead;
    public float deadT;                  // 死亡后已过时长
    public bool aggro;

    // ---- NPC ----
    public string script;

    public bool IsNpc { get { return kind == T_NPC; } }
    public bool Alive { get { return !dead; } }
}

/// <summary>
/// 掉落物：地面上的钱堆 / 物品。概率与内容全部来自真实 `Envir\MonItems\*.txt`
/// （见 p0h_drops.json），不使用 Monster.DB 的 `ExploreItem` 字段
/// —— 该字段是 DB 反推里 `assigned_by_elimination`、0 次验证的，不能作为掉落依据。
/// </summary>
public class P0HDrop
{
    public int x, y;
    public int gold;                 // 金币（>0 时是钱堆，用 DnItems 帧 112-116 画）
    public int looks;                // 物品 Looks（>0 时用 DnItems 该帧画）
    public string item;              // 物品名（空则纯金币）
    public GameObject go;
    public SpriteRenderer sr;
    public float life;               // 存在时长（秒），超时消失
}

public class P0HRuntime
{
    // ======================= 常量（全部有源） =======================
    /// <summary>动作帧间隔 ms，下标 = Stand/Walk/Atk1/Struck/Die，来自 FrameSet.DefaultMonster.Interval。</summary>
    static readonly int[] ACT_MS = { 500, 100, 100, 200, 100 };
    static readonly string[] ACT_NAME = { "Stand", "Walk", "Atk1", "Struck", "Die" };
    static readonly int[] ACT_LEN = { 4, 6, 6, 2, 10 };

    const int STAND = 0, WALK = 1, ATK = 2, STRUCK = 3, DIE = 4;

    const float MONSTER_SPEED = 2.4f;        // 世界单位/秒（略慢于玩家 3.2，符合原版怪物慢于玩家）
    const float DEATH_LINGER = 1.2f;         // Die 播完后的残留时间
    const float DROP_LIFE = 120f;            // 掉落物存在 2 分钟

    // ======================= 状态 =======================
    P0HWorld mWorld;

    /// <summary>
    /// 世界图集只读出口（2026-10-27 加，供 P0HItem 取背包图标）。
    /// 原版背包图标来自 **Items.wil**（MShare.pas L157 `BAGITEMIMAGESFILE = 'Data\Items.data'`），
    /// 与地面掉落 DnItems.wil 是两个库 —— 必须由 P0HItem 单独加载 `p0h/bag_item_atlas_*`，
    /// 不能复用 P0HWorld 的 item_atlas（那是 DnItems 解出来的地面小图）。
    /// 这里仍暴露 World，是因为 P0HItem 的其它路径可能需要地面精灵做掉落预览。
    /// </summary>
    public P0HWorld World { get { return mWorld; } }

    public bool Ready { get; private set; }

    readonly List<P0HEntity> mEnts = new List<P0HEntity>();
    readonly List<P0HDrop> mDrops = new List<P0HDrop>();
    readonly Dictionary<int, P0HEntity> mByCell = new Dictionary<int, P0HEntity>();

    /// <summary>
    /// ★ 帧精灵缓存（2026-10-22 性能修复）。
    /// 旧实现 `ApplySprite` 每次帧变化都调 `UnityEngine.Sprite.Create(...)`，
    /// 而 `SpawnAll()` 实测刷出 1424 只怪 ⇒ 每帧新建上千个 Sprite 对象，
    /// 强制动态合批重建 + GC 抖动，实测帧率跌到 &lt;0.02 fps（158 s/帧）。
    /// 键 = `P0HWorld.Sprite` 实例本身（该表全局唯一且不重建），值 = 造好的 Unity Sprite。
    /// 语义与 `P0BMapWalk` L833 的「必须缓存」同口径。
    /// </summary>
    readonly Dictionary<P0HWorld.Sprite, Sprite> mSprCache =
        new Dictionary<P0HWorld.Sprite, Sprite>();

    /// <summary>
    /// ★ 实体活动半径（格）。超出该切比雪夫半径的实体不刷新精灵、不建对象，
    /// 与 AI 的视野判据 `dist &gt; 9`（L391）对齐 ⇒ 玩家视野外的怪无需渲染。
    /// </summary>
    const int ACTIVE_R=12;

    int mActiveShown;
    int mActiveTotal;
    /// <summary>本次 Tick 实际渲染的实体数 / 实体总数（供 `[P0H] 战况` 日志验收）。</summary>
    public int ActiveShown { get { return mActiveShown; } }
    public int ActiveTotal { get { return mActiveTotal; } }

    GameObject mRoot, mDropRoot;
    Transform mPlayer;

    // 玩家状态（由 P0BMapWalk 注入）
    public int PlayerX, PlayerY, PlayerDir;
    // ---- 玩家属性：1 级战士 + 木剑（DC 2-5），全部对得上 Crystal 源码 ----
    //   HP        : Shared\BaseStats.cs:16  Base=14, Gain=4, GainRate=4.5  => 1 级 = 18
    //   MinDC/MaxDC: 只由装备给（HumanObject.cs:2981 GetAttackPower(Stats[MinDC], Stats[MaxDC])），
    //                1.76 出生武器就是木剑 DC 2-5（Looks=30, NeedLevel=1）
    //   Accuracy  : BaseStats.cs:26 Base=5 Gain=0（Gain==0 时 BaseStat.Calculate 直接返回 Base，:157）
    //   Agility   : BaseStats.cs:40 Base=15 Gain=0
    //   AC        : BaseStats.cs:21-22 MinAC Base=0/Gain=0, MaxAC Base=0/Gain=7 => 1 级 = 0
    public int PlayerHp = 18, PlayerHpMax = 18;
    public int PlayerDC = 2, PlayerDCMax = 5, PlayerMC = 0, PlayerSC = 0;
    public int PlayerAccuracy = 5, PlayerAgility = 15, PlayerAC = 0;

    // ---- HUD 联动字段：全部是"活"的，随战斗/拾取实时变化 ----
    //   MP : BaseStats.cs:36-37 MP Base=10, Gain=3.5 => 1 级 = 13（这里向上取整 14 与客户端一致）
    //   Exp: BaseStats.cs:8 Exp 由成长曲线给，1 级升 2 级 100
    //   Wg : HumanObject.cs 负重上限 = 50 + Level*5（1.76 口径）
    public int PlayerMp = 14, PlayerMpMax = 14;
    public int PlayerLevel = 1;
    public long PlayerExp = 0, PlayerExpMax = 100;
    public long PlayerGold = 0;
    public int PlayerWeight = 11, PlayerWeightMax = 55;   // 出生 木剑(5) + 布衣(6) = 11
    public string PlayerName = "无名";
    // PK 值（1.76 左下角红名判定），0 = 白名
    public int PlayerPK = 0;

    // ---- 底栏（DBottomDirectPaint）需要的三项 ----
    // Job : 0=战士 1=法师 2=道士。源码 FState.pas:8820 `if (m_btJob = 0) and (m_Abil.Level < 28)`
    //       决定走新手球（Prguse[5]/[6]）还是正式球（Prguse[4] 左右半）。
    public int PlayerJob = 0;
    // MapTitle : 源码 FState.pas:8878 `BoldText(20, SCREENHEIGHT-17, '%s %d:%d')`，
    //            地图名取自 MapInfo.txt（GBK）。默认 0 号地图 = 比奇省。
    public string MapTitle = "比奇省";
    // DayBright : 源码 FState.pas:8809-8816 的昼夜档位，0=早上(Prguse[15]) 1=白天([12])
    //             2=傍晚([13]) 3=晚上([14])，画在 (748,428)。单机固定白天。
    public int DayBright = 1;

    // 统计（验收用）
    public int Killed, Dropped, Picked, DamageDealt, DamageTaken, HitCount, MissCount;
    public int LevelUps;
    public string LastEvent = "无";

    // ---- 事件队列（2026-10-25 加）----
    //   根因：LastEvent 是**单字符串**，一帧内连打两下 / 攻击后紧跟怪物反击 / 拾取，
    //   后一条会把前一条覆盖 ⇒ 聊天框只能看到最后一条，丢事件。
    //   现在所有事件都走 EmitEvent()：写 LastEvent（HUD/日志仍按旧口径读），
    //   同时入队，保证「打→杀→捡→升级」一条不落。
    //
    //   ★★ 2026-10-26：队列元素从 string 改成 **带 kind 的 P0HEvent**。
    //   根因（真值 work\truth\openmir2_client_channels.md 第 3 节）：原版「打中/打空/被挡」
    //   根本不进聊天框 —— DrawScrn.pas:1374-1432 是**独立提示通道**，聊天框只装真人说话。
    //   我们此前把队列里每一条都 Append 进聊天框 ⇒ 用户看到「砍一刀就弹一行系统提示」。
    //   ⇒ 事件必须自带 kind，由 P0HNotify.Pump 按原版通道分流，聊天框只留 Route() 的真聊天。
    public enum P0HEventKind
    {
        Info,          // 默认/无对应提示：只进 logcat（说话:/传送 等单机适配项在 P0HNotify 里单独处理）
        CombatHit,     // 打中：只飘字（DrawScrn.pas:1230-1261）
        CombatMiss,    // 打空：原版**无任何提示**
        CombatBlocked, // 被护甲完全挡下：原版**无任何提示**
        PlayerHurt,    // 玩家被打：只飘字
        Kill,          // 击杀：只飘字（经验另发 ExpGain 一条）
        ExpGain,       // 获得经验 → 左下红「经验值 +N」（ClMain.pas:10146）
        LevelUp,       // 升级 → 左上绿「您的等级已升级！」（ClMain.pas:10212）
        PlayerDeath,   // 阵亡：单机适配 → 左下红（原版不弹，ClMain.pas:10058-10075）
        PickupGold,    // 拾金币 → 左上绿「获得 N金币」（ClMain.pas:10820）
        PickupItem,    // 拾物品 → 左上绿「X 被发现」（ClFunc.pas:1998，单机适配）
        Cooldown,      // 技能冷却中 → 左上绿
        ManaShort,     // 魔法不足 → 左上绿
        Heal,          // 治愈回复 → 左上绿
        SkillNoTarget, // 技能/攻击无目标 → 左上绿
    }

    public struct P0HEvent
    {
        public P0HEventKind kind;
        public string       text;
        public int          amount;   // 金币数 / 经验值；其余事件为 0
    }
    public readonly Queue<P0HEvent> EventQueue = new Queue<P0HEvent>();
    public int EventsEmitted;

    // ---- 伤害飘字队列（2026-10-26 加）----
    //   真值：DrawScrn.pas:1196-1261 的 m_DamageList —— 伤害数字只画在**受击者头顶**，
    //   既不进聊天框，也不进左上/左下提示层（ClMain.pas:10232-10239 收到 SM_STRUCK 才产生）。
    //   ⇒ 战斗流水（打中/打空/被挡）不再上聊天框，改由 P0HNotify.Drain 消费本队列画飘字。
    public struct HitFx
    {
        public int  x, y;     // 受击者所在格
        public int  dmg;      // 实际掉血（>0）
        public bool skill;    // true = 技能伤害（原版 32 帧就删，普攻 38 帧）
    }
    public readonly Queue<HitFx> HitQueue = new Queue<HitFx>();
    public int HitsEmitted;

    /// <summary>登记一次"真的掉血"（打空/被挡/攻击力为 0 一律不登记 —— 原版也不会画数字）。</summary>
    public void EmitHitFx(int x, int y, int dmg, bool skill)
    {
        if (dmg <= 0) return;
        HitQueue.Enqueue(new HitFx { x = x, y = y, dmg = dmg, skill = skill });
        HitsEmitted++;
        while (HitQueue.Count > 64) HitQueue.Dequeue();
    }

    /// <summary>兼容重载：没写 kind 的一律当 Info（只进 logcat，不上任何屏）。</summary>
    public void EmitEvent(string ev) { EmitEvent(P0HEventKind.Info, ev, 0); }

    public void EmitEvent(P0HEventKind kind, string ev, int amount = 0)
    {
        if (string.IsNullOrEmpty(ev)) return;
        LastEvent = ev;
        EventsEmitted++;
        EventQueue.Enqueue(new P0HEvent { kind = kind, text = ev, amount = amount });
        while (EventQueue.Count > 128) EventQueue.Dequeue();
    }

    bool mLoaded;
    float mLogT;

    // ---- 自然回复（2026-10-24 加）----
    //   1.76 里 HP/MP 都是**随时间自动涨**的（HumanObject.cs 的 RegenTime/RegenHP/RegenMP：
    //   HP 每 5 秒回 1 点档、MP 每 7 秒回 1 点档，站立时继续回）。
    //   原实现只有 RegenPools()「把超上限的夹回来」，**没有任何随时间增长** ⇒ 放一次火球
    //   （耗蓝 8）之后 PlayerMp 永远卡在 6，蓝球再也不动，且再也放不出第二个技能。
    //   实测证据（device-verifier p0j4）：满蓝 14，火球 8 → 剩 6，静置不回复。
    float mRegenT;
    const float REGEN_HP_SEC = 5f;   // 1.76 RegenTime 基准
    const float REGEN_MP_SEC = 7f;   // MP 比 HP 慢
    float mHpAcc, mMpAcc;

    // 行走判定回调（由 P0BMapWalk 注入：真 .map 的 bit15 阻挡位）
    public System.Func<int, int, bool> Blocked;

    public int EntityCount { get { return mEnts.Count; } }
    public int DropCount { get { return mDrops.Count; } }
    public IList<P0HEntity> Entities { get { return mEnts; } }

    // ======================= 初始化 =======================
    public bool Init(P0HWorld world, Transform player)
    {
        mWorld = world;
        mPlayer = player;
        if (world == null || !world.Ready) { Debug.LogError("[P0H] ★世界数据未就绪，实体层不启动"); return false; }

        mRoot = new GameObject("P0H_Entities");
        mDropRoot = new GameObject("P0H_Drops");

        SpawnAll();
        SpawnNpcs();
        Ready = true;
        Debug.Log("[P0H] 实体层就绪: 怪物 " + MonCount() + " NPC " + NpcCount() + " 掉落 " + mDrops.Count +
                  " | 刷怪点 " + world.SPawnCount);
        return true;
    }

    int MonCount() { int n = 0; foreach (var e in mEnts) if (!e.IsNpc) n++; return n; }
    int NpcCount() { int n = 0; foreach (var e in mEnts) if (e.IsNpc) n++; return n; }

    void SpawnAll()
    {
        int skipped = 0;
        foreach (var sp in mWorld.Spawns)
        {
            string nm = GetS(sp, "name");
            int x = GetI(sp, "x"), y = GetI(sp, "y"), cnt = GetI(sp, "count");
            int range = GetI(sp, "range");
            if (cnt <= 0) cnt = 1;
            for (int i = 0; i < cnt; i++)
            {
                int sx = x, sy = y;
                if (range > 0)
                {
                    // ★ 2026-10-26 修「怪站在墙里/树里，掉的金币永远捡不到」的根因：
                    //   原版刷怪落点必须是可走格（M2Server 的怪只能站在 CanWalk 的格子上，
                    //   一旦落在 bit15 阻挡格就永远动不了，死后金币跟着卡在不可达格）。
                    //   旧代码直接 Random.Range 落点、不做任何校验 ⇒ 实测稻草人0 被刷到
                    //   (290,630) 这种 blocked 格，打死它掉的金币踩不上去。
                    //   现改为「可走才收，最多重掷 12 次，全失败这只就不刷」。
                    bool ok = false;
                    for (int t = 0; t < 12; t++)
                    {
                        int cx = x + Random.Range(-range, range + 1);
                        int cy = y + Random.Range(-range, range + 1);
                        if (Blocked != null && Blocked(cx, cy)) continue;
                        if (cx == PlayerX && cy == PlayerY) continue;
                        if (mByCell.ContainsKey(CellKey(cx, cy))) continue;
                        sx = cx; sy = cy; ok = true; break;
                    }
                    if (!ok) { skipped++; continue; }
                }
                SpawnMon(nm, sx, sy);
            }
        }
        if (skipped > 0)
            Debug.Log("[P0H] 刷怪跳过 " + skipped + " 只（找不到可走落点，绝不塞进墙里）");
    }

    void SpawnMon(string nm, int x, int y)
    {
        var def = mWorld.GetMon(nm);
        if (def == null) { Debug.LogWarning("[P0H] ★刷怪点引用了未知怪物: " + nm); return; }
        var e = new P0HEntity
        {
            kind = P0HEntity.T_MON, name = nm, appr = def.appr,
            x = x, y = y, dir = Random.Range(0, 4),
            hpMax = Mathf.Max(1, def.hp), level = def.level, exp = def.exp,
            ac = def.ac, mac = def.mac, dc = def.dc, dcMax = def.dcMax, mc = def.mc, sc = def.sc,
            accuracy = def.accuracy, agility = def.agility,
            // DB 里 ATTACK_SPD / WALK_SPD 本身就是毫秒间隔；
            // MonsterObject.cs:829-830 `if (MoveSpeed < 400) MoveSpeed = 400; if (AttackSpeed < 400) AttackSpeed = 400;`
            attackIntervalMs = Mathf.Max(400, def.attackspeed),
            moveIntervalMs = Mathf.Max(400, def.movespeed),
        };
        e.hp = e.hpMax;
        Play(e, STAND);
        BuildGo(e);
        mEnts.Add(e);
        mByCell[CellKey(x, y)] = e;
    }

    void SpawnNpcs()
    {
        foreach (var nd in mWorld.Npcs)
        {
            var e = new P0HEntity
            {
                kind = P0HEntity.T_NPC, name = nd.label, appr = nd.appr,
                x = nd.x, y = nd.y, dir = Mathf.Clamp(nd.dir, 0, 3),
                script = nd.script, hpMax = 1, hp = 1,
            };
            Play(e, STAND);
            BuildGo(e);
            mEnts.Add(e);
        }
    }

    void BuildGo(P0HEntity e)
    {
        e.go = new GameObject((e.IsNpc ? "NPC_" : "Mon_") + e.name + "_" + e.x + "_" + e.y);
        e.go.transform.SetParent(mRoot.transform, false);
        e.sr = e.go.AddComponent<SpriteRenderer>();
        e.sr.sortingOrder = 10000 + e.y * 10 + 3;   // 与前景层同一套 y 序（同行怪在树前后按行号定）
        Place(e);
        ApplySprite(e);
    }

    // ======================= 坐标 =======================
    static int CellKey(int x, int y) { return x * 100000 + y; }

    /// <summary>
    /// 逻辑格 → 世界坐标（**格子中心**）。与 P0BMapWalk.World 同一个投影（纯正交）：
    ///   worldX = (x + 0.5) * 48 * 0.01
    ///   worldY = -(y + 0.5) * 32 * 0.01
    /// 权威出处 `Client\MirScenes\GameScene.cs`：
    ///   L10292-10293 `CellWidth = 48; CellHeight = 32;`
    ///   L10680 drawX = (x - User.X + OffSetX) * CellWidth
    ///   L10684 drawY = (y - User.Y + OffSetY) * CellHeight
    /// ⇒ 官方是**纯正交（俯视方格）**，没有 (x-y)/(x+y) 这种等距菱形项。
    ///   客户端 y 向下、Unity y 向上 ⇒ worldY 取负。
    /// sprite 用 pivot(0,1)（左上角）⇒ GameObject.position 既是「帧左上角」的横坐标基准、
    /// 又是「格子上边沿」的纵坐标基准，两者再叠加锚点偏移 (ox,oy)（Crystal MLibrary.cs:665 语义：
    /// `if (offSet) point.Offset(mi.X, mi.Y);`）。
    /// </summary>
    public static Vector3 WorldOf(int x, int y)
    {
        return new Vector3((x + 0.5f) * 48 * 0.01f, -(y + 0.5f) * 32 * 0.01f, 0f);
    }

    void Place(P0HEntity e)
    {
        if (e.go == null) return;
        e.go.transform.position = WorldOf(e.x, e.y);
        e.sr.sortingOrder = 10000 + e.y * 10 + 3;
    }

    // ======================= 取帧 =======================
    static readonly int[] ACT_OF_LEN = { 4, 6, 6, 2, 10 };

    int ActIndex(string act)
    {
        for (int i = 0; i < ACT_NAME.Length; i++) if (ACT_NAME[i] == act) return i;
        return 0;
    }

    void Play(P0HEntity e, int actIdx)
    {
        // Die 缺失（老式库 Mon1/Mon2，见 mon_layout.json 里 acts.Die=null）⇒ 用 Struck 顶替，
        // 不静默停帧（停帧会让怪物尸体会动、看不出死没死）。
        if (actIdx == DIE && e.kind == P0HEntity.T_MON && mWorld.MonSprite(e.appr, "Die", 0, 0) == null)
            actIdx = STRUCK;
        e.act = ACT_NAME[actIdx];
        e.frame = 0;
        e.animT = 0f;
        e.actT = 0f;
        e.actLen = ACT_LEN[actIdx];
        e.frameMs = ACT_MS[actIdx];
    }

    /// <summary>4 向（0上1下2左3右）→ 8 向贴图列号。映射表即原版同姿态合并，非推测。</summary>
    static int Dir8(int dir4)
    {
        // 8 向列名见 mon_layout.json dirnames: Up, UpRight, Right, DownRight, Down, DownLeft, Left, UpLeft
        // 4 向驱动时取最接近的正向列（上=0 下=4 左=6 右=2）
        switch (dir4)
        {
            case 0: return 0;   // 上   -> Up
            case 1: return 4;   // 下   -> Down
            case 2: return 6;   // 左   -> Left
            default: return 2;  // 右   -> Right
        }
    }

    void ApplySprite(P0HEntity e)
    {
        if (e.sr == null) return;
        P0HWorld.Sprite sp;
        if (e.IsNpc)
        {
            // NPC 只有 3 个朝向组（dir 0/1/2，见 npc_layout.json），dir 3 夹到 2
            int d = Mathf.Clamp(e.dir, 0, 2);
            sp = mWorld.NpcSprite(e.appr, d, e.frame);
            if (sp == null) sp = mWorld.NpcSprite(e.appr, 0, e.frame);
        }
        else
        {
            sp = mWorld.MonSprite(e.appr, e.act, Dir8(e.dir), e.frame);
            if (sp == null) sp = mWorld.MonSprite(e.appr, "Stand", Dir8(e.dir), 0);
        }
        if (sp == null) { e.sr.enabled = false; return; }

        e.sr.enabled = true;
        // ★ 性能修复：Sprite 按 `P0HWorld.Sprite` 实例缓存，**不再每帧 Sprite.Create**。
        //   旧代码这里每帧为每只怪 new 一个 Sprite，1424 只怪 ⇒ 帧率崩到 <0.02 fps。
        if (e.sr.sprite == null || e.curSp != sp)
        {
            Sprite cached;
            if (!mSprCache.TryGetValue(sp, out cached))
            {
                // pivot (0,1) = 左上角；PPU=100 使 1 世界单位 = 100px，与 P0BMapWalk 同口径
                cached = UnityEngine.Sprite.Create(sp.tex, sp.rect,
                                                   new Vector2(0f, 1f), 100f);
                mSprCache[sp] = cached;
            }
            e.sr.sprite = cached;
            e.curSp = sp;
            e.sr.flipX = sp.flipX;
        }
        // 锚点：sprite 左上角 = 格点 + (ox, oy) 像素 → 世界单位
        e.go.transform.position = WorldOf(e.x, e.y) + new Vector3(sp.ox * 0.01f, -sp.oy * 0.01f, 0f);
    }

    // ======================= 主循环 =======================
    public void Tick(float dt)
    {
        if (!Ready) return;
        TickEntities(dt);
        TickDrops(dt);
        TickRegen(dt);

        if (Time.time - mLogT > 10f)
        {
            mLogT = Time.time;
            Debug.Log("[P0H] 战况 怪=" + MonCount() + " 尸=" + CorpseCount() +
                      " 掉落=" + mDrops.Count + " 杀=" + Killed + " 拾=" + Picked +
                      " 伤害出=" + DamageDealt + " 受=" + DamageTaken +
                      " 命中=" + HitCount + " 未中=" + MissCount +
                      " | 活跃=" + mActiveShown + "/" + mActiveTotal +
                      " | Sprite缓存=" + mSprCache.Count +
                      " | 最近: " + LastEvent);
            LogNearest();
        }
    }

    /// <summary>
    /// 调试用：打出最近的 3 只活怪（名字 + 格坐标 + 距离），只进 logcat、不上聊天窗。
    /// 用途：真机验证战斗联动时，人工按坐标走过去触发击杀/升级/拾取/阵亡。
    /// </summary>
    void LogNearest()
    {
        string s = "";
        for (int k = 0; k < 3; k++)
        {
            P0HEntity best = null; int bd = int.MaxValue;
            foreach (var e in mEnts)
            {
                if (e.IsNpc || e.dead) continue;
                int d = Mathf.Abs(e.x - PlayerX) + Mathf.Abs(e.y - PlayerY);
                if (d < bd && (k == 0 || !s.Contains("(" + e.x + "," + e.y + ")")))
                {
                    bd = d; best = e;
                }
            }
            if (best == null) break;
            s += " [" + best.name + " (" + best.x + "," + best.y + ") d=" + bd + "]";
        }
        Debug.Log("[P0H] 最近怪 我=(" + PlayerX + "," + PlayerY + ")" + s);
    }

    /// <summary>
    /// 自然回复（2026-10-24 加）：HP 每 5 秒 +1，MP 每 7 秒 +1，回满为止。
    /// 口径取自 1.76 HumanObject 的 RegenTime/RegenHP/RegenMP（站立不回血是战斗态的差异，
    /// 这里先不做战斗态判定：本项目还没有「脱战」概念，宁可能回血也不要僵死）。
    /// 用累积器而不是「每隔 N 秒 +1」，是为了 dt 抖动（低帧率）时不丢时间。
    /// </summary>
    void TickRegen(float dt)
    {
        if (PlayerHp < PlayerHpMax)
        {
            mHpAcc += dt;
            while (mHpAcc >= REGEN_HP_SEC && PlayerHp < PlayerHpMax)
            {
                mHpAcc -= REGEN_HP_SEC;
                PlayerHp++;
            }
            if (PlayerHp >= PlayerHpMax) mHpAcc = 0f;
        }
        else mHpAcc = 0f;

        if (PlayerMp < PlayerMpMax)
        {
            mMpAcc += dt;
            while (mMpAcc >= REGEN_MP_SEC && PlayerMp < PlayerMpMax)
            {
                mMpAcc -= REGEN_MP_SEC;
                PlayerMp++;
            }
            if (PlayerMp >= PlayerMpMax) mMpAcc = 0f;
        }
        else mMpAcc = 0f;
    }

    int CorpseCount()
    {
        int n = 0;
        foreach (var e in mEnts) if (e.dead && !e.IsNpc) n++;
        return n;
    }

    void TickEntities(float dt)
    {
        int shown = 0;
        for (int i = mEnts.Count - 1; i >= 0; i--)
        {
            var e = mEnts[i];
            if (e.go == null) { mEnts.RemoveAt(i); continue; }

            if (e.atkCd > 0f) e.atkCd -= dt;
            if (e.movCd > 0f) e.movCd -= dt;

            // ---- ★ 活动范围剔除（性能修复）----
            //  AI 只在 `dist <= 9` 才动（L425），渲染沿用同一口径放宽到 ACTIVE_R。
            //  超出则整个 GameObject 失活：不进渲染批次、不建网格，
            //  否则 1424 个 SpriteRenderer 每帧提交会把帧率打到 <0.02 fps。
            int ddx = PlayerX - e.x, ddy = PlayerY - e.y;
            int cdist = Mathf.Max(Mathf.Abs(ddx), Mathf.Abs(ddy));   // 切比雪夫
            bool active = cdist <= ACTIVE_R;
            if (e.go.activeSelf != active) e.go.SetActive(active);
            if (!active) continue;
            shown++;

            // ---- 动画推进 ----
            e.animT += dt * 1000f;
            int guard = 0;
            while (e.animT >= e.frameMs && guard++ < 8)
            {
                e.animT -= e.frameMs;
                e.actT += e.frameMs;
                if (e.frame + 1 < e.actLen) e.frame++;
                else
                {
                    // 非循环动作播完后的收尾
                    if (e.act == "Die") e.frame = e.actLen - 1;      // 尸体定格最后一帧
                    else if (e.act == "Atk1" || e.act == "Struck") { Play(e, STAND); }
                    else e.frame = 0;                                 // Stand/Walk 循环
                }
            }
            ApplySprite(e);

            // ---- 死亡收尾 ----
            if (e.dead)
            {
                e.deadT += dt;
                if (e.deadT > DEATH_LINGER + e.actLen * e.frameMs / 1000f)
                {
                    DropLoot(e);
                    mByCell.Remove(CellKey(e.x, e.y));
                    Object.Destroy(e.go);
                    e.go = null;
                    mEnts.RemoveAt(i);
                }
                continue;
            }
            if (e.IsNpc) continue;

            // ---- AI：视野内追，贴身打 ----
            e.thinkT -= dt;
            if (e.thinkT > 0f) continue;
            e.thinkT = 0.25f;

            int dx = PlayerX - e.x, dy = PlayerY - e.y;
            int dist = Mathf.Abs(dx) + Mathf.Abs(dy);

            if (dist > 9) { e.aggro = false; if (e.act != "Stand") Play(e, STAND); continue; }
            e.aggro = true;

            if (dist <= 1)
            {
                // MonsterObject.cs:2166 `AttackTime = Envir.Time + AttackSpeed;` —— 出手有冷却，
                // 弓箭护卫 1200ms、鸡 3000ms；不能靠 think 的 250ms 硬刷
                if (e.atkCd > 0f) continue;
                if (e.act != "Atk1" && e.act != "Struck")
                {
                    e.dir = FaceTo(dx, dy);
                    Play(e, ATK);
                    e.atkCd = e.attackIntervalMs / 1000f;
                    MonsterAttack(e);
                }
                continue;
            }

            // 追：朝玩家走一格（MonsterObject.cs:2129 `MoveTime = Envir.Time + MoveSpeed;`）
            if (e.movCd > 0f) continue;
            e.dir = FaceTo(dx, dy);
            if (e.act != "Walk" && e.act != "Atk1") Play(e, WALK);
            int nx = e.x + (dx > 0 ? 1 : dx < 0 ? -1 : 0);
            int ny = e.y + (dy > 0 ? 1 : dy < 0 ? -1 : 0);
            if (Mathf.Abs(dx) >= Mathf.Abs(dy)) { nx = e.x + (dx > 0 ? 1 : dx < 0 ? -1 : 0); ny = e.y; }
            else { nx = e.x; ny = e.y + (dy > 0 ? 1 : dy < 0 ? -1 : 0); }
            TryMove(e, nx, ny);
            e.movCd = e.moveIntervalMs / 1000f;
        }
        mActiveShown = shown;
        mActiveTotal = mEnts.Count;
    }

    static int FaceTo(int dx, int dy)
    {
        if (Mathf.Abs(dx) >= Mathf.Abs(dy)) return dx >= 0 ? 3 : 2;   // 右 / 左
        return dy >= 0 ? 1 : 0;                                        // 下 / 上
    }

    void TryMove(P0HEntity e, int nx, int ny)
    {
        if (Blocked != null && Blocked(nx, ny)) return;
        if (mByCell.ContainsKey(CellKey(nx, ny))) return;
        if (nx == PlayerX && ny == PlayerY) return;
        mByCell.Remove(CellKey(e.x, e.y));
        e.x = nx; e.y = ny;
        mByCell[CellKey(nx, ny)] = e;
        Place(e);
    }

    // ======================= 战斗 =======================
    /// <summary>
    /// 怪物打玩家。严格照 Crystal 两处：
    ///   ① MonsterObject.cs:2168-2169  damage = GetAttackPower(MinDC,MaxDC)；damage==0 直接返回
    ///   ② HumanObject.cs:7222-7314    GetArmour 里先做 命中/闪避 判定
    ///      `Random.Next(Agility+1) > attacker.Accuracy` ⇒ Miss（MapObject.cs:466-472）
    ///      再过 GetDefencePower(MinAC,MaxAC)（MapObject.cs:274-280）
    ///      **L7250-7253 `if (armour >= damage) { Miss; return 0; }` —— 没有保底 1 点伤害**
    /// </summary>
    void MonsterAttack(P0HEntity e)
    {
        // ① 攻击力：DB 的 DC-DCMAX，区间随机（含上界）
        int dmg = GetAttackPower(e.dc, e.dcMax);
        if (dmg <= 0) { MissCount++; EmitEvent(P0HEventKind.CombatMiss, e.name + " 攻击力为 0"); return; }

        // ② 命中判定：玩家闪避 15，怪物命中 3-15 ⇒ 空刀是真实概率，不是 bug
        int agi = Mathf.Max(0, PlayerAgility);
        if (Random.Range(0, agi + 1) > Mathf.Max(0, e.accuracy))
        {
            MissCount++;
            EmitEvent(P0HEventKind.CombatMiss, e.name + " 打空了");
            return;
        }

        // ③ 防御：玩家 1 级 AC = 0，仍走完整公式（将来穿装备自动生效）
        int armour = GetDefencePower(PlayerAC, PlayerAC);
        if (armour >= dmg)
        {
            MissCount++;
            EmitEvent(P0HEventKind.CombatBlocked, e.name + " 被护甲完全挡下");
            return;
        }
        int final = dmg - armour;
        PlayerHp = Mathf.Max(0, PlayerHp - final);
        DamageTaken += final;
        // ★ 玩家被打也画飘字（原版 SM_STRUCK 对玩家同样触发，画在自己头顶）。
        EmitHitFx(PlayerX, PlayerY, final, false);
        EmitEvent(P0HEventKind.PlayerHurt, e.name + " 打中 -" + final + " HP (剩 " + PlayerHp + "/" + PlayerHpMax + ")");
        if (PlayerHp <= 0) { PlayerHp = PlayerHpMax; EmitEvent(P0HEventKind.PlayerDeath, "★玩家阵亡(已原地复活)"); }
    }

    /// <summary>`MapObject.cs:243-260` GetAttackPower：先夹逼，再区间随机（上界含）。</summary>
    static int GetAttackPower(int min, int max)
    {
        if (min < 0) min = 0;
        if (min > max) max = min;
        return Random.Range(min, max + 1);
    }

    /// <summary>`MapObject.cs:274-280` GetDefencePower：先夹逼，再区间随机（上界含）。</summary>
    static int GetDefencePower(int min, int max)
    {
        if (min < 0) min = 0;
        if (min > max) max = min;
        return Random.Range(min, max + 1);
    }

    int acOfPlayer() { return PlayerAC; }
    int agilityOfPlayer() { return PlayerAgility; }

    /// <summary>
    /// 玩家攻击（由 P0BMapWalk 在「砍怪」按钮/空格时调用），语义照 Crystal：
    ///   `PlayerObject.cs:1416` 服务端**只认方向不认目标**：先按朝向算出正前方一格
    ///   （`HumanObject.cs:2978 Point target = Functions.PointMove(CurrentLocation, dir, 1);`），
    ///   再看那一格里有哪些合法对象（:3029-3033）——客户端选的怪只是表现。
    ///   伤害 `HumanObject.cs:2981 GetAttackPower(Stats[MinDC], Stats[MaxDC])`＝只由装备给。
    ///   命中 `MapObject.cs:466-472`，伤害减免 `MonsterObject.cs:2586-2592`。
    /// 返回被攻击的实体（无目标返回 null）。
    /// </summary>
    public P0HEntity PlayerAttack()
    {
        if (!Ready) return null;
        int tx = PlayerX + DirX(PlayerDir);
        int ty = PlayerY + DirY(PlayerDir);
        P0HEntity tgt;
        if (!mByCell.TryGetValue(CellKey(tx, ty), out tgt) || tgt.IsNpc || tgt.dead)
        {
            // 原客户端点怪时会先转身（TurnTo）再把方向发给服务端；这里同语义：
            // 面前没怪就朝四周找一只活的，转身再砍。否则玩家每次都得先用方向键对准。
            tgt = null;
            for (int d = 0; d < 4; d++)
            {
                P0HEntity c;
                if (!mByCell.TryGetValue(CellKey(PlayerX + DirX(d), PlayerY + DirY(d)), out c)) continue;
                if (c.IsNpc || c.dead) continue;
                PlayerDir = d;
                tgt = c;
                break;
            }
            if (tgt == null)
            {
                // ★ 聊天窗只上玩家看得懂的文案；朝向/坐标是内部调试量，只进 logcat。
                Debug.LogFormat("[P0H] 攻击无目标 朝向={0} 面前格={1},{2}", PlayerDir, tx, ty);
                EmitEvent(P0HEventKind.SkillNoTarget, "附近没有可攻击的目标");
                return null;
            }
        }

        // ① 命中/闪避：玩家命中 5 vs 怪物闪避 ⇒ 空刀率 5/11~5/16，原版就这样
        int agi = Mathf.Max(0, tgt.agility);
        if (Random.Range(0, agi + 1) > Mathf.Max(0, PlayerAccuracy))
        {
            MissCount++;
            EmitEvent(P0HEventKind.CombatMiss, "打 " + tgt.name + " 空了");
            Play(tgt, STRUCK);
            return tgt;
        }

        // ② 伤害：木剑 DC 2-5，减怪 AC
        int dmg = GetAttackPower(PlayerDC, PlayerDCMax);
        int armour = GetDefencePower(tgt.ac, tgt.ac);
        if (armour >= dmg)
        {
            // MonsterObject.cs:2588-2592 —— 这就是原版「砍不动」的体验，不保底
            MissCount++;
            Debug.LogFormat("[P0H] 被甲挡下 name={0} armour={1} dmg={2}", tgt.name, armour, dmg);
            EmitEvent(P0HEventKind.CombatBlocked, "打 " + tgt.name + " 被护甲完全挡下");
            Play(tgt, STRUCK);
            return tgt;
        }
        dmg -= armour;
        HitCount++;
        tgt.hp -= dmg;
        DamageDealt += dmg;
        // ★ 只登记"真的掉血"；打空 / 被甲挡下 上面已经 return，进不到这里 ⇒ 原版也不会飘数字。
        EmitHitFx(tgt.x, tgt.y, dmg, false);
        if (tgt.hp <= 0)
        {
            Kill(tgt);
            EmitEvent(P0HEventKind.Kill, "杀死 " + tgt.name + " -" + dmg + " HP (+" + tgt.exp + " EXP)", tgt.exp);
        }
        else
        {
            Play(tgt, STRUCK);
            EmitEvent(P0HEventKind.CombatHit, "打 " + tgt.name + " -" + dmg + " HP (剩 " + tgt.hp + "/" + tgt.hpMax + ")");
        }
        return tgt;
    }

    static int DirX(int d) { return d == 2 ? -1 : d == 3 ? 1 : 0; }
    static int DirY(int d) { return d == 0 ? 1 : d == 1 ? -1 : 0; }

    void Kill(P0HEntity e)
    {
        e.dead = true;
        e.deadT = 0f;
        e.hp = 0;
        Killed++;
        Play(e, DIE);
        e.sr.color = new Color(0.85f, 0.75f, 0.75f, 1f);
        // ---- HUD 联动：击杀必给经验（Crystal MonsterInfo.EXP），经验条当场动 ----
        AddExp(Mathf.Max(1, e.exp));
    }

    /// <summary>
    /// 加经验并处理升级。口径照 Crystal：Exp 满 ⇒ Level++，Exp 归零后再攒下一级。
    /// 升级时 HP/MP 直接补满（1.76 客户端升级补满血蓝），并刷新 HP/MP 上限。
    /// </summary>
    public void AddExp(int amount)
    {
        if (amount <= 0) return;
        PlayerExp += amount;
        // ★ 原版 ClMain.pas:10139-10157 两个分支：g_ShowWinExpMode=true → AddSysMsgBottom('经验值 +%d')；
        //   false → 聊天框 'N经验值增加'（MShare.pas:2074 默认 False）。
        //   ⇒ 用户明确讨厌聊天框系统提示，本项目**固定走左下红字**（等价于源码头一个分支）。
        EmitEvent(P0HEventKind.ExpGain, "经验值 +" + amount, amount);
        while (PlayerExp >= PlayerExpMax && PlayerLevel < 50)
        {
            PlayerExp -= PlayerExpMax;
            PlayerLevel++;
            ApplyLevelStats();
            // ★ 文案 1:1 取客户端中文语言包：
            //   Client\Localization\Chinese.json  "LevelUp": "恭喜！你升级了，生命值和魔法值已恢复。"
            //   原版 GameScene.cs L3854 收到 LevelChanged 就是 ReceiveChat(LevelUp, ChatType.LevelUp)。
            EmitEvent(P0HEventKind.LevelUp, "★升级！恭喜！你升级了，生命值和魔法值已恢复。");
            LevelUps++;
        }
        if (PlayerLevel >= 50) { PlayerLevel = 50; PlayerExp = PlayerExpMax; }
        RegenPools();
    }

    /// <summary>
    /// 按 Crystal `Shared/BaseStats.cs` 的 `BaseStat.Calculate()` 重算上限（1 级战士成长曲线）：
    ///   HP : BaseStats.cs L16 Base=14 Gain=4 GainRate=4.5
    ///        => 14 + (int)((lv/4 + 4.5 + lv/20) * lv)     （Health 专用公式 L163）
    ///   MP : BaseStats.cs L17 Base=11 Gain=3.5 GainRate=0
    ///        => 11 + (int)(lv * 3.5)                       （Mana 默认公式 L173）
    ///   Wg : 沿用本工程 HUD 既有口径 55 + (lv-1)*5（出生 55 已与参考帧对齐，不再改动）
    /// 升级/命令设等级都必须走这一个入口，避免两条公式打架。
    /// </summary>
    void ApplyLevelStats()
    {
        int lv = PlayerLevel;
        PlayerHpMax = 14 + (int)((lv / 4f + 4.5f + lv / 20f) * lv);
        PlayerMpMax = 11 + (int)(lv * 3.5f);
        PlayerWeightMax = 55 + (lv - 1) * 5;
        PlayerHp = PlayerHpMax;
        PlayerMp = PlayerMpMax;
        PlayerExpMax = ExpNeed(lv);
    }

    // ======================= GM 命令（单机：本地玩家＝管理员）=======================
    //  出处 `Server/MirObjects/PlayerObject.cs` 的 @ 命令分支，单机没有第二个玩家，
    //  所以 LEVEL/GIVEGOLD 的「指定别人」分支不适用，只走「作用于自己」那条。

    /// <summary>@LEVEL n —— PlayerObject.cs L2296-2305：Level=level; LevelUp();</summary>
    public void AdminSetLevel(int level)
    {
        if (level < 1) level = 1;
        if (level > 50) level = 50;
        int old = PlayerLevel;
        PlayerLevel = level;
        PlayerExp = 0;
        ApplyLevelStats();
        // ★ 上屏文案与真实升级同一条（客户端中文语言包 LevelUp 原文），
        //   否则长文本会在聊天框里折行成两行，和原版「一行提示」不一致。
        //   旧等级 → 新等级的对照只进 logcat。
        EmitEvent(P0HEventKind.LevelUp, "★升级！恭喜！你升级了，生命值和魔法值已恢复。");
        LevelUps++;
        Debug.Log("[P0CHAT] GM LEVEL " + old + " -> " + level +
                  " HP=" + PlayerHpMax + " MP=" + PlayerMpMax);
    }

    /// <summary>@GIVEGOLD n —— PlayerObject.cs L3090-3095：player.GainGold(count)</summary>
    public void AdminGiveGold(int count)
    {
        if (count <= 0) return;
        // 原版 L3092-3093 的溢出夹逼：`if (count + Gold >= uint.MaxValue) count = uint.MaxValue - Gold;`
        const long GOLD_CAP = 4294967295L;
        if (PlayerGold + count > GOLD_CAP) count = (int)System.Math.Max(0, GOLD_CAP - PlayerGold);
        if (count <= 0) return;
        PlayerGold += count;
        Debug.Log("[P0CHAT] GM GIVEGOLD +" + count + " 现有=" + PlayerGold);
    }

    /// <summary>@DIE —— PlayerObject.cs L3454-3457：Die()；单机就地复活，保持可继续玩。</summary>
    public void AdminDie()
    {
        PlayerHp = 0;
        PlayerHp = PlayerHpMax;
        EmitEvent(P0HEventKind.PlayerDeath, "★玩家阵亡(已原地复活)");
        Debug.Log("[P0CHAT] GM DIE 已执行，HP 复位=" + PlayerHp + "/" + PlayerHpMax);
    }

    /// <summary>升到 lv 级还需要多少经验（1.76 经验表近似：100 * lv * lv / 2 递增）</summary>
    static long ExpNeed(int lv)
    {
        return 100L * lv * lv;
    }

    /// <summary>HP/MP 回复到上限（拾取、喝药、升级都走这里，保证 HUD 一定跟着动）</summary>
    public void RegenPools()
    {
        if (PlayerHp > PlayerHpMax) PlayerHp = PlayerHpMax;
        if (PlayerMp > PlayerMpMax) PlayerMp = PlayerMpMax;
    }

    /// <summary>
    /// 掉落。概率与内容全部照真实 1.76 掉落表（p0h_drops.json，
    /// 由 `Envir\MonItems\&lt;怪名&gt;.txt` 生成），判定口径：
    ///   MonsterInfo.cs:510-524  rate = (int)(Chance / Settings.DropRate); rate&lt;1⇒1;
    ///                          if (Random.Next(rate) != 0) return null   // 1/N 概率
    ///   MonsterInfo.cs:529-542  gold = Random.Next(Gold/2, Gold + Gold/2)   // 上界开
    /// 没有掉落表的怪（弓箭护卫）就是不掉，不臆造。
    /// </summary>
    void DropLoot(P0HEntity e)
    {
        if (e.IsNpc) return;
        var rows = mWorld.DropsOf(e.name);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int rate = Mathf.Max(1, r.c);          // Settings.DropRate = 1
            if (Random.Range(0, rate) != 0) continue;
            var d = new P0HDrop { x = e.x, y = e.y, life = DROP_LIFE };
            if (r.gold > 0)
            {
                // 金币：Random.Next(lower, upper) 上界开区间；lower 可能大于 upper 时取 upper
                int lower = r.gold / 2;
                int upper = r.gold + r.gold / 2;
                if (lower > upper) lower = upper;
                d.gold = Random.Range(lower, Mathf.Max(lower + 1, upper));
            }
            else
            {
                d.looks = r.looks;
                d.item = r.item;
            }
            if (d.gold <= 0 && d.looks <= 0) continue;
            PlaceDropOnWalkable(d);          // ★ 保险：落点绝不在墙里/树里
            BuildDropGo(d);
            mDrops.Add(d);
            Dropped++;
        }
    }

    /// <summary>
    /// ★ 2026-10-26：掉落落点兜底。原版掉落物一定落在怪死的那格，而怪只可能站在可走格上，
    /// 所以正常情况永远走得到。但本工程存在两种情况：①修刷怪前的旧存档局面里怪本来就在
    /// blocked 格 ②地图 bit15 与 LogIn 时的阻挡判定有极少数差异。为保证「金币一定能捡」，
    /// 落点若不可走就就近取一格可走的（半径 1..4，先上下左右再看斜角），一格都找不到才留在原地。
    /// 这不是"改成不掉"，只是把落点从墙里挪到墙边，不臆造内容。
    /// </summary>
    void PlaceDropOnWalkable(P0HDrop d)
    {
        if (Blocked == null || !Blocked(d.x, d.y)) return;
        for (int r = 1; r <= 4; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                    int cx = d.x + dx, cy = d.y + dy;
                    if (Blocked(cx, cy)) continue;
                    d.x = cx; d.y = cy;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// `ItemObject.cs:44-69` 金币帧按数量分档：&lt;100⇒112, &lt;200⇒113, &lt;500⇒114, &lt;1000⇒115, else 116。
    /// 金币不是物品、没有 Looks，只能走这套帧号。
    /// </summary>
    static int GoldFrame(int gold)
    {
        if (gold < 100) return 112;
        if (gold < 200) return 113;
        if (gold < 500) return 114;
        if (gold < 1000) return 115;
        return 116;
    }

    /// <summary>
    /// 地面掉落物的显示对象。美术 = DnItems（物品 Looks 帧 / 金币 112-116 帧），
    /// 位置照 `ItemObject.cs:78-79`：先落到格子，再 `Offset((CellWidth-W)/2,(CellHeight-H)/2)`
    /// 居中在 96×64 的格子里（本工程格宽 96*0.01=0.96、格高 64*0.01=0.64 世界单位）。
    /// </summary>
    void BuildDropGo(P0HDrop d)
    {
        int looks = d.gold > 0 ? GoldFrame(d.gold) : d.looks;
        P0HWorld.Sprite sp = mWorld.ItemSprite(looks);
        if (sp == null)
        {
            // 缺帧：不画（绝不拿白方块冒充，否则验收会假绿）
            Debug.LogWarning("[P0H] ★掉落缺帧 looks=" + looks + " (" +
                             (d.gold > 0 ? "金币" + d.gold : d.item) + ")——不画");
            return;
        }

        d.go = new GameObject("Drop_" + looks + "_" + d.x + "_" + d.y);
        d.go.transform.SetParent(mDropRoot.transform, false);
        d.sr = d.go.AddComponent<SpriteRenderer>();

        Texture2D tex = sp.tex;
        // 与怪物帧同一套约定：pivot 左上是 (0,1)，再按索引里的 rawY 翻成 Unity 左下原点
        var rect = new Rect(sp.rect.x, tex.height - sp.rawY - sp.rect.height,
                            sp.rect.width, sp.rect.height);
        d.sr.sprite = UnityEngine.Sprite.Create(tex, rect, new Vector2(0f, 1f), 100f);

        // 居中：ItemObject.cs:78-79 是
        //   DrawLocation = 格子左上 + ((CellWidth-W)/2, (CellHeight-H)/2)
        // 而本工程 WorldOf(x,y) 是格子中心、tile 半宽 48px / 半高 32px，所以
        //   左上 = 中心 + (-48 + (96-W)/2, +32 - (64-H)/2) = 中心 + (-W/2, +H/2)
        float dx = -sp.rect.width * 0.005f;
        float dy = sp.rect.height * 0.005f;
        d.go.transform.position = WorldOf(d.x, d.y) + new Vector3(dx, dy, 0f);
        d.sr.sortingOrder = 10000 + d.y * 10 + 1;   // 与前景层同一套 y 序：地上物品会被同行的树挡住
    }

    void TickDrops(float dt)
    {
        for (int i = mDrops.Count - 1; i >= 0; i--)
        {
            var d = mDrops[i];
            d.life -= dt;
            // 拾取：玩家走到同格自动拾
            if (d.x == PlayerX && d.y == PlayerY)
            {
                Pick(d);
                mDrops.RemoveAt(i);
                continue;
            }
            if (d.life <= 0f)
            {
                if (d.go != null) Object.Destroy(d.go);
                mDrops.RemoveAt(i);
            }
        }
    }

    void Pick(P0HDrop d)
    {
        Picked++;
        if (d.gold > 0)
        {
            // ---- HUD 联动：金币数当场变，右下角金币标签跟着跳 ----
            PlayerGold += d.gold;
            EmitEvent(P0HEventKind.PickupGold, "获得 " + d.gold + "金币", d.gold);
        }
        else
        {
            EmitEvent(P0HEventKind.PickupItem, d.item + " 被发现");
            // 拾到实物 ⇒ 负重增加（1.76 每件按 1 计，上限 55 + (Lv-1)*5）
            if (PlayerWeight < PlayerWeightMax) PlayerWeight++;
            RegenPools();
        }
        if (d.go != null) Object.Destroy(d.go);
    }

    // ======================= 交互 =======================
    /// <summary>玩家点击主视口时调用：把屏幕点换算成格坐标，命中 NPC 则返回它的 script（供对话层用）。</summary>
    public P0HEntity NpcAt(int x, int y, int radius)
    {
        P0HEntity best = null; int bd = int.MaxValue;
        foreach (var e in mEnts)
        {
            if (!e.IsNpc) continue;
            int d = Mathf.Abs(e.x - x) + Mathf.Abs(e.y - y);
            if (d <= radius && d < bd) { bd = d; best = e; }
        }
        return best;
    }

    // ======================= JSON 取值（spawns 是 Dictionary） =======================
    static int GetI(Dictionary<string, object> d, string k)
    {
        object o;
        if (!d.TryGetValue(k, out o) || o == null) return 0;
        if (o is double) return (int)System.Math.Round((double)o);
        int r; if (int.TryParse(o.ToString(), out r)) return r;
        return 0;
    }

    static string GetS(Dictionary<string, object> d, string k)
    {
        object o;
        return (d.TryGetValue(k, out o) && o != null) ? o.ToString() : "";
    }
}
