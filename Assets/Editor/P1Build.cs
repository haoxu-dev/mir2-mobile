// P1 —— 出包入口：验证「真源码内核在 IL2CPP + Android 上能不能跑」。
//
// 与 P0KBuild 的关系：
//   P0KBuild 打的是「照着原版描」的 P0A+P0B 双场景（已交付、冻结）。
//   本脚本是独立的单场景包，只打 P1，用来回答一个特定问题：
//     Crystal 的 4.66 万行逻辑编成的 netstandard2.1 DLL，
//     在 Android 真机上（IL2CPP + ARM64 + 剥离）能不能加载、解析真地图、跑 A*。
//
//   它不修改、不覆盖任何已交付产物（AppId 独立、ApkPath 独立）。
//
// 用法（两进程阶梯，照抄 P0KBuild 已验证的写法）：
//   Tuanjie.exe -batchmode -quit -executeMethod P1Build.SetPathsOnly   （先落工具路径）
//   Tuanjie.exe -batchmode -quit -executeMethod P1Build.BuildApk        （再真构建）
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class P1Build
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ProjRoot = @"C:\Users\haoxu\ai\_scratch\_tjproj_and";
    const string ApkPath = ProjRoot + @"\Builds\p1core.apk";

    const string Scene = "Assets/Scenes/P1.unity";
    const string AppId = "com.mir2.p1core";

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

        // Android 扩展真正读的是 AndroidExternalToolsSettings 的 3 个静态属性
        try
        {
            var asm = System.Reflection.Assembly.Load("UnityEditor.Android.Extensions");
            var t = asm.GetType("UnityEditor.Android.AndroidExternalToolsSettings");
            if (t == null) { Debug.LogError("[P1B] ★找不到 AndroidExternalToolsSettings"); return; }
            t.GetProperty("sdkRootPath").SetValue(null, SdkRoot);
            t.GetProperty("ndkRootPath").SetValue(null, NdkRoot);
            t.GetProperty("jdkRootPath").SetValue(null, JdkRoot);
            Debug.Log("[P1B] AndroidExternalToolsSettings 已写入 sdk/ndk/jdk");
        }
        catch (Exception e) { Debug.LogError("[P1B] ★写 AndroidExternalToolsSettings 失败: " + e); }
    }

    public static void SetPathsOnly()
    {
        SetPaths();
        Debug.Log("[P1B] prefs persisted (仅落偏好，不构建)");
    }

    public static void BuildApk()
    {
        try
        {
            SetPaths();

            // 场景清单：只 P1
            var scenes = new[] { new EditorBuildSettingsScene(Scene, true) };
            EditorBuildSettings.scenes = scenes;
            Debug.Log("[P1B] EditorBuildSettings.scenes = " + EditorBuildSettings.scenes.Length);
            foreach (var s in EditorBuildSettings.scenes)
                Debug.Log("[P1B]   " + s.path + " enabled=" + s.enabled);

            if (!Directory.Exists(ProjRoot + @"\Builds")) Directory.CreateDirectory(ProjRoot + @"\Builds");

            // ★ stripEngineCode 必须 false（P0K 的事故结论，见 P0KBuild.cs 头注释）
            PlayerSettings.stripEngineCode = false;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Android, ApiCompatibilityLevel.NET_Standard_2_0);
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AppId);
            PlayerSettings.productName = "Mir2 P1Core";
            PlayerSettings.companyName = "Mir2";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.bundleVersion = "1.0";

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

            Debug.Log("[P1B] 开始构建 → " + ApkPath);
            BuildReport rep = BuildPipeline.BuildPlayer(opts);
            var sum = rep.summary;

            Debug.Log("[P1B] result=" + sum.result +
                      " totalSize=" + sum.totalSize +
                      " errors=" + sum.totalErrors + " warnings=" + sum.totalWarnings +
                      " time=" + sum.totalTime);

            string verdict = "FAIL";
            if (sum.result == BuildResult.Succeeded && File.Exists(ApkPath))
            {
                var fi = new FileInfo(ApkPath);
                string md5;
                using (var m = MD5.Create())
                using (var fs = File.OpenRead(ApkPath))
                    md5 = BitConverter.ToString(m.ComputeHash(fs)).Replace("-", "");

                Debug.Log("[P1B] APK 产物: " + ApkPath + " 大小=" + fi.Length + " 字节 md5=" + md5);
                verdict = "PASS";
            }
            else
            {
                Debug.LogError("[P1B] ★构建失败，result=" + sum.result);
            }

            Debug.Log("[P1B] VERDICT " + verdict);
            File.WriteAllText(@"C:\ai\tmp\p1_build.txt",
                "result=" + sum.result + "\n" +
                "size=" + (File.Exists(ApkPath) ? new FileInfo(ApkPath).Length.ToString() : "0") + "\n" +
                "errors=" + sum.totalErrors + "\n" +
                "VERDICT " + verdict + "\n");
        }
        catch (Exception e)
        {
            Debug.LogError("[P1B] ★异常: " + e);
            File.WriteAllText(@"C:\ai\tmp\p1_build.txt", "EXCEPTION\n" + e + "\nVERDICT FAIL\n");
        }
    }
}
