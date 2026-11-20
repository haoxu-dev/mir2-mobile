// P0-F —— 出包入口（产物 = Builds\p0f.apk）
//
// 为什么单独开一份而不是改 P0EBuild.cs：
//   p0e.apk 已交付（25,502,069 B / 2026-10-16 23:12），它的构建链路是被验收过的证据链。
//   直接改 P0EBuild 会让「p0e 是怎么出包的」失去可复现性。⇒ 复制一份，只改方法前缀、
//   产物路径、AppId 与冻结清单（冻结清单**追加** p0e.apk）。
//
// ★ 外部工具链写法、两进程阶梯、VERDICT 单空格 —— 三项全部照抄 P0EBuild（已被验证可出包）：
//   6 个 EditorPrefs 键 + 3 个 UseEmbedded=false；先跑 SetPathsOnly 再跑 BuildApk；
//   `VERDICT = SUCCESS` 必须单空格。
//
// ★ 本包唯一的内容增量：Assets/Resources/p0f_text_layout.json + Assets/Scripts/P0FText.cs
//   不新增任何纹理，故不新增 Import 步骤；P0EImport/P0DImport/P0BImport 照旧全跑，
//   否则 0039 与地图/行走图集会在本次构建里丢掉导入设置。
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P0FBuild
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ProjRoot = @"C:\Users\haoxu\ai\_scratch\_tjproj_and";
    const string ApkPath = ProjRoot + @"\Builds\p0f.apk";
    const string ScenePath = "Assets/Scenes/P0F.unity";
    const string AppId = "com.mir2.p0f";

    static readonly string[] Frozen =
    {
        ProjRoot + @"\Builds\p0a.apk",
        ProjRoot + @"\Builds\p0b.apk",
        ProjRoot + @"\Builds\p0c.apk",
        ProjRoot + @"\Builds\p0d.apk",
        ProjRoot + @"\Builds\p0e.apk",
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
    [MenuItem("P0F/0. Set External Tool Paths Only")]
    public static void SetPathsOnly()
    {
        SetPaths();
        Debug.Log("[P0F] prefs persisted (仅落偏好，不构建)");
        Debug.Log("[P0F]   AndroidSdkRoot     = " + EditorPrefs.GetString("AndroidSdkRoot") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidSdkRoot")));
        Debug.Log("[P0F]   AndroidNdkRootR23B = " + EditorPrefs.GetString("AndroidNdkRootR23B") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidNdkRootR23B")));
        Debug.Log("[P0F]   JavaHome           = " + EditorPrefs.GetString("JavaHome") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JavaHome")));
        Debug.Log("[P0F]   JdkPath            = " + EditorPrefs.GetString("JdkPath") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JdkPath")));
        bool ok = EditorPrefs.GetString("AndroidSdkRoot") == SdkRoot
                  && EditorPrefs.GetString("AndroidNdkRootR23B") == NdkRoot
                  && EditorPrefs.GetString("JavaHome") == JdkRoot
                  && EditorPrefs.GetString("JdkPath") == JdkRoot;
        Debug.Log("[P0F] PREFS_VERDICT = " + (ok ? "PASS" : "FAIL") + " (回读四值与源码常量逐字比对)");
        EditorApplication.Exit(ok ? 0 : 4);
    }

    [MenuItem("P0F/1. Build APK")]
    public static void BuildApk()
    {
        try
        {
            SetPaths();
            Debug.Log("[P0F] SDK=" + EditorPrefs.GetString("AndroidSdkRoot") + " exists=" + Directory.Exists(SdkRoot));
            Debug.Log("[P0F] NDK=" + EditorPrefs.GetString("AndroidNdkRootR23B") + " exists=" + Directory.Exists(NdkRoot));
            Debug.Log("[P0F] JDK=" + EditorPrefs.GetString("JavaHome") + " exists=" + Directory.Exists(JdkRoot));

            Debug.Log("[P0F] 几何口径[编译期常量·非实测·不可用作设备证据] 屏 2616 1140 | 逻辑 800 600 | 渲染 1600 1200" +
                      " | 主视口 1600 1140 | 侧栏 508 | 缩放 2");

            // ★ 文字层数据自检：文件不存在就该立刻失败，而不是出个"能装但什么都不画"的包
            string textLayout = ProjRoot + @"\Assets\Resources\p0f_text_layout.json";
            if (!File.Exists(textLayout))
            {
                Debug.LogError("[P0F] ★ 文字层数据缺失: " + textLayout +
                               "（须先跑 m2spec/gen_text_layout.py）");
                Debug.Log("[P0F] VERDICT = FAILURE");
                EditorApplication.Exit(5);
                return;
            }
            Debug.Log("[P0F] 文字层数据 = " + textLayout + " " + new FileInfo(textLayout).Length + " B md5=" +
                      Md5Of(textLayout));
            // ★ 刻意**不**调用 AssetDatabase.ImportAsset(ForceUpdate)。
            //   实测（2026-10-17）：只要在 BuildPlayer 之前强制重导这个 Resources 资产，
            //   本次构建的 UnityLinker 就会把 GUISkin(class 115) 剥掉，装机后
            //   GUIUtility:GetDefaultSkin -> GUI:DoSetSkin -> NullReferenceException 刷屏，
            //   [P0F]/[P0D] 一条日志都不出；而去掉这行、其余逐字不变时同一份代码构建即正常。
            //   该文件是纯文本 Resources 资产，Editor 已自动导入，无需在此强制刷新。

            var frozenBefore = new string[Frozen.Length];
            for (int i = 0; i < Frozen.Length; i++)
            {
                frozenBefore[i] = Md5Of(Frozen[i]);
                Debug.Log("[P0F] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建前 MD5 = " + frozenBefore[i]);
            }

            P0EImport.Apply();
            P0DImport.Apply();
            P0BImport.Apply();

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AppId);
            PlayerSettings.productName = "P0FText";
            PlayerSettings.bundleVersion = "1.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Android, ApiCompatibilityLevel.NET_Standard_2_0);

            // ★★★ stripEngineCode = **false**（与 P0E 口径不同，这是实测逼出来的）
            //
            //   事故链（2026-10-17，同一工程、同一份 C#、同一台模拟器，两次构建对照）：
            //     P0F 链路构建 → libtuanjie.so = 13,218,816 B → 装机后 GUISkin(class 115) 缺失
            //       → GUIUtility:GetDefaultSkin → GUI:DoSetSkin(null) → NullReferenceException
            //       刷屏 341~379 次，[P0F]/[P0D] 一条日志都不出，OnGUI 整条渲染路径失效。
            //     P0E 链路构建（同样含 P0FText 代码）→ libtuanjie.so = 13,219,424 B（多 608 B）
            //       → 同一台设备 NRE=0，[P0F] 16 行 / [P0D] 20 行 / TEXT_VERDICT=PASS。
            //   两包的 libil2cpp.so、global-metadata、内置资源哈希全部逐字节相同，
            //   唯一差异就是引擎库裁剪结果 ⇒ 是剥离器这一环把 IMGUI 的 GUISkin 裁掉了。
            //   Assets/link.xml 已写 `UnityEngine.IMGUIModule preserve="all"` 且确实进了
            //   UnityLinker 的 rsp 参数，仍然被裁 —— 该白名单在本工程未能可靠生效。
            //   ⇒ 本包显式关闭引擎代码剥离，用包体换确定性；link.xml 保留不动，
            //     日后若有人重新开启剥离，白名单仍在。
            PlayerSettings.stripEngineCode = false;
            Debug.Log("[P0F] stripEngineCode(false) —— 规避 GUISkin(class115) 被剥离导致 OnGUI 全挂" +
                      "（对照证据见 P0FBuild.cs 注释；P0E 链路保持 true 不动）");

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
            catch (Exception e) { Debug.LogWarning("[P0F] splash 不可配置: " + e.Message); }

            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[P0F] 场景已保存: " + ScenePath + " (空场景, 由 P0BMapWalk 的 AfterSceneLoad 引导自建)");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            Debug.Log("[P0F] === BuildPipeline.BuildPlayer START === " + ApkPath);
            var s = BuildPipeline.BuildPlayer(opts);

            Debug.Log("[P0F] RESULT = " + s.summary.result);
            Debug.Log("[P0F] TOTAL_ERRORS = " + s.summary.totalErrors);

            bool ok = s.summary.result == BuildResult.Succeeded && File.Exists(ApkPath);
            if (ok)
            {
                Debug.Log("[P0F] APK_BYTES = " + new FileInfo(ApkPath).Length);
                Debug.Log("[P0F] APK_MD5 = " + Md5Of(ApkPath));
            }
            Debug.Log("[P0F] APK_EXISTS = " + File.Exists(ApkPath));

            for (int i = 0; i < Frozen.Length; i++)
            {
                string now = Md5Of(Frozen[i]);
                bool same = now == frozenBefore[i];
                Debug.Log("[P0F] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建后 MD5 = " + now +
                          " 未改动=" + same);
                if (!same) { Debug.LogError("[P0F] ★已交付产物被改动: " + Frozen[i]); ok = false; }
            }

            Debug.Log("[P0F] VERDICT = " + (ok ? "SUCCESS" : "FAILURE"));
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0F] BUILD EXCEPTION: " + e);
            EditorApplication.Exit(3);
        }
    }
}
