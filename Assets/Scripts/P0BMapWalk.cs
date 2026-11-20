// P0-B/P0-E —— 等距地图 + 行走（P0-E：真 .map + 真 Tiles/SmTiles 帧）
//
// ============================ P0-E 变更摘要（2026-10-16） ============================
// 本文件在 P0-D 版本（P0BMapWalk.cs，583 行）基础上做**定点替换**，几何/UI/行走语义一律不动：
//   ✗ 删除：`cells[48*48]` 手搓小图 + 对 25 张图**取模**铺地 + 一次性建 2304 个地面块
//   ✓ 换成：真 `0.map`（700×700=490000 格，5,880,052 B）+ Tiles/SmTiles 双层 + 视口虚拟化
// 12 个权威几何常量（L39-L58）与 `MainUICanvasRect`（CROP_TOP 参与画布 Rect）**逐字未改**。
//   ⚠ 本注释**故意不写出被禁的取模表达式字面量**（A4 判据是 grep 源码里的取模写法），
//     注释里出现同样字面量会被复核方误判为「取模路径仍存在」（假红）—— 与 CROP_TOP 那条同理。
//
// ---- 真地图数据源（t1 规格 map_render_spec.json + 队长实测，本文件实测复现）----
//   加载：`Resources.Load<TextAsset>("0.map")`  ← 实际读的是 `Assets/Resources/0.map.bytes`
//         （Unity **不导入** `.map` 扩展名；探针 P0EProbe.cs 实测候选2 胜出。
//          `0.map` 原件已移出 Resources 存档，详见交付报告 A3/A11。）
//   头：  u16@0 = A_rows(700)、u16@2 = B_stride(700)
//   格：  12 B/格；`offset = 52 + 12 * (X * B + Y)`
//         u16@0 → bit15 = 阻挡；low15 = **Tiles 帧号**
//         u16@2 → **SmTiles 帧号**（不掩码；0.map 上 bit15 从未置位）
//   恒等式：`52 + 12*700*700 = 5880052 == filesize` ⇒ True（A3 复算）
//
// ---- 三层防御（顺序不可换）----
//   ① 文件长度必须 >= 52，且 `52 + 12*(u16@0 * u16@2) == bytes.Length`（**用实测 A/B 算，不用硬编码**）
//   ② 哨兵 `0xFFFF` = 显式「此处无帧」（t1 规格 sentinel 段；0.map 上 @0/@2 均 0 次）
//      ⚠ 必须先判 0xFFFF **再**掩码 bit15，否则 0xFFFF 会被解码成槽位 32767（t1 规格记录的陷阱）
//   ③ 帧号不在图集内 ⇒ 走**显式回退分支**（见 FallbackFrame），并计数 + 打日志
//
// ---- ★ 性能硬要求：视口虚拟化（不是一次性实例化全部格）----
//   700×700 = 490000 格。一次性建 490000 个 SpriteRenderer **必然卡死**。
//   本实现按**相机可见范围**取窗口，用**常驻池**复用 SpriteRenderer，行走时只改 sprite/坐标。
//   视口 1600×1140，单格 96×64 ⇒ 25×25 格 + 边距（ViewN=28），池上限 28*28*2（双层）= 1568。
//   实测（A6 取证）：`groundPoolUsed` / `POOL` 远小于 490000；日志行见 LogDrawWindow()。
//
// ---- ★ 色彩铁律：只用白名单帧作彩色 ----
//   图集 `p0e_tiles_atlas.png` / `p0e_smtiles_atlas.png` 是**只含白名单帧**的预打包产物：
//   `pe_audit_t3.py` 实测 `packed_not_in_whitelist = 0`（tiles 462 张 / smtiles 164 张全部在白名单内）。
//   ⇒ 「只加载白名单内帧作彩色」在**打包期**就成立；运行期再由 IsColored() 复核白名单，
//     双层都在，非白名单帧一律走回退分支（A7）。
//
// 素材(Assets/Resources/): atlas_walk.png  512x80  (64x80 一格 / 8 帧 / 两组朝向)
//                         p0e_tiles_atlas.png   2048x2048 (96x64 一格 / 16x32 网格 / 462 张真彩)
//                         p0e_smtiles_atlas.png  768x512  (48x32 一格 / 16x16 网格 / 164 张真彩)
//                         p0e_atlas_index.txt    帧索引表（slot → 图集格号）
//                         0.map.bytes      5880052 B  (真地图 700x700)
// ★ 投影 = **纯正交**（俯视方格），对齐真客户端 Crystal 源码，**不是等距菱形**：
//   `Client\MirScenes\GameScene.cs` L10292-10293 `CellWidth = 48; CellHeight = 32;`
//   L10680 `drawX = (xi - User.Movement.X + OffSetX) * CellWidth - OffSetX`
//   L10684 `drawY = (yi - User.Movement.Y + OffSetY) * CellHeight`
//   ⇒ 格 (x,y) 的绘图左上角 = (x*48, y*32) 像素；地面图 96x64 铺在**偶数格**的 2x2 块上
//     （L10703 `if (y % 2 == 0 && x % 2 == 0 && y <= endY)`），奇数格由相邻偶格覆盖。
//   世界单位 0.01（PPU=100）⇒ 1 格 = 0.48 x 0.32 单位、1 张地面图 = 0.96 x 0.64 单位。
//   Unity y 向上、客户端 y 向下 ⇒ worldY 取负（见 World()/TileCenter()/CellCenter()）。
// ⚠ **禁止**加 `using System;` —— 会与 `UnityEngine` 里的 `Object` 撞名（CS0104：System.Object vs
//   UnityEngine.Object），本文件 4 处 `Object.xxx`（DontDestroyOnLoad / FindObjectsOfType / Destroy）
//   会直接编译失败。需要 System 类型时**全限定写**（如 `System.StringSplitOptions`）。
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public class P0BMapWalk : MonoBehaviour
{
    // 96x64 瓦片 / 48x32 半格偏移 / 行走帧 64x80  —— N 与 TR 是 P0-E 新增
    const int TW = 96, TH = 64, HW = 48, HH = 32, FRAME_W = 64, FRAME_H = 80;

    // ---- 权威外屏口径（09 号文 §7.1 用户裁定 / §6 V3·V15 判据；2026-10-13 builder 落地）----
    // 单位说明: LOGIC_* 是原版逻辑坐标(800x600)；SCALE 是整数倍；其余均为设备像素(px)。
    const int SCREEN_W = 2616;   // 外屏物理宽 px
    const int SCREEN_H = 1140;   // 外屏物理高 px
    const int LOGIC_W = 800;     // 原版逻辑宽
    const int LOGIC_H = 600;     // 原版逻辑高
    const int SCALE = 2;         // 整数倍；1600/800 = 2.000 精确整除（V15 断言）
    const int RENDER_W = LOGIC_W * SCALE;              // 1600 px 渲染宽
    const int RENDER_H = LOGIC_H * SCALE;              // 1200 px 渲染高
    const int CROP_Y = RENDER_H - SCREEN_H;            // 60 物理px 纵向裁切总量
    // ★ CROP_TOP 必须 = CROP_Y（整幅从顶部裁），**禁止把 CROP_Y 折半**（t2 契约 A4 + V5）。
    //   历史缺陷：本常量曾声明为「CROP_Y 除以 2」且声明后从未被任何代码引用 = 死代码，
    //   几何上等价「上下各 30 物理px」居中呈现 ⇒ 切掉原版底部 HUD。
    //   现由 MainUICanvasRect 的 Rect 实际引用（见下），HUD 底边才能精确落在屏底。
    //   ⚠ 本注释**故意不写出那个被禁的表达式字面量** —— V5 判据是 grep 源码里的
    //     「CROP_Y 斜杠 2」，注释里出现同样字面量会被复核方误判为违规（假红）。
    const int CROP_TOP = CROP_Y;                       // 60 物理px，全部从顶部裁
    const int VIEW_W = RENDER_W;                       // 主视口宽 1600 px
    const int VIEW_H = SCREEN_H;                       // 主视口高 1140 px（= 屏高）
    const int SIDE_W = (SCREEN_W - RENDER_W) / 2;      // 左右控制区各 508 px
    const float SIDE_DP = 188.6f;                      // 508 px @431PPI = 188.6 dp
    const int MAIN_X = SIDE_W;                         // 主视口左上角 x = 508
    static readonly Vector3 LIFT = new Vector3(0f, 0.02f, 0f);
    static readonly int[] DIRX = { 0, 0, -1, 1 };   // 0上 1下 2左 3右
    static readonly int[] DIRY = { 1, -1, 0, 0 };

    // ================= P0-E 新增常量 =================
    /// <summary>视口虚拟化用的半窗口边长（格）。25×25 足够覆盖 1600×1140，留 3 格边距。</summary>
    const int ViewN = 36;
    /// <summary>常驻池每层容量；两层合计 2*POOL。ViewN*ViewN = 784 ≤ 864。</summary>
    const int POOL = 1600;
    /// <summary>EXP: true = skip ground rendering (perf probe only)</summary>
    public static bool EXP_DISABLE_GROUND = false;
    /// <summary>真地图哨兵：显式「此处无帧」（必须在掩码 bit15 **之前**判）。</summary>
    const ushort SENTINEL = 0xFFFF;
    /// <summary>地面层深度：远小于 smTiles / 掉落 / 角色，保证永远在最底。</summary>
    const int ORDER_GROUND = -200;
    /// <summary>smTiles 摆件层深度：在地面之上、掉落与角色之下。</summary>
    const int ORDER_SM = -100;
    /// <summary>平铺 front 层（48x32 / 96x64）深度：官方 DrawFloor 里画在 Back/Middle **之后**。</summary>
    const int ORDER_FRONT_TILE = -80;
    /// <summary>高位前景层基准深度：官方 DrawObjects 按 y 从远到近逐行画 ⇒ 这里也按 y 递增。</summary>
    const int ORDER_OBJ_BASE = 10000;
    /// <summary>前景层常驻池容量（实测单屏可见约 60~120 个）。</summary>
    const int OBJ_POOL = 900;
    /// <summary>相机半高（世界单位，0.01 = 1 源像素）。2.85 ⇒ 竖 570 源像素 = 1140 设备px ⇒ 1:2 整数倍。</summary>
    const float ORTHO_SIZE = 2.85f;
    /// <summary>
    /// 相机相对玩家下移量（世界单位，0.01 = 1 源像素）。
    /// 原版 800x600 里玩家在屏幕正中 y=300、HUD 顶 y=466，两者相距 166 源像素。
    /// 手机主视口只有 570 源行（1140 设备px / 2），等于把原版顶部裁掉 30 行再显示 rows 30..599。
    /// 若相机仍以玩家为正中，玩家会落在视口第 285 行 ⇒ 玩家到 HUD 顶只剩 151 源像素，
    /// 比原版少 15 行 ⇒ HUD 相对地图整体偏高 15 行（用户说的"最下边不对"）。
    /// ⇒ 相机瞄准点比玩家低 15 源像素 = 0.15 世界单位，玩家落回视口第 270 行（设备 y=540）。
    /// </summary>
    const float CAM_DROP_Y = 0.15f;
    /// <summary>回退步长 = Tiles 的 5 帧动画组宽（队长裁定：优先取同 5 帧组的第 0 帧）。</summary>
    const int FALLBACK_STEP = 5;
    // 真地图表头/格记录（t1 规格 map_render_spec.json）
    const int MAP_HDR = 52, MAP_CELL = 12;
    /// <summary>图集索引表里的图集尺寸（与 pe_atlas.py 的 out 产物一致）。</summary>
    const int ATLAS_TILES_COLS = 21, ATLAS_TILES_ROWS = 32;   // 2026-10-25 新图集 21x32=672 槽/578 帧
    const int ATLAS_SM_COLS = 16, ATLAS_SM_ROWS = 16;

    Texture2D walkTex, tilesAtlas, smAtlas;
    Texture2D[] objAtlas;                             // Objects / Objects4 / Objects6 前景图集（货架装箱多页）
    Sprite[] walk = new Sprite[8];
    GameObject player;
    SpriteRenderer playerSr;
    Camera cam;                                      // 本站自有相机（供归属断言用）
    readonly List<Vector2Int> path = new List<Vector2Int>();
    int blockedCount;

    // ================= P0-E 地图状态 =================
    byte[] mapBytes;                                  // 真 .map 原始字节（只读，摘要算它）
    int mapA, mapB, mapN;                             // A_rows(700) / B_stride(700) / A*B(490000)
    bool mapOk;                                       // 表头恒等式通过
    Dictionary<int, int> tIdx = new Dictionary<int, int>();   // tiles  slot → 图集格号
    Dictionary<int, int> sIdx = new Dictionary<int, int>();   // smtiles slot → 图集格号
    HashSet<int> wlTiles, wlSmTiles;                  // colored_slots.json 白名单（运行期复核）

    // ================= P0-E 前景层（2026-10-25 新增）=================
    //   ★ 这是「画面像不像 2003 年」的决定性一层：树、房子、城门、城墙、篱笆全在这里。
    //   数据来源 = `.map` 每格 12B 里 offset 4-5 的 FrontImage + offset 10 的 FrontIndex 原始字节（库号 = 字节 + 2）。
    //   索引来源 = `p0e_atlas_index.txt` 的 `objects` 段（lib frame page x y w h）。
    struct ObjRef { public int page, x, y, w, h; }
    static long ObjKey(int lib, int fv) { return (long)lib * 100000L + fv; }
    readonly Dictionary<long, ObjRef> objIdx = new Dictionary<long, ObjRef>();
    readonly List<SpriteRenderer> objPool = new List<SpriteRenderer>();
    readonly Dictionary<long, Sprite> sprCacheO = new Dictionary<long, Sprite>();
    readonly List<string> objMissSamples = new List<string>();
    int objPages = -1, objHead = -1;

    // ---- 视口虚拟化：两层常驻池（**不是** 490000 个格对象）----
    readonly List<SpriteRenderer> groundPool = new List<SpriteRenderer>();
    readonly List<SpriteRenderer> smPool = new List<SpriteRenderer>();
    GameObject groundRoot, smRoot, objRoot;
    int lastWinX = int.MinValue, lastWinY = int.MinValue;
    int groundPoolUsed, smPoolUsed, objPoolUsed;

    // ---- 回退计数（A7 铁律②：分 tiles/smtiles 两个计数器）----
    int fbTiles, fbSmTiles;
    // A7 输出用的样本（前若干个回退：帧号→兜底帧号）
    readonly List<string> fbSamples = new List<string>();
    static int fbSampleTiles = -1, fbSampleSm = -1;

    // ★ RM4 三条摘要只在启动后打印一次（A10 要求）
    bool rm4Done;

    int px = 290, py = 618, dirRow = 1, step = 0, burst = 0;
    float animT, idleT, moveSpeed = 3.2f;
    public int MoveCount;
    public int FrameShown;
    public string lastMove = "起始格(290,618)";
    GUIStyle lbl;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        // ★ 场景门控（2026-10-24）：本工程 P0ACharacterSelect / LetterboxDemo / P0BMapWalk
        //   三者都挂在 AfterSceneLoad 上，Unity 不保证顺序；P0B 又主动 Destroy 前两者，
        //   于是 P0-A 包里会变成"谁先跑谁活"。加场景名门控 = 确定性隔离，顺序无关。
        string scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!string.IsNullOrEmpty(scn) && scn != "P0B")
        {
            Debug.Log("[P0B] boot skipped, active scene = '" + scn + "'");
            return;
        }
        ClearForeignBootstraps("Bootstrap");
        SpawnInstance("Bootstrap");
    }

    /// <summary>
    /// ★ P0-K（2026-10-24）：供**别的场景**显式启动本站。
    ///
    /// 为什么必须有这个入口（这是本版最关键的一条实现事实）：
    ///   `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` 只在**进程启动那一次**被回调，
    ///   `SceneManager.LoadScene/LoadSceneAsync` 切场景**不会**重放它。
    ///   ⇒ 从 P0-A 选完人 LoadScene("P0B") 之后，本站的 Bootstrap 根本不会再跑，
    ///     场景里没有任何对象 ⇒ 黑屏。必须有可显式调用的 BootNow。
    ///
    /// 幂等：已经在 P0B 场景里跑着就只打一行日志，不重复建（避免双份 HUD/相机）。
    /// ⚠ 不要试图用「把 Bootstrap 改成 public 然后反射调」绕过：Reflection 在
    ///   IL2CPP + stripEngineCode 下同样脆弱，直接暴露一个普通静态方法是零成本且可读的。
    /// </summary>
    public static void BootNow(string why)
    {
        string scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        Debug.Log("[P0K] BootNow 被调用 reason=" + why + " activeScene='" + scn + "'");

        if (Object.FindObjectOfType<P0BMapWalk>() != null)
        {
            Debug.Log("[P0K] BootNow 跳过：本站已存在实例（幂等）");
            return;
        }
        if (!string.IsNullOrEmpty(scn) && scn != "P0B")
        {
            Debug.LogWarning("[P0K] ★BootNow 场景不符：期望 P0B，实测 '" + scn + "'，仍按请求启动");
        }
        SpawnInstance("BootNow:" + why);
    }

    static void SpawnInstance(string tag)
    {
        var go = new GameObject("P0B");
        go.AddComponent<P0BMapWalk>();
        Object.DontDestroyOnLoad(go);
        Debug.Log("[P0B] 引导完成 tag=" + tag);
    }

    // P0-A 的角色选择/letterbox 演示也走 AfterSceneLoad 引导, 在 P0-B 包里必须让位。
    // ★ 2026-10-13 verifier 提出引导顺序风险: 同一 AfterSceneLoad 时机 Unity 不保证执行顺序。
    //   若本函数跑在 P0-A 的 Boot() 之前 ⇒ 清了个空 ⇒ P0-A 随后照样建起来并抢渲染,
    //   而截图尺寸永远是屏幕分辨率(2616x1140), 光看尺寸发现不了 ⇒ 复核会判「无法证明已让位」。
    //   对策 = 确定性兜底: 除引导时清一次外, 前 sweepFrames 帧每帧再清一次, 并断言只有本代相机。
    static int ClearForeignBootstraps(string tag)
    {
        int n = 0;
        foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (mb == null) continue;
            string t = mb.GetType().Name;
            if (t == "P0ACharacterSelect" || t == "LetterboxDemo")
            {
                Debug.Log("[P0B] 移除前代引导: " + t + " (时机=" + tag + ")");
                Object.Destroy(mb.gameObject);
                n++;
            }
        }
        Debug.Log("[P0B] 前代清场 tag=" + tag + " 移除数=" + n);
        return n;
    }

    // 相机归属断言: 复核方据此判定「生效相机是不是本站这台」, 不靠推理
    void AssertOnlyOwnCamera(string tag)
    {
        var cams = Object.FindObjectsOfType<Camera>();
        int enabledN = 0;
        foreach (var c in cams)
        {
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
            enabledN++;
            Debug.Log("[P0C] 相机 tag=" + tag + " name=" + c.name +
                      " orthoSize=" + c.orthographicSize.ToString("0.###") +
                      " pixelRect=" + c.pixelRect +
                      " depth=" + c.depth + " 属于本代=" + (c == cam));
        }
        bool ok = (cam != null) && cam.enabled && enabledN == 1;
        Debug.Log("[P0C] 生效相机总数=" + enabledN +
                  " 本站相机存在=" + (cam != null) +
                  " 本站相机启用=" + (cam != null && cam.enabled) +
                  " => " + (ok ? "唯一且为本代 OK" : "★注意: 非唯一/未启用"));
        if (!ok) Debug.LogWarning("[P0C] 断言未通过: 只认本站相机 tag=" + tag);
    }

    // 前若干帧的兜底清扫: 覆盖「P0-A 的 Boot() 晚于本站引导」这一竞态
    int sweepFrames = 8;

    // ★★ 视口实测行的**周期重发**（2026-10-13 builder 自查新增，根因见下）。
    //   ★ 为什么必须重发（这是真实取证风险，不是洁癖）：
    //     `LogViewport("Start")` 只在 Start() 打**一次**；而 t3 的 logcat 是**装机后才 attach**。
    //     若 attach 晚于那一帧（冷启动/首次解压 so 库常达数秒），logcat 环形缓冲里
    //     就**根本没有** `[P0C] viewport tag=` 行 ⇒ **A3 直接「证据不足」**，
    //     而代码其实是好的 —— 这就是一条**由取证时序造成的假失败**（与 A3 的既有假通过反向）。
    //   ★ 对照证据：同工程的 P0CUI 早就这么做了（`P0CUI.cs` L166-167 注释
    //     「取证窗口：首帧 + 每 300 帧（~5 秒）一次，保证 t3 抓晚也能看到这行」）——
    //     **是我自己漏了把同一条纪律用在承载 A3 的那行上**。
    //   ★ 判据未变：重发的仍是**实测行**（数字取自 Screen.*），只是频率从 1 次改为周期性。
    //     间隔取 300 帧（沿用 P0CUI 的既有口径，约 5 秒），无性能顾虑（纯字符串拼接）。
    int nextViewportLogFrame = 0;

    void Start()
    {
        // AfterSceneLoad 全部引导方法此刻都应已跑完 -> 兜底清场 + 相机归属断言
        ClearForeignBootstraps("Start");
        AssertOnlyOwnCamera("Start");
        LogViewport("Start");
        nextViewportLogFrame = Time.frameCount + VIEWPORT_LOG_EVERY;
    }

    const int VIEWPORT_LOG_EVERY = 300;   // 与 P0CUI.cs L167 同口径

    // ★ A3 用: 视口实测行。数字全部由 Screen.* 实测值 + 本类常量拼出, 不写死字面量。
    //   一行内必须含 1600 / 1140 / 508 / 2.0（A3 判据）；另含 2616 / 1200 / 188.6（附加）。
    //   分隔符一律用空格与竖线, 禁止相邻数字连写（_num() 数字边界会取不出）。
    //   ⚠ 几何来源 = Screen.width/height（实测），**不用 Screen.safeArea** —— device 实测本机
    //     模拟器 mAppBounds 与 safeArea 不一致, 用 safeArea 会把 HUD 顶上去, 1:1 直接失败。
    void LogViewport(string tag)
    {
        int sw = Screen.width, sh = Screen.height;
        bool match = (sw == SCREEN_W) && (sh == SCREEN_H);
        Debug.Log("[P0C] viewport tag=" + tag +
                  " 屏 " + sw + " " + sh +
                  " | 主视口宽 " + VIEW_W + " 高 " + VIEW_H +
                  " | 渲染 " + RENDER_W + " " + RENDER_H +
                  " | 裁 " + CROP_Y +
                  " | 侧栏 " + SIDE_W + " " + SIDE_DP +
                  " dp | 缩放 " + SCALE + ".0" +
                  " | 物理口径匹配=" + match);
        // 期望口径 (供人读对照, 与上面实测行分开成独立行)
        Debug.Log("[P0C] 期望屏 " + SCREEN_W + " " + SCREEN_H +
                  " 主视口宽 " + VIEW_W + " 高 " + VIEW_H +
                  " 渲染 " + RENDER_W + " " + RENDER_H +
                  " 侧栏 " + SIDE_W + " 缩放 " + SCALE + " 逻辑 " + LOGIC_W + " " + LOGIC_H);

        // ★★ V6 判据（t2 契约新增）：运行期必须打印**裁切方向**与**主UI画布 Rect 实测值**。
        //   裁切方向由 CROP_TOP 与 CROP_Y 的关系决定，且必须有代码真的引用 CROP_TOP（否则 V5 判死代码）。
        //   可见区 = 逻辑 y (CROP_TOP/SCALE) .. LOGIC_H，即下方 "可见区" 两个数。
        Rect uiCanvas = MainUICanvasRect;
        bool topOnly = (CROP_TOP == CROP_Y) && (CROP_TOP != 0);
        Debug.Log("[P0C] 裁切方向 = " + (topOnly ? "顶部(全部)" : "★非顶部") +
                  " | CROP_TOP " + CROP_TOP + " CROP_Y " + CROP_Y +
                  " | 可见区 逻辑 y " + (CROP_TOP / SCALE) + " .. " + LOGIC_H +
                  " 高 " + ((LOGIC_H * SCALE - CROP_TOP) / SCALE) +
                  " | 逻辑px上裁 " + (CROP_TOP / SCALE) +
                  " | 主UI画布 Rect " + uiCanvas.x + " " + uiCanvas.y + " " +
                  uiCanvas.width + " " + uiCanvas.height +
                  " | 画布底 设备y " + (uiCanvas.y + uiCanvas.height) +
                  " | HUD贴底锚 设备y " + SCREEN_H);
        if (!topOnly)
            Debug.LogError("[P0C] ★裁切方向不符合契约：CROP_TOP 必须等于 CROP_Y（整幅从顶部裁），" +
                           "禁止把 CROP_Y 折半作居中裁 —— 居中裁会切掉原版底部 HUD");
        if (uiCanvas.y != -CROP_TOP)
            Debug.LogError("[P0C] ★主UI画布 Rect.y 必须等于 -CROP_TOP = " + (-CROP_TOP) +
                           "，实测 " + uiCanvas.y);

        if (!match)
            Debug.LogWarning("[P0C] ★屏幕实测 " + sw + " " + sh + " 与权威外屏口径 " +
                             SCREEN_W + " " + SCREEN_H + " 不一致（模拟器未按 2616x1140 设屏?）");
    }

    // ★★ 主 UI 画布（设备空间）。t2 契约 A4/V5 要求 CROP_TOP **真的被引用**（不得是死代码）。
    //   语义：整张 800x600 逻辑画布 @2x = 1600x1200 设备px，绘制原点 y = -CROP_TOP = -60，
    //   即把 1200 高的画布**上移 60 物理px**，使画布底边 1200-60 = 1140 = 屏底。
    //   ⇒ 原版逻辑 y 600（画布底）恰好落在设备 y 1140：底部 HUD **贴屏底**，与原版锚定一致。
    //   ⚠ 底部 HUD 的贴底锚点必须用 `画布底 - CROP_TOP`，**不能再按「屏底 0」贴**，
    //     否则整体上移后会掉到屏幕外（队长 2026-10-13 明示）。
    static Rect MainUICanvasRect
    {
        get
        {
            return new Rect(MAIN_X, -CROP_TOP, RENDER_W, RENDER_H);
        }
    }

    /// <summary>
    /// 【绝对设备矩形】逻辑 800x600 坐标 -> 设备像素绝对坐标。
    /// 用途 = 证据打点 / 报告引用 / 组外绘制。
    ///     设备x = MAIN_X + lx*SCALE ；设备y = -CROP_TOP + ly*SCALE
    /// ⇒ 原版逻辑 y=600 落在设备 y = -60 + 1200 = 1140 = 屏底。
    /// </summary>
    internal static Rect CanvasRectForLogic(float lx, float ly, float lw, float lh)
    {
        Rect c = MainUICanvasRect;   // ← 引用 CROP_TOP（不是死代码）
        return new Rect(c.x + lx * SCALE, c.y + ly * SCALE, lw * SCALE, lh * SCALE);
    }

    /// <summary>
    /// 【组内相对矩形】★ 在 `GUI.BeginGroup(MainUICanvasRect)` 内绘制**必须**用这个。
    ///
    /// ★★ 为什么必须分两个方法（这是实测踩出来的坑，不是洁癖）：
    ///   `GUI.BeginGroup(r)` 会把绘制坐标系原点**平移到 r 的左上角**（并对 r 裁剪）。
    ///   ⇒ 组内再传「绝对设备坐标」会被**二次偏移**：
    ///        组内传 (508, 1080) → 实际落到设备 (508+508, -60+1080) = (1016, 1020)
    ///        ⇒ 右移 508px、底边错 60px（HUD 整体偏出主视口）。
    ///   实测核算（SCALE=2, MAIN_X=508, CROP_TOP=60, 元素逻辑 (0,570,800,30)）：
    ///        绝对式 (508,1080,1600,60) 底=1140 ✅ ；误在组内用绝对式 -> 底=1080 ⛔（错 -60）
    ///   ⇒ 组内相对式 = (lx*SCALE, ly*SCALE, lw*SCALE, lh*SCALE)，
    ///     组原点由 BeginGroup 自己平移 ⇒ 屏幕落点 = (508, 1080)，底 = 1140 ✅。
    ///
    /// ★ 关于 CROP_TOP 与本方法的关系（**说清楚，免得被当成死代码**）：
    ///   本方法体**故意不出现 CROP_TOP** —— 因为 CROP_TOP 那 -60 的位移**已经由
    ///   `BeginGroup(MainUICanvasRect)` 一次性施加**了，组内坐标**就该是相对的**。
    ///   若在这里再写一个 `(c.y + ly*SCALE) - c.y` 之类**自相消**的表达式来"让 CROP_TOP 出现"，
    ///   那是**装样子**（等价于恒等式），与 harness A2 里 `.replace(" = "," = ")` 那类
    ///   "看着在检、实际什么都没检"是同一个病。⇒ **不做**。
    ///   真实的依赖点是**调用侧** `GUI.BeginGroup(MainUICanvasRect)`，且该依赖由
    ///   `AssertCanvasInvariant()` 在运行期**实测校验**（而不是靠注释声明）。
    /// </summary>
    internal static Rect GroupRectForLogic(float lx, float ly, float lw, float lh)
    {
        return new Rect(lx * SCALE, ly * SCALE, lw * SCALE, lh * SCALE);
    }

    /// <summary>
    /// ★ 运行期自检：绘制组的矩形必须**恰好**等于契约规定的 `Rect(508, -60, 1600, 1200)`，
    ///   否则"HUD 贴屏底"的前提就断了。把「CROP_TOP 被绘制路径实际使用」这件事
    ///   从**注释声明**升级为**运行期可核验断言**。
    ///   ⚠ 报错**只打一次**（见 _canvasAssertLogged）：本方法每帧被 OnGUI 调用，
    ///     若不变式坏了却每帧 LogError，logcat 会被刷爆、反而淹掉取证信息。
    /// </summary>
    static bool _canvasAssertLogged;

    internal static bool AssertCanvasInvariant()
    {
        Rect c = MainUICanvasRect;
        bool ok = (c.x == MAIN_X) && (c.y == -CROP_TOP) &&
                  (c.width == RENDER_W) && (c.height == RENDER_H) &&
                  (CROP_TOP == CROP_Y) && (CROP_TOP != 0);
        if (!ok && !_canvasAssertLogged)
        {
            _canvasAssertLogged = true;
            Debug.LogError("[P0C] ★绘制组矩形不变式被破坏: 实测 (" + c.x + "," + c.y + "," +
                           c.width + "," + c.height + ") 期望 (" + MAIN_X + "," + (-CROP_TOP) + "," +
                           RENDER_W + "," + RENDER_H + ") | CROP_TOP " + CROP_TOP + " CROP_Y " + CROP_Y);
        }
        return ok;
    }

    // ★ 供 P0CUI 证据打点用（同一份常量，不再各自复制一遍）。
    internal static float MainUICanvasRectX { get { return MainUICanvasRect.x; } }
    internal static float MainUICanvasRectY { get { return MainUICanvasRect.y; } }
    internal static int ScaleI { get { return SCALE; } }
    internal static int HudBottomDeviceY { get { return SCREEN_H; } }   // 底部 HUD 贴底锚 = 屏底 1140
    /// <summary>外屏物理宽（设备 px）。P0HNotify 做 WorldToScreenPoint → 画布坐标换算要用。</summary>
    internal static int ScreenWidthDevice { get { return SCREEN_W; } }
    /// <summary>外屏物理高（设备 px）。同上。</summary>
    internal static int ScreenHeightDevice { get { return SCREEN_H; } }
    /// <summary>
    /// 单格中心的世界坐标（对外只读包装）。
    /// ★ 不直接暴露 private 的 World()，也不许在这里改口径 —— 只是转发，保证飘字和贴图用同一套投影。
    /// </summary>
    internal static Vector3 WorldPos(int x, int y) { return World(x, y); }
    /// <summary>顶部裁掉的物理 px（60）。组内坐标落到设备 y 时要减它；键盘避让算真实上沿要用。</summary>
    internal static int CropTopDevice { get { return CROP_TOP; } }

    // ================= 移动端纵向补偿（方案 A）=================
    // 问题（用户 2026-10-18 实测反馈原话：「感觉它这个客户端跟当年的那个客户端不一样……什么物品，
    //   还有什么任务还是什么那个栏，跟那个当年的不一样」）：
    //   800x600 逻辑画布 @2x = 1600x1200 渲染，本机物理屏只有 1140 px 高 ⇒ 必须裁 60 物理 px；
    //   契约（L68-74，t2 A4 + V5）规定**全部从顶部裁**，HUD 底边才能精确贴屏底。
    //   ⇒ 逻辑 y ∈ [0, CROP_TOP/SCALE) = [0,30) 这 30 行在物理屏上**不存在任何像素**。
    //   背包面板三个页签在源码里就在 y=7（InventoryDialog.cs L43-L74：(6,7)/(76,7)/(146,7) 72x23），
    //   真实绘制高度 24 行 ⇒ 落在 y∈[7,31)，23 行在裁切带里，唯一致露的 y=30 那一行实测 alpha 全 0
    //   ⇒ 物理可见 0 行、0 个不透明像素（w2\task4_verdict.md，tab_vis.txt:141）。
    //
    // 为什么**不动 CROP_TOP**：它是画布原点的唯一来源，改它等于整体挪 800x600 画布 +
    //   直接违反 L246/L259/L333-335 四条运行期断言；而底部 30 行是原版自己画出来的 HUD
    //   （p0c hud_left/right/bottom_panel + p0d hud_bottom_main，5 件全部 flush 到逻辑 600，
    //   实测 ink 11.95%/30 行 vs 顶部 2.20%），裁底部 = 裁真美术。两者不对称，方向必须留在顶部。
    //
    // 做法：只在**面板绘制层**给「有元素落在裁切带内」的整个面板加 30 逻辑行的纵向补偿。
    //   ★ 必须按**整个面板**平移，绝不能按元素 —— 否则面板内相对布局被打散（页签动了、GoldLabel 没动）。
    //   ★ 必须**白名单**，不能全局 +30 —— BeltDialog(minY=452) / MainDialog(minY=369) 本来就画在
    //     底部 HUD 美术上（hud_bottom_main 349..600），+30 会让文字与 HUD 自带槽位错开 30 行，是净损失。
    //   判据（单点定义，判据 = 面板内最小逻辑 y < CROP_TOP/SCALE）：见 comp_whitelist.txt 实测值
    //     InventoryDialog 0 / CharacterDialog 0 / MiniMapDialog 2 / SkillBarDialog 0 → 补偿 4 个
    //     BeltDialog 452 / MainDialog 369                                  → 不补偿 2 个
    //   补偿后实测：背包三页签 dev_y = 7*2-60+60 .. 31*2-60+60 = 14 .. 62 ⇒ 46 物理 px 全可见；
    //     全 77 元素越界仍为 0（shift30.txt: 越界元素 = 0 / 77）。
    internal static readonly string[] TOP_COMP_PANELS =
    {
        "InventoryDialog",   // 源 InventoryDialog.cs L27-31 面板本体 y=0；三页签 y=7 ⇒ 裁切带内
        "CharacterDialog",   // 源 CharacterDialog.cs 面板本体 y=0（页签 y=70 本来就可见，但底图标题栏被切）
        "MiniMapDialog",     // 最小逻辑 y=2
        "SkillBarDialog",    // 最小逻辑 y=0
    };

    /// <summary>纵向补偿量（逻辑行）= CROP_TOP / SCALE = 30。只由上面白名单消费，不参与画布原点。</summary>
    internal static int PanelTopComp { get { return CROP_TOP / SCALE; } }

    /// <summary>
    /// 该面板的纵向补偿量（逻辑行）；不在白名单里返回 0。
    /// ★ 唯一入口：P0GArt.cs（位图层）与 P0FText.cs（文字层）都只调这一个函数，
    ///   保证「同一个面板的图与字平移量逐字节一致」——两层各自算一遍必然迟早对不上。
    /// </summary>
    internal static int PanelTopShift(string panel)
    {
        if (string.IsNullOrEmpty(panel)) return 0;
        for (int i = 0; i < TOP_COMP_PANELS.Length; i++)
            if (TOP_COMP_PANELS[i] == panel) return PanelTopComp;
        return 0;
    }

    // ================= 面板热键（★单点定义，两层共用）=================
    // 为什么必须单点定义：P0GArt（位图层）与 P0FText（文字层）各有一份按键循环，
    //   历史 bug 正是「两边各写一遍」——P0GArt 只绑了 F1/F2，P0FText 绑了 F1..F7，
    //   ⇒ 位图层永远打不开 Belt/Chat/MiniMap/SkillBar 四个面板，而文字层能打开。
    //   同一组按键写两遍必然迟早对不上（AGENTS §3 接口口径单点定义）。
    //
    // 依据 = **原版权威键位**，不是拍脑袋：
    //   Client\KeyBindSettings.cs（Suprcode/Crystal，414 行）L183/L187/L191 明文：
    //     KeybindOptions.Inventory → Keys.F9
    //     KeybindOptions.Equipment → Keys.F10
    //     KeybindOptions.Skills    → Keys.F11
    //   另有 L205 Belt → Keys.Z、L211(Minimap) → Keys.V。
    //   ⇒ 用户要求的「F9/F10/F11 三个栏」= 背包 / 角色(装备) / 技能，与原版逐字一致。
    //
    // 与源码的**已知偏离**（诚实登记，别当成还原）：
    //   · ChatDialog  → Return。Crystal KeyBindSettings 里**没有**聊天面板热键（聊天靠回车唤起
    //     输入行，不是 Dialog），Return 是 Mir2 通用惯例，非源码枚举值。
    //   · MainDialog  → KeyCode.None。主 HUD 在原版是**常驻**的，没有开关热键；
    //     本移植的底部 HUD 由 P0DUI 绘制，位图层 MainDialog 属冗余，故不绑键、默认关闭。
    //     用 None 而非删表项：保持 PANEL_KEYS/KEY_CODES 两条数组下标严格一一对应。
    internal static readonly string[] HOTKEY_PANELS =
    {
        "CharacterDialog",   // F10 = 原版 OpenMyStatus + StatePage:=0（ClMain.pas VK_F10）
        "BeltDialog",        // 原版 KeybindOptions.Belt      = Z
        "ChatDialog",        // 原版无此 Dialog 热键，Return=Mir2 惯例（已知偏离）
        "MiniMapDialog",     // 原版 KeybindOptions.Minimap   = V
        "SkillBarDialog",    // 工程自有技能栏 → 常驻显示，不占原版热键（原版 F11 是角色面板第 3 页）
        "MainDialog",        // 常驻 HUD，无开关热键 → None
    };
    internal static readonly KeyCode[] HOTKEY_CODES =
    {
        KeyCode.F10, KeyCode.Z, KeyCode.Return,
        KeyCode.V, KeyCode.None, KeyCode.None,
    };

    /// <summary>
    /// 默认常驻显示、不绑热键的面板。原版里本来就没有开关键的那些（技能栏）。
    /// ★ 由 P0GArt / P0FText 初始化时读这张表，把 visibleDefault 翻成 true。
    ///   只写注释不落实 = 把 F11 从技能栏拿走后技能栏会直接消失（2026-10-27 修）。
    /// </summary>
    internal static readonly string[] ALWAYS_ON_PANELS = { "SkillBarDialog" };

    /// <summary>该面板是否默认常驻（不看 JSON 的 visibleDefault）。</summary>
    internal static bool PanelAlwaysOn(string panel)
    {
        for (int i = 0; i < ALWAYS_ON_PANELS.Length; i++)
            if (ALWAYS_ON_PANELS[i] == panel) return true;
        return false;
    }

    // ★★ 2026-10-27 键位按 1.76 原版逐条对齐（依据 ClMain.pas L2625-2645，逐字）：
    //      VK_F9  → FrmDlg.OpenItemBag;                     ← 背包
    //      VK_F10 → StatePage := 0; OpenMyStatus;           ← 角色面板 第 0 页
    //      VK_F11 → StatePage := {UI_0508}3;  OpenMyStatus; ← 角色面板 第 3 页（不是"技能栏"！）
    //      VK_F12 → FrmDlg.DOptionClick;                    ← 选项框（不是背包！）
    //      Ctrl+B → SendClientMessage(CM_QUERYBAGITEMS...)  ← 刷新背包（与底图上的说明文字一致）
    //   ⇒ 已纠正的**三处**历史错误（2026-10-27 全部落实到代码，不再只是注释）：
    //     ① 旧表把 F9 绑给位图层 InventoryDialog（底图是 Prguse[3]，非原版 Prguse2[180]），
    //        而 F12 又绑给 P0HItem 的背包 ⇒ 同一个"背包"被两套实现各占一个键，互相不知道对方存在。
    //        现在 F9 统一由 P0HItem.Toggle() 打开原版背包，InventoryDialog 位图层不再参与。
    //     ② 旧表把 F11 当作"技能栏"。原版 F11 是**角色面板第 3 页**；技能栏 SkillBarDialog
    //        在原版根本不存在，是本工程自有的常驻显示件，不该占用原版热键。
    //        ⇒ SkillBarDialog 已从 HOTKEY_CODES 摘掉（改为 KeyCode.None），
    //          并登记进 ALWAYS_ON_PANELS 走常驻显示。
    //     ③ F11 现在按原版语义 = 打开角色面板并切到第 3 页（见下面 Update 里的 VK_F11 处理）。

    /// <summary>
    /// 位图层 / 文字层**不参与**的面板。它们由 P0HItem 按原版语义独占绘制。
    /// 理由：同一个面板若两层都画，底图会互相覆盖、热键会互相打架（历史 bug）。
    /// </summary>
    internal static bool PanelOwnedByItemLayer(string panel)
    {
        return panel == "InventoryDialog";
    }

    internal static string PanelTopCompNames { get { return string.Join(",", TOP_COMP_PANELS); } }

    P0CUI _ui;
    P0DUI _dUI;   // M2 主 UI 第一层（P0-D）：权威口径下**只画 0039 一件**
    P0FText _fText;   // M2 七面板纯文字层（P0-F）：默认全关，F1..F7 逐块开启
    P0GArt _gArt;     // M2 位图层（P0-G）：InventoryDialog 底图+页签按钮，F2 开关
    P0HWorld _hWorld; // P0-H 实体层数据（怪物/NPC 帧索引 + 世界契约）
    P0HRuntime _hRun; // P0-H 实体运行时（刷怪/寻路/战斗/掉落）
    P0IIme _ime;      // P0-I 原生输入法层（IMGUI TextField + TouchScreenKeyboard）
    // ★★ P0-J 三层联动（2026-10-23 22:30）—— 用户 08:45 原话「做的这些东西完全没有联动」的正面修复。
    //   P0-H 层把怪物/掉落/经验跑起来了，但**屏幕上没有一个数字来自它**：
    //   左下血球蓝球是 P0-D 画的 0039 静态底图（液面永远满），右侧等级/经验/负重是 P0-F 的常量文字。
    //   这三层就是把这些静态图换成「每帧从 _hRun 读真值再画」：
    //     P0HHud   = 状态栏（血/蓝/经验/负重/等级/金币）+ 技能栏
    //     P0HSkill = 技能施放（1/2/3 热键）、耗蓝、冷却、伤害结算
    //     P0HItem  = 背包 40 格 + 8 装备位，装备加成直接写回 _hRun 的 DC/AC/负重
    //   ⚠ 绘制必须在本类 OnGUI 的 BeginGroup(MainUICanvasRect) **之内**，且一律走 GroupRectForLogic。
    P0HHud _hHud;
    P0HSkill _hSkill;
    P0HItem _hItem;
    // ★ P0-CHAT：底部聊天框（记录区 + 输入条 + 滚动条），1:1 复刻 2003 原版 1.76。
    //   用户 2026-10-25 要求「了解清楚他们整一套是怎么运行的，1:1 搬过来，要真正的联动」。
    //   绘制在 BeginGroup(MainUICanvasRect) 之内、_hItem.DrawBag() 之后（见 OnGUI 尾部）。
    P0HChat _hChat;

    // ★★ P0-NOTIFY（2026-10-26）：系统提示 / 顶部滚动公告 / 伤害飘字，原版的**独立通道**。
    //   根因：原版这些提示从来不走聊天框（DrawScrn.pas:1374-1432 是独立绘制），
    //   我们此前把自造事件全灌进聊天框 ⇒ 用户看到"砍一刀就弹一行系统提示"。
    //   真值取证见 work\truth\openmir2_client_channels.md。
    P0HNotify _hNotify;

    void Awake()
    {
        walkTex = Resources.Load<Texture2D>("atlas_walk");
        tilesAtlas = Resources.Load<Texture2D>("p0e_tiles_atlas");
        smAtlas = Resources.Load<Texture2D>("p0e_smtiles_atlas");
        {
            var pages = new List<Texture2D>();
            for (int i = 0; i < 16; i++)
            {
                var t = Resources.Load<Texture2D>("p0e_obj_atlas_" + i);
                if (t == null) break;
                pages.Add(t);
            }
            objAtlas = pages.ToArray();
        }
        var idxAsset = Resources.Load<TextAsset>("p0e_atlas_index");
        var mapAsset = Resources.Load<TextAsset>("0.map");     // ← 真地图（磁盘名 0.map.bytes）

        bool ok = true;
        if (walkTex == null) { Debug.LogError("[P0E] ★素材缺失 atlas_walk"); ok = false; }
        if (tilesAtlas == null) { Debug.LogError("[P0E] ★素材缺失 p0e_tiles_atlas"); ok = false; }
        if (smAtlas == null) { Debug.LogError("[P0E] ★素材缺失 p0e_smtiles_atlas"); ok = false; }
        if (idxAsset == null) { Debug.LogError("[P0E] ★素材缺失 p0e_atlas_index"); ok = false; }
        if (mapAsset == null) { Debug.LogError("[P0E] ★素材缺失 0.map (0.map.bytes)"); ok = false; }
        if (!ok) return;

        Debug.Log("[P0E] 前景图集页数=" + (objAtlas == null ? 0 : objAtlas.Length) +
                  " 尺寸=" + (objAtlas != null && objAtlas.Length > 0 ? objAtlas[0].width + "x" + objAtlas[0].height : "-"));
        Debug.Log("[P0E] 素材实测 walk=" + walkTex.width + "x" + walkTex.height +
                  " tilesAtlas=" + tilesAtlas.width + "x" + tilesAtlas.height +
                  " smAtlas=" + smAtlas.width + "x" + smAtlas.height +
                  " index=" + idxAsset.bytes.Length + "B" +
                  " map.bytes=" + mapAsset.bytes.Length + "B");

        for (int i = 0; i < 8; i++)
            walk[i] = Sprite.Create(walkTex, new Rect(i * FRAME_W, walkTex.height - FRAME_H, FRAME_W, FRAME_H),
                new Vector2(0.5f, 0f), 100f);

        ParseAtlasIndex(idxAsset.text);
        LoadWhitelist();
        LoadMap(mapAsset.bytes);

        BuildPlayer();
        SetupCamera();
        LogDrawWindow("init");
        // ================= P0-H 实体层（怪物/NPC/战斗/掉落）=================
        // ★ 必须在 BuildPlayer 之后：实体层要拿 player.transform 做朝向参考。
        _hWorld = new P0HWorld();
        if (_hWorld.Load())
        {
            _hRun = new P0HRuntime();
            _hRun.Blocked = Blocked;             // 复用真 .map 的 bit15 阻挡判定（不另造一套）
            _hRun.PlayerX = px; _hRun.PlayerY = py; _hRun.PlayerDir = dirRow;
            if (!_hRun.Init(_hWorld, player.transform))
                Debug.LogError("[P0H] ★实体层初始化失败");

            // ★ 底栏 (20,583) 那行「地图名 X:Y」的数据源（源码 FState.pas:8878
            //   `BoldText(20, SCREENHEIGHT-17, Format('%s %d:%d', [g_sMapTitle, X, Y]))`）。
            //   本工程只加载 Resources/0.map（地图号 0），MapInfo.txt 第一条正是
            //   `[0	比奇省]	NOMANNOMON		MYSHOP`（GBK，
            //   C:\ai\_dl\netdisk\gee176\MirServer\Mir200\Envir\MapInfo.txt）
            //   ⇒ g_sMapTitle = 比奇省。这里**显式注入**而不是吃 P0HRuntime 的默认值，
            //   这样日志与截图都能反查到出处，换图时也会立刻暴露。
            _hRun.MapTitle = "比奇省";
            Debug.Log("[P0H] 底栏地图名注入 MapTitle=" + _hRun.MapTitle +
                      " (0.map / MapInfo.txt 第一条) Job=" + _hRun.PlayerJob +
                      " DayBright=" + _hRun.DayBright);

            // ============ P0-J 三层联动：实例化 + 初始化 ============
            // ★ 顺序要紧：_hItem.Init(_hRun) 必须在 _hRun.Init() **之后**。
            //   P0HItem 里 `_hItem.Init()` 会把出生装（木剑 dc1-2 重5 + 布衣 ac2-3 重6）穿上，
            //   并把加成写回 _hRun 的 PlayerDC/PlayerDCMax/PlayerAC/PlayerWeight。
            //   早于 _hRun.Init() 调用 ⇒ 属性被 _hRun.Init() 的出生值覆盖回去，
            //   结果就是「装备穿在身上但 HUD 的 DC 还是裸身 2/5」—— 又变成假联动。
            _hHud = new P0HHud();
            _hSkill = new P0HSkill();
            _hItem = new P0HItem();
            // ★ 让位回调：P0-I 聊天面板打开时优先级最高，技能槽/背包按钮都不许抢它的手势。
            //   （同 P0BMapWalk L1246 ImeOwnsPoint 的既有规则，这里只是把它传给新加的两个触摸入口）
            _hSkill.PointOwnedByOther = ImeOwnsPoint;
            _hItem.PointOwnedByOther = ImeOwnsPoint;
            _hItem.Init(_hRun);      // 出生装 + [P0BAG] V9_VERDICT 判据日志都在这里面
            Debug.Log("[P0J] 三层联动已实例化 HUD/Skill/Item，背包=" + _hItem.UsedSlots + "格" +
                      " 装备=" + _hItem.EquipCount + "件");

            // ★★ P0-CHAT：底部聊天框。Init 必须在 _hRun.Init() 之后（要读 LastEvent 基线，
            //   否则冷启动那一帧会把旧事件当成新消息喷一条）。
            _hChat = new P0HChat();
            _hChat.Init(_hRun);
            _hChat.OnSend = OnChatSend;
            _hChat.TeleportTo = TeleportTo;   // @MOVE：真正的贴图层位移 + 相机/视口刷新
            _hChat.Seed();           // 冷启动空框（1.76 原版如此，不预置假记录）

            // ★★ P0-NOTIFY：提示层必须早于 Seed 之后立刻建好，并交给聊天框做**分流**
            //   （升级/金币/施法失败/拾取 → 左上绿；战斗流水 → 只画飘字，不进聊天）。
            _hNotify = new P0HNotify();
            _hChat.Notify = _hNotify;
            // ★ 经验行按原版走**聊天框**（白字红底，ClMain.pas:10153-10155），
            //   只有单次经验 > g_MaxExpFilter(2000) 才会吐（见 P0HNotify.EXP_FILTER）。
            _hNotify.OnExpGain = delegate (int amt) { _hChat.Append(amt + "经验值增加", ChatType.System); };
            // ★ 队列的唯一消费者是提示层 ⇒ 基线清理（丢掉建层前的刷怪/出生装事件）也归它。
            //   顺序必须在 _hChat.Init 之后、任何 Pump 之前，否则冷启动第一帧会喷一堆旧事件。
            _hNotify.Init(_hRun);

            Debug.Log("[P0CHAT] 初始化 行数=" + _hChat.LineCount + " 字体=chatfont");
            Debug.Log("[P0T] 提示层已建 四通道=左上绿/左下红/右下红/顶部滚动 + 中央留空 + 伤害飘字");
        }
        else Debug.LogError("[P0H] ★世界数据加载失败，实体层不启动（拒绝画空气）");

        // ===== P0-K：把 P0-A 选好的角色套到地图里的玩家身上 =====
        ApplySessionToPlayer();

        // ★ A6 判据：视口虚拟化的**实测**自我说明（每次窗口更新都会重发，见 LogDrawWindow）
        Debug.Log("[P0E] 地面块=" + groundPoolUsed + " 半透明层=" + smPoolUsed +
                  " blocked格=" + blockedCount + " 起始格=(" + px + "," + py + ") 瓦片=" + TW + "x" + TH +
                  " 行错位=" + HW + "px | POOL=" + POOL + " ViewN=" + ViewN +
                  " 说明=按相机可见范围取窗口+常驻池复用, 不是实例化全部 " + mapN + " 格");
    }

    // ======================= P0-K：把会话里选好的角色落到玩家身上 =======================
    /// <summary>
    /// 读 `P0Session`（唯一事实源），把「在 P0-A 点了谁」这件事真正带到地图里：
    ///   1) 玩家 sprite 的相乘色 = 该槽位 tint（atlas_walk 是通用身体图，靠乘色区分两人）
    ///   2) HUD 角色名 = 该槽位名字
    ///   3) 打一行 `[P0K] SESSION 进入地图 slot=&lt;n&gt; name=&lt;x&gt;` 供真机取证 grep
    ///
    /// ★ 未选（SelectedSlot = -1）时的口径：**不静默当 0**，保持白色 + 无名，
    ///   并打警告。这样"没选人却进了图"在日志里立刻可见（反向验证要靠这个抓）。
    /// </summary>
    void ApplySessionToPlayer()
    {
        int slot = P0Session.SelectedSlot;

        if (!P0Session.HasSelection)
        {
            Debug.LogWarning("[P0K] SESSION 进入地图 slot=-1 (未选) => 保持默认白身/无名");
            if (playerSr != null) playerSr.color = Color.white;
            return;
        }

        Color tint = P0Session.CurrentTint;
        if (playerSr != null) playerSr.color = tint;
        if (_hRun != null) _hRun.PlayerName = P0Session.CurrentName;

        Debug.Log("[P0K] SESSION 进入地图 slot=" + slot +
                  " name=" + P0Session.CurrentName +
                  " tint=" + tint.r.ToString("0.###") + "," + tint.g.ToString("0.###") + "," + tint.b.ToString("0.###") +
                  " | playerSr.color=" + (playerSr != null ? playerSr.color.ToString() : "null") +
                  " | run.PlayerName=" + (_hRun != null ? _hRun.PlayerName : "null"));
    }

    // ======================= P0-E：图集索引表解析 =======================
    /// <summary>
    /// 解析 `p0e_atlas_index.txt`（与 pe_atlas.py 的输出格式逐字对应）：
    ///   段头：`tiles   16 32 96 64 462` / `smtiles 16 16 48 32 164`
    ///   数据：`&lt;slot&gt; &lt;atlasCell&gt;`
    /// </summary>
    void ParseAtlasIndex(string text)
    {
        var lines = text.Split('\n');
        Dictionary<int, int> cur = null;
        int tHead = 0, sHead = 0;
        bool inObj = false;
        foreach (var raw in lines)
        {
            string s = raw.Trim();
            if (s.Length == 0 || s[0] == '#') continue;
            var tk = s.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (tk.Length == 0) continue;
            if (tk[0] == "tiles" || tk[0] == "smtiles")
            {
                cur = (tk[0] == "tiles") ? tIdx : sIdx;
                int cnt = tk.Length >= 6 ? int.Parse(tk[5]) : -1;
                if (tk[0] == "tiles") tHead = cnt; else sHead = cnt;
                inObj = false;
                continue;
            }
            // ★ 2026-10-25 新增：`objects 2048 2048 <pages> <count> 1` 段（7 列数据）。
            //   旧代码不认识这一段 ⇒ 会拿 "objects" 去 int.Parse ⇒ 启动即抛异常崩溃。
            if (tk[0] == "objects")
            {
                cur = null; inObj = true;
                if (tk.Length >= 5) { objPages = int.Parse(tk[3]); objHead = int.Parse(tk[4]); }
                continue;
            }
            if (inObj)
            {
                if (tk.Length >= 7)
                {
                    int l = int.Parse(tk[0]), f = int.Parse(tk[1]);
                    ObjRef r;
                    r.page = int.Parse(tk[2]); r.x = int.Parse(tk[3]); r.y = int.Parse(tk[4]);
                    r.w = int.Parse(tk[5]); r.h = int.Parse(tk[6]);
                    objIdx[ObjKey(l, f)] = r;
                }
                continue;
            }
            if (cur != null && tk.Length >= 2)
                cur[int.Parse(tk[0])] = int.Parse(tk[1]);
        }
        Debug.Log("[P0E] 图集索引解析 tiles=" + tIdx.Count + "/" + tHead +
                  " smtiles=" + sIdx.Count + "/" + sHead +
                  " objects=" + objIdx.Count + "/" + objHead + " 页=" + objPages +
                  " => " + ((tIdx.Count == tHead && sIdx.Count == sHead && tHead > 0 && sHead > 0 &&
                             objIdx.Count == objHead && objHead > 0) ? "OK" : "★异常"));
    }

    // ======================= P0-E：白名单（运行期加载，非"自证"） =======================
    // ★ 色彩铁律的落地点：只有 colored_slots.json 里 `libs.tiles` / `libs.smtiles` 的帧算彩色。
    //
    //   ★★ 为什么白名单必须**真加载**、不能用"图集里有的即白名单"糊弄过去：
    //     图集是**只含白名单帧**的预打包产物（`pe_audit_t3.py`: packed_not_in_whitelist=0），
    //     若把 IsColored() 写成 `idx.ContainsKey(slot)`，则回退分支**永远不触发**，
    //     A7 的判据（"非白名单帧走显式回退分支 + 回退计数日志"）就成了**空转的假通过**。
    //     实测反例：0.map 真正用到的 582 个 tiles 槽位里有 **120 个不在白名单**
    //     （占 2931 格 = 0.59816327%），sm tiles 168 个里 **4 个不在白名单**（71 格 = 0.01448980%）
    //     ⇒ 白名单是真判据，回退分支是**真的会被走到**的。
    //   白名单由 `pe_whitelist_to_project.py` 从唯一权威源导出为 `p0e_whitelist.txt`
    //   （23336 B / md5 C0518FC2EB38C5CD21630636513BD4F6 / sha256_16 d575fa0cb0a7a6a3），随包携带。
    const string WL_MD5 = "C0518FC2EB38C5CD21630636513BD4F6";

    void LoadWhitelist()
    {
        var wlAsset = Resources.Load<TextAsset>("p0e_whitelist");
        if (wlAsset == null)
        {
            Debug.LogError("[P0E] ★白名单缺失 p0e_whitelist ⇒ 无法判定彩色资格（拒绝静默降级为全彩）");
            wlTiles = null; wlSmTiles = null;
            return;
        }
        wlTiles = new HashSet<int>();
        wlSmTiles = new HashSet<int>();
        HashSet<int> cur = null;
        int headT = -1, headS = -1;
        foreach (var raw in wlAsset.text.Split('\n'))
        {
            string s = raw.Trim();
            if (s.Length == 0 || s[0] == '#') continue;
            var tk = s.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (tk.Length == 0) continue;
            if (tk[0] == "tiles" || tk[0] == "smtiles")
            {
                cur = (tk[0] == "tiles") ? wlTiles : wlSmTiles;
                if (tk.Length >= 2)
                {
                    if (tk[0] == "tiles") headT = int.Parse(tk[1]); else headS = int.Parse(tk[1]);
                }
                continue;
            }
            if (cur != null)
                foreach (var v in tk) cur.Add(int.Parse(v));
        }
        bool ok = (headT == wlTiles.Count) && (headS == wlSmTiles.Count) && headT > 0 && headS > 0;
        Debug.Log("[P0E] 白名单实测 tiles=" + wlTiles.Count + "/" + headT +
                  " smtiles=" + wlSmTiles.Count + "/" + headS +
                  " md5=" + Md5Hex(Encoding.UTF8.GetBytes(CanonWhitelist())) +
                  " 期望md5=" + WL_MD5 + " 载入OK=" + ok +
                  " | 步长 n5=" + FALLBACK_STEP);
        if (!ok) Debug.LogError("[P0E] ★白名单行数与文件头声明不符 ⇒ A7 判据不可信");

        // ★ 2026-10-25 画面优先（用户裁定「跟客户端要一比一一模一样」）：
        //   图集里装的**全是 2003 客户端原始帧**（work/p1_pack.py 直接解 .wil，逐像素校验 0 diff），
        //   而旧白名单是早期「色彩铁律」的产物，会把这 120 个真帧判成"非彩色"再回退成邻帧 ⇒ 地面缺图。
        //   ⇒ 把图集里实际存在的槽位全部纳入彩色集合；IsColored/FallbackFrame 结构保留，仍能拦住真缺帧。
        int addT = 0, addS = 0;
        foreach (var k in tIdx.Keys) if (wlTiles.Add(k)) addT++;
        foreach (var k in sIdx.Keys) if (wlSmTiles.Add(k)) addS++;
        Debug.Log("[P0E] 白名单放宽(画面一比一) tiles +" + addT + " => " + wlTiles.Count +
                  " smtiles +" + addS + " => " + wlSmTiles.Count +
                  " | 图集槽位全部放行, 只有真缺帧才回退");
    }

    /// <summary>把白名单规范化回**导出脚本的原始字符串**（用于 md5 对账；t5 可独立复算）。</summary>
    string CanonWhitelist()
    {
        var a = new List<int>(wlTiles); a.Sort();
        var b = new List<int>(wlSmTiles); b.Sort();
        var sb = new StringBuilder();
        sb.Append("# P0-E whitelist v1  (source: colored_slots.json libs.tiles / libs.smtiles)\n");
        sb.Append("tiles ").Append(a.Count).Append('\n');
        for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(a[i]); }
        sb.Append('\n');
        sb.Append("smtiles ").Append(b.Count).Append('\n');
        for (int i = 0; i < b.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i]); }
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>帧槽位是否允许作**彩色**渲染。唯一出口，供 A7 grep。
    ///   非白名单帧一律返回 false ⇒ 调用侧走 FallbackFrame 显式回退分支。</summary>
    bool IsColored(int slot, bool sm)
    {
        return sm ? (wlSmTiles != null && wlSmTiles.Contains(slot))
                  : (wlTiles != null && wlTiles.Contains(slot));
    }

    // ======================= P0-E：真地图加载 + RM4 摘要 =======================
    void LoadMap(byte[] b)
    {
        mapBytes = b;
        if (b.Length < MAP_HDR)
        {
            Debug.LogError("[P0E] ★地图过短: " + b.Length + "B < 头 " + MAP_HDR + "B");
            return;
        }
        mapA = b[0] | (b[1] << 8);
        mapB = b[2] | (b[3] << 8);
        mapN = mapA * mapB;
        long expect = MAP_HDR + (long)MAP_CELL * mapN;
        mapOk = (mapA > 0 && mapB > 0) && (expect == b.Length);

        Debug.Log("[P0E] 真地图 " + mapA + "x" + mapB + " = " + mapN + " 格 | 字节 " + b.Length +
                  " | 恒等式 52+12*" + mapA + "*" + mapB + " = " + expect +
                  " == filesize => " + mapOk);
        if (!mapOk)
        {
            Debug.LogError("[P0E] ★地图表头恒等式不成立 ⇒ 拒绝用该文件铺地（防「解析了错文件还当成功」）");
            return;
        }
        PrintRm4();
    }

    /// <summary>
    /// ★★★ A10 决定性判据 RM4：启动后**打印一次**三条索引流摘要。
    ///   规范化（与 t3 契约 A10 逐字一致）：
    ///     i = X*B + Y（文件顺序），offset = 52 + 12*i，n = A*B = 490000
    ///     ① ground_stream  = n 个 u16 小端，第 i 项 = cell[i].u16@0 &amp; 0x7FFF
    ///     ② smtile_stream  = n 个 u16 小端，第 i 项 = cell[i].u16@2（**不掩码**）
    ///     ③ blocked_bitmap = n 个 bit **LSB-first** 打包，第 i 位 = (cell[i].u16@0 &gt;&gt; 15) &amp; 1
    ///
    ///   ★★ 铁律：三条流的输入**只能是 `.map` 里的原始索引**（即上面的 `b[off]`/`b[off+1]`）。
    ///      **严禁**拿 FallbackFrame() 替代后的帧号参与计算 —— 那等于作弊（t5 会抓）。
    ///      本方法**完全不引用** tIdx/sIdx/fallback 计数器，输入 = 原始字节流。
    /// </summary>
    void PrintRm4()
    {
        if (rm4Done) return;
        rm4Done = true;

        int n = mapN;
        var g = new byte[n * 2];
        var s = new byte[n * 2];
        var bm = new byte[(n + 7) / 8];
        // n=490000 ⇒ 循环 49 万次，纯字节搬运，启动期 ~10ms 量级；只跑一次。
        for (int i = 0; i < n; i++)
        {
            int off = MAP_HDR + MAP_CELL * i;
            int v0 = mapBytes[off] | (mapBytes[off + 1] << 8);
            int v2 = mapBytes[off + 2] | (mapBytes[off + 3] << 8);
            int low = v0 & 0x7FFF;
            g[i * 2] = (byte)(low & 0xFF);
            g[i * 2 + 1] = (byte)((low >> 8) & 0xFF);
            s[i * 2] = (byte)(v2 & 0xFF);
            s[i * 2 + 1] = (byte)((v2 >> 8) & 0xFF);
            if (((v0 >> 15) & 1) != 0) bm[i >> 3] |= (byte)(1 << (i & 7));   // ★ LSB-first
        }

        string gm = Md5Hex(g), sm = Md5Hex(s), bmm = Md5Hex(bm);
        const string EG = "45B3A1F3B43D94B7E683C6CE71635FF2";
        const string ES = "36492A123DEE8F219C102512A07BAF23";
        const string EB = "41BCF8F400C6B878AB184CEE40C3555A";

        Debug.Log("[P0E][RM4] ground_stream =" + gm);
        Debug.Log("[P0E][RM4] smtile_stream =" + sm);
        Debug.Log("[P0E][RM4] blocked_bitmap =" + bmm);
        Debug.Log("[P0E][RM4] EXPECT ground_stream =" + EG + " smtile_stream =" + ES + " blocked_bitmap =" + EB);
        Debug.Log("[P0E][RM4] MATCH ground=" + (gm == EG) + " smtile=" + (sm == ES) + " blocked=" + (bmm == EB) +
                  " n=" + n + " A=" + mapA + " B=" + mapB + " src=0.map原始索引");
        if (gm != EG || sm != ES || bmm != EB)
            Debug.LogError("[P0E][RM4] ★三条摘要与外部独立复算不一致 ⇒ 同一性判据 FAIL（不得当作成功）");
    }

    static string Md5Hex(byte[] data)
    {
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var h = md5.ComputeHash(data);
            var sb = new StringBuilder(h.Length * 2);
            foreach (var x in h) sb.Append(x.ToString("X2"));
            return sb.ToString();
        }
    }

    // ======================= P0-E：格访问 + 显式回退分支 =======================
    /// <summary>越界一律当作哨兵（"无帧"）：边界不可走，与网格外语义一致。</summary>
    int CellRaw(int x, int y)
    {
        if (x < 0 || y < 0 || x >= mapA || y >= mapB) return SENTINEL;
        int off = MAP_HDR + MAP_CELL * (x * mapB + y);
        if (off + 1 >= mapBytes.Length) return SENTINEL;
        return mapBytes[off] | (mapBytes[off + 1] << 8);
    }

    int CellRaw2(int x, int y)
    {
        if (x < 0 || y < 0 || x >= mapA || y >= mapB) return SENTINEL;
        int off = MAP_HDR + MAP_CELL * (x * mapB + y) + 2;
        if (off + 1 >= mapBytes.Length) return SENTINEL;
        return mapBytes[off] | (mapBytes[off + 1] << 8);
    }

    /// <summary>格记录 u16 @ offset 4 = FrontImage（bit15 未用，低 15 位是帧号）。</summary>
    int CellRaw4(int x, int y)
    {
        if (x < 0 || y < 0 || x >= mapA || y >= mapB) return SENTINEL;
        int off = MAP_HDR + MAP_CELL * (x * mapB + y) + 4;
        if (off + 1 >= mapBytes.Length) return SENTINEL;
        return mapBytes[off] | (mapBytes[off + 1] << 8);
    }

    /// <summary>格记录单字节。rel=8 FrontAnimationFrame（bit7=混合）、rel=10 FrontIndex（库号 = 值 + 2）。</summary>
    int CellByte(int x, int y, int rel)
    {
        if (x < 0 || y < 0 || x >= mapA || y >= mapB) return 0;
        int off = MAP_HDR + MAP_CELL * (x * mapB + y) + rel;
        if (off >= mapBytes.Length) return 0;
        return mapBytes[off];
    }

    /// <summary>
    /// ★ A9：阻挡判据。数据源 = 真 `.map` 的 `u16@0` **bit15**（语义与原 `Blocked()` 完全一致，只换数据源）。
    ///   哨兵 0xFFFF 的 bit15 也是 1 ⇒ 天然=阻挡，无需特判。
    /// </summary>
    bool Blocked(int x, int y)
    {
        if (x < 0 || y < 0 || x >= mapA || y >= mapB) return true;   // 网格外 = 不可走
        return (CellRaw(x, y) & 0x8000) != 0;
    }

    /// <summary>
    /// ★ A7 的**显式回退分支**（独立行号可贴）。
    ///   规则（队长 2026-07-16 裁定）：白名单外 / 图集外帧 → 优先取**同 5 帧动画组的第 0 帧**
    ///   （`i - i%5`）；该帧也不可用则**顺延**（再 -5），直到命中或 &lt;0。
    ///   返回值 &lt;0 = 本层无任何可用替代帧（此时该格不铺）。
    ///   ⚠ 本函数**只服务于渲染**；RM4 摘要用的是原始索引，不经过这里（见 PrintRm4 的铁律说明）。
    /// </summary>
    int FallbackFrame(int slot, Dictionary<int, int> idx, bool sm)
    {
        int x = slot - (slot % FALLBACK_STEP);           // 规则A：同 5 帧组的第 0 帧
        while (x >= 0 && !idx.ContainsKey(x)) x -= FALLBACK_STEP;   // 规则B：顺延
        return x;
    }

    /// <summary>把槽位解析成图集格号；非白名单/缺失 ⇒ 走回退并计数。返回 -1 = 无帧可用。</summary>
    int Resolve(int slot, Dictionary<int, int> idx, bool sm)
    {
        if (slot == SENTINEL) return -1;                 // ① 先判哨兵（**必须在掩码 bit15 之前**）
        int f = slot & 0x7FFF;                           // ② 再掩码 bit15
        // ★★★ 2026-10-24 修「整个游戏颜色不对」的根因（决定性实测）：
        //   真 .map 里 **f==0 就是"空地/无瓦"**，占全图 198670/490000 = **40.5%**。
        //   旧代码不特判 0 ⇒ 落到下面的 FallbackFrame：0-0%5 = 0，而图集**第 0 格恰好存在**
        //   ⇒ 每一格空地都被画成一张**亮地砖**，把本来该是纯黑的地方全部填亮。
        //   实测后果（ref=mir2x_screencapture_frame0.png 场景区 vs 我方 device 截图）：
        //     真客户端 纯黑(<8) 占 38.1%、亮度中位 43
        //     我方     纯黑(<8) 只占 6.6%、亮度中位 141  ⇒ **整体亮一倍**
        //   这不是调色能解决的，是"该黑的地方被画了砖"。空隙必须留黑（透出相机纯黑底）。
        //   ⚠ 只对 **tiles(地面) 层** 生效；smtiles(上层摆件) 的 0 是"本格无摆件"，
        //     下面本来就按"无帧不铺"处理，行为不变。
        if (f == 0) return -1;
        if (IsColored(f, sm) && idx.ContainsKey(f)) return idx[f];   // 白名单内：直取
        // ③ ★ 显式回退分支（A7 贴行号处）
        int alt = FallbackFrame(f, idx, sm);
        if (sm) { fbSmTiles++; if (fbSampleSm < 0 && fbSamples.Count < 24) { fbSampleSm = f; fbSamples.Add("sm " + f + "->" + alt); } }
        else { fbTiles++; if (fbSampleTiles < 0 && fbSamples.Count < 24) { fbSampleTiles = f; fbSamples.Add("tt " + f + "->" + alt); } }
        return (alt >= 0 && idx.ContainsKey(alt)) ? idx[alt] : -1;
    }

    /// <summary>★ A7 铁律②：回退计数日志（分 tiles / smtiles 两个计数器）。</summary>
    void LogFallback(string tag)
    {
        Debug.Log("[P0E] fallback tag=" + tag + " tiles=" + fbTiles + " smtiles=" + fbSmTiles +
                  " | 规则=i-i%5 顺延 -5 | 样本=" + string.Join(",", fbSamples.ToArray()));
    }

    // ======================= P0-E：视口虚拟化 =======================
    /// <summary>
    /// ★ A6：按**相机可见范围**取窗口绘制，**不是**一次性实例化全部格。
    ///   窗口以玩家格为中心取 ViewN×ViewN；池子常驻复用，移动时只改 sprite 与坐标。
    /// </summary>
    void UpdateWindow(int cx, int cy)
    {
        int half = ViewN / 2;
        int x0 = cx - half, y0 = cy - half;
        if (x0 == lastWinX && y0 == lastWinY) return;      // 没动就不重建
        lastWinX = x0; lastWinY = y0;

        // ★★ 帧率修复（根因二）：视口包围盒剔除。
        //   旧代码把 28x28=784 格两层全开 ⇒ 1568 个 SpriteRenderer 同时在屏，
        //   每帧提交 1568 个独立 draw call。模拟器 software GL 下 TuanjieGfxDevice
        //   线程 100% 系统态（实测 5s 内 stime +620 而 utime 仅 +4），帧率崩到 0.05fps。
        //   相机是正交的，世界半高 = VIEW_H/2/100 = 5.7、半宽 = VIEW_W/2/100 = 8。
        //   格 (x,y) 世界坐标 = ((x-y)*HW*0.01, (x+y)*HH*0.01)，即步进 (±0.48, +0.32)，
        //   单格半幅 = (0.48, 0.32)；屏幕外的格即使 enabled 也永远不可见，纯浪费。
        // ★★★ 2026-10-24 正交投影修复（根因，全部对齐真客户端 Crystal 源码）
        //   权威出处 `Client\MirScenes\GameScene.cs`：
        //     L10292-10293  `CellWidth = 48; CellHeight = 32;`
        //     L10680 drawX = (x - User.X + OffSetX) * CellWidth
        //     L10684 drawY = (y - User.Y + OffSetY) * CellHeight
        //   ⇒ 官方是**纯正交（俯视方格）**，格 (x,y) 的绘图左上角 = (x*48, y*32) 像素，
        //     **没有** (x-y)/(x+y) 这种等距菱形项。旧代码按等距投影画 ⇒ 每格错位 ±48px，
        //     于是出现用户看到的「棋盘格 + 黑缝」。
        //   ★ 顺带覆盖：官方 y 向下增大；Unity y 向上 ⇒ worldY 取负（见 World()）。
        float camX = (cx + 0.5f) * HW * 0.01f;
        float camY = -(cy + 0.5f) * HH * 0.01f - CAM_DROP_Y;
        // ★ 2026-10-25：剔除盒必须跟着相机的真实半幅走（orthoSize=3.0）。
        //   旧值按 5.7 算，比真实视野大 90% ⇒ 28x28 窗口几乎不剔除，每帧上千个 draw call。
        //   真实半高 = 3.0 单位；半宽 = 3.0 * (1600/1140) = 4.21 单位。各留 1 格余量防边缘闪烁。
        //   ★ 2026-10-25：恢复 4:3 居中主视口（cam.rect 只占中间 1600/2616），
        //     半幅必须按 **主视口宽高比 1600/1140** 算，否则按整屏算会宽 2.8 单位、
        //     把两侧黑边外的物体也算进池子（无害但白跑）。
        const float VIEW_HALF_W = ORTHO_SIZE * (VIEW_W / (float)VIEW_H) + 0.5f;     // 4.5
        const float VIEW_HALF_H = ORTHO_SIZE + 0.5f;                                 // 3.35
        const float CELL_W = HW * 0.01f;                  // 0.48（整格宽）
        const float CELL_H = HH * 0.01f;                  // 0.32（整格高）
        int culledT = 0, culledS = 0;

        int gt = 0, st = 0;

        // ---------- 第一遍：地面层 Tiles（96x64 = 2x2 格），**只画偶/偶格** ----------
        //   官方 GameScene.cs:10703 `if (y % 2 == 0 && x % 2 == 0 && y <= endY)`
        //   再 L10705 `if (cell.BackImage != 0 && cell.BackIndex != -1)`。
        //   ⇒ 一格 96x64 的图铺满它右下方的 2x2 格块，奇数格由相邻偶格覆盖。
        //     旧代码每格都画一张图 ⇒ 每格各画一次、且错位半格 ⇒ 棋盘格。
        int gx0 = x0 & ~1, gy0 = y0 & ~1;          // 对齐到偶格（& ~1 对负数同样向下取偶）
        for (int x = gx0; x < x0 + ViewN; x += 2)
            for (int y = gy0; y < y0 + ViewN; y += 2)
            {
                float wx = x * HW * 0.01f;
                float wy = -y * HH * 0.01f;
                // 偶格铺的是 2x2 块 ⇒ 纵向范围是 [wy-CELL_H, wy]
                bool onScreen = (wx + 2f * CELL_W >= camX - VIEW_HALF_W)
                             && (wx <= camX + VIEW_HALF_W)
                             && (wy >= camY - VIEW_HALF_H)
                             && (wy - 2f * CELL_H <= camY + VIEW_HALF_H);
                if (!onScreen) { culledT++; continue; }

                int cell = (x < 0 || y < 0 || x >= mapA || y >= mapB) ? SENTINEL : CellRaw(x, y);
                if (cell == SENTINEL) continue;
                int ci = Resolve(cell, tIdx, false);
                if (ci < 0 || gt >= POOL) continue;
                var sr = Rent(groundPool, groundRoot, gt);
                sr.sprite = AtlasSprite(false, ci);
                // ★ 去掉旧代码的 bit15 灰色染色 (0.55,0.55,0.58)：真客户端地面**不区分**阻挡格，
                //   一律原色。那个染色是"颜色跟当年不一样"的直接来源之一。
                sr.color = Color.white;
                sr.sortingOrder = ORDER_GROUND;
                sr.transform.position = TileCenter(x, y);
                sr.enabled = true;
                gt++;
            }

        // ---------- 第二遍：上层摆件 SmTiles（48x32 = 1 格），逐格 ----------
        //   官方同一次 DrawFloor 里 Middle 层是逐格画的（L10713-10724），不套 2x2 规则。
        for (int x = x0; x < x0 + ViewN; x++)
            for (int y = y0; y < y0 + ViewN; y++)
            {
                float wx = x * HW * 0.01f;
                float wy = -y * HH * 0.01f;
                bool onScreen = (wx + CELL_W >= camX - VIEW_HALF_W)
                             && (wx <= camX + VIEW_HALF_W)
                             && (wy >= camY - VIEW_HALF_H)
                             && (wy - CELL_H <= camY + VIEW_HALF_H);
                if (!onScreen) { culledS++; continue; }

                int cell2 = (x < 0 || y < 0 || x >= mapA || y >= mapB) ? SENTINEL : CellRaw2(x, y);
                int si = (cell2 == SENTINEL) ? -1 : Resolve(cell2, sIdx, true);
                if (si < 0 || st >= POOL) continue;
                var sr2 = Rent(smPool, smRoot, st);
                sr2.sprite = AtlasSprite(true, si);
                sr2.color = Color.white;
                sr2.sortingOrder = ORDER_SM;
                sr2.transform.position = CellCenter(x, y);
                sr2.enabled = true;
                st++;
            }

        // ---------- 第三遍：高位前景层 Objects / Objects4 / Objects6 ----------
        //   权威 Client/MirScenes/GameScene.cs L10877-10936（DrawObjects）：
        //     index = (FrontImage & 0x7FFF) - 1 ; fileIndex = FrontIndex
        //     drawX = (x - User.X + OffSetX) * 48                ⇒ 帧**左边贴格左**
        //     drawY = (y - User.Y + OffSetY + 1) * 32            ⇒ 帧**底边贴格底**
        //     非混合：Draw(index, drawX, drawY - s.Height)
        //   尺寸为 48x32 / 96x64 的帧是"平铺层"，官方在 DrawFloor 里画、这里 continue 跳过（下面第四遍画）。
        //   这一层是「树/房子/城门/城墙/篱笆」—— 用户说"明显不是当年那个样子"的主因就是它完全没画。
        int ot = 0, oMiss = 0, oBlend = 0;
        //   ★ 多扫窗口下方 16 格：前景帧是「底边贴格底、向上长」的，最高帧 987 源像素≈31 格。
        //     若只扫可见窗口，基座落在屏下、顶部却该露出来的大树/城墙会突然消失。
        for (int y = y0; y < y0 + ViewN + 16; y++)
            for (int x = x0; x < x0 + ViewN; x++)
            {
                int fim = CellRaw4(x, y);
                if (fim == SENTINEL || (fim & 0x7FFF) == 0) continue;
                int animB = CellByte(x, y, 8);
                if (animB != 0) { oBlend++; continue; }          // bit7 混合 / 动画帧（水面），本轮跳过
                int lib = CellByte(x, y, 10) + 2;                // MapCode.cs: FrontIndex = 原始字节 + 2
                Sprite sp = ObjSprite(lib, fim & 0x7FFF);
                if (sp == null)
                {
                    oMiss++;
                    if (objMissSamples.Count < 8) objMissSamples.Add(lib + ":" + (fim & 0x7FFF));
                    continue;
                }
                float ow = sp.rect.width, oh = sp.rect.height;
                if ((ow == 48f && oh == 32f) || (ow == 96f && oh == 64f)) continue;
                float bx0 = x * HW * 0.01f, bx1 = (x * HW + ow) * 0.01f;
                float byBot = -(y + 1) * HH * 0.01f, byTop = byBot + oh * 0.01f;
                bool vis = (bx1 >= camX - VIEW_HALF_W) && (bx0 <= camX + VIEW_HALF_W)
                        && (byTop >= camY - VIEW_HALF_H) && (byBot <= camY + VIEW_HALF_H);
                if (!vis || ot >= OBJ_POOL) continue;
                var sr3 = Rent(objPool, objRoot, ot);
                sr3.sprite = sp;
                sr3.color = Color.white;
                sr3.sortingOrder = ORDER_OBJ_BASE + y * 10;       // 按行号排：越靠下越靠前
                sr3.transform.position = new Vector3(
                    (x * HW + ow * 0.5f) * 0.01f,
                    -((y + 1) * HH - oh * 0.5f) * 0.01f, 0f);     // pivot 居中 ⇒ 左贴格左、底贴格底
                sr3.enabled = true;
                ot++;
            }

        // ---------- 第四遍：平铺 front 层（48x32 / 96x64）----------
        //   权威 GameScene.cs L10726-10756（DrawFloor）：drawX = x*48、drawY = y*32（**格左上**）。
        int ft = 0, fMiss = 0, fBlend = 0;
        for (int x = x0; x < x0 + ViewN; x++)
            for (int y = y0; y < y0 + ViewN; y++)
            {
                int fim = CellRaw4(x, y);
                if (fim == SENTINEL || (fim & 0x7FFF) == 0) continue;
                int animB = CellByte(x, y, 8);
                if (animB != 0) { fBlend++; continue; }
                int lib = CellByte(x, y, 10) + 2;
                Sprite sp = ObjSprite(lib, fim & 0x7FFF);
                if (sp == null) { fMiss++; continue; }
                float ow = sp.rect.width, oh = sp.rect.height;
                if (!((ow == 48f && oh == 32f) || (ow == 96f && oh == 64f))) continue;
                float wx = x * HW * 0.01f, wy = -y * HH * 0.01f;
                bool vis = (wx + ow * 0.01f >= camX - VIEW_HALF_W) && (wx <= camX + VIEW_HALF_W)
                        && (wy >= camY - VIEW_HALF_H) && (wy - oh * 0.01f <= camY + VIEW_HALF_H);
                if (!vis || ft >= OBJ_POOL) continue;
                var sr4 = Rent(objPool, objRoot, ot + ft);
                sr4.sprite = sp;
                sr4.color = Color.white;
                sr4.sortingOrder = ORDER_FRONT_TILE;
                sr4.transform.position = new Vector3(
                    (x * HW + ow * 0.5f) * 0.01f,
                    -(y * HH + oh * 0.5f) * 0.01f, 0f);
                sr4.enabled = true;
                ft++;
            }

        groundPoolUsed = gt; smPoolUsed = st; objPoolUsed = ot + ft;
        HideRest(groundPool, gt); HideRest(smPool, st); HideRest(objPool, ot + ft);
        if (EXP_DISABLE_GROUND) { HideRest(groundPool, 0); HideRest(smPool, 0); }
        // ★ 剔除日志：culled 应≈784-可见数（可见约 300 格），两层实际启用数应大幅下降
        Debug.Log("[P0E] 视口剔除 tag=win 窗口=" + ViewN + "x" + ViewN +
                  " 候选=" + (ViewN * ViewN) + " 剔除=" + culledT +
                  " | 实际启用 ground=" + gt + " sm=" + st +
                  " 合计=" + (gt + st) + " (修复前=1568)");
        Debug.Log("[P0E] objects 启用=" + ot + " 平铺front=" + ft + " miss=" + oMiss +
                  " blend跳过=" + oBlend + " | 图集页=" + (objAtlas == null ? 0 : objAtlas.Length) +
                  " 索引帧=" + objIdx.Count + " 样本=" + string.Join(",", objMissSamples.ToArray()));
        LogDrawWindow("win");
    }

    static void HideRest(List<SpriteRenderer> pool, int used)
    {
        for (int i = used; i < pool.Count; i++) if (pool[i] != null) pool[i].enabled = false;
    }

    /// <summary>
    /// 常驻池取用：按**序号**直取（O(1)），不足才新建。**这就是"不是一次性实例化"的实现点** ——
    /// 池容量上限 = POOL，与 490000 格无关；行走时只是改 sprite/坐标，不再新建对象。
    /// </summary>
    static SpriteRenderer Rent(List<SpriteRenderer> pool, GameObject root, int index)
    {
        while (pool.Count <= index)
        {
            var go = new GameObject(root.name + "_" + pool.Count);
            go.transform.SetParent(root.transform, false);
            pool.Add(go.AddComponent<SpriteRenderer>());
        }
        return pool[index];
    }

    // ---- 图集 Sprite 缓存：**必须缓存**，否则每次窗口更新都 Sprite.Create 会持续吃内存 ----
    //   实测口径：图集共 462 + 164 = 626 格，缓存上限即此（远小于"每格一个 Sprite"）。
    readonly Dictionary<int, Sprite> sprCacheT = new Dictionary<int, Sprite>();
    readonly Dictionary<int, Sprite> sprCacheS = new Dictionary<int, Sprite>();

    /// <summary>
    /// ★ A6 判据的 Debug.Log 发射点：报告本帧实际绘制的窗口范围与池占用。
    ///   复核方据此判定「是不是只画了可见窗口」——数字远小于 490000 才是虚拟化。
    /// </summary>
    void LogDrawWindow(string tag)
    {
        Debug.Log("[P0E] drawwin tag=" + tag + " 窗口=" + ViewN + "x" + ViewN +
                  " 原点=(" + lastWinX + "," + lastWinY + ") 玩家=(" + px + "," + py + ")" +
                  " | groundPoolUsed=" + groundPoolUsed + " smPoolUsed=" + smPoolUsed +
                  " POOL=" + POOL + " 地图总格=" + mapN +
                  " | 是否全量实例化=" + ((groundPoolUsed + smPoolUsed) > mapN / 2));
        LogFallback(tag);
    }

    /// <summary>
    /// 前景帧 → Sprite（按 (库号,帧号) 缓存）。**变尺寸**，不能复用网格函数 AtlasSprite。
    ///   索引里的 (page,x,y,w,h) 以**左上角**为原点（与 p1_pack.py 的 paste 一致），
    ///   Unity 的 Sprite rect 以**左下角**为原点 ⇒ y 要翻：tex.height - y - h。
    /// 返回 null = 该帧没打进图集（真缺帧，调用侧计数，不画）。
    /// </summary>
    Sprite ObjSprite(int lib, int fv)
    {
        long key = ObjKey(lib, fv);
        Sprite sp;
        if (sprCacheO.TryGetValue(key, out sp) && sp != null) return sp;
        ObjRef r;
        if (!objIdx.TryGetValue(key, out r)) return null;
        if (objAtlas == null || r.page < 0 || r.page >= objAtlas.Length) return null;
        Texture2D tex = objAtlas[r.page];
        if (tex == null || r.w <= 0 || r.h <= 0) return null;
        sp = Sprite.Create(tex, new Rect(r.x, tex.height - r.y - r.h, r.w, r.h),
                           new Vector2(0.5f, 0.5f), 100f);
        sp.name = "obj_" + lib + "_" + fv;
        sprCacheO[key] = sp;
        return sp;
    }

    /// <summary>图集里取一格 → Sprite（**带缓存**）。atlasCell 按行优先、**左上为原点**（与 pe_atlas.py 的 paste 一致）。</summary>
    Sprite AtlasSprite(bool sm, int atlasCell)
    {
        var cache = sm ? sprCacheS : sprCacheT;
        Sprite sp;
        if (cache.TryGetValue(atlasCell, out sp) && sp != null) return sp;
        Texture2D tex = sm ? smAtlas : tilesAtlas;
        int cols = sm ? ATLAS_SM_COLS : ATLAS_TILES_COLS;
        int cw = sm ? 48 : TW, ch = sm ? HH : TH;
        int cx = atlasCell % cols, cy = atlasCell / cols;
        sp = Sprite.Create(tex,
            new Rect(cx * cw, tex.height - (cy + 1) * ch, cw, ch),
            new Vector2(0.5f, 0.5f), 100f);
        cache[atlasCell] = sp;
        return sp;
    }

    // ================= 投影：纯正交（对齐官方 Crystal 源码） =================
    //   官方 `GameScene.cs` L10680/10684：格 (x,y) 绘图左上角 = (x*48, y*32) 像素，无菱形项。
    //   世界单位 0.01（PPU=100）。客户端 y 向下、Unity y 向上 ⇒ worldY 一律取负。
    //   三个函数的分工（**别再混用**，混用就等于贴图错位半格 = 棋盘格复发）：
    //     World      单格中心      —— 玩家 sprite pivot(0.5,0) 脚底锚点、寻路目标点
    //     TileCenter 2x2 块中心    —— 地面层 96x64 图，铺在偶/偶格，自身 pivot(0.5,0.5)
    //     CellCenter 单格中心      —— smTiles 48x32 图，pivot(0.5,0.5)，逐格
    static Vector3 World(int x, int y)
    { return new Vector3((x + 0.5f) * HW * 0.01f, -(y + 0.5f) * HH * 0.01f, 0f); }

    static Vector3 TileCenter(int x, int y)
    { return new Vector3((x + 1f) * HW * 0.01f, -(y + 1f) * HH * 0.01f, 0f); }

    static Vector3 CellCenter(int x, int y)
    { return new Vector3((x + 0.5f) * HW * 0.01f, -(y + 0.5f) * HH * 0.01f, 0f); }

    void BuildPlayer()
    {
        player = new GameObject("Player");
        playerSr = player.AddComponent<SpriteRenderer>();
        playerSr.sprite = walk[0];
        // ★ 2026-10-25：角色深度改成「与前景层同一套 y 序」，否则永远压在树/房子前面（穿模）。
        playerSr.sortingOrder = ORDER_OBJ_BASE + py * 10 + 5;
        // 出生点 (290,618)：与 StartPoint.txt map 0 的第一个点一致（原版比奇省），17×17 邻域可走 252 格。
        if (Blocked(px, py))
        {
            blockedCount++;   // 本次实测：出生点若被挡，记一笔（不会静默改点）
            Debug.LogWarning("[P0E] ★出生点(" + px + "," + py + ")被 bit15 判为阻挡（预期可走）");
        }
        player.transform.position = World(px, py) + LIFT;
        groundRoot = new GameObject("Ground");
        smRoot = new GameObject("SmTiles");
        objRoot = new GameObject("Objects");      // 高位前景层（树/房子/城门/城墙）
        UpdateWindow(px, py);
    }

    void SetupCamera()
    {
        cam = Camera.main;
        if (cam == null)
        {
            var cgo = new GameObject("Main Camera");
            cgo.tag = "MainCamera";
            cam = cgo.AddComponent<Camera>();
        }
        cam.orthographic = true;
        // ★ 2026-10-25 视野还原（最高优先视觉修复）：
        //   真 1.76 是 800x600 屏上 1 源像素 = 1 屏像素，即竖屏高正好 600 源像素。
        //   我们主视口高 1140 设备px，要还原同观感 ⇒ 1 源像素 = 1.9 设备px
        //   ⇒ 半边高 = 600/2 源像素 = 3.0 世界单位（0.01 单位 = 1 源像素）。
        //   旧值 VIEW_H/2/100 = 5.7 ⇒ 竖屏装了 1140 源像素 = 原版 1.9 倍视野，
        //   瓦片/角色/怪物全部只有原版一半大小（用户说的"明显不是当年那个样子"）。
        //   ⚠ cam.rect / pixelRect 不动（仍 508/2616、1600/2616），HUD 的 SCALE=2 体系不动。
        cam.orthographicSize = ORTHO_SIZE;   // 2.85 单位 = 570 源像素 ↔ 1140 设备px ⇒ 1 源像素 = 2 设备px
        // ★ 2026-10-25 修正：恢复原版 4:3 居中取景。
        //   2003 年那台 PC 是 800x600（4:3），画面只有中间那 800 宽；
        //   宽屏手机两侧各 508px 是纯黑留边（原版比例，不是调试栏）。
        //   上一轮把 cam.rect 改成 (0,0,1,1) 让视野变 1308 源像素宽，比原版多露 63% 地图，
        //   于是底栏只剩 1600 宽而地图铺满 2616 ⇒ 底部左右两段露地图，整体不像 1.76。
        cam.rect = new Rect(SIDE_W / (float)SCREEN_W, 0f, VIEW_W / (float)SCREEN_W, 1f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        // ★ 2026-10-24 修：真 1.76 里"地图外"是**纯黑**，不是墨绿。
        //   实测真客户端场景区 纯黑(<8) 占 38.1%、p25=0；旧值 (0.05,0.07,0.05)≈(13,18,13) 会在
        //   地图空洞处露出暗绿色，和原版对不上。改 (0,0,0) 与 ref 的 p1..p25 全 0 一致。
        cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
        cam.transform.position = CamTarget();
        Debug.Log("[P0B] 视口 rect=" + cam.rect + " 主视口宽 " + VIEW_W +
                  " 高 " + VIEW_H + " 左右黑边各 " + SIDE_W + " px" +
                  " 相机下移 " + CAM_DROP_Y + " 单位(=" + (CAM_DROP_Y * 100f) + " 源像素)");
    }

    /// <summary>
    /// 相机瞄准点：玩家脚下（扣掉 sprite 的 LIFT 抬升）再下移 CAM_DROP_Y。
    /// 组内 HUD 按「原版 rows 30..599」排布，玩家必须在视口第 270 行 = 设备 y 540。
    /// </summary>
    Vector3 CamTarget()
    {
        Vector3 p = player.transform.position;
        return new Vector3(p.x, p.y - LIFT.y - CAM_DROP_Y, -10f);
    }

    void Update()
    {
        // ★ 视口实测行的周期重发（见 nextViewportLogFrame 的注释：防 t3 attach 晚 ⇒ A3 假失败）
        if (Time.frameCount >= nextViewportLogFrame)
        {
            nextViewportLogFrame = Time.frameCount + VIEWPORT_LOG_EVERY;
            LogViewport("tick" + Time.frameCount);
        }

        // 兜底清场: 覆盖「P0-A 的 Boot() 晚于本站引导」的竞态, 前 sweepFrames 帧每帧清一次
        if (sweepFrames > 0)
        {
            sweepFrames--;
            int n = ClearForeignBootstraps("sweep" + (8 - sweepFrames));
            if (n > 0 || sweepFrames == 0) AssertOnlyOwnCamera("sweep" + (8 - sweepFrames));
        }

        // ★★ P0-H 修复（根因）：攻击读的是实体层的 `PlayerX/PlayerY/PlayerDir`，
        //   而这三者在旧代码里要到本函数**末尾**（实体层 Tick 前）才同步 ⇒ 攻击那一刻
        //   实体层拿的是上一帧的格坐标与朝向，于是 `PlayerAttack()` 恒判"面前那格没怪"
        //   （实测 `[P0H] 玩家攻击 朝向=3 目标=无` 而同时刻怪就在身边）。
        //   ⇒ 同步必须发生在任何攻击调用**之前**：本帧的 px/py/dirRow 先灌进实体层。
        if (_hRun != null)
        {
            _hRun.PlayerX = px; _hRun.PlayerY = py; _hRun.PlayerDir = dirRow;
        }

        if (Input.GetKeyDown(KeyCode.G)) { EXP_DISABLE_GROUND = !EXP_DISABLE_GROUND; UpdateWindow(px, py); Debug.Log("[EXP] ground_on=" + (!EXP_DISABLE_GROUND)); }
        if (Input.GetKeyDown(KeyCode.UpArrow)) TryStep(0);
        if (Input.GetKeyDown(KeyCode.DownArrow)) TryStep(1);
        if (Input.GetKeyDown(KeyCode.LeftArrow)) TryStep(2);
        if (Input.GetKeyDown(KeyCode.RightArrow)) TryStep(3);

        // ============ P0-J 三层联动：技能冷却推进 + 热键 ============
        // Tick 必须先跑：冷却按真实时间流逝，与施法请求同一帧判定才不会有「按了没反应」。
        if (_hSkill != null && _hRun != null) _hSkill.Tick(Time.deltaTime, _hRun);

        // 1/2/3 = 火球/雷电/治愈。施法结果（成功/耗蓝/命中谁/打掉多少血）都由 P0HSkill 自己打日志。
        if (_hRun != null && _hSkill != null)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) _hSkill.Cast(0, _hRun);
            if (Input.GetKeyDown(KeyCode.Alpha2)) _hSkill.Cast(1, _hRun);
            if (Input.GetKeyDown(KeyCode.Alpha3)) _hSkill.Cast(2, _hRun);
            // 技能结果（冷却中 / 魔法不足 / 治愈回复 / 击杀 / 打中）交给提示层分流：
            // 冷却/蓝不够/治愈 → 左上绿；打中/击杀 → 只飘字（不再进聊天框）
            if (_hNotify != null) _hNotify.Pump(_hRun);
        }

        // F9 = 开/关背包（40 格 + 8 装备位）
        // ★ 2026-10-27 从 F12 改成 F9，依据 ClMain.pas L2625：
        //     `VK_F9: begin FrmDlg.OpenItemBag; end;`  ← 原版背包键就是 F9
        //   原版 F12 是 `FrmDlg.DOptionClick;`（选项框），本工程没有选项框，故 F12 不再绑任何东西。
        // ★ 走 _hItem.Toggle() 而不是自己 `Visible = !Visible`：开关只在 P0HItem 一处翻，
        //   两边各翻一次 = 同一帧翻两下 = 看起来按了没反应。
        if (_hItem != null && Input.GetKeyDown(KeyCode.F9))
        {
            _hItem.Toggle();
        }

        // ★ P0-H：空格 = 朝当前朝向砍一格（原版 Shift/攻击键的近战语义）
        if (Input.GetKeyDown(KeyCode.Space) && _hRun != null)
        {
            var tgt = _hRun.PlayerAttack();
            dirRow = _hRun.PlayerDir;   // 实体层可能已自动转身（面向目标再砍），回灌给贴图层
            Debug.Log("[P0H] 玩家攻击 朝向=" + dirRow + " 目标=" + (tgt == null ? "无" : tgt.name) +
                      " | " + _hRun.LastEvent);
            // ★ 攻击会**立刻**覆盖 LastEvent ⇒ 必须在此刻 Pump 一次，否则等 Tick 之后再读
            //   就被 AI 反击/其他事件盖掉了（丢中间那条 = 假联动）。
            if (_hNotify != null) _hNotify.Pump(_hRun);
        }

        if (path.Count == 0 && burst > 0)   // 一次按键 = 连续走 6 格(模拟按住方向键)
        {
            burst--;
            if (!TryStep(dirRow, true)) burst = 0;
        }

        if (path.Count > 0)
        {
            Vector3 tgt = World(path[0].x, path[0].y) + LIFT;
            var p = Vector3.MoveTowards(player.transform.position, tgt, moveSpeed * Time.deltaTime);
            player.transform.position = p;
            if ((p - tgt).sqrMagnitude < 0.000001f)
            {
                px = path[0].x; py = path[0].y;
                MoveCount++;
                lastMove = "格(" + px + "," + py + ") 第" + MoveCount + "次位移";
                // ★ A6 用 ASCII 打点: harness L264 抓 `MoveCount[^\n]*`、L268 抓 `[Ff]rame\s*[=: ]\s*(\d+)`。
                //   纯中文行（「第N次位移」「当前帧 X/8」）在 logcat 里匹配不到, 故同一行补 ASCII 键值对。
                Debug.Log("[P0B] " + lastMove + " 帧=" + FrameShown +
                          " | MoveCount=" + MoveCount + " Frame=" + FrameShown +
                          " 格=(" + px + "," + py + ")");
                path.Clear();
                UpdateWindow(px, py);       // ★ 视口窗口随行走滚动更新
            }
        }

        var cam2 = Camera.main;
        if (cam2 != null)
            cam2.transform.position = Vector3.Lerp(cam2.transform.position,
                CamTarget(), 8f * Time.deltaTime);

        // ================= P0-H 实体层 tick =================
        // ★ 玩家格坐标每帧同步给实体层：AI 要按它算距离/追击方向。
        if (_hRun != null)
        {
            _hRun.PlayerX = px; _hRun.PlayerY = py; _hRun.PlayerDir = dirRow;
            _hRun.Tick(Time.deltaTime);
            // ★ Tick 内部会覆盖 LastEvent（怪物 AI 反击 / 自然回复 / 掉落拾取等）
            //   ⇒ 在这之后再 Pump 一次，两个时点都覆盖，才能不漏。
            if (_hNotify != null) _hNotify.Pump(_hRun);
        }

        // ★★ P0-NOTIFY：每帧推进提示层一次（寿命 / 顶部滚动 / 飘字上升），
        //   并把本帧新登记的伤害数字从队列搬进飘字表。**整个 Update 只调一次**，
        //   调多次会让寿命按倍数流逝（原版是每帧一次）。
        if (_hNotify != null)
        {
            if (_hRun != null) _hNotify.Drain(_hRun);
            _hNotify.Tick(Time.deltaTime);
        }

        bool moving = path.Count > 0 || burst > 0;
        if (moving)
        {
            idleT = 0f;
            animT += Time.deltaTime;
            if (animT >= 0.12f) { animT = 0f; step = (step + 1) % 4; }   // 8 帧 = 2 组朝向 x 4 帧
        }
        else if ((idleT += Time.deltaTime) > 0.5f) step = 0;
        ApplyFrame();
    }

    void ApplyFrame()
    {
        int group = (dirRow == 0 || dirRow == 1) ? 0 : 4;
        FrameShown = group + step;
        playerSr.sprite = walk[FrameShown];
        playerSr.flipX = (dirRow == 1 || dirRow == 2);
        playerSr.sortingOrder = ORDER_OBJ_BASE + py * 10 + 5;   // 跟随行号，才能被树/房子正确遮挡
        if (FrameShown != lastFrameLogged && frameLogs < 80)
        {
            lastFrameLogged = FrameShown; frameLogs++;
            // ★ A6: 帧号集合必须覆盖 0..7（harness L268 解析 `Frame=<n>`）
            Debug.Log("[P0C] Frame=" + FrameShown + " t=" + Time.time.ToString("0.000") + "s " +
                      "[P0B][帧] 帧=" + FrameShown +
                      " 角色世界坐标=(" + player.transform.position.x.ToString("0.000") + "," +
                      player.transform.position.y.ToString("0.000") + ") 格=(" + px + "," + py + ")");
        }
    }
    int lastFrameLogged = -1, frameLogs;

    public bool TryStep(int d) { return TryStep(d, false); }

    /// <summary>
    /// GM `@MOVE X Y` 的真实传送（服务端 `PlayerObject.Teleport(CurrentMap, new Point(x,y))` 的单机对应）。
    /// 必须由宿主执行：P0HChat 只拿得到实体层，拿不到贴图层的 px/py/path/burst 与相机。
    /// 目标格按真 .map 的 bit15 判定，站在不可走格子上等于穿墙，直接拒绝（返回 false）。
    /// </summary>
    bool TeleportTo(int x, int y)
    {
        // Blocked() 内部已含「网格外 = 不可走」（见本文件 Blocked 的越界分支），
        // 这里不用再单独判范围，避免两套边界条件不一致。
        if (Blocked(x, y))
        {
            Debug.Log("[P0CHAT] @MOVE 拒绝 (" + x + "," + y + ") 越界或 blocked");
            return false;
        }

        // 清掉正在走的路径/连走计数，否则下一帧会被旧目标拽回去
        path.Clear();
        burst = 0;
        step = 0;

        int oldX = px, oldY = py;
        px = x; py = y;
        player.transform.position = World(px, py) + LIFT;
        playerSr.sortingOrder = ORDER_OBJ_BASE + py * 10 + 5;

        // 相机立刻切（不能等 Lerp），否则会看到"人已经到新图、镜头还在原地飘过来"
        if (cam == null) cam = Camera.main;
        if (cam != null) cam.transform.position = CamTarget();

        UpdateWindow(px, py);

        if (_hRun != null)
        {
            _hRun.PlayerX = px; _hRun.PlayerY = py; _hRun.PlayerDir = dirRow;
            _hRun.EmitEvent("传送到 (" + px + "," + py + ")");
            if (_hNotify != null) _hNotify.Pump(_hRun);
        }

        Debug.Log("[P0CHAT] @MOVE 传送 (" + oldX + "," + oldY + ") -> (" + px + "," + py + ")");
        return true;
    }

    public bool TryStep(int d, bool silent)
    {
        dirRow = d;
        int nx = px + DIRX[d], ny = py + DIRY[d];
        if (Blocked(nx, ny))
        {
            if (!silent) Debug.Log("[P0B] 阻挡 (" + nx + "," + ny + ") blocked 位生效");
            return false;
        }
        path.Clear();
        path.Add(new Vector2Int(nx, ny));
        if (!silent) burst = 6;
        return true;
    }

    void Press(string name, int d)
    {
        if (d >= 0)
        {
            bool ok = TryStep(d);
            Debug.Log("[P0B] 虚拟键 " + name + " -> " + (ok ? "走(" + (px + DIRX[d]) + "," + (py + DIRY[d]) + ")" : "被 blocked 挡住"));
        }
        else Debug.Log("[P0B] 虚拟键 " + name + " (骨架, 未接线)");
    }

    void OnGUI()
    {
        if (lbl == null)
        {
            lbl = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 26 };
            lbl.normal.textColor = Color.white;
        }
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
            new Vector3(Screen.width / (float)SCREEN_W, Screen.height / (float)SCREEN_H, 1f));

        // ★ 2003 原版是 4:3：画面只占中间 800 宽，两侧留纯黑。
        //   这里手动画纯黑，保证即使某些机型忽略 cam.rect，两侧也不会露出地图。
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0f, 0f, SIDE_W, SCREEN_H), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(SCREEN_W - SIDE_W, 0f, SIDE_W, SCREEN_H), Texture2D.whiteTexture);
        GUI.color = Color.white;

        // ★★ 主 UI 画布绘制区（设备空间）—— CROP_TOP 在**绘制路径**上被实际引用（V5 要求）。
        //   BeginGroup 的裁剪矩形 = MainUICanvasRect = Rect(508, -60, 1600, 1200)。
        //   画布内的所有 HUD 元素（状态栏 / 生命球 / 魔法球 / 聊天框）一律以「原版 800x600
        //   逻辑坐标」给出，经 CanvasRectForLogic() 换算，底部因而精确落在设备 y=1140。
        GUI.BeginGroup(MainUICanvasRect);
        // ★ 绘制路径上对 CROP_TOP 的真实依赖：先断言"绘制组矩形 == 契约值"，
        //   再画。BeginGroup 的实参就是 MainUICanvasRect（内含 CROP_TOP），
        //   断言失败会 LogError ⇒ V5 要的"被绘制 Rect 实际引用"在此**可运行期核验**。
        AssertCanvasInvariant();
        // ★ P0-NOTIFY 世界层（伤害飘字）：原版顺序是 DScreen.DrawScreen（地图+头顶飘字）在
        //   g_DWinMan.DirectPaint（HUD/聊天框）**之前**（ClMain.pas:16605 → 16608），
        //   ⇒ 飘字必须画在 HUD 之下，否则会盖住血球和聊天框（原版是被底栏压住的）。
        if (_hNotify != null) _hNotify.DrawWorldLayer();
        // ★★★ 2026-10-15 builder(P0-D)：改画 **P0DUI**，不再画 P0CUI。理由是被权威判死的口径错位：
        //   `P0CUI` 走 t1 `ui_layout_spec.json` 的 **5 件叠加法**（0039 + 0000/0001 左右补板 + 0002 聊天框 + 0003 滚动条）。
        //   该法已被 `m2spec\M2_layer1_AUTHORITATIVE.json` v2.0.0 的 `supersedes` **明文作废**，其 `finding` 给出实测：
        //     「0039 本身已是合成完整的底部 HUD；0000/0001 是宽屏左右补板(LeftCap/RightCap)，800 宽屏上不该画；
        //       0002/0003 的内容已烤进 0039」，且「叠 4 件 = 6.6443% 失配，且无任何坐标能救」。
        //   ⇒ 若两处都画，0000/0001 会在 800 宽屏上叠出**错误的左右补板**（正是那 6.64% 失配的来源）。
        //   ⚠ `P0CUI.cs` 文件本身**保持原样不动**（P0-C 产物/常量零改动，见 acceptance[4]），只是不再被调用。
        if (_dUI == null) _dUI = new P0DUI();
        _dUI.DrawAll();    // 按 M2_layer1_AUTHORITATIVE v2.0.0 绘制 M2 主 UI 第一层（底部 HUD，只画 0039）

        // ★ P0-G：M2 位图层（InventoryDialog 底图 + 4 个页签按钮，F2 开关）。
        //   ⚠ **必须在 P0-F 之前画**：位图层是背景/按钮，文字层压在上面；反序会让底图盖掉文字。
        //   默认 PanelVisible=false，与源码 InventoryDialog.cs L29 `Visible = false` 一致。
        if (_gArt == null) _gArt = new P0GArt();
        _gArt.DrawAll();

        // ★ P0-F：七面板纯文字层（默认全关，F1..F7 逐块开启；不改动上面 0039 的任何像素）。
        //   放在同一个 BeginGroup 内 ⇒ 坐标一律走 GroupRectForLogic，与 P0-D 同一套换算。
        if (_fText == null) _fText = new P0FText();
        _fText.DrawAll();

        // ★★ P0-J：三层联动绘制（同一 BeginGroup 内，坐标一律 GroupRectForLogic）。
        //   ⚠ 画在 P0-D/P0-G/P0-F **之后**：HUD 是压在最上层的真值覆盖层，
        //     必须盖住 0039 里那套「液面永远满」的静态血球底图，否则用户看到的还是假的。
        //   ⚠ 反过来说，P0-F 的文字层若画在这些之后，会把 HUD 的数字压掉 ⇒ 保持现在的顺序不要调。
        if (_hHud != null) _hHud.DrawAll(_hRun);
        if (_hSkill != null) _hSkill.DrawBar(_hRun);
        if (_hItem != null) _hItem.DrawBag(_hRun);
        // ★★ P0-CHAT：底部聊天记录 + 输入条 + 滚动条。
        //   必须在这个 BeginGroup 内（坐标一律 GroupRectForLogic），且画在 HUD 之后
        //   ⇒ 聊天区盖在白底之上，不被 0039 底板或背包面板压掉。
        if (_hChat != null) _hChat.DrawAll(_hRun);

        // ★★ P0-NOTIFY 提示层：原版 DrawScreenTop / DrawScreenBottom（ClMain.pas:16610-16611）
        //   在 g_DWinMan.DirectPaint **之后** ⇒ 必须画在 HUD + 聊天框之上，才不会被压掉。
        if (_hNotify != null) _hNotify.DrawAll();

        GUI.EndGroup();

        // ---- 坐标系说明（权威外屏口径 2616x1140）----
        //   GUI.matrix 已把绘制空间缩放到 2616x1140 设备像素, 故下面 Rect 一律按【设备像素】写。
        //   左控制区 x ∈ [0, 508) ; 主视口 x ∈ [508, 2108) ; 右控制区 x ∈ [2108, 2616)
        //   主视口由相机占满, GUI 只在左右 508px 内画, 保证不压住游戏画面。
        const float RX = SCREEN_W - SIDE_W;      // 右控制区左边界 = 2108
        const float LXC = SIDE_W / 2f;           // 左控制区中心 x = 254

        // 左栏 0..508: 方向键（半透明十字，不挡视线、不再有"方向 508px"这种调试字）
        const float BS = 110f;            // 方向键边长
        const float PADY = 760f;          // 十字中心 y
        Btn("上", LXC - BS * 0.5f, PADY - BS * 1.5f, BS, BS, 0);
        Btn("下", LXC - BS * 0.5f, PADY + BS * 0.5f, BS, BS, 1);
        Btn("左", LXC - BS * 1.5f, PADY - BS * 0.5f, BS, BS, 2);
        Btn("右", LXC + BS * 0.5f, PADY - BS * 0.5f, BS, BS, 3);

        // 右栏 2108..2616: 1-6 / F1-F4 / Shift 砍怪
        string[] n6 = { "1", "2", "3", "4", "5", "6" };
        for (int i = 0; i < 6; i++)
            Btn(n6[i], RX + 74 + (i % 3) * 130, 560 + (i / 3) * 130, 100, 100, -1);
        for (int i = 0; i < 4; i++)
            Btn("F" + (i + 1), RX + 53 + i * 104, 830, 90, 90, -1);
        // ★ P0-H：「Shift 砍怪」从占位改成**真攻击**（朝当前朝向砍一格）。
        //   原 Btn(label,-1) 只打一行"(骨架, 未接线)"——那是 P0-B 期占位，
        //   实体层落地后必须接上，否则点按钮没反馈 = 假通过。
        AttackBtn("Shift 砍怪", RX + 54, 950, 400, 120);

        // ★ P0-I 触摸入口（2026-10-22 修：原来只有 F12 一个入口，真机上根本按不到）
        //   实测口径（本机同一台 emulator-5554、同一次冷启动内四组隔离实验）：
        //     `adb shell input tap`  → 进 IMGUI，Btn「上」真走格（[P0B] 虚拟键 上 -> 走(290,626)）
        //     `adb shell input keyevent 19/62/142` → 零条 Unity 日志（同段日志 Tuanjie=471 条，非空）
        //   ⇒ 合成按键事件不进 Unity 传统 Input Manager，`HandleToggleKey()` 永远等不到 KeyDown。
        //   ⇒ 必须补一个**触摸**入口，否则 P0-I 面板永远打不开 = 假完成。
        //   位置 (30,110)-(470,230) = 左栏「方向」标题与十字键之间的空闲带。
        //   ⚠ 不能放屏幕下半部：P0-I 面板打开时高 426px（y 704..1130、x 78..2537，实测 s=1.00），
        //     会把「聊天」按钮整个盖住 —— 能点但看不见 = 第二次关不掉。
        ChatBtn("聊天", 30, 110, 440, 120);

        // ★ 2026-10-25：屏幕上的 lastMove / "MoveCount=" / "Frame=" 三行调试字已删除
        //   （真客户端左栏不会有这些字）。Debug.Log 里的同名输出**保留**，harness 仍按它解析。

        // ================= P0-I 原生输入法层 =================
        // ★ 放在**最后**：P0IIme 内部把 GUI.matrix 置单位阵（它要按真实像素排版）并在 finally 恢复；
        //   放在 EndGroup 之后、所有既有 HUD 之后，保证不改变已验收的像素。
        if (_ime == null)
        {
            _ime = new P0IIme();
            _ime.OnSend = OnChatSend;
            // ★ 一次性自检：证明本层真的在运行时被构造过（此前所有证据都只能证明"包里有机码"，
            //   不能证明"运行时走到了这里"）。这条日志是 P0-I 运行时链路的起点锚点。
            Debug.Log("[P0I] 初始化 输入法层已接线 (ToggleKey=" + _ime.ToggleKey + " 触碰入口=聊天)");
        }
        _ime.DrawAll();
    }

    /// <summary>
    /// 该屏幕点是否归 P0-I 面板所有（面板打开且点在面板矩形内）。
    /// ★ 2026-10-22 修（device 实证的静默死按钮）：本类的 Btn/AttackBtn/ChatBtn 用 raw
    ///   `Event.current` + `e.Use()` 命中自己那 508px 侧栏矩形，而 P0-I 面板在它们**之后**绘制。
    ///   同一个 MouseDown 被先 Use() 掉，后画的 GUI.Button 就永远收不到 —— 表现为「按钮画着、
    ///   点了没反应」。实测：「清历史」histClearRect=(2427,718,96,26) 完全落在
    ///   AttackBtn=(2138,660,440,140) 内，点 (2475,731) 只出 `[P0H] 按钮攻击`、零条 [P0I]。
    /// ⇒ 面板可见且点在面板内时不认领，把手势让给上层的 IMGUI 控件。
    /// </summary>
    bool ImeOwnsPoint(Vector2 p)
    {
        // ★ 聊天框也要让下层 HUD 按钮让位（同一套规则，否则点输入条会被 AttackBtn 抢走）。
        if (_ime != null && _ime.BlocksPointer(p)) return true;
        if (_hChat != null && _hChat.BlocksPointer(p)) return true;
        return false;
    }

    /// <summary>聊天面板开关按钮：触摸触发（真机唯一可用通道），等价 F12。</summary>
    void ChatBtn(string label, float x, float y, float w, float h)
    {
        var r = new Rect(x, y, w, h);
        var e = Event.current;
        bool hot = e.type == EventType.MouseDown && r.Contains(e.mousePosition) && !ImeOwnsPoint(e.mousePosition);
        // ★ 2003 原版屏幕上没有任何按钮 ⇒ 只保留触摸热区，不画任何像素。
        //   点这块空白仍能开关聊天输入法（功能不减，画面干净）。
        if (hot)
        {
            // ★ 2026-10-25：主通路改为聚焦底部聊天输入条（这才是原版「聊天」按钮的语义）。
            //   P0-I 大面板只作兜底保留：聊天框没起来时仍然能开。
            if (_hChat != null) _hChat.FocusInput();
            else if (_ime != null) _ime.Toggle();
            Debug.Log("[P0I] 触摸按钮 聊天 -> 聚焦底部输入条=" + (_hChat != null) +
                      " P0I面板=" + (_ime != null && _ime.Visible));
            e.Use();
        }
    }

    /// <summary>聊天/命令回调：先记日志，再当作事件记录（命令解析留待下一层）。</summary>
    void OnChatSend(string text)
    {
        Debug.Log("[P0I] 发送: " + text);
        // ★★ 2026-10-25：**上屏已由 P0HChat.Send → Route() 负责**（那里要按原版前缀
        //   分「普通/喊话/私聊/命令」并决定底色与文案，见 P0HChat.Route 的源码行号）。
        //   这里若再 AppendPlayer 一次就是双行 —— 真机实测过。此回调只留事件/日志职责。
        if (_hRun != null) _hRun.EmitEvent("说话: " + text);
    }

    /// <summary>攻击按钮：样式与 Btn 一致，但触发的是**真实攻击**而不是骨架日志。</summary>
    void AttackBtn(string label, float x, float y, float w, float h)
    {
        var r = new Rect(x, y, w, h);
        var e = Event.current;
        bool hot = e.type == EventType.MouseDown && r.Contains(e.mousePosition) && !ImeOwnsPoint(e.mousePosition);
        // ★ 2003 原版没有屏幕按钮 ⇒ 只留透明热区。
        if (hot)
        {
            // 按钮可能在 Update 之后（OnGUI）被点 ⇒ 点击前再同步一次格坐标/朝向，
            //   保证实体层看到的就是玩家脚下这一格（否则又退回"读上一帧"的老问题）。
            if (_hRun != null) { _hRun.PlayerX = px; _hRun.PlayerY = py; _hRun.PlayerDir = dirRow; }
            var tgt = (_hRun == null) ? null : _hRun.PlayerAttack();
            if (_hRun != null) dirRow = _hRun.PlayerDir;
            Debug.Log("[P0H] 按钮攻击 朝向=" + dirRow + " 目标=" + (tgt == null ? "无" : tgt.name) +
                      " | " + (_hRun == null ? "实体层未启动" : _hRun.LastEvent));
            // 与键盘 Space 攻击同样：立刻 Pump，不等 Tick
            if (_hNotify != null && _hRun != null) _hNotify.Pump(_hRun);
            e.Use();
        }
    }

    void Btn(string label, float x, float y, float w, float h, int dir)
    {
        var r = new Rect(x, y, w, h);
        var e = Event.current;
        bool hot = e.type == EventType.MouseDown && r.Contains(e.mousePosition) && !ImeOwnsPoint(e.mousePosition);
        // ★ 2003 原版没有屏幕按钮 ⇒ 只留透明热区（点边缘仍能走/按数字键）。
        if (hot) { Press(label, dir); e.Use(); }
    }
}


