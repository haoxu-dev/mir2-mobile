// P0-D —— 出包入口（产物 = Builds\p0d.apk）
//
// ★ 外部工具链写法**照抄** P0CBuild / P0BBuild / P0ABuild 的既有口径（不是自己发明的）：
//   6 个 EditorPrefs 键 + 3 个 UseEmbedded=false。少写一个就会去找不存在的内嵌 SDK 而构建失败。
//
// ★ 命令行（与契约 verify[1] 逐字一致）：
//   & 'C:\Program Files\Tuanjie\Hub\Editor\2022.3.62t16\Editor\Tuanjie.exe' -batchmode -nographics `
//     -projectPath 'C:\Users\haoxu\ai\_scratch\_tjproj_and' -executeMethod P0DBuild.BuildApk `
//     -logFile 'C:\Users\haoxu\ai\_scratch\_p0c\m2build\build.log' -quit
//
// ★★ VERDICT 行是本文件最脆的一处，别"美化"：
//   harness.py L152 的 A2 判定原文 =
//       "VERDICT = SUCCESS" in bl  or  "VERDICT=SUCCESS" in bl.replace(" = ", " = ")
//   后半段 replace 的两个操作数**完全相同**（恒等变换），无法把多空格对齐的
//   "[P0D] VERDICT      = SUCCESS" 变成能匹配的形式（P0-B 实测 A2=False）。
//   ⇒ 必须打出**单空格**的 "VERDICT = SUCCESS" 字面量。改对齐 = A2 直接挂。
//
// ★★★ 产物命名纪律（本队 t2 死因，务必遵守）：
//   唯一权威名 = `Builds\p0d.apk`。契约 description 里的 `builds\p0c_m2.apk` **已作废**。
//   且**不得覆盖已冻结的 p0c.apk**（md5 7A8ECC28…）—— 本文件在构建前后各量一次它的 md5 并断言不变。
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P0DBuild
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    // ★ 唯一权威产物路径（大写 Builds，与契约 acceptance[7] 一致）
    const string ApkPath = @"C:\Users\haoxu\ai\_scratch\_tjproj_and\Builds\p0d.apk";
    // ★ 已冻结的 P0-C 产物：本任务**只读**，构建前后必须逐字节一致
    const string FrozenP0C = @"C:\Users\haoxu\ai\_scratch\_tjproj_and\builds\p0c.apk";
    const string ScenePath = "Assets/Scenes/P0D.unity";
    const string AppId = "com.haoxu.p0d";

    static string Md5Of(string p)
    {
        if (!File.Exists(p)) return "(不存在)";
        using (var md5 = MD5.Create())
        using (var fs = File.OpenRead(p))
        {
            var h = md5.ComputeHash(fs);
            var sb = new System.Text.StringBuilder();
            foreach (var b in h) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }

    static void SetPaths()
    {
        EditorPrefs.SetString("AndroidSdkRoot", SdkRoot);
        EditorPrefs.SetString("AndroidNdkRootR23B", NdkRoot);
        EditorPrefs.SetString("AndroidNdkRootDir", NdkRoot);
        EditorPrefs.SetString("AndroidNdkRoot", NdkRoot);
        EditorPrefs.SetString("JavaHome", JdkRoot);
        EditorPrefs.SetString("JdkPath", JdkRoot);
        EditorPrefs.SetBool("SdkUseEmbedded", false);
        EditorPrefs.SetBool("NdkUseEmbedded", false);
        EditorPrefs.SetBool("JdkUseEmbedded", false);
    }

    /// <summary>★ 只落偏好、不构建。必须**单独一个进程**先跑它。
    ///   根因（builder2 实测 + 队长复核，2026-10-15）：Android 扩展在本进程 **加载编辑器时**就已解析
    ///   并把工具路径**缓存**；`BuildApk()` 内部再 `EditorPrefs.SetString` 已经太晚 —— 缓存是脏的，
    ///   于是报 `UnityException: JDK not found`（而磁盘上 JDK 明明存在，自证 exists=True）。
    ///   ⇒ 拆两进程：进程1 只落盘偏好 → 进程2 在加载期就从注册表读到正确路径。
    ///   旁证：P0C 当年同款 SetPaths() 能过，是因为编辑器带 GUI 跑、偏好早就在注册表里了。
    [MenuItem("P0D/0. Set External Tool Paths Only")]
    public static void SetPathsOnly()
    {
        SetPaths();
        Debug.Log("[P0D] prefs persisted (仅落偏好，不构建)");
        Debug.Log("[P0D]   AndroidSdkRoot   = " + EditorPrefs.GetString("AndroidSdkRoot") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidSdkRoot")));
        Debug.Log("[P0D]   AndroidNdkRootR23B= " + EditorPrefs.GetString("AndroidNdkRootR23B") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidNdkRootR23B")));
        Debug.Log("[P0D]   JavaHome         = " + EditorPrefs.GetString("JavaHome") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JavaHome")));
        Debug.Log("[P0D]   JdkPath          = " + EditorPrefs.GetString("JdkPath") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JdkPath")));
        // ★ 反读判据：写不进去就当场报错，别让"写了但没生效"静默通过
        bool ok = EditorPrefs.GetString("AndroidSdkRoot") == SdkRoot
                  && EditorPrefs.GetString("AndroidNdkRootR23B") == NdkRoot
                  && EditorPrefs.GetString("JavaHome") == JdkRoot
                  && EditorPrefs.GetString("JdkPath") == JdkRoot;
        Debug.Log("[P0D] PREFS_VERDICT = " + (ok ? "PASS" : "FAIL") +
                  " (回读四值与源码常量逐字比对)");
        EditorApplication.Exit(ok ? 0 : 4);
    }

    [MenuItem("P0D/2. Build APK")]
    public static void BuildApk()
    {
        try
        {
            SetPaths();
            Debug.Log("[P0D] SDK=" + EditorPrefs.GetString("AndroidSdkRoot") + " exists=" + Directory.Exists(SdkRoot));
            Debug.Log("[P0D] NDK=" + EditorPrefs.GetString("AndroidNdkRootR23B") + " exists=" + Directory.Exists(NdkRoot));
            Debug.Log("[P0D] JDK=" + EditorPrefs.GetString("JavaHome") + " exists=" + Directory.Exists(JdkRoot));

            // ★ 几何口径自证（写进 build.log 供复核核对源码常量）
            //   ⚠⚠ 但这是**编译期常量**，**不是设备实测** —— 必须显式标死，否则会变成"假通过"燃料：
            //     队长 2026-10-13 实测指出 A3 曾有**三条假通过**，其中路径 (b) 就是这类自证行。
            //     真正的设备/运行期实测行只有 `P0BMapWalk.LogViewport()` 打的
            //     `[P0C] viewport tag=… 屏 <sw> <sh>`（sw/sh 取自 Screen.width/height）。
            Debug.Log("[P0D] 几何口径[编译期常量·非实测·不可用作A3证据] 屏 2616 1140 | 逻辑 800 600 | 渲染 1600 1200 | 主视口 1600 1140" +
                      " | 侧栏 508 188.6 | 缩放 2 | 语义 视口宽 1600 高 1140 侧栏 508 scale 2.0");

            // ★ 冻结产物基线（改前）
            string frozenBefore = Md5Of(FrozenP0C);
            Debug.Log("[P0D] 冻结产物 p0c.apk 构建前 MD5 = " + frozenBefore);

            P0BImport.Apply();
            P0DImport.Apply();   // M2 主 UI 素材：Point / 无 mipmap / 无压缩（A5 判据）

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AppId);
            PlayerSettings.productName = "P0DMainUI";
            PlayerSettings.bundleVersion = "1.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Android, ApiCompatibilityLevel.NET_Standard_2_0);
            // ★★★ 引擎代码剥离：保持开启，但用 Assets/link.xml 钉死 IMGUI
            //   事故（2026-10-15）：`Builds\p0d.apk` md5 A8424E60 装到设备后 OnGUI 整条失效 ——
            //     E/Tuanjie: Could not produce class with ID 115.
            //     E/Tuanjie: This could be caused by a class being stripped from the build even
            //                though it is needed. Try disabling 'Strip Engine Code' in Player Settings.
            //     GUIUtility:GetDefaultSkin() -> GUI:DoSetSkin(GUISkin) -> NullReferenceException
            //     -> at P0BMapWalk.OnGUI()；[P0D] V9_VERDICT 归零、NRE 3588 条。
            //   Unity class ID 115 = GUISkin（OnGUI/IMGUI 的必需类型）。
            //   受控对照（同 managed 程序集逐字节相同，APK 内仅 2 个 .so 不同）：
            //     好包 6503F71D(lib 14265184 B) -> class115 报错  0 次 / V9_VERDICT PASS 296 次
            //     坏包 A8424E60(lib 14264576 B) -> class115 报错 83 次 / V9_VERDICT 0 次
            //   ★ 为什么不直接 stripEngineCode=false（试过，是弯路）：
            //     关掉后 .so 14.27MB -> 39.09MB、APK 20.96MB -> 38.83MB，**翻倍**；
            //     而历史已交付已验证的 p0a/p0b/p0c 全是剥离态(~14MB)且 OnGUI 正常
            //     ⇒ 剥离本身无罪，是"某个必需类没被保住"。正解 = 保留剥离 + link.xml 白名单。
            PlayerSettings.stripEngineCode = true;
            Debug.Log("[P0D] stripEngineCode(true) + Assets/link.xml(IMGUI 白名单) —— 修 class 115 缺失");

            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.Android.renderOutsideSafeArea = true;

            try
            {
                PlayerSettings.SplashScreen.show = false;
                PlayerSettings.SplashScreen.showUnityLogo = false;
            }
            catch (Exception e) { Debug.LogWarning("[P0D] splash 不可配置: " + e.Message); }

            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[P0D] 场景已保存: " + ScenePath + " (空场景, 由 P0BMapWalk 的 AfterSceneLoad 引导自建)");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            Debug.Log("[P0D] === BuildPipeline.BuildPlayer START === " + ApkPath);
            var s = BuildPipeline.BuildPlayer(opts);

            Debug.Log("[P0D] RESULT = " + s.summary.result);
            Debug.Log("[P0D] TOTAL_ERRORS = " + s.summary.totalErrors);
            Debug.Log("[P0D] TOTAL_WARNS = " + s.summary.totalWarnings);
            Debug.Log("[P0D] OUTPUT_PATH = " + s.summary.outputPath);
            Debug.Log("[P0D] SIZE_BYTES = " + s.summary.totalSize);

            bool ok = s.summary.result == BuildResult.Succeeded && File.Exists(ApkPath);
            if (ok)
            {
                Debug.Log("[P0D] APK_BYTES = " + new FileInfo(ApkPath).Length);
                Debug.Log("[P0D] APK_MD5 = " + Md5Of(ApkPath));
            }
            Debug.Log("[P0D] APK_EXISTS = " + File.Exists(ApkPath));

            // ★ 冻结产物守卫：p0c.apk 必须逐字节不变，否则立即判失败（防"覆盖了还说成功"）
            string frozenAfter = Md5Of(FrozenP0C);
            bool frozenOk = (frozenBefore == frozenAfter);
            Debug.Log("[P0D] 冻结产物 p0c.apk 构建后 MD5 = " + frozenAfter +
                      " 未改动=" + (frozenOk ? "OK" : "★被改动"));
            if (!frozenOk) ok = false;

            // ⚠ 单空格，勿改为对齐写法（见文件头说明）
            Debug.Log("[P0D] VERDICT = " + (ok ? "SUCCESS" : "FAILURE"));
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0D] BUILD EXCEPTION: " + e);
            EditorApplication.Exit(3);
        }
    }
}
