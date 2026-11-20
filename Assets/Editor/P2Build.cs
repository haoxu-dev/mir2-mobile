// P2 —— 出包入口：验证「真源码自己画的画面」能不能在手机上逐像素对上 PC 基准。
//
// 与 P1Build 的区别：
//   P1 验证的是「内核能不能跑」（手写地图 + 真 A*）。
//   P2 验证的是「画面是不是原版」（MapControl.DrawFloor/DrawObjects 原样出图）。
//
//   它不修改、不覆盖任何已交付产物（AppId 独立、ApkPath 独立）。
//
// 用法（两进程阶梯，照抄 P1Build 已验证的写法）：
//   Tuanjie.exe -batchmode -quit -executeMethod P2Build.SetPathsOnly   （先落工具路径）
//   Tuanjie.exe -batchmode -quit -executeMethod P2Build.BuildApk        （再真构建）
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class P2Build
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ProjRoot = @"C:\Users\haoxu\ai\_scratch\_tjproj_and";
    const string ApkPath = ProjRoot + @"\Builds\p2core.apk";

    const string Scene = "Assets/Scenes/P2.unity";
    const string AppId = "com.mir2.p2core";

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

        try
        {
            var asm = System.Reflection.Assembly.Load("UnityEditor.Android.Extensions");
            var t = asm.GetType("UnityEditor.Android.AndroidExternalToolsSettings");
            if (t == null) { Debug.LogError("[P2B] ★找不到 AndroidExternalToolsSettings"); return; }
            t.GetProperty("sdkRootPath").SetValue(null, SdkRoot);
            t.GetProperty("ndkRootPath").SetValue(null, NdkRoot);
            t.GetProperty("jdkRootPath").SetValue(null, JdkRoot);
            Debug.Log("[P2B] AndroidExternalToolsSettings 已写入 sdk/ndk/jdk");
        }
        catch (Exception e) { Debug.LogError("[P2B] ★写 AndroidExternalToolsSettings 失败: " + e); }
    }

    /// <summary>出包版本号（Android versionCode 必须单调递增，否则装不上/分不出新旧）。
    /// yyyyMMdd 只有 8 位且每天递增；改成 unsigned int 完全够用。</summary>
    static int BuildStamp()
    {
        return int.Parse(DateTime.Now.ToString("yyyyMMdd"));
    }

    public static void SetPathsOnly()
    {
        SetPaths();
        Debug.Log("[P2B] prefs persisted (仅落偏好，不构建)");
    }

    public static void BuildApk()
    {
        try
        {
            SetPaths();

            var scenes = new[] { new EditorBuildSettingsScene(Scene, true) };
            EditorBuildSettings.scenes = scenes;
            Debug.Log("[P2B] EditorBuildSettings.scenes = " + EditorBuildSettings.scenes.Length);
            foreach (var s in EditorBuildSettings.scenes)
                Debug.Log("[P2B]   " + s.path + " enabled=" + s.enabled);

            if (!Directory.Exists(ProjRoot + @"\Builds")) Directory.CreateDirectory(ProjRoot + @"\Builds");

            // ★ stripEngineCode 必须 false（P0K 的事故结论）
            PlayerSettings.stripEngineCode = false;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Android, ApiCompatibilityLevel.NET_Standard_2_0);
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AppId);
            PlayerSettings.productName = "Mir2 P2Core";
            PlayerSettings.companyName = "Mir2";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            // ★ 2026-10-30 改：从 ARM64 单架构改成 ARM64 + x86_64 双架构。
            //   原因（实测取证，非推测）：
            //     · 旧包只含 lib/arm64-v8a/{libil2cpp,libmain,libtuanjie,libunity}.so
            //       （用 python zipfile 列 p2core.apk 得到，共 4 个 .so，无其它 ABI）。
            //     · 模拟器 emulator-5554 的 ro.product.cpu.abi = x86_64，
            //       ro.dalvik.vm.native.bridge = libndk_translation.so，
            //       ro.hardware.egl = emulation（无 GPU）。
            //     ⇒ 整套纯 CPU 软件光栅化是在 ARM→x86 动态翻译层上跑的，
            //       这是日志里「合成 17ms↔98ms、UI绘制 95ms↔286ms」大幅抖动、
            //       以及 loadavg 6.28（4 核）的根因。
            //   加上 x86_64 后模拟器跑原生指令，逻辑帧时间才能稳定过 100ms 闸门。
            //   真机仍是 arm64，两者互不影响。
            PlayerSettings.Android.targetArchitectures =
                AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            // ★ 2026-11-18：以前 6 个包全是 versionCode=1 / versionName=1.0，
            //   装到手机上分不出新旧（系统认为同版本，覆盖安装也可能被判成同一个）。
            //   改成日期版本：versionName = 1.76.yyyyMMdd，code = yyyyMMdd（单调递增）。
            //   同一天多次出包靠时间戳区分，仍会比前一天大。
            PlayerSettings.Android.bundleVersionCode = BuildStamp();
            PlayerSettings.bundleVersion = "1.76." + DateTime.Now.ToString("yyyyMMdd");
            // StreamingAssets 里的 30MB 图库不能压缩进 APK 后再解压到内存
            PlayerSettings.Android.useAPKExpansionFiles = false;

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

            Debug.Log("[P2B] 开始构建 → " + ApkPath);
            BuildReport rep = BuildPipeline.BuildPlayer(opts);
            var sum = rep.summary;

            Debug.Log("[P2B] result=" + sum.result +
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

                Debug.Log("[P2B] APK 产物: " + ApkPath + " 大小=" + fi.Length + " 字节 md5=" + md5);
                verdict = "PASS";
            }
            else
            {
                Debug.LogError("[P2B] ★构建失败，result=" + sum.result);
            }

            Debug.Log("[P2B] VERDICT " + verdict);
            File.WriteAllText(@"C:\ai\tmp\p2_build.txt",
                "result=" + sum.result + "\n" +
                "size=" + (File.Exists(ApkPath) ? new FileInfo(ApkPath).Length.ToString() : "0") + "\n" +
                "errors=" + sum.totalErrors + "\n" +
                "VERDICT " + verdict + "\n");
        }
        catch (Exception e)
        {
            Debug.LogError("[P2B] ★异常: " + e);
            File.WriteAllText(@"C:\ai\tmp\p2_build.txt", "EXCEPTION\n" + e + "\nVERDICT FAIL\n");
        }
    }
}
