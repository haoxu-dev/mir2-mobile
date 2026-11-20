// P0-G —— 出包入口（产物 = Builds\p0g.apk）
//
// 为什么另开一份而不改 P0FBuild.cs：
//   p0f.apk 已交付（43,125,211 B / 2026-10-17 15:46），它的链路是「P0-F 文字层如何出包」的证据。
//   改 P0FBuild 会让那份证据失去可复现性 ⇒ 复制一份，只改方法前缀、产物路径、AppId、
//   以及冻结清单**追加 p0f.apk**。
//
// ★ 外部工具链写法、两进程阶梯、VERDICT 单空格 —— 三项照抄 P0FBuild（已被验证可出包）：
//   6 个 EditorPrefs 键 + 3 个 UseEmbedded=false；先跑 SetPathsOnly 再跑 BuildApk。
//
// ★ 本包的内容增量（相对 p0f）：
//   1) Assets/Resources/p0g/            —— 4 张从 Title.Lib 解码的真实 UI 图
//   2) Assets/Resources/p0g_art_layout.json —— 12 条登记（4 可画 + 8 登记不画）
//   3) Assets/Scripts/P0GArt.cs         —— 位图层渲染
//   4) Assets/Editor/P0GImport.cs       —— 新目录的导入设置（必须跑，否则纹理会被压/被 NPOT 缩放）
//   其余（P0EImport/P0DImport/P0BImport）照旧全跑，否则 0039 与地图/行走图集会丢导入设置。
//
// ★★ stripEngineCode 必须保持 **false**（不是可选优化，是事故结论）：
//   本工程 Tuanjie 2022.3.62t16 构建链上，stripEngineCode=true 会把
//   UnityEngine.IMGUIModule 的 GUISkin(class 115) 整类剥掉，`link.xml` 白名单未能可靠生效；
//   装机后 GUIUtility:GetDefaultSkin → GUI:DoSetSkin(null) → NullReferenceException 刷屏，
//   OnGUI 整条渲染路径失效（[P0D]/[P0F]/[P0G] 一条日志都不出）。
//   P0-G 全程走 GUI.DrawTexture，属于同一受害面 ⇒ 必须关闭剥离。代价是包体约 43MB。
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P0GBuild
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ProjRoot = @"C:\Users\haoxu\ai\_scratch\_tjproj_and";
    const string ApkPath = ProjRoot + @"\Builds\p0g.apk";
    const string ScenePath = "Assets/Scenes/P0G.unity";
    const string AppId = "com.mir2.p0g";

    static readonly string[] Frozen =
    {
        ProjRoot + @"\Builds\p0a.apk",
        ProjRoot + @"\Builds\p0b.apk",
        ProjRoot + @"\Builds\p0c.apk",
        ProjRoot + @"\Builds\p0d.apk",
        ProjRoot + @"\Builds\p0e.apk",
        ProjRoot + @"\Builds\p0f.apk",
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

    /// <summary>只落偏好、不构建。必须单独一个进程先跑它（Android 扩展在加载期缓存工具路径）。</summary>
    [MenuItem("P0G/0. Set External Tool Paths Only")]
    public static void SetPathsOnly()
    {
        SetPaths();
        Debug.Log("[P0G] prefs persisted (仅落偏好，不构建)");
        Debug.Log("[P0G]   AndroidSdkRoot     = " + EditorPrefs.GetString("AndroidSdkRoot") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidSdkRoot")));
        Debug.Log("[P0G]   AndroidNdkRootR23B = " + EditorPrefs.GetString("AndroidNdkRootR23B") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidNdkRootR23B")));
        Debug.Log("[P0G]   JavaHome           = " + EditorPrefs.GetString("JavaHome") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JavaHome")));
        Debug.Log("[P0G]   JdkPath            = " + EditorPrefs.GetString("JdkPath") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JdkPath")));
        bool ok = EditorPrefs.GetString("AndroidSdkRoot") == SdkRoot
                  && EditorPrefs.GetString("AndroidNdkRootR23B") == NdkRoot
                  && EditorPrefs.GetString("JavaHome") == JdkRoot
                  && EditorPrefs.GetString("JdkPath") == JdkRoot;
        Debug.Log("[P0G] PREFS_VERDICT = " + (ok ? "PASS" : "FAIL") + " (回读四值与源码常量逐字比对)");
        EditorApplication.Exit(ok ? 0 : 4);
    }

    [MenuItem("P0G/1. Build APK")]
    public static void BuildApk()
    {
        try
        {
            SetPaths();
            Debug.Log("[P0G] SDK=" + EditorPrefs.GetString("AndroidSdkRoot") + " exists=" + Directory.Exists(SdkRoot));
            Debug.Log("[P0G] NDK=" + EditorPrefs.GetString("AndroidNdkRootR23B") + " exists=" + Directory.Exists(NdkRoot));
            Debug.Log("[P0G] JDK=" + EditorPrefs.GetString("JavaHome") + " exists=" + Directory.Exists(JdkRoot));

            Debug.Log("[P0G] 几何口径[编译期常量·非实测·不可用作设备证据] 屏 2616 1140 | 逻辑 800 600 | 渲染 1600 1200" +
                      " | 主视口 1600 1140 | 侧栏 508 | 缩放 2");

            // ★ 位图层数据自检：缺一个就该立刻失败，而不是出个"能装但背包空白"的包
            string artLayout = ProjRoot + @"\Assets\Resources\p0g_art_layout.json";
            if (!File.Exists(artLayout))
            {
                Debug.LogError("[P0G] ★ 位图层数据缺失: " + artLayout +
                               "（须先跑 m2spec/gen_m2_art.py）");
                Debug.Log("[P0G] VERDICT = FAILURE");
                EditorApplication.Exit(5);
                return;
            }
            Debug.Log("[P0G] 位图层数据 = " + artLayout + " " + new FileInfo(artLayout).Length +
                      " B md5=" + Md5Of(artLayout));

            string artDir = ProjRoot + @"\Assets\Resources\p0g";
            int pngN = Directory.Exists(artDir) ? Directory.GetFiles(artDir, "*.png").Length : 0;
            Debug.Log("[P0G] 位图层纹理 = " + artDir + " 共 " + pngN + " 张");
            if (pngN == 0)
            {
                Debug.LogError("[P0G] ★ 位图层纹理为 0 张 —— 背包底图/页签必然空白，中止构建");
                Debug.Log("[P0G] VERDICT = FAILURE");
                EditorApplication.Exit(6);
                return;
            }

            // ★ 刻意**不**调用 AssetDatabase.ImportAsset(ForceUpdate) 刷新 Resources 里的 json。
            //   实测（2026-10-17，见 m2-asset-revision-1017.md §5）：只要强制重导 Resources 资产，
            //   本次构建的 UnityLinker 就会剥掉 GUISkin(class 115)，装机后 NRE 刷屏、UI 全黑。
            //   纯文本 Resources 资产由 Editor 自动导入，无需强制刷新。

            var frozenBefore = new string[Frozen.Length];
            for (int i = 0; i < Frozen.Length; i++)
            {
                frozenBefore[i] = Md5Of(Frozen[i]);
                Debug.Log("[P0G] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建前 MD5 = " + frozenBefore[i]);
            }

            P0GImport.Apply();
            P0EImport.Apply();
            P0DImport.Apply();
            P0BImport.Apply();

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AppId);
            PlayerSettings.productName = "P0GArt";
            PlayerSettings.bundleVersion = "1.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Android, ApiCompatibilityLevel.NET_Standard_2_0);

            PlayerSettings.stripEngineCode = false;
            Debug.Log("[P0G] stripEngineCode(false) —— 规避 GUISkin(class115) 被剥离导致 OnGUI 全挂" +
                      "（事故链见文件头注释；P0E 链路保持 true 不动）");

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
            catch (Exception e) { Debug.LogWarning("[P0G] splash 不可配置: " + e.Message); }

            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[P0G] 场景已保存: " + ScenePath + " (空场景, 由 P0BMapWalk 的 AfterSceneLoad 引导自建)");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            Debug.Log("[P0G] === BuildPipeline.BuildPlayer START === " + ApkPath);
            var s = BuildPipeline.BuildPlayer(opts);

            Debug.Log("[P0G] RESULT = " + s.summary.result);
            Debug.Log("[P0G] TOTAL_ERRORS = " + s.summary.totalErrors);

            bool ok = s.summary.result == BuildResult.Succeeded && File.Exists(ApkPath);
            if (ok)
            {
                Debug.Log("[P0G] APK_BYTES = " + new FileInfo(ApkPath).Length);
                Debug.Log("[P0G] APK_MD5 = " + Md5Of(ApkPath));
            }
            Debug.Log("[P0G] APK_EXISTS = " + File.Exists(ApkPath));

            for (int i = 0; i < Frozen.Length; i++)
            {
                string now = Md5Of(Frozen[i]);
                bool same = now == frozenBefore[i];
                Debug.Log("[P0G] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建后 MD5 = " + now +
                          " 未改动=" + same);
                if (!same) { Debug.LogError("[P0G] ★已交付产物被改动: " + Frozen[i]); ok = false; }
            }

            Debug.Log("[P0G] VERDICT = " + (ok ? "SUCCESS" : "FAILURE"));
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0G] BUILD EXCEPTION: " + e);
            EditorApplication.Exit(3);
        }
    }
}
