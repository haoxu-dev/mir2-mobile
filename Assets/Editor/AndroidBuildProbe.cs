using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AndroidBuildProbe
{
    public const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    public const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    public const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";
    public const string ApkPath = @"C:\Users\haoxu\ai\_scratch\_tjproj_and\Builds\probe.apk";
    public const string ApkPathDual = @"C:\Users\haoxu\ai\_scratch\_tjproj_and\Builds\letterbox.apk";

    public static void SetPaths()
    {
        // Unity/Tuanjie reads these EditorPrefs keys for external tool locations.
        EditorPrefs.SetString("AndroidSdkRoot", SdkRoot);
        EditorPrefs.SetString("AndroidNdkRootR23B", NdkRoot);
        EditorPrefs.SetString("AndroidNdkRootDir", NdkRoot);
        EditorPrefs.SetString("AndroidNdkRoot", NdkRoot);
        EditorPrefs.SetString("JavaHome", JdkRoot);
        EditorPrefs.SetString("JdkPath", JdkRoot);
        EditorPrefs.SetBool("SdkUseEmbedded", false);
        EditorPrefs.SetBool("NdkUseEmbedded", false);
        EditorPrefs.SetBool("JdkUseEmbedded", false);
        Debug.Log("[PROBE] paths written to EditorPrefs");
    }

    public static void DumpPaths()
    {
        Debug.Log("[PROBE] AndroidSdkRoot      = " + EditorPrefs.GetString("AndroidSdkRoot", "<unset>"));
        Debug.Log("[PROBE] AndroidNdkRootR23B  = " + EditorPrefs.GetString("AndroidNdkRootR23B", "<unset>"));
        Debug.Log("[PROBE] JavaHome            = " + EditorPrefs.GetString("JavaHome", "<unset>"));
        Debug.Log("[PROBE] ANDROID_SDK_ROOT env = " + (Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? "<null>"));
        Debug.Log("[PROBE] ANDROID_NDK_ROOT env = " + (Environment.GetEnvironmentVariable("ANDROID_NDK_ROOT") ?? "<null>"));
        Debug.Log("[PROBE] JAVA_HOME env        = " + (Environment.GetEnvironmentVariable("JAVA_HOME") ?? "<null>"));
        Debug.Log("[PROBE] SDK dir exists = " + Directory.Exists(SdkRoot));
        Debug.Log("[PROBE] NDK dir exists = " + Directory.Exists(NdkRoot));
        Debug.Log("[PROBE] JDK dir exists = " + Directory.Exists(JdkRoot));
    }

    public static void BuildApk()
    {
        try
        {
            SetPaths();
            DumpPaths();

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.haoxu.probe");
            PlayerSettings.productName = "Probe";
            PlayerSettings.bundleVersion = "1.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            // 双架构：ARM64 给真机，x86_64 给本机模拟器（QEMU 不支持 x86 宿主跑 arm64）
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Android, ApiCompatibilityLevel.NET_Standard_2_0);

            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Main.unity");
            Debug.Log("[PROBE] scene saved");

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPathDual));

            // P0 口径：只允许横屏（外屏横屏为准）
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.Android.renderOutsideSafeArea = true;
            Debug.Log("[PROBE] orientation forced = Landscape");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Main.unity" },
                locationPathName = ApkPathDual,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Debug.Log("[PROBE] === BuildPipeline.BuildPlayer START ===");
            BuildReport report = BuildPipeline.BuildPlayer(opts);
            BuildSummary s = report.summary;

            Debug.Log("[PROBE] RESULT      = " + s.result);
            Debug.Log("[PROBE] TOTAL_ERRORS= " + s.totalErrors);
            Debug.Log("[PROBE] TOTAL_WARNS = " + s.totalWarnings);
            Debug.Log("[PROBE] OUTPUT_PATH = " + s.outputPath);
            Debug.Log("[PROBE] SIZE_BYTES  = " + s.totalSize);
            Debug.Log("[PROBE] DURATION_S  = " + s.totalTime.TotalSeconds.ToString("F1"));

            bool ok = s.result == BuildResult.Succeeded && File.Exists(ApkPathDual);
            Debug.Log("[PROBE] APK_EXISTS  = " + File.Exists(ApkPathDual));
            Debug.Log(ok ? "[PROBE] VERDICT=SUCCESS" : "[PROBE] VERDICT=FAILURE");
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            Debug.LogError("[PROBE] EXCEPTION: " + ex);
            EditorApplication.Exit(3);
        }
    }
}
