// P0-E —— 出包入口（产物 = Builds\p0e.apk）
//
// ★ 外部工具链写法**照抄** P0DBuild / P0CBuild / P0BBuild / P0ABuild 的既有口径（不是自己发明的）：
//   6 个 EditorPrefs 键 + 3 个 UseEmbedded=false。少写一个就会去找不存在的内嵌 SDK 而构建失败。
//
// ★★ 两进程阶梯（P0-D 实测踩出来的，**不能合并**）：
//   进程1 `SetPathsOnly` 只落偏好 → 进程2 `BuildApk` 在**加载期**就从注册表读到正确路径。
//   根因：Android 扩展在**加载编辑器时**就解析并缓存工具路径，`BuildApk()` 内部再
//   `EditorPrefs.SetString` 已经太晚 —— 缓存是脏的，报 `UnityException: JDK not found`
//   （而磁盘上 JDK 明明存在）。故 build_p0e.ps1 必须先跑一次 SetPathsOnly。
//
// ★★★ VERDICT 行是本文件最脆的一处，别"美化"：
//   harness 的 A2 判定原文匹配**单空格**的 `VERDICT = SUCCESS` 字面量；
//   P0-B 实测过对齐写法（多空格）会把 A2 判成 False。⇒ 保持单空格。
//
// ★ 冻结产物守卫：构建前后各量一次已交付的 p0a/p0b/p0c/p0d.apk 的 md5 并断言不变
//   （防"覆盖了还说成功"）。本文件**不写**这 4 个路径，只读。
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P0EBuild
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ProjRoot = @"C:\Users\haoxu\ai\_scratch\_tjproj_and";
    const string ApkPath = ProjRoot + @"\builds\p0e.apk";
    const string ScenePath = "Assets/Scenes/P0E.unity";
    const string AppId = "com.mir2.p0e";

    static readonly string[] Frozen =
    {
        ProjRoot + @"\builds\p0a.apk",
        ProjRoot + @"\builds\p0b.apk",
        ProjRoot + @"\builds\p0c.apk",
        ProjRoot + @"\builds\p0d.apk",
    };

    static string Md5Of(string p)
    {
        if (!File.Exists(p)) return "(不存在)";
        using (var md5 = MD5.Create())
        using (var fs = File.OpenRead(p))
            return BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "");
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

    /// <summary>★ 只落偏好、不构建。必须**单独一个进程**先跑它（见文件头）。</summary>
    [MenuItem("P0E/0. Set External Tool Paths Only")]
    public static void SetPathsOnly()
    {
        SetPaths();
        Debug.Log("[P0E] prefs persisted (仅落偏好，不构建)");
        Debug.Log("[P0E]   AndroidSdkRoot   = " + EditorPrefs.GetString("AndroidSdkRoot") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidSdkRoot")));
        Debug.Log("[P0E]   AndroidNdkRootR23B = " + EditorPrefs.GetString("AndroidNdkRootR23B") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidNdkRootR23B")));
        Debug.Log("[P0E]   JavaHome         = " + EditorPrefs.GetString("JavaHome") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JavaHome")));
        Debug.Log("[P0E]   JdkPath          = " + EditorPrefs.GetString("JdkPath") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JdkPath")));
        // ★ 反读判据：写不进去就当场报错，别让"写了但没生效"静默通过
        bool ok = EditorPrefs.GetString("AndroidSdkRoot") == SdkRoot
                  && EditorPrefs.GetString("AndroidNdkRootR23B") == NdkRoot
                  && EditorPrefs.GetString("JavaHome") == JdkRoot
                  && EditorPrefs.GetString("JdkPath") == JdkRoot;
        Debug.Log("[P0E] PREFS_VERDICT = " + (ok ? "PASS" : "FAIL") + " (回读四值与源码常量逐字比对)");
        EditorApplication.Exit(ok ? 0 : 4);
    }

    [MenuItem("P0E/1. Build APK")]
    public static void BuildApk()
    {
        try
        {
            SetPaths();
            Debug.Log("[P0E] SDK=" + EditorPrefs.GetString("AndroidSdkRoot") + " exists=" + Directory.Exists(SdkRoot));
            Debug.Log("[P0E] NDK=" + EditorPrefs.GetString("AndroidNdkRootR23B") + " exists=" + Directory.Exists(NdkRoot));
            Debug.Log("[P0E] JDK=" + EditorPrefs.GetString("JavaHome") + " exists=" + Directory.Exists(JdkRoot));

            // ★ 几何口径自证（写进 build.log 供复核核对源码常量）
            //   ⚠⚠ 但这是**编译期常量**，**不是设备实测** —— 必须显式标死，否则会变成"假通过"燃料。
            //     真正的设备/运行期实测行只有 `P0BMapWalk.LogViewport()` 打的
            //     `[P0C] viewport tag=… 屏 <sw> <sh>`（sw/sh 取自 Screen.width/height）。
            Debug.Log("[P0E] 几何口径[编译期常量·非实测·不可用作A3证据] 屏 2616 1140 | 逻辑 800 600 | 渲染 1600 1200 | 主视口 1600 1140" +
                      " | 侧栏 508 188.6 | 缩放 2 | 语义 视口宽 1600 高 1140 侧栏 508 scale 2.0");

            var frozenBefore = new string[Frozen.Length];
            for (int i = 0; i < Frozen.Length; i++)
            {
                frozenBefore[i] = Md5Of(Frozen[i]);
                Debug.Log("[P0E] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建前 MD5 = " + frozenBefore[i]);
            }

            // ★ P0-E 新增素材的导入设置（Point / 无 mipmap / 无压缩 / NPOT 不缩放 / Android 块）
            P0EImport.Apply();
            // ★ P0-D 主 UI 素材的导入设置必须**继续**应用：P0EImport 不碰 p0d/，而本包仍要画 0039。
            P0DImport.Apply();
            P0BImport.Apply();

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AppId);
            PlayerSettings.productName = "P0EMap";
            PlayerSettings.bundleVersion = "1.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Android, ApiCompatibilityLevel.NET_Standard_2_0);

            // ★★★ 引擎代码剥离：保持开启，但用 Assets/link.xml 钉死 IMGUI（**必须**，见下）
            //   事故（2026-10-15）：p0d.apk md5 A8424E60 装到设备后 OnGUI 整条失效 ——
            //     E/Tuanjie: Could not produce class with ID 115.（Unity class ID 115 = GUISkin）
            //     GUIUtility:GetDefaultSkin() -> GUI:DoSetSkin(GUISkin) -> NullReferenceException
            //     -> at P0BMapWalk.OnGUI()；V9_VERDICT 归零、NRE 3588 条。
            //   受控对照（同 managed 程序集逐字节相同，APK 内仅 2 个 .so 不同）：
            //     好包 6503F71D(lib 14265184 B) -> class115 报错 0 次 / V9_VERDICT PASS 296 次
            //     坏包 A8424E60(lib 14264576 B) -> class115 报错 83 次 / V9_VERDICT 0 次
            //   ★ 本包的 P0-E 渲染路径**新增**了 SpriteRenderer + 大量 Sprite.Create，
            //     而 link.xml 已 preserve `UnityEngine.Sprite` / `Texture2D` / `Shader` / `Material`
            //     ⇒ 不需要改 link.xml（改了反而要重新验证）。**本次不动它。**
            PlayerSettings.stripEngineCode = true;
            Debug.Log("[P0E] stripEngineCode(true) + Assets/link.xml(IMGUI/Sprite 白名单) —— 沿用 p0d 已验证口径");

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
            catch (Exception e) { Debug.LogWarning("[P0E] splash 不可配置: " + e.Message); }

            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[P0E] 场景已保存: " + ScenePath + " (空场景, 由 P0BMapWalk 的 AfterSceneLoad 引导自建)");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            Debug.Log("[P0E] === BuildPipeline.BuildPlayer START === " + ApkPath);
            var s = BuildPipeline.BuildPlayer(opts);

            Debug.Log("[P0E] RESULT = " + s.summary.result);
            Debug.Log("[P0E] TOTAL_ERRORS = " + s.summary.totalErrors);

            bool ok = s.summary.result == BuildResult.Succeeded && File.Exists(ApkPath);
            if (ok)
            {
                Debug.Log("[P0E] APK_BYTES = " + new FileInfo(ApkPath).Length);
                Debug.Log("[P0E] APK_MD5 = " + Md5Of(ApkPath));
            }
            Debug.Log("[P0E] APK_EXISTS = " + File.Exists(ApkPath));

            // ★ 冻结产物守卫：已交付包必须逐字节不变，否则立即判失败
            for (int i = 0; i < Frozen.Length; i++)
            {
                string now = Md5Of(Frozen[i]);
                bool same = now == frozenBefore[i];
                Debug.Log("[P0E] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建后 MD5 = " + now +
                          " 未改动=" + same);
                if (!same) { Debug.LogError("[P0E] ★已交付产物被改动: " + Frozen[i]); ok = false; }
            }

            // ⚠ 单空格，勿改为对齐写法（见文件头说明）
            Debug.Log("[P0E] VERDICT = " + (ok ? "SUCCESS" : "FAILURE"));
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0E] BUILD EXCEPTION: " + e);
            EditorApplication.Exit(3);
        }
    }
}
