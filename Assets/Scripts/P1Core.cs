// P1-Core —— 真源码内核 + 真瓦片渲染。第一个「能看能动」的画面。
//
// 这一版的定位：
//   P0BMapWalk 是「照着原版描」——地图解析、投影、绘制全是自己手写的。
//   本脚本是「用真源码」——地图解析、格子数据、A* 寻路来自 Crystal 的 4.66 万行原码
//   （编译成 MirClientCore.dll），瓦片素材直接来自原版 .Lib 图库，
//   投影口径逐字照抄 GameScene.cs:10657-10760 的 DrawFloor。
//
// 屏幕上出现的每一个像素都来自 0.map + Tiles/SmTiles/Objects 图库：
//   · 地面（Back 层）= 图库 0 的 96x64 叠瓦
//   · 过渡（Middle 层）= 图库 1
//   · 草、石头、树（Front 层）= 图库 2/5/7
//
// 验收标准（画面上肉眼可见）：
//   · 看到的是真正的传奇地图画面，不是伪彩色方块
//   · 角色沿真 A* 路径自动行走，镜头跟着走
//   · 顶部显示坐标 / 帧率 / 步数
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Client.MirCore;
using Client.MirScenes;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class P1Core : MonoBehaviour
{
    // 逻辑分辨率：按设备屏幕比例算，保持和 960x540 同等的像素量。
    // ★ 不能写死 960x540 —— 手机是 2616x1140（2.295:1），写死就会被横向拉长，
    //   实机截图跟离线基准一比就看出来了（整幅画面比例不对）。
    static int SW = 960;
    static int SH = 540;

    MapControl _map;
    P1Map _renderer;
    int _mapW, _mapH;

    bool _ready;
    string _status = "初始化中…";
    long _genMs, _parseMs, _pathMs;

    // ── 行走 ──
    Vector2Int _cell = new Vector2Int(200, 200);
    Vector2Int _target = new Vector2Int(600, 600);
    List<Vector2Int> _path = new List<Vector2Int>();
    int _pathIdx;
    float _stepT;                 // 0..1 当前这一格走了多少
    const float StepTime = 0.30f; // 每格 0.30 秒（原版走路是 600ms/2 格 ≈ 0.30s/格）
    bool _autoWalk = true;    // 自动沿路径行走（验收阶段站定截图时改 false）

    // 启动后先站定一段时间再走：给验收截图留一个「镜头停在已知坐标」的窗口。
    // 基准图是 (200,200) 的 DrawFloor+DrawObjects 复算结果，只有站定在这里才能逐像素比对。
    const float BootHold = 20f;
    float _bootHoldUntil;

    // ── 角色 ──
    int _armour = 0;              // CArmour 序号，0 = 默认布衣（Hum00.Lib）
    int _dir = 4;                 // 朝向 0..7（0=Up 顺时针），起手朝下
    int _walkFrame;               // 走路帧序号 0..5
    float _walkAnimT;             // 动画累计时间，用于算帧号
    const float WalkFrameTime = 0.10f;   // Frame(32, 6, 0, 100) → 每帧 100ms

    // ── 画面 ──
    Texture2D _tex;
    float _fps;
    int _frames;
    float _fpsT;
    int _drawMs;
    float _lastRenderT;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        string scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!string.IsNullOrEmpty(scn) && scn != "P1")
        {
            UnityEngine.Debug.Log("[P1CORE] boot skipped, active scene = '" + scn + "'");
            return;
        }
        var go = new GameObject("P1Core");
        go.AddComponent<P1Core>();
        UnityEngine.Object.DontDestroyOnLoad(go);
        UnityEngine.Debug.Log("[P1CORE] 引导完成");
    }

    void Start()
    {
        try { Boot(); }
        catch (Exception e)
        {
            _status = "失败: " + e.Message;
            Debug.LogError("[P1CORE] " + e);
        }
    }

    void Boot()
    {
        var sw = Stopwatch.StartNew();

        // 0) ★ 必须先做：把进程当前目录切到手机上可写的位置。
        //    真源码全是相对路径（".\Data\"、".\Mir2Config.ini"），
        //    Android 上进程 cwd 是只读的 "/"，任何写操作都会抛异常，
        //    一抛就把 Client.Settings 的静态构造炸掉（真机已实测）。
        //    这一步必须在碰 Client.Settings 之前，所以 Bridge 的任何调用都得排在它后面。
        bool initOk = PortableInit.Init(Application.persistentDataPath);
        Debug.Log("[P1CORE] PortableInit=" + initOk + " root=" + PortableInit.RootPath);

        // 1) 逻辑分辨率：跟设备同比例，像素量对齐 960x540，避免画面被拉伸。
        //    MapControl 构造函数按 ScreenWidth/Height 算 OffSetX/OffSetY/ViewRangeX/ViewRangeY，
        //    所以这两个值必须在这里先定，再看 OffSet 对不对。
        ComputeLogicalSize();
        Bridge.SetScreenSize(SW, SH);
        Debug.Log("[P1CORE] 逻辑分辨率 " + SW + "x" + SH + "  设备 " + Screen.width + "x" + Screen.height);

        // 2) 真源码地图对象
        _map = Bridge.CreateMap();

        // 3) 真地图字节 → 真 MapReader
        var mapAsset = Resources.Load<TextAsset>("0.map");   // 磁盘名 0.map.bytes
        if (mapAsset == null) { _status = "找不到 Resources/0.map"; return; }

        string tempDir = Application.temporaryCachePath;
        bool ok = Bridge.LoadMapFromBytes(_map, mapAsset.bytes, tempDir);
        Bridge.GetSize(_map, out _mapW, out _mapH);
        _parseMs = sw.ElapsedMilliseconds;
        if (!ok || _mapW <= 0) { _status = "地图解析失败 (" + _mapW + "x" + _mapH + ")"; return; }

        // 4) 图库（原版 .Lib 原样剪切出来的迷你库，按需解压）
        sw.Restart();
        _renderer = new P1Map { ScreenW = SW, ScreenH = SH };
        string err = _renderer.Load("p1_atlas2");
        _genMs = sw.ElapsedMilliseconds;
        if (err != null) { _status = "图库加载失败: " + err; return; }

        // 5) 起点必须可走；不可走就在附近找一格
        int reqX = _cell.x, reqY = _cell.y;
        if (!Bridge.CanWalk(_map, _cell.x, _cell.y)) FindWalkableNear(ref _cell);
        Debug.Log("[P1CORE] 起点 请求(" + reqX + "," + reqY + ") 实际(" + _cell.x + "," + _cell.y + ")");

        // 6) 真 A* 寻路
        sw.Restart();
        RecalcPath();
        _pathMs = sw.ElapsedMilliseconds;

        _tex = _renderer.Texture;
        _ready = true;
        _bootHoldUntil = Time.realtimeSinceStartup + BootHold;

        _status = string.Format("真源码内核 {0}x{1} · 图库 {2} 张 {3}MB · 路径 {4} 步 · 解析 {5}/{6}ms",
            _mapW, _mapH, _renderer.TileCount, _renderer.CachedMB, _path.Count, _parseMs, _pathMs);
        Debug.Log("[P1CORE] " + _status);

        // 7) 抽样打印几格真数据，证明不是空的
        var sb = new StringBuilder("[P1CORE] 真 M2CellInfo 抽样:\n");
        int shown = 0;
        for (int x = 200; x < 700 && shown < 8; x += 60)
        {
            Bridge.GetCell(_map, x, 200, out short bi, out int bimg, out short mi, out int mimg, out short fi, out int fimg);
            sb.AppendLine(string.Format("  ({0},200) Back={1}/{2} Middle={3}/{4} Front={5}/{6}", x, bi, bimg, mi, mimg, fi, fimg));
            shown++;
        }
        Debug.Log(sb.ToString());

        // 8) 先画一帧，起手不是黑屏
        RenderNow();
        Debug.Log("[P1CORE] 首帧合成 " + _drawMs + "ms  视口 " +
                  _renderer.ViewRangeX + "x" + _renderer.ViewRangeY +
                  "  OffSet " + _renderer.OffsetX + "/" + _renderer.OffsetY);
    }

    /// <summary>
    /// 逻辑分辨率按设备比例算。目标像素量 ≈ 960x540（518400）。
    /// 例：2616x1140（2.295:1）→ 1090x475，视口 22x14 格。
    /// 这样 GUI.DrawTexture 缩放时是等比，画面不会变形。
    /// </summary>
    static void ComputeLogicalSize()
    {
        int dw = Mathf.Max(1, Screen.width);
        int dh = Mathf.Max(1, Screen.height);
        if (dh > dw) { int t = dw; dw = dh; dh = t; }   // 拿到竖屏尺寸时先摆正

        float aspect = dw / (float)dh;

        // 高度先按「像素量 ≈ 960x540」定，再对齐到整格（32 的倍数）
        int h = Mathf.RoundToInt(Mathf.Sqrt(960f * 540f / aspect));
        h = Mathf.Clamp(h, P1Map.CellH * 10, P1Map.CellH * 34);
        SH = Mathf.Max(1, Mathf.RoundToInt(h / (float)P1Map.CellH)) * P1Map.CellH;

        // 宽度按同一比例推，对齐到半格（48/2=24，原版 drawX 有 -OffSetX 的半格偏移）
        SW = Mathf.RoundToInt(SH * aspect / 24f) * 24;
    }

    void FindWalkableNear(ref Vector2Int c)
    {
        for (int r = 1; r < 60; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = c.x + dx, y = c.y + dy;
                    if (x <= 0 || y <= 0 || x >= _mapW || y >= _mapH) continue;
                    if (Bridge.CanWalk(_map, x, y)) { c = new Vector2Int(x, y); return; }
                }
    }

    void RecalcPath()
    {
        _path.Clear();
        _pathIdx = 0;
        _stepT = 0;

        var raw = Bridge.FindPath(_map,
            new System.Drawing.Point(_cell.x, _cell.y),
            new System.Drawing.Point(_target.x, _target.y));
        for (int i = 0; i < raw.Length; i++) _path.Add(new Vector2Int(raw[i].X, raw[i].Y));

        // 原版路径第 0 个点就是脚下，跳过
        if (_path.Count > 0 && _path[0] == _cell) _path.RemoveAt(0);
        _pathIdx = 0;
    }

    /// <summary>格子增量 → 方向 0..7（0=Up，顺时针 45°，与原版 MirDirection 一致）。</summary>
    static int DirFromDelta(int dx, int dy)
    {
        if (dx == 0 && dy < 0) return 0;    // Up
        if (dx > 0 && dy < 0) return 1;    // UpRight
        if (dx > 0 && dy == 0) return 2;   // Right
        if (dx > 0 && dy > 0) return 3;    // DownRight
        if (dx == 0 && dy > 0) return 4;   // Down
        if (dx < 0 && dy > 0) return 5;    // DownLeft
        if (dx < 0 && dy == 0) return 6;   // Left
        if (dx < 0 && dy < 0) return 7;    // UpLeft
        return 4;
    }

    /// <summary>把「格子 + 格内插值」换算成原版 User.OffSetMove 的像素偏移。</summary>
    void GetOffMove(out int omx, out int omy)
    {
        // 原版：走一格的过程中 OffSetMove 从 0 变到 ∓CellW/CellH。
        // 这里 _cell 是「已经站定的那一格」，_stepT 是本格走完的比例，
        // 所以偏移必须从 0 起、到 -dx*CellW 止（t=1 时正好等于下一格归零的画面）。
        // 第一版写成了 dx*(t-1)，等于起手就反向偏了一整格 —— 实机对离线基准差 42 灰度。
        var nxt = _pathIdx < _path.Count ? _path[_pathIdx] : _cell;
        int dx = nxt.x - _cell.x;
        int dy = nxt.y - _cell.y;
        float t = Mathf.Clamp01(_stepT);

        omx = Mathf.RoundToInt(-dx * P1Map.CellW * t);
        omy = Mathf.RoundToInt(-dy * P1Map.CellH * t);
    }

    void RenderNow()
    {
        int omx, omy;
        GetOffMove(out omx, out omy);

        var sw = Stopwatch.StartNew();
        _renderer.Render(_map, _cell.x, _cell.y, _mapW, _mapH, omx, omy);

        // 角色：站在 (0,0) 相对位置 —— 玩家永远是镜头中心。
        // 朝向按走的方向算，与原版 DirectionFromPoint 同义（0=Up 顺时针 45°）。
        int dir = _dir;
        int fi = _walkFrame;
        // 站定期画站立帧；走起来才画走路帧（离线基准就是站立帧 32 = 朝下）
        bool moving = _pathIdx < _path.Count && Time.realtimeSinceStartup >= _bootHoldUntil;
        _renderer.DrawPlayer(_cell.x, _cell.y, _cell.x, _cell.y, omx, omy,
                             _armour, dir, moving, fi);

        // ★ 必须在地图 + 角色都画完之后才上传纹理（见 P1Map.Flush 的注释）
        _renderer.Flush();

        _drawMs = (int)sw.ElapsedMilliseconds;
        _lastRenderT = Time.realtimeSinceStartup;
    }

    void Update()
    {
        _frames++;
        _fpsT += Time.unscaledDeltaTime;
        if (_fpsT >= 0.5f)
        {
            _fps = _frames / _fpsT;
            _frames = 0;
            _fpsT = 0;
        }

        if (!_ready) return;

        // 触摸/点击 = 改目标点，走真 A*
        if (Input.touchCount > 0)
        {
            var tc = Input.GetTouch(0);
            if (tc.phase == TouchPhase.Ended) HandleTap(tc.position);
        }
        else if (Input.GetMouseButtonUp(0))
        {
            HandleTap(Input.mousePosition);
        }

        if (!_autoWalk) return;

        // 启动站定期：镜头停在 (200,200)，角色保持站立帧 0，供逐像素验收
        if (Time.realtimeSinceStartup < _bootHoldUntil) return;

        // 沿路径走
        if (_pathIdx < _path.Count)
        {
            _stepT += Time.unscaledDeltaTime / StepTime;

            // 角色动画：走路 6 帧循环，每帧 100ms（原版 Frame(32, 6, 0, 100)）
            _walkAnimT += Time.unscaledDeltaTime;
            _walkFrame = (int)(_walkAnimT / WalkFrameTime) % 6;

            // 朝向 = 当前这一步的方向（原版 DirectionFromPoint：(0=Up 顺时针 45°)
            var nxt = _path[_pathIdx];
            int ddx = nxt.x - _cell.x, ddy = nxt.y - _cell.y;
            _dir = DirFromDelta(ddx, ddy);

            // 合成没必要每帧都做：格内插值到 1/8 格才重画一次
            if (Time.realtimeSinceStartup - _lastRenderT >= 1f / 20f) RenderNow();

            while (_stepT >= 1f && _pathIdx < _path.Count)
            {
                _stepT -= 1f;
                _cell = _path[_pathIdx];
                _pathIdx++;
            }
            if (_pathIdx >= _path.Count)
            {
                _stepT = 0;
                _walkAnimT = 0;
                _walkFrame = 0;      // 站定回到 Standing 帧 0
            }
        }
    }

    void HandleTap(Vector3 screenPos)
    {
        // 屏幕坐标 → 纹理坐标 → 地图格子（照抄原版 MapControl 的反向换算）
        float sx = screenPos.x / Mathf.Max(1, Screen.width) * SW;
        float sy = (1f - screenPos.y / Mathf.Max(1, Screen.height)) * SH;   // Unity GUI y 轴向下

        int cx = _cell.x + Mathf.RoundToInt((sx + _renderer.OffsetX) / P1Map.CellW) - _renderer.OffsetX;
        int cy = _cell.y + Mathf.RoundToInt(sy / P1Map.CellH) - _renderer.OffsetY;

        if (cx <= 0 || cy <= 0 || cx >= _mapW || cy >= _mapH) return;
        if (!Bridge.CanWalk(_map, cx, cy)) return;

        _target = new Vector2Int(cx, cy);
        RecalcPath();
    }

    void OnGUI()
    {
        // 真瓦片画面：先整屏铺满（这样贴图区的长宽比 = 逻辑分辨率的长宽比，不会变形），
        // 再把状态条盖在上面。
        if (_ready && _tex != null)
        {
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _tex, ScaleMode.StretchToFill, false);
        }

        // 顶部状态条
        GUI.color = new Color(0f, 0f, 0f, 0.72f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, 32), Texture2D.whiteTexture);
        GUI.color = Color.white;

        var st = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
        st.normal.textColor = _ready ? new Color(0.55f, 1f, 0.55f) : new Color(1f, 0.6f, 0.4f);
        GUI.Label(new Rect(10, 0, Screen.width - 20, 32), "[P1] " + _status, st);

        if (!_ready) return;

        // 底部信息
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0, Screen.height - 30, Screen.width, 30), Texture2D.whiteTexture);
        GUI.color = Color.white;

        var info = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleLeft };
        info.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
        GUI.Label(new Rect(10, Screen.height - 30, Screen.width - 20, 30),
            string.Format("坐标 ({0},{1})  目标 ({2},{3})  剩余 {4} 步  {5:F0} FPS  合成 {6}ms  逻辑 {7}x{8} 视口 {9}x{10} 格",
                _cell.x, _cell.y, _target.x, _target.y,
                Mathf.Max(0, _path.Count - _pathIdx), _fps, _drawMs,
                SW, SH, _renderer.ViewRangeX * 2 + 1, _renderer.ViewRangeY * 2 + 6), info);
    }
}
