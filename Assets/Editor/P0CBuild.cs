// P0-C —— 出包入口(产物 = builds\p0c.apk)
// ★ 外部工具链写法照抄 P0BBuild / P0ABuild / AndroidBuildProbe:
//   6 个 EditorPrefs 键 + 3 个 UseEmbedded=false。少写会去找不存在的内嵌 SDK 而构建失败。
// 命令行(原样):
//   & 'C:\Program Files\Tuanjie\Hub\Editor\2022.3.62t16\Editor\Tuanjie.exe' -batchmode -nographics `
//     -projectPath '<本机>/_scratch\_tjproj_and' -executeMethod P0CBuild.BuildApk `
//     -logFile '<本机>/_scratch\_p0c\build.log' -quit
//
// ★★ VERDICT 行是本文件最脆的一处，别"美化"：
//   harness.py L152 的 A2 判定原文 =
//       "VERDICT = SUCCESS" in bl  or  "VERDICT=SUCCESS" in bl.replace(" = ", " = ")
//   后半段的 replace(" = ", " = ") 两个操作数**完全相同**(无操作数)，是恒等变换 ——
//   它**无法**把 "[P0C] VERDICT      = SUCCESS"(多空格) 变成能匹配的形式。
//   实测: P0-B 的 "[P0B] VERDICT      = SUCCESS" 在 A2 表达式上 = False。
//   ⇒ 本文件必须打出**单空格**的 "VERDICT = SUCCESS" 字面量。改对齐 = A2 直接挂。
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P0CBuild
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ApkPath = @"C:\Users\haoxu\ai\_scratch\_tjproj_and\builds\p0c.apk";
    const string ScenePath = "Assets/Scenes/P0C.unity";
    const string AppId = "com.haoxu.p0c";

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

    [MenuItem("P0C/2. Build APK")]
    public static void BuildApk()
    {
        try
        {
            SetPaths();
            Debug.Log("[P0C] SDK=" + EditorPrefs.GetString("AndroidSdkRoot") + " exists=" + Directory.Exists(SdkRoot));
            Debug.Log("[P0C] NDK=" + EditorPrefs.GetString("AndroidNdkRootR23B") + " exists=" + Directory.Exists(NdkRoot));
            Debug.Log("[P0C] JDK=" + EditorPrefs.GetString("JavaHome") + " exists=" + Directory.Exists(JdkRoot));

            // ★ 几何口径自证（写进 build.log，供复核方核对源码常量已按权威口径落地）
            //   ⚠⚠ 但这行是**编译期常量**，**不是设备实测** —— 必须显式自我标死，否则会变成「假通过」的燃料：
            //     队长 2026-10-13 实测指出 A3 曾有**三条假通过**，其中路径 (b) 就是**本行**：
            //       该行含 `屏 2616 1140 … 1600 … 1140 … 508 … 2.0`，全与 A3 要的四个数吻合，
            //       于是「**只跑构建、没装包、没跑模拟器**」也能让 A3 绿 —— 因为这些数**与设备无关**。
            //     harness 侧的修法是「A3 只从 t3_ 运行期日志取数」（已由 verifier 落地）。
            //     **我在源头再加一道**：把本行标注为「编译期常量·非实测」，
            //       使**任何**人/任何工具 grep 到这行时都不会误当成运行期证据（不依赖 harness 的过滤正确）。
            //     ⇒ 真正的实测行只有 `P0BMapWalk.LogViewport()` 打的 `[P0C] viewport tag=… 屏 <sw> <sh>`
            //       （sw/sh 取自 `Screen.width/height`），那才是 A3 的判据来源。
            Debug.Log("[P0C] 几何口径[编译期常量·非实测·不可用作A3证据] 屏 2616 1140 | 逻辑 800 600 | 渲染 1600 1200 | 主视口 1600 1140" +
                      " | 侧栏 508 188.6 | 缩放 2 | 语义 视口宽 1600 高 1140 侧栏 508 scale 2.0");

            P0BImport.Apply();
            P0CImport.Apply();   // 主 UI 素材：Point / 无 mipmap / 无压缩（A5 判据）

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AppId);
            PlayerSettings.productName = "P0CMainUI";
            PlayerSettings.bundleVersion = "1.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Android, ApiCompatibilityLevel.NET_Standard_2_0);

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
            catch (Exception e) { Debug.LogWarning("[P0C] splash 不可配置: " + e.Message); }

            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[P0C] 场景已保存: " + ScenePath + " (空场景, 由 P0BMapWalk 的 AfterSceneLoad 引导自建)");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            Debug.Log("[P0C] === BuildPipeline.BuildPlayer START === " + ApkPath);
            var s = BuildPipeline.BuildPlayer(opts);

            Debug.Log("[P0C] RESULT = " + s.summary.result);
            Debug.Log("[P0C] TOTAL_ERRORS = " + s.summary.totalErrors);
            Debug.Log("[P0C] TOTAL_WARNS = " + s.summary.totalWarnings);
            Debug.Log("[P0C] OUTPUT_PATH = " + s.summary.outputPath);
            Debug.Log("[P0C] SIZE_BYTES = " + s.summary.totalSize);

            bool ok = s.summary.result == BuildResult.Succeeded && File.Exists(ApkPath);
            if (ok)
                Debug.Log("[P0C] APK_BYTES = " + new FileInfo(ApkPath).Length);
            Debug.Log("[P0C] APK_EXISTS = " + File.Exists(ApkPath));
            // ⚠ 单空格，勿改为对齐写法（见文件头说明）
            Debug.Log("[P0C] VERDICT = " + (ok ? "SUCCESS" : "FAILURE"));
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0C] BUILD EXCEPTION: " + e);
            EditorApplication.Exit(3);
        }
    }
}
