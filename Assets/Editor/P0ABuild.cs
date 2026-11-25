// t19 / P0-A —— 出包入口（独立于 AndroidBuildProbe，产物 = Builds\p0a.apk）
// 复用 AndroidBuildProbe 已验证的外部工具链写法（6 个 EditorPrefs 键 + 3 个 UseEmbedded=false），
// 因为 AndroidBuildProbe.BuildApk() 把成功判据硬编码在 letterbox.apk 上，P0-A 不能借用它的成功判据。
// 命令行（原样）：
//   & 'C:\Program Files\Tuanjie\Hub\Editor\2022.3.62t16\Editor\Tuanjie.exe' -batchmode -nographics `
//     -projectPath '<本机>/_scratch\_tjproj_and' -executeMethod P0ABuild.BuildP0A `
//     -logFile '<本机>/_scratch\_t19\build_p0a.log' -quit
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;   // BuildReport / BuildResult / BuildSummary 都住在这里
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P0ABuild
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ApkPath = @"C:\Users\haoxu\ai\_scratch\_tjproj_and\Builds\p0a.apk";
    const string ScenePath = "Assets/Scenes/P0A.unity";
    const string AppId = "com.haoxu.p0a";

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
        Debug.Log("[P0A] paths written to EditorPrefs");
    }

    public static void DumpPaths()
    {
        Debug.Log("[P0A] SDK Pref   = " + EditorPrefs.GetString("AndroidSdkRoot", "<none>"));
        Debug.Log("[P0A] NDK Pref   = " + EditorPrefs.GetString("AndroidNdkRootR23B", "<none>"));
        Debug.Log("[P0A] JDK Pref   = " + EditorPrefs.GetString("JavaHome", "<none>"));
        Debug.Log("[P0A] exists SDK = " + Directory.Exists(SdkRoot));
        Debug.Log("[P0A] exists NDK = " + Directory.Exists(NdkRoot));
        Debug.Log("[P0A] exists JDK = " + Directory.Exists(JdkRoot));
    }

    [MenuItem("P0A/2. Build APK")]
    public static void BuildP0A()
    {
        try
        {
            SetPaths();
            DumpPaths();

            P0AImportSettings.Apply();

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AppId);
            PlayerSettings.productName = "P0AChrSel";
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
            catch (Exception e) { Debug.LogWarning("[P0A] splash screen not configurable: " + e.Message); }

            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[P0A] scene saved: " + ScenePath);

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            Debug.Log("[P0A] === BuildPipeline.BuildPlayer START === " + ApkPath);
            var s = BuildPipeline.BuildPlayer(opts);

            Debug.Log("[P0A] RESULT        = " + s.summary.result);
            Debug.Log("[P0A] TOTAL_ERRORS  = " + s.summary.totalErrors);
            Debug.Log("[P0A] TOTAL_WARNS   = " + s.summary.totalWarnings);
            Debug.Log("[P0A] OUTPUT_PATH   = " + s.summary.outputPath);
            Debug.Log("[P0A] SIZE_BYTES    = " + s.summary.totalSize);

            bool ok = s.summary.result == BuildResult.Succeeded && File.Exists(ApkPath);
            Debug.Log("[P0A] APK_EXISTS    = " + File.Exists(ApkPath));
            Debug.Log("[P0A] VERDICT       = " + (ok ? "SUCCESS" : "FAILURE"));
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0A] BUILD EXCEPTION: " + e);
            EditorApplication.Exit(3);
        }
    }
}
