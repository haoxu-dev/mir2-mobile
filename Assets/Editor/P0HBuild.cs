// P0-H —— 出包入口（产物 = Builds\p0h.apk）
//
// 为什么另开一份而不改 P0GBuild.cs：
//   p0g.apk 已交付（43,860,819 B），它的链路是「P0-G 背包位图层如何出包」的证据。
//   改 P0GBuild 会让那份证据失去可复现性 ⇒ 复制一份，只改方法前缀、产物路径、场景路径、
//   以及冻结清单**追加 p0g.apk**。
//
// ★ 外部工具链写法、两进程阶梯、VERDICT 单空格 —— 三项照抄 P0GBuild（已被验证可出包）：
//   6 个 EditorPrefs 键 + 3 个 UseEmbedded=false；先跑 SetPathsOnly 再跑 BuildApk。
//
// ★ JDK 必须是 17，且必须**覆盖**机器级 JAVA_HOME 指向的 jdk21：
//   JavaHome / JdkPath 两个键都显式钉到 jdk-17.0.17.10-hotspot，并置 JdkUseEmbedded=false。
//   这一步是「Unity 用自己缓存的 jdk21 起 gradle → 出包失败」的直接规避，不许改成跟随环境变量。
//
// ★ 本包的内容增量（相对 p0g）：
//   1) Assets/Resources/p0h/            —— 怪物图集 mon_atlas.png + 索引 mon_index.txt
//   2) Assets/Editor/P0HImport.cs       —— 新目录的导入设置（必须跑，否则纹理会被压/被 NPOT 缩放）
//   其余（P0EImport/P0DImport/P0BImport）照旧全跑，否则 0039 与地图/行走图集会丢导入设置。
//
// ★★ stripEngineCode 必须保持 **false**（不是可选优化，是事故结论）：
//   本工程 Tuanjie 2022.3.62t16 构建链上，stripEngineCode=true 会把
//   UnityEngine.IMGUIModule 的 GUISkin(class 115) 整类剥掉，`link.xml` 白名单未能可靠生效；
//   装机后 GUIUtility:GetDefaultSkin → GUI:DoSetSkin(null) → NullReferenceException 刷屏，
//   OnGUI 整条渲染路径失效（[P0D]/[P0F]/[P0G] 一条日志都不出）。
//   P0-H 沿用同一套 GUI.DrawTexture 渲染路径，属于同一受害面 ⇒ 必须关闭剥离。代价是包体约 43MB。
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P0HBuild
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ProjRoot = @"C:\Users\haoxu\ai\_scratch\_tjproj_and";
    const string ApkPath = ProjRoot + @"\Builds\p0h.apk";
    const string ScenePath = "Assets/Scenes/P0H.unity";
    const string AppId = "com.mir2.p0g";

    static readonly string[] Frozen =
    {
        ProjRoot + @"\Builds\p0a.apk",
        ProjRoot + @"\Builds\p0b.apk",
        ProjRoot + @"\Builds\p0c.apk",
        ProjRoot + @"\Builds\p0d.apk",
        ProjRoot + @"\Builds\p0e.apk",
        ProjRoot + @"\Builds\p0f.apk",
        ProjRoot + @"\Builds\p0g.apk",
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
    [MenuItem("P0H/0. Set External Tool Paths Only")]
    public static void SetPathsOnly()
    {
        SetPaths();
        Debug.Log("[P0H] prefs persisted (仅落偏好，不构建)");
        Debug.Log("[P0H]   AndroidSdkRoot     = " + EditorPrefs.GetString("AndroidSdkRoot") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidSdkRoot")));
        Debug.Log("[P0H]   AndroidNdkRootR23B = " + EditorPrefs.GetString("AndroidNdkRootR23B") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidNdkRootR23B")));
        Debug.Log("[P0H]   JavaHome           = " + EditorPrefs.GetString("JavaHome") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JavaHome")));
        Debug.Log("[P0H]   JdkPath            = " + EditorPrefs.GetString("JdkPath") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JdkPath")));
        bool ok = EditorPrefs.GetString("AndroidSdkRoot") == SdkRoot
                  && EditorPrefs.GetString("AndroidNdkRootR23B") == NdkRoot
                  && EditorPrefs.GetString("JavaHome") == JdkRoot
                  && EditorPrefs.GetString("JdkPath") == JdkRoot;
        Debug.Log("[P0H] PREFS_VERDICT = " + (ok ? "PASS" : "FAIL") + " (回读四值与源码常量逐字比对)");
        EditorApplication.Exit(ok ? 0 : 4);
    }

    [MenuItem("P0H/1. Build APK")]
    public static void BuildApk()
    {
        try
        {
            SetPaths();
            Debug.Log("[P0H] SDK=" + EditorPrefs.GetString("AndroidSdkRoot") + " exists=" + Directory.Exists(SdkRoot));
            Debug.Log("[P0H] NDK=" + EditorPrefs.GetString("AndroidNdkRootR23B") + " exists=" + Directory.Exists(NdkRoot));
            Debug.Log("[P0H] JDK=" + EditorPrefs.GetString("JavaHome") + " exists=" + Directory.Exists(JdkRoot));

            Debug.Log("[P0H] 几何口径[编译期常量·非实测·不可用作设备证据] 屏 2616 1140 | 逻辑 800 600 | 渲染 1600 1200" +
                      " | 主视口 1600 1140 | 侧栏 508 | 缩放 2");

            // ★ 怪物位图层数据自检：缺一个就该立刻失败，而不是出个"能装但怪物层空白"的包。
            //   与 P0GBuild 的唯一差异在这里：p0h 的数据文件不是 *_art_layout.json，
            //   而是 mon_index.txt（图集分块索引）+ mon_atlas.png（图集本体）。
            string monIndex = ProjRoot + @"\Assets\Resources\p0h\mon_index.txt";
            if (!File.Exists(monIndex))
            {
                Debug.LogError("[P0H] ★ 怪物位图层索引缺失: " + monIndex +
                               "（须先生成 p0h 的图集索引与 mon_atlas.png）");
                Debug.Log("[P0H] VERDICT = FAILURE");
                EditorApplication.Exit(5);
                return;
            }
            Debug.Log("[P0H] 怪物位图层索引 = " + monIndex + " " + new FileInfo(monIndex).Length +
                      " B md5=" + Md5Of(monIndex));

            string artDir = ProjRoot + @"\Assets\Resources\p0h";
            int pngN = Directory.Exists(artDir) ? Directory.GetFiles(artDir, "*.png").Length : 0;
            Debug.Log("[P0H] 怪物位图层纹理 = " + artDir + " 共 " + pngN + " 张");
            if (pngN == 0)
            {
                Debug.LogError("[P0H] ★ 怪物位图层纹理为 0 张 —— 怪物图集必然空白，中止构建");
                Debug.Log("[P0H] VERDICT = FAILURE");
                EditorApplication.Exit(6);
                return;
            }

            // ★ 刻意**不**调用 AssetDatabase.ImportAsset(ForceUpdate) 刷新 Resources 里的纯文本资产。
            //   实测（2026-10-17，见 m2-asset-revision-1017.md §5）：只要强制重导 Resources 资产，
            //   本次构建的 UnityLinker 就会剥掉 GUISkin(class 115)，装机后 NRE 刷屏、UI 全黑。
            //   纯文本 Resources 资产由 Editor 自动导入，无需强制刷新。

            var frozenBefore = new string[Frozen.Length];
            for (int i = 0; i < Frozen.Length; i++)
            {
                frozenBefore[i] = Md5Of(Frozen[i]);
                Debug.Log("[P0H] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建前 MD5 = " + frozenBefore[i]);
            }

            P0HImport.Apply();
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
            Debug.Log("[P0H] stripEngineCode(false) —— 规避 GUISkin(class115) 被剥离导致 OnGUI 全挂" +
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
            catch (Exception e) { Debug.LogWarning("[P0H] splash 不可配置: " + e.Message); }

            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[P0H] 场景已保存: " + ScenePath + " (空场景, 由 P0BMapWalk 的 AfterSceneLoad 引导自建)");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            Debug.Log("[P0H] === BuildPipeline.BuildPlayer START === " + ApkPath);
            var s = BuildPipeline.BuildPlayer(opts);

            Debug.Log("[P0H] RESULT = " + s.summary.result);
            Debug.Log("[P0H] TOTAL_ERRORS = " + s.summary.totalErrors);

            bool ok = s.summary.result == BuildResult.Succeeded && File.Exists(ApkPath);
            if (ok)
            {
                Debug.Log("[P0H] APK_BYTES = " + new FileInfo(ApkPath).Length);
                Debug.Log("[P0H] APK_MD5 = " + Md5Of(ApkPath));
            }
            Debug.Log("[P0H] APK_EXISTS = " + File.Exists(ApkPath));

            for (int i = 0; i < Frozen.Length; i++)
            {
                string now = Md5Of(Frozen[i]);
                bool same = now == frozenBefore[i];
                Debug.Log("[P0H] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建后 MD5 = " + now +
                          " 未改动=" + same);
                if (!same) { Debug.LogError("[P0H] ★已交付产物被改动: " + Frozen[i]); ok = false; }
            }

            Debug.Log("[P0H] VERDICT = " + (ok ? "SUCCESS" : "FAILURE"));
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0H] BUILD EXCEPTION: " + e);
            EditorApplication.Exit(3);
        }
    }
}
