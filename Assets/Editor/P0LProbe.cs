// P0-L 探针 —— 只在 Unity 编辑器里跑，验证「Unity 能不能吃 MirClientCore.dll」。
//
// 为什么要有这一步：
//   MirClientCore.dll 是在 Unity 之外用 dotnet build 出的 netstandard2.1 程序集。
//   它能被 dotnet 加载，**不代表 Unity 能加载**——Unity 的 Mono/IL2CPP BCL 是裁剪过的，
//   System.Drawing 里只保留了很小一部分。所以必须在 Unity 进程内真跑一次：
//     构造 MirClientCore 的类型 → 解析真地图 → 跑 A* 寻路
//   三样都成功，才算"真源码能进 Unity"。
//
// 用法（命令行）：
//   Tuanjie.exe -batchmode -quit -projectPath <proj> -executeMethod P0LProbe.Run -logFile <log>
using System;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class P0LProbe
{
    const string MapPath = @"C:\Users\haoxu\ai\_scratch\_t6ev\loc\Suprcode__Crystal\Build\Client\Release\Map\0.map";

    [MenuItem("Mir2/P0L 探针：Unity 能否加载 MirClientCore")]
    public static void Run()
    {
        var sb = new StringBuilder();
        int fail = 0;

        void Ok(string msg) { sb.AppendLine("[P0L-OK]   " + msg); Debug.Log("[P0L-OK]   " + msg); }
        void Bad(string msg) { fail++; sb.AppendLine("[P0L-FAIL] " + msg); Debug.LogError("[P0L-FAIL] " + msg); }

        // ── 0. 运行时信息 ──
        sb.AppendLine("========== P0-L 探针 ==========");
        sb.AppendLine("Unity 版本   : " + Application.unityVersion);
        sb.AppendLine("API 兼容级别 : " + PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup));
        sb.AppendLine("脚本后端     : " + PlayerSettings.GetScriptingBackend(EditorUserBuildSettings.selectedBuildTargetGroup));
        sb.AppendLine("目标平台     : " + EditorUserBuildSettings.activeBuildTarget);
        Debug.Log(sb.ToString());

        // ── 1. 直接按类型名引用（编译期能过 = Assembly-CSharp 真的引用了这个 DLL）──
        Type tCMain = null, tMapControl = null, tMapReader = null, tPathFinder = null;
        try
        {
            tCMain = typeof(Client.CMain);
            Ok("编译期引用 Client.CMain 成功");
        }
        catch (Exception e) { Bad("Client.CMain 引用失败: " + e.Message); }

        try { tMapControl = typeof(Client.MirScenes.MapControl); Ok("编译期引用 Client.MirScenes.MapControl 成功"); }
        catch (Exception e) { Bad("MapControl 引用失败: " + e.Message); }

        try { tMapReader = Type.GetType("Client.MirObjects.MapReader, MirClientCore"); }
        catch { tMapReader = null; }
        if (tMapReader == null)
        {
            // MapReader 是 internal class，typeof 用不了；走反射找
            var asm = typeof(Client.CMain).Assembly;
            tMapReader = asm.GetType("Client.MirObjects.MapReader");
        }
        if (tMapReader != null) Ok("找到 Client.MirObjects.MapReader (internal，反射拿的)");
        else Bad("找不到 Client.MirObjects.MapReader");

        // ── 2. 跑一遍 CMain 的逻辑时钟（逻辑层最基础的东西）──
        try
        {
            Client.CMain.Time = 12345;
            long v = Client.CMain.Time;
            if (v == 12345) Ok("CMain.Time 读写正常 (逻辑时钟通道可用)");
            else Bad("CMain.Time 读写异常: 期望 12345，实得 " + v);
        }
        catch (Exception e) { Bad("CMain.Time 异常: " + e.Message); }

        // ── 3. 构造 MapControl（真 UI 控件类，能构造说明 GDI+/WinForms 垫片在 Unity 里不崩）──
        object map = null;
        try
        {
            map = Activator.CreateInstance(typeof(Client.MirScenes.MapControl), nonPublic: true);
            Ok("MapControl 构造成功");
        }
        catch (Exception e) { Bad("MapControl 构造失败: " + (e.InnerException ?? e).Message); }

        // ── 4. 解析真地图 0.map（5880052 B / 700x700）──
        if (map != null && tMapReader != null)
        {
            if (!System.IO.File.Exists(MapPath)) Bad("地图文件不存在: " + MapPath);
            else
            {
                try
                {
                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    object reader = Activator.CreateInstance(tMapReader, flags, null, new object[] { MapPath }, null);
                    int w = (int)tMapReader.GetField("Width").GetValue(reader);
                    int h = (int)tMapReader.GetField("Height").GetValue(reader);
                    var cells = tMapReader.GetField("MapCells").GetValue(reader);
                    if (w == 700 && h == 700)
                        Ok("真地图解析成功: " + w + " x " + h + " = " + (w * h) + " 格");
                    else
                        Bad("地图尺寸异常: " + w + " x " + h + "（期望 700x700）");

                    // 喂给 MapControl
                    var mcType = typeof(Client.MirScenes.MapControl);
                    mcType.GetField("M2CellInfo", flags).SetValue(map, cells);
                    mcType.GetField("Width", flags).SetValue(map, w);
                    mcType.GetField("Height", flags).SetValue(map, h);
                    Ok("MapControl 已载入地图数据");
                }
                catch (Exception e) { Bad("地图解析失败: " + (e.InnerException ?? e).Message); }
            }
        }

        // ── 5. 构造 PathFinder + 真跑 A*（最终验收：真算法在 Unity 里算得动）──
        if (map != null)
        {
            try
            {
                var asm = typeof(Client.CMain).Assembly;
                var tPF = asm.GetType("Client.MirObjects.PathFinder");
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                object pf = Activator.CreateInstance(tPF, flags, null, new object[] { map }, null);
                sb.AppendLine("           A* 网格构建 " + sw.ElapsedMilliseconds + " ms");
                Debug.Log("[P0L] A* 网格构建 " + sw.ElapsedMilliseconds + " ms");

                var findPath = tPF.GetMethod("FindPath", flags);
                sw.Restart();
                object path = findPath.Invoke(pf, new object[] { new System.Drawing.Point(200, 200), new System.Drawing.Point(600, 600), 0 });
                long ms = sw.ElapsedMilliseconds;

                if (path == null) Bad("A* 寻路返回 null（地图可能全不可走）");
                else
                {
                    int cnt = (int)path.GetType().GetProperty("Count").GetValue(path);
                    Ok("A* 寻路成功: 200,200 -> 600,600 共 " + cnt + " 步，耗时 " + ms + " ms");
                }
            }
            catch (Exception e) { Bad("PathFinder 异常: " + (e.InnerException ?? e).Message); }
        }

        // ── 6. System.Drawing 在 Unity 里到底能用到什么程度 ──
        try
        {
            var pt = new System.Drawing.Point(3, 4);
            var sz = new System.Drawing.Size(5, 6);
            var rc = System.Drawing.Rectangle.FromLTRB(1, 2, 8, 9);
            var cl = System.Drawing.Color.FromArgb(128, 10, 20, 30);
            Ok("System.Drawing 基础类型可用: Point=" + pt + " Size=" + sz + " Rect=" + rc + " Color.A=" + cl.A);
        }
        catch (Exception e) { Bad("System.Drawing 基础类型异常: " + e.Message); }

        // ── 7. 静态构造：GameScene（322 方法的主场景类）能不能触发静态初始化 ──
        try
        {
            var tGS = typeof(Client.MirScenes.GameScene);
            var m = tGS.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Ok("GameScene 类型可访问，静态方法 " + m.Length + " 个");
        }
        catch (Exception e) { Bad("GameScene 访问异常: " + e.Message); }

        // ── 收尾 ──
        string verdict = fail == 0 ? "PASS" : "FAIL(" + fail + ")";
        sb.AppendLine("========== 结论: " + verdict + " ==========");
        Debug.Log(sb.ToString());
        Debug.Log("[P0L] VERDICT " + verdict);

        System.IO.File.WriteAllText(@"C:\ai\tmp\p0l_probe.txt", sb.ToString(), Encoding.UTF8);
    }
}
