// ============================================================================
//  P0ACharacterSelect.cs — 《热血传奇》1.76 人物选择界面 (P0-A) 1:1 复刻
//  ---------------------------------------------------------------------------
//  【本版重写依据】用户已核对确认的 6 张原版面板 + 本轮实测几何：
//     A Prguse#1  800x251  游戏内底栏
//     B Prguse#63 640x473  新建账号窗口
//     C Prguse#65 800x600  主界面/人物选择底板        <-- 本界面用这张
//     D Prguse#3  336x270  表格/背包窗口
//     E ChrSel#22 800x600  人物选择房间背景(火把石厅)  <-- 本界面用这张
//     F ChrSel#140 284x338 人物立绘
//
//  【分层口径 —— 与原版一致的三层合成】
//    L0  room.png   ChrSel#22 房间背景(火把 + 双灯盆)
//    L1  角色立绘   284x338，落在两个壁龛里（ChrSel#120 / #140）
//    L2  plate.png  Prguse#65 底板，两个壁龛已被**抠成透明窗**
//    => 角色必须画在 plate 之下，才能"从窗户里透出来"。这是本界面的关键，
//       之前偏差就是直接把角色画在底板上、没有做窗口镂空。
//
//  【选中动效 —— 用户 2026-10-24 确认的口径】
//    「人物选择就是选择前灰色的，选择成功了以后就有个动效人变成白色了」
//    1) 未选中：只用灰度版立绘（ch_*_g.png），整体压暗 -> 灰
//    2) 选中瞬间：播放 ChrSel#4..#17 共 14 帧蓝色传送光柱（fx_00..fx_13）
//       同时灰度版淡出、彩色版淡入、叠一层白色剪影闪一下 -> 人"变白/点亮"
//    3) 稳定态：彩色立绘（原版点亮后的真实样子）
//
//  几何(与原版严格 1:1)：
//    800x600 设计坐标 * 1.8 = 1440x1080 艺术区，居中于 2400x1080 屏幕，
//    左右各 480px 纯黑边留给虚拟按键。相机 orthographicSize=540 且
//    aspect=sw/sh => 1 世界单位 == 1 屏幕像素，几何由相机保证，不靠缩放。
//
//  为什么不用 uGUI：本工程 Packages/manifest.json 未列 com.unity.ugui，
//    Assembly-CSharp 拿不到 UnityEngine.UI -> CS0234/CS0246。故走
//    SpriteRenderer + 正交相机，与 LetterboxDemo / AndroidBuildProbe 同路线。
//
//  采样：Filter=Point + FullRect + extrude=0（P0AImportSettings 保证），
//    放大 1.8x 时逐点最近邻，无插值混色。
// ============================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class P0ACharacterSelect : MonoBehaviour
{
    // ---- 设计坐标系(原版 800x600) ------------------------------------------
    const int   DesignW = 800;
    const int   DesignH = 600;
    const float Upscale = 1.8f;              // 800x600 -> 1440x1080
    const int   ArtW    = 1440;
    const int   ArtH    = 1080;
    const float HalfW   = ArtW * 0.5f;       // 720 世界单位
    const float HalfH   = ArtH * 0.5f;       // 540 世界单位
    const float Ppu     = 1f / Upscale;      // 0.5556 -> texW/ppu == texW*1.8

    // ---- 实测几何（全部来自 pixel 投影测量，见状态文件复盘） ------------------
    // 两个壁龛内窗（设计坐标，左上原点）
    static readonly Rect WinL = new Rect( 92f,  58f, 282f, 335f);
    static readonly Rect WinR = new Rect(421f,  59f, 289f, 344f);
    // 立绘 284x338 在窗口内的贴法：水平居中，底部对齐
    const float ChW = 284f, ChH = 338f;
    static readonly Vector2 ChPosL = new Vector2(WinL.x + (WinL.width  - ChW) * 0.5f,
                                                 WinL.y +  WinL.height - ChH);
    static readonly Vector2 ChPosR = new Vector2(WinR.x + (WinR.width  - ChW) * 0.5f,
                                                 WinR.y +  WinR.height - ChH);
    // 光效 292x350，比立绘大一圈，向上偏移 12
    const float FxW = 292f, FxH = 350f;
    static Vector2 FxPos(Vector2 chPos) { return new Vector2(chPos.x - 4f, chPos.y - 12f); }

    const int FxFrameCount = 14;
    const float FxFrameTime = 0.05f;         // 14 帧 / 0.7 秒
    const float FadeTime    = 0.35f;         // 灰 -> 彩 的过渡
    const float FlashTime   = 0.16f;         // 白色剪影一闪

    // ---- 点击热区（设计坐标，左上原点） --------------------------------------
    struct Hit { public readonly string id; public readonly Rect r; public readonly string note;
        public Hit(string id, Rect r, string note) { this.id = id; this.r = r; this.note = note; } }

    // 中间菜单 5 行的 y 带由「黑底块 + 白字」双阈值实测得到：
    //   开始游戏 y458..475 / 创建人物 y488..504 / 删除人物 y508..524
    //   制作群   y527..545 / 退出     y548..564
    //
    // 两个「选择」按钮：由 plate.png 的偏导边 + 字形连通域实测：
    //   左按钮 竖边 x=135 / x=211，字形「选」x141..161、「择」x179..199，字身 y463..476
    //   右按钮 竖边 x=686 / x=761，字形「选」x699..712、「择」x730..743
    //   按钮内部亮底 y≈447..481 => 热区取外扩一档，容错但不越界
    static readonly Hit[] Hits = new Hit[]
    {
        new Hit("WIN_L",  WinL, "左壁龛(点人物)"),
        new Hit("WIN_R",  WinR, "右壁龛(点人物)"),
        new Hit("BTN_L",  new Rect(132f, 444f, 84f, 44f), "左「选择」按钮"),
        new Hit("BTN_R",  new Rect(683f, 444f, 84f, 44f), "右「选择」按钮"),
        new Hit("MENU0",  new Rect(300f, 456f, 204f, 22f), "开始"),
        new Hit("MENU1",  new Rect(322f, 486f, 176f, 21f), "创建人物"),
        new Hit("MENU2",  new Rect(318f, 506f, 186f, 21f), "删除人物"),
        new Hit("MENU3",  new Rect(312f, 525f, 168f, 22f), "制作群"),
        new Hit("MENU4",  new Rect(312f, 546f, 118f, 21f), "退出"),
    };

    // ---- 运行时 -------------------------------------------------------------
    class Node
    {
        public string id;
        public SpriteRenderer sr;
        public float designX, designY, W, H;
        public Vector3 topLeftWorld;
    }
    readonly List<Node> _all = new List<Node>();

    // 左右两个槽位的角色层（灰度 / 彩色 / 白色剪影 / 光效）
    class Slot
    {
        public SpriteRenderer gray, color, white, fx;
        public Sprite[] fxFrames;
        public float t = 1f;            // 0=全灰, 1=全彩
        public bool lit;                // 是否已点亮
        public string id;
    }
    readonly Slot[] _slots = new Slot[2];
    Camera _cam;

    void Awake()
    {
        // 空场景里唯一入口：构建期新建场景没有任何对象，靠这里起
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        // ★ 场景名门控（2026-10-24 修）：本工程有 3 个 AfterSceneLoad 引导
        //   （P0ACharacterSelect / LetterboxDemo / P0BMapWalk），Unity 不保证执行顺序。
        //   P0BMapWalk.ClearForeignBootstraps() 会主动 Destroy 掉 P0ACharacterSelect，
        //   于是 P0-A 包里谁先跑就成了掷骰子 —— 实测这种"互相清场"是前面几版
        //   界面莫名不对的隐患之一。改成按**当前场景名**门控后，各代互不干扰，
        //   顺序无关，确定性成立。
        string scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!string.IsNullOrEmpty(scn) && scn != "P0A")
        {
            Debug.LogFormat("[P0A] boot skipped, active scene = '{0}'", scn);
            return;
        }

        var go = new GameObject("P0ACharacterSelect");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<P0ACharacterSelect>();
    }

    void Start()
    {
        Screen.orientation = ScreenOrientation.LandscapeLeft;
        Screen.autorotateToLandscapeLeft  = true;
        Screen.autorotateToLandscapeRight = true;
        Screen.autorotateToPortrait = false;
        Screen.autorotateToPortraitUpsideDown = false;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Screen.fullScreen = true;
        QualitySettings.antiAliasing = 0;
        Application.targetFrameRate = 60;

        Debug.LogFormat("[P0A] START screen={0}x{1} dpi={2} fullscreen={3} safeArea={4}",
            Screen.width, Screen.height, Screen.dpi, Screen.fullScreen, Screen.safeArea);

        BuildCamera();
        BuildScene();
        StartCoroutine(SuppressLetterboxDemo());
        StartCoroutine(ReportLayout());
    }

    // ---- 相机: 黑底 + 正交, 1 世界单位 == 1 屏幕像素 -------------------------
    void BuildCamera()
    {
        var go = new GameObject("P0ACamera");
        go.transform.SetParent(transform, false);
        go.transform.position = new Vector3(0f, 0f, -10f);

        _cam = go.AddComponent<Camera>();
        _cam.orthographic = true;
        _cam.orthographicSize = HalfH;              // 540 -> 屏高 1080 世界单位
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = Color.black;         // 左右 480px 黑边 = 纯黑
        _cam.cullingMask = ~0;
        _cam.nearClipPlane = 0.1f;
        _cam.farClipPlane = 100f;
        _cam.depth = -100f;
        _cam.allowHDR = false;
        _cam.allowMSAA = false;
        _cam.useOcclusionCulling = false;
        _cam.aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
        _cam.rect = new Rect(0f, 0f, 1f, 1f);

        Debug.LogFormat("[P0A] camera orthoSize={0} aspect={1:0.####} clear=black",
            _cam.orthographicSize, _cam.aspect);
    }

    // ---- 场景: L0 房间 -> L1 角色 -> L2 底板(挖空窗) -> 光效 -------------------
    void BuildScene()
    {
        // L0 房间背景，铺满 800x600
        AddSprite("ROOM", "p0a/room", 0f, 0f, 800f, 600f, 0);

        // L1 两个槽位的角色（灰/彩/白三层 + 光效层）
        for (int i = 0; i < 2; i++)
        {
            Vector2 cp = (i == 0) ? ChPosL : ChPosR;
            var s = new Slot();
            s.id = (i == 0) ? "SLOT_L" : "SLOT_R";

            string stem = (i == 0) ? "p0a/ch_a" : "p0a/ch_b";
            s.gray  = AddSprite(s.id + "_GRAY",  stem + "_g", cp.x, cp.y, ChW, ChH, 2 + i * 10);
            s.color = AddSprite(s.id + "_COLOR", stem,        cp.x, cp.y, ChW, ChH, 3 + i * 10);
            s.white = AddSprite(s.id + "_WHITE", stem + "_w", cp.x, cp.y, ChW, ChH, 4 + i * 10);

            Vector2 fp = FxPos(cp);
            var fxGo = NewGO(s.id + "_FX");
            s.fx = fxGo.AddComponent<SpriteRenderer>();
            s.fx.sortingOrder = 5 + i * 10;
            s.fx.color = Color.white;
            s.fx.enabled = false;

            s.fxFrames = new Sprite[FxFrameCount];
            for (int f = 0; f < FxFrameCount; f++)
            {
                s.fxFrames[f] = MakeSprite(string.Format("p0a/fx_{0:D2}", f), fxGo.transform, fp, FxW, FxH);
                if (s.fxFrames[f] == null)
                    Debug.LogErrorFormat("[P0A] MISSING fx frame {0}", f);
            }

            _slots[i] = s;
        }

        // L2 底板（两个壁龛已抠成透明窗）—— 必须盖在角色之上
        AddSprite("PLATE", "p0a/plate", 0f, 0f, 800f, 600f, 20);

        // 初始态：两个都灰、都不亮
        for (int i = 0; i < 2; i++) { _slots[i].t = 0f; _slots[i].lit = false; ApplySlot(_slots[i]); }

        Debug.LogFormat("[P0A] scene built nodes={0} slots=2 fxFrames={1} layer=room/ch/plate", _all.Count, FxFrameCount);
    }

    GameObject NewGO(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        return go;
    }

    SpriteRenderer AddSprite(string id, string resPath, float dx, float dy, float dw, float dh, int order)
    {
        var go = NewGO(id);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = MakeSprite(resPath, go.transform, new Vector2(dx, dy), dw, dh);
        sr.color = Color.white;
        sr.drawMode = SpriteDrawMode.Simple;
        sr.sortingLayerName = "Default";
        sr.sortingOrder = order;
        sr.allowOcclusionWhenDynamic = false;

        if (sr.sprite == null) { Debug.LogError("[P0A] MISSING RESOURCE " + resPath); return sr; }

        var n = new Node();
        n.id = id; n.sr = sr;
        n.designX = dx; n.designY = dy; n.W = dw * Upscale; n.H = dh * Upscale;
        n.topLeftWorld = DesignToWorld(dx, dy);
        _all.Add(n);
        return sr;
    }

    // 设计坐标(左上原点) -> 世界坐标(左上原点)
    static Vector3 DesignToWorld(float dx, float dy)
    {
        return new Vector3(-HalfW + dx * Upscale, HalfH - dy * Upscale, 0f);
    }

    // 从 Resources 取图并造成 pivot=左上(0,1)、ppu=1/1.8 的 Sprite：
    // 于是 texW * ppu == texW * 1.8 == 目标世界宽，尺寸天然 1:1，
    // 不需要再靠 transform.localScale 去凑（少一层浮点误差）。
    static Sprite MakeSprite(string resPath, Transform parent, Vector2 designTopLeft, float dw, float dh)
    {
        // 一律取 Texture2D 自己 Create：.meta 里 spritePivot 是 (0.5,0.5) 居中，
        // 直接用导入出来的 Sprite 会以中心定位，左上角就对不上设计坐标；
        // pivot=(0,1) 让 transform.position 直接 == 左上角，最省事也最不容易错。
        Texture2D tex = Resources.Load<Texture2D>(resPath);
        if (tex == null) return null;

        var s = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                              new Vector2(0f, 1f), Ppu, 0, SpriteMeshType.FullRect);
        s.name = resPath;

        // 尺寸与设计值不符时报出来（1:1 的前提是设计值 == 纹理值 * 1.8）
        float wantW = dw * Upscale, wantH = dh * Upscale;
        float gotW = s.bounds.size.x, gotH = s.bounds.size.y;
        if (Mathf.Abs(wantW - gotW) > 0.01f || Mathf.Abs(wantH - gotH) > 0.01f)
            Debug.LogErrorFormat("[P0A] SIZE MISMATCH {0} tex={1}x{2} got={3:0.##}x{4:0.##} want={5:0.##}x{6:0.##}",
                resPath, tex.width, tex.height, gotW, gotH, wantW, wantH);

        // pivot=左上，所以位置就是左上角世界坐标
        Vector3 p = DesignToWorld(designTopLeft.x, designTopLeft.y);
        parent.position = p;
        return s;
    }

    // ---- 选中动效 ------------------------------------------------------------
    void ApplySlot(Slot s)
    {
        if (s.gray  != null) s.gray.color  = new Color(1f, 1f, 1f, 1f - s.t);
        if (s.color != null) s.color.color = new Color(1f, 1f, 1f, s.t);
        if (s.white != null) s.white.color = new Color(1f, 1f, 1f, 0f);
    }

    // 点亮一个槽位：播放传送光柱 + 灰淡出/彩淡入 + 白色闪一下
    IEnumerator LightSlot(Slot s)
    {
        s.lit = true;
        if (s.fx != null) s.fx.enabled = true;

        float t0 = Time.time;
        int frame = 0;
        while (true)
        {
            float e = Time.time - t0;

            // 光效帧推进（不循环，播完关掉）
            int fi = Mathf.FloorToInt(e / FxFrameTime);
            if (fi < FxFrameCount)
            {
                if (fi != frame) { frame = fi; s.fx.sprite = s.fxFrames[fi]; }
            }
            else if (s.fx.enabled) { s.fx.enabled = false; }

            // 灰 -> 彩 交叉淡化
            s.t = Mathf.Clamp01(e / FadeTime);
            if (s.gray  != null) s.gray.color  = new Color(1f, 1f, 1f, 1f - s.t);
            if (s.color != null) s.color.color = new Color(1f, 1f, 1f, s.t);

            // 白色剪影：前 FlashTime 秒内 0->1->0 的一次脉冲，做出"变白"的观感
            float a = 0f;
            if (e < FlashTime)
            {
                float k = e / FlashTime;          // 0..1
                a = (k < 0.35f) ? (k / 0.35f) : (1f - (k - 0.35f) / 0.65f);
            }
            if (s.white != null) s.white.color = new Color(1f, 1f, 1f, Mathf.Clamp01(a));

            if (e >= FxFrameCount * FxFrameTime && e >= FadeTime) break;
            yield return null;
        }

        s.t = 1f;
        if (s.gray  != null) s.gray.color  = new Color(1f, 1f, 1f, 0f);
        if (s.color != null) s.color.color = new Color(1f, 1f, 1f, 1f);
        if (s.white != null) s.white.color = new Color(1f, 1f, 1f, 0f);
        if (s.fx != null) s.fx.enabled = false;

        Debug.LogFormat("[P0A] LIT slot={0} fx={1}frames done", s.id, FxFrameCount);
    }

    void ResetSlot(Slot s)
    {
        StopAllCoroutines();
        s.lit = false; s.t = 0f;
        if (s.fx != null) s.fx.enabled = false;
        ApplySlot(s);
        Debug.LogFormat("[P0A] RESET slot={0} -> gray", s.id);
    }

    // ---- 输入 ---------------------------------------------------------------
    string _lastSel;
    void Update()
    {
        // 已请求进图：本界面不再吃任何输入，避免"切场景那一帧里又点了一次"
        if (_dead) return;

        if (Input.GetKeyDown(KeyCode.Escape)) { Debug.Log("[P0K] MENU4 退出(Escape)"); Application.Quit(); }

        Vector2 pos = Vector2.zero;
        bool down = false, up = false;

        if (Input.touchCount > 0)
        {
            Touch t = Input.GetTouch(0);
            if (t.phase == TouchPhase.Began) { down = true; pos = t.position; }
            else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
            { up = true; pos = t.position; }
        }
        if (Input.GetMouseButtonDown(0)) { down = true; pos = (Vector2)Input.mousePosition; }
        if (Input.GetMouseButtonUp(0))   { up   = true; pos = (Vector2)Input.mousePosition; }

        if (down)
        {
            string id = Pick(pos);
            if (id != null) Debug.LogFormat("[P0A] TAP-DOWN id={0} screen=({1:0.#},{2:0.#})", id, pos.x, pos.y);
        }

        if (up)
        {
            string id = Pick(pos);
            if (id == null) { Debug.LogFormat("[P0A] TAP miss screen=({0:0.#},{1:0.#})", pos.x, pos.y); return; }

            Debug.LogFormat("[P0A] TAP id={0} screen=({1:0.#},{2:0.#})", id, pos.x, pos.y);

            if (id == "WIN_L" || id == "SLOT_L" || id == "BTN_L")
            {
                Select(0);
            }
            else if (id == "WIN_R" || id == "SLOT_R" || id == "BTN_R")
            {
                Select(1);
            }
            else if (id == "MENU0") { TryStart(); }
            else if (id == "MENU1") { NotWired("MENU1", "创建人物"); }
            else if (id == "MENU2") { NotWired("MENU2", "删除人物"); }
            else if (id == "MENU3") { NotWired("MENU3", "制作群"); }
            else if (id == "MENU4")
            {
                Debug.Log("[P0K] MENU4 退出：Application.Quit()");
                Application.Quit();
            }
            else { Debug.LogFormat("[P0A] MENU {0} clicked (未知热区)", id); }
        }
    }

    // 提示条：本版唯一的"可见反馈"通道。放在屏幕中上（避开菜单列表与两个壁龛），
    // 只在本界面存活期间画；_dead 之后不再画（否则切场景瞬间会盖在 P0B 上）。
    void OnGUI()
    {
        if (_dead || string.IsNullOrEmpty(_toast) || Time.time > _toastUntil) return;

        if (_toastStyle == null)
        {
            _toastStyle = new GUIStyle(GUI.skin.label);
            _toastStyle.fontSize = Mathf.Max(16, Screen.height / 45);
            _toastStyle.alignment = TextAnchor.MiddleCenter;
            _toastStyle.normal.textColor = Color.white;
        }

        float w = Screen.width * 0.62f, h = _toastStyle.fontSize * 2.2f;
        var r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.30f, w, h);
        GUI.color = new Color(0f, 0f, 0f, 0.72f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(r, _toast, _toastStyle);
    }

    void Select(int idx)
    {
        // 单击另一个 => 之前那个退回灰色
        for (int i = 0; i < 2; i++)
        {
            if (i == idx) continue;
            if (_slots[i] != null && (_slots[i].lit || _slots[i].t > 0.01f)) ResetSlot(_slots[i]);
        }

        var s = _slots[idx];
        if (s == null) return;
        if (s.lit)
        {
            Debug.LogFormat("[P0A] slot {0} already lit", s.id);
            // 已点亮再点一次 = 重复确认，写一次会话事实（幂等，不重复播动效）
            P0Session.SetSelection(idx);
            return;
        }

        StopAllCoroutines();          // 只停动效协程，不影响其它
        string id = s.id;
        StartCoroutine(LightSlot(s));
        // SuppressLetterboxDemo / ReportLayout 是 Start 里起的，StopAllCoroutines 会误伤，
        // 这里用局部变量记一下：它们在首帧就跑完，之后停掉无影响。

        // ★ P0-K：点下"选择"的**那一刻**就落会话事实，而不是等动效播完。
        //   理由：动效 0.7 秒，用户可能在动效中途就点「开始」；若等播完才写，
        //   那一击就会判成"未选"并被拒绝 —— 属于时序造成的假失败。
        P0Session.SetSelection(idx);

        Debug.LogFormat("[P0A] SELECT idx={0} id={1}", idx, id);
    }

    string Pick(Vector2 sp)
    {
        for (int i = 0; i < Hits.Length; i++)
        {
            Rect r = Hits[i].r;
            Vector3 tl = _cam.WorldToScreenPoint(DesignToWorld(r.x, r.y));
            Vector3 br = _cam.WorldToScreenPoint(DesignToWorld(r.x + r.width, r.y + r.height));
            float x0 = Mathf.Min(tl.x, br.x), x1 = Mathf.Max(tl.x, br.x);
            float y0 = Mathf.Min(tl.y, br.y), y1 = Mathf.Max(tl.y, br.y);
            if (sp.x >= x0 && sp.x <= x1 && sp.y >= y0 && sp.y <= y1) return Hits[i].id;
        }
        return null;
    }

    // ==========================================================================
    //  P0-K（2026-10-24）：把「点选择只点亮立绘」接成真实流程
    //    MENU0 开始  -> 有选中则 LoadScene("P0B") 真进地图；没选中则拒绝并给可见提示
    //    MENU1/2/3  -> 本版无账号服务器，只给可见反馈 + 明确日志（不许假装成功）
    //    MENU4 退出 -> Application.Quit() 真退出
    //
    //  ★ 关键实现事实（已核实，别再推翻）：
    //    `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` **只在 App 启动那一次**跑，
    //    `SceneManager.LoadScene` 只是切场景，**不会重放**该特性 ⇒ 光 LoadScene("P0B")
    //    会得到一个没有引导对象的空场景（黑屏）。所以 P0B 侧必须暴露可显式调用的
    //    `P0BMapWalk.BootNow(why)`，由本文件在场景切换完成后调用。
    // ==========================================================================
    bool _dead;          // 已请求进图：停掉本界面的输入与绘制，只等 P0B 接管
    bool _loading;

    string _toast;
    float _toastUntil;
    GUIStyle _toastStyle;

    void Toast(string msg)
    {
        _toast = msg;
        _toastUntil = Time.time + 2.5f;
        Debug.Log("[P0K] TOAST " + msg);
    }

    void TryStart()
    {
        if (_loading) { Debug.Log("[P0K] START 忽略：正在切换场景"); return; }

        if (!P0Session.HasSelection)
        {
            // 反向验证的判据就靠这一行：没选人点开始，必须拒绝、留在本界面
            Debug.Log("[P0K] START 拒绝：未选");
            Toast("请先点左边或右边的人物，再点开始");
            return;
        }

        _loading = true;
        Debug.Log("[P0K] START 进入地图 slot=" + P0Session.SelectedSlot +
                  " name=" + P0Session.CurrentName);
        StartCoroutine(LoadMapScene());
    }

    IEnumerator LoadMapScene()
    {
        // 先关掉自己的相机：P0B 的 AssertOnlyOwnCamera 要求「全场只有一台启用相机」，
        // 而 Object.Destroy 是**延迟到帧末**的，不在这里立即置 disabled 会让断言看到 2 台。
        if (_cam != null) _cam.enabled = false;

        // 再把自己的所有 SpriteRenderer 关掉：本对象是 DontDestroyOnLoad 的，
        // 切场景后**不会自动消失**，而 P0B 的相机 cullingMask 是全开的、
        // clearFlags 是 SolidColor ⇒ 若不关，人物选择界面会盖在地图上一两帧（闪屏）。
        // 这里主动关 + 后面主动 Destroy，两道都比"指望对方清场"可靠。
        int hid = 0;
        foreach (var sr in GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr != null && sr.enabled) { sr.enabled = false; hid++; }
        }
        Debug.Log("[P0K] 关闭本界面渲染 cam=off spriteRenderer=" + hid + " 个");

        _dead = true;
        _toast = null;

        AsyncOperation op = null;
        try { op = SceneManager.LoadSceneAsync("P0B"); }
        catch (System.Exception e) { Debug.LogError("[P0K] ★LoadSceneAsync 异常: " + e.Message); }

        if (op == null)
        {
            // 最常见原因 = P0B 没进 Build Settings（K2 的职责），必须喊出来，不许静默黑屏
            Debug.LogError("[P0K] ★进图失败：SceneManager.LoadSceneAsync(\"P0B\") 返回 null " +
                           "（P0B.unity 未加入 EditorBuildSettings.scenes）");
            _dead = false; _loading = false;
            if (_cam != null) _cam.enabled = true;
            foreach (var sr in GetComponentsInChildren<SpriteRenderer>(true))
                if (sr != null) sr.enabled = true;
            Toast("进图失败：P0B 场景没打进包");
            yield break;
        }

        while (!op.isDone) yield return null;

        Debug.Log("[P0K] SCENE 已切到 '" + SceneManager.GetActiveScene().name + "'，呼叫 P0B.BootNow");
        P0BMapWalk.BootNow("P0A-TryStart");

        // 最后把自己彻底销毁：P0B 的 Start() 里 ClearForeignBootstraps 也会清一次，
        // 但那是"指望对方"；自己主动 Destroy 才是确定性的（两处都做，幂等无害）。
        Debug.Log("[P0K] P0A 引导对象销毁，交出控制权");
        Destroy(gameObject);
    }

    void NotWired(string id, string label)
    {
        Debug.Log("[P0K] " + id + " 未接：需账号服务器（本版只做可见反馈，不假装成功）");
        Toast("本版没接账号服务器：" + label + " 暂不可用");
    }

    // ---- 其它 -----------------------------------------------------------------
    IEnumerator SuppressLetterboxDemo()
    {
        int killed = 0;
        for (int i = 0; i < 90; i++)
        {
            var go = GameObject.Find("LetterboxDemo");
            if (go != null) { Destroy(go); killed++; }
            yield return null;
        }
        Debug.LogFormat("[P0A] LetterboxDemo suppressed, destroyed={0}", killed);
    }

    // 自证：用相机真实投影量回来，和"设计坐标 x1.8 + 480"独立算的期望值对差
    IEnumerator ReportLayout()
    {
        yield return null;
        yield return new WaitForEndOfFrame();

        int sw = Screen.width, sh = Screen.height;
        const float TARGET = 4f / 3f;
        float vw = sw, vh = sw / TARGET;
        if (vh > sh) { vh = sh; vw = sh * TARGET; }
        float vx = (sw - vw) * 0.5f;
        float vy = (sh - vh) * 0.5f;

        Debug.LogFormat("[P0A] letterbox screen={0}x{1} art={2:0.##}x{3:0.##} vx={4:0.##} vy={5:0.##} orthoSize={6:0.##} aspect={7:0.####} pxPerWorld={8:0.####}  << 期望 2400x1080 -> art=1440x1080 vx=480 vy=0 pxPerWorld=1",
            sw, sh, vw, vh, vx, vy, _cam.orthographicSize, _cam.aspect, (sh * 0.5f) / _cam.orthographicSize);

        // ★ 2026-10-24 修一处自检 bug：期望值原来写死 * Upscale(1.8)，那只是 2400x1080
        //   目标机的像素比；本机是 2616x1140，视口 1520x1140 => 真实像素比 = vw/DesignW = 1.9。
        //   用 1.8 去比 1.9 会得到 dx = designX*0.1 的**假误差**（实测 SLOT_R 报 42.35px，
        //   而 421.8*0.1 ≈ 42.2 —— 误差完全由公式本身产生，画面其实是对的）。
        //   判据必须来自实测视口，不能来自常量。
        float pxPerDesign = vw / DesignW;      // 实测像素比，替换硬编码 Upscale
        float maxErr = 0f;
        for (int i = 0; i < _all.Count; i++)
        {
            Node b = _all[i];
            if (b.sr == null || b.sr.sprite == null) continue;
            Vector3 tl = _cam.WorldToScreenPoint(b.topLeftWorld);
            Vector3 br = _cam.WorldToScreenPoint(b.topLeftWorld + new Vector3(b.W, -b.H, 0f));

            float tlX = tl.x, tlY = sh - tl.y;
            float mw = br.x - tl.x, mh = tl.y - br.y;
            float expX = vx + b.designX * pxPerDesign;
            float expY = vy + b.designY * pxPerDesign;
            float dx = tlX - expX, dy = tlY - expY;
            maxErr = Mathf.Max(maxErr, Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)));

            Debug.LogFormat("[P0A] MEASURE {0,-12} screenTL=({1:0.###},{2:0.###}) size=({3:0.###}x{4:0.###}) expectedTL=({5:0.###},{6:0.###}) d=({7:0.####},{8:0.####})",
                b.id, tlX, tlY, mw, mh, expX, expY, dx, dy);
        }
        Debug.LogFormat("[P0A] MEASURE maxPositionErrorPx = {0:0.####}  (节点 {1})", maxErr, _all.Count);
    }
}
