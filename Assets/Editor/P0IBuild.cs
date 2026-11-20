// P0-I —— 出包入口（产物 = Builds\p0i.apk）
//
// 为什么另开一份而不改 P0HBuild.cs：
//   p0h.apk 已交付（57,035,885 B），它的链路是「P0-H 实体层/战况如何出包」的证据。
//   改 P0HBuild 会让那份证据失去可复现性 ⇒ 复制一份，只改方法前缀、产物路径、
//   以及冻结清单**追加 p0h.apk**。
//
// ★ 外部工具链写法、两进程阶梯、VERDICT 单空格 —— 三项照抄 P0HBuild（已被验证可出包）：
//   6 个 EditorPrefs 键 + 3 个 UseEmbedded=false；先跑 SetPathsOnly 再跑 BuildApk。
//
// ★ JDK 必须是 17，且必须**覆盖**机器级 JAVA_HOME 指向的 jdk21：
//   JavaHome / JdkPath 两个键都显式钉到 jdk-17.0.17.10-hotspot，并置 JdkUseEmbedded=false。
//
// ★ 本包的内容增量（相对 p0h）：
//   1) Assets/Scripts/P0IIme.cs         —— 原生输入法层（IMGUI TextField + TouchScreenKeyboard）
//   2) Assets/Scripts/P0BMapWalk.cs     —— 已接线：L444 字段 / L1127-1132 DrawAll / L1136-1140 OnChatSend
//   该层**没有**独立 Resources 资产 ⇒ 不需要新的 Import 脚本，沿用 P0HImport/P0EImport/P0DImport/P0BImport。
//
// ★★ stripEngineCode 必须保持 **false**（不是可选优化，是事故结论）：
//   stripEngineCode=true 会把 UnityEngine.IMGUIModule 的 GUISkin(class 115) 整类剥掉，
//   装机后 GUIUtility:GetDefaultSkin → GUI:DoSetSkin(null) → NullReferenceException 刷屏，
//   OnGUI 整条渲染路径失效。P0-I 全部走 IMGUI（GUI.TextField 依赖 IMGUI 文本编辑），
//   是本事故的**正中心** ⇒ 必须关闭剥离。
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class P0IBuild
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ProjRoot = @"C:\Users\haoxu\ai\_scratch\_tjproj_and";
    const string ApkPath = ProjRoot + @"\Builds\p0i.apk";
    const string ScenePath = "Assets/Scenes/P0I.unity";
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
        ProjRoot + @"\Builds\p0h.apk",
    };

    static string Md5Of(string p)
    {
        if (!File.Exists(p)) return "(不存在)";
        using (var md5 = MD5.Create())
        using (var fs = File.OpenRead(p))
            return BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "");
    }

    /// <summary>
    /// 从产物 APK 里 dump 二进制清单（aapt2 dump xmltree 或 aapt dump xmltree）。
    /// 返回 null = 工具不可用（只告警，不单独判失败；上层按「找不到 windowSoftInputMode」处理）。
    /// </summary>
    static string DumpManifestFromApk(string apk)
    {
        try
        {
            var cands = new System.Collections.Generic.List<string>();
            string bt = SdkRoot + @"\build-tools";
            if (Directory.Exists(bt))
            {
                var dirs = Directory.GetDirectories(bt);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = dirs.Length - 1; i >= 0; i--)   // 新版优先
                {
                    cands.Add(Path.Combine(dirs[i], "aapt2.exe"));
                    cands.Add(Path.Combine(dirs[i], "aapt.exe"));
                }
            }
            foreach (var tool in cands)
            {
                if (!File.Exists(tool)) continue;
                bool isAapt2 = tool.EndsWith("aapt2.exe", StringComparison.OrdinalIgnoreCase);
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = isAapt2 ? ("dump xmltree \"" + apk + "\" --file AndroidManifest.xml")
                                        : ("dump xmltree \"" + apk + "\" AndroidManifest.xml"),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e2 = p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    Debug.Log("[P0I] 清单 dump 工具 = " + tool + " exit=" + p.ExitCode + " 长=" + o.Length);
                    if (!string.IsNullOrEmpty(e2)) Debug.Log("[P0I] 清单 dump stderr: " + e2.Trim());
                    if (o.Length > 0) return o;
                }
            }
            Debug.LogWarning("[P0I] 找不到可用的 aapt/aapt2，跳过产物清单回验");
            return null;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[P0I] 清单 dump 异常（已吞）: " + e.Message);
            return null;
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

        // ★★ 2026-10-22 关键修复：以上 6 个 EditorPrefs 键 **不是** Android 扩展真正的读取源。
        //   Android 构建读的是 `UnityEditor.Android.AndroidExternalToolsSettings`
        //   的 sdkRootPath / ndkRootPath / jdkRootPath（已反射该 dll 确认存在这 3 个属性）。
        //   只写 EditorPrefs ⇒ 构建时抛 `UnityException: JDK not found`
        //   （实测 p0i_build9.log，即便同进程先调 SetPaths 也不行）。
        //   这里用反射写真正的源，避免在 asmdef 之外直接引用 Android 扩展程序集。
        try
        {
            var asm = System.Reflection.Assembly.Load("UnityEditor.Android.Extensions");
            var t = asm.GetType("UnityEditor.Android.AndroidExternalToolsSettings");
            if (t == null) { Debug.LogError("[P0I] ★找不到 AndroidExternalToolsSettings"); return; }
            t.GetProperty("sdkRootPath").SetValue(null, SdkRoot);
            t.GetProperty("ndkRootPath").SetValue(null, NdkRoot);
            t.GetProperty("jdkRootPath").SetValue(null, JdkRoot);
            Debug.Log("[P0I] AndroidExternalToolsSettings 已写入: sdk=" + t.GetProperty("sdkRootPath").GetValue(null) +
                      " ndk=" + t.GetProperty("ndkRootPath").GetValue(null) +
                      " jdk=" + t.GetProperty("jdkRootPath").GetValue(null));
        }
        catch (Exception e)
        {
            Debug.LogError("[P0I] ★写 AndroidExternalToolsSettings 失败: " + e);
        }
    }

    /// <summary>只落偏好、不构建。必须单独一个进程先跑它（Android 扩展在加载期缓存工具路径）。</summary>
    [MenuItem("P0I/0. Set External Tool Paths Only")]
    public static void SetPathsOnly()
    {
        SetPaths();
        Debug.Log("[P0I] prefs persisted (仅落偏好，不构建)");
        Debug.Log("[P0I]   AndroidSdkRoot     = " + EditorPrefs.GetString("AndroidSdkRoot") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidSdkRoot")));
        Debug.Log("[P0I]   AndroidNdkRootR23B = " + EditorPrefs.GetString("AndroidNdkRootR23B") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidNdkRootR23B")));
        Debug.Log("[P0I]   JavaHome           = " + EditorPrefs.GetString("JavaHome") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JavaHome")));
        Debug.Log("[P0I]   JdkPath            = " + EditorPrefs.GetString("JdkPath") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JdkPath")));
        bool ok = EditorPrefs.GetString("AndroidSdkRoot") == SdkRoot
                  && EditorPrefs.GetString("AndroidNdkRootR23B") == NdkRoot
                  && EditorPrefs.GetString("JavaHome") == JdkRoot
                  && EditorPrefs.GetString("JdkPath") == JdkRoot;
        Debug.Log("[P0I] PREFS_VERDICT = " + (ok ? "PASS" : "FAIL") + " (回读四值与源码常量逐字比对)");
        EditorApplication.Exit(ok ? 0 : 4);
    }

    /// <summary>
    /// ★ 2026-10-22 新增：单进程同时落偏好 + 构建。
    /// 背景：`-batchmode` 下 `SetPathsOnly` 以 `EditorApplication.Exit(0)` 早退，
    /// EditorPrefs 未 flush 到注册表；随后起第二个进程跑 `BuildApk` 时，
    /// Android 扩展在**程序集加载期**就缓存了工具路径 ⇒ 抛
    /// `UnityException: JDK not found`（实测 p0i_build4b.log:1241）。
    /// 因此把两步合成一个 executeMethod，保证 SetPaths() 与 BuildPipeline 同进程。
    /// </summary>
    [MenuItem("P0I/2. Set Paths + Build APK")]
    public static void SetPathsAndBuild()
    {
        SetPaths();
        Debug.Log("[P0I] SetPathsAndBuild: prefs 已落(A) > 立即构建");
        BuildApk();
    }

    [MenuItem("P0I/1. Build APK")]
    public static void BuildApk()
    {
        try
        {
            SetPaths();
            Debug.Log("[P0I] SDK=" + EditorPrefs.GetString("AndroidSdkRoot") + " exists=" + Directory.Exists(SdkRoot));
            Debug.Log("[P0I] NDK=" + EditorPrefs.GetString("AndroidNdkRootR23B") + " exists=" + Directory.Exists(NdkRoot));
            Debug.Log("[P0I] JDK=" + EditorPrefs.GetString("JavaHome") + " exists=" + Directory.Exists(JdkRoot));

            Debug.Log("[P0I] 几何口径[编译期常量·非实测·不可用作设备证据] 屏 2616 1140 | 逻辑 800 600 | 渲染 1600 1200" +
                      " | 主视口 1600 1140 | 侧栏 508 | 缩放 2");

            // ★ P0-I 自检：输入法层源码必须在，且必须已被 P0BMapWalk 接线。
            //   缺一个就立刻失败，而不是出个"能装但没有输入面板"的包。
            string imePath = ProjRoot + @"\Assets\Scripts\P0IIme.cs";
            if (!File.Exists(imePath))
            {
                Debug.LogError("[P0I] ★ 输入法层源码缺失: " + imePath);
                Debug.Log("[P0I] VERDICT = FAILURE");
                EditorApplication.Exit(5);
                return;
            }
            Debug.Log("[P0I] 输入法层源码 = " + imePath + " " + new FileInfo(imePath).Length +
                      " B md5=" + Md5Of(imePath));

            string walkPath = ProjRoot + @"\Assets\Scripts\P0BMapWalk.cs";
            if (!File.Exists(walkPath))
            {
                Debug.LogError("[P0I] ★ 接线文件缺失: " + walkPath);
                Debug.Log("[P0I] VERDICT = FAILURE");
                EditorApplication.Exit(5);
                return;
            }
            string walkSrc = File.ReadAllText(walkPath);
            bool wired = walkSrc.Contains("P0IIme _ime;") && walkSrc.Contains("_ime.DrawAll()");
            Debug.Log("[P0I] 接线检查 P0BMapWalk.cs 含 P0IIme 字段=" + walkSrc.Contains("P0IIme _ime;") +
                      " 含 DrawAll=" + walkSrc.Contains("_ime.DrawAll()") + " md5=" + Md5Of(walkPath));
            if (!wired)
            {
                Debug.LogError("[P0I] ★ 输入法层未接线（P0IIme 字段或 DrawAll 缺失）—— 出包也不会有面板，中止");
                Debug.Log("[P0I] VERDICT = FAILURE");
                EditorApplication.Exit(7);
                return;
            }

            // ★ 怪物位图层数据自检（沿用 P0H：缺了怪物层会空白，但不是本层重点，只告警不中止）
            string monIndex = ProjRoot + @"\Assets\Resources\p0h\mon_index.txt";
            Debug.Log("[P0I] 怪物位图层索引 = " + monIndex + " exists=" + File.Exists(monIndex));

            // ★ 刻意**不**调用 AssetDatabase.ImportAsset(ForceUpdate) 刷新 Resources 里的纯文本资产。
            //   实测（2026-10-17，见 m2-asset-revision-1017.md §5）：只要强制重导 Resources 资产，
            //   本次构建的 UnityLinker 就会剥掉 GUISkin(class 115)，装机后 NRE 刷屏、UI 全黑。
            var frozenBefore = new string[Frozen.Length];
            for (int i = 0; i < Frozen.Length; i++)
            {
                frozenBefore[i] = Md5Of(Frozen[i]);
                Debug.Log("[P0I] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建前 MD5 = " + frozenBefore[i]);
            }

            // ★★ P0-I 缺陷修复自检：系统输入法遮挡面板（2026-10-22）。
            //   根因：合并后清单的 <activity> 无 windowSoftInputMode ⇒ 安卓默认 adjustPan，
            //         键盘弹起只推窗口不缩高度，Screen.height 仍 1140，面板贴底 ⇒ 整块被键盘盖住。
            //   修法：自定义主清单里写 adjustResize，让 Screen.height 随键盘缩，贴底公式自动避开。
            //   ★ 必须是「主清单」：activity 声明在 tuanjieLibrary（主清单侧），
            //     写进 launcher 清单对 activity 无效，会静默不生效。
            string manifestPath = ProjRoot + @"\Assets\Plugins\Android\AndroidManifest.xml";
            bool manOk = File.Exists(manifestPath);
            if (!manOk)
            {
                Debug.LogError("[P0I] ★ 自定义主清单缺失: " + manifestPath + " —— 输入法会再次盖住面板，中止");
                Debug.Log("[P0I] VERDICT = FAILURE");
                EditorApplication.Exit(8);
                return;
            }
            string manSrc = File.ReadAllText(manifestPath);
            bool hasResize = manSrc.Contains("android:windowSoftInputMode=\"adjustResize\"");
            Debug.Log("[P0I] 自定义主清单 = " + manifestPath + " " + new FileInfo(manifestPath).Length +
                      " B md5=" + Md5Of(manifestPath) + " 含adjustResize=" + hasResize);
            if (!hasResize)
            {
                Debug.LogError("[P0I] ★ 主清单缺 android:windowSoftInputMode=\"adjustResize\" —— 中止（否则白出包）");
                Debug.Log("[P0I] VERDICT = FAILURE");
                EditorApplication.Exit(9);
                return;
            }
            // useCustomMainManifest 必须为真，否则上面的文件根本不会被采用
            var projSettingsPath = ProjRoot + @"\ProjectSettings\ProjectSettings.asset";
            string projSettings = File.Exists(projSettingsPath) ? File.ReadAllText(projSettingsPath) : string.Empty;
            bool flagOn = projSettings.Contains("useCustomMainManifest: 1");
            Debug.Log("[P0I] ProjectSettings.useCustomMainManifest=1 ? " + flagOn);
            if (!flagOn)
            {
                Debug.LogError("[P0I] ★ useCustomMainManifest 未开启 —— 自定义清单不会进构建，中止");
                Debug.Log("[P0I] VERDICT = FAILURE");
                EditorApplication.Exit(10);
                return;
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
            Debug.Log("[P0I] stripEngineCode(false) —— 规避 GUISkin(class115) 被剥离导致 OnGUI 全挂" +
                      "（P0-I 全走 IMGUI，是本事故正中心）");

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
            catch (Exception e) { Debug.LogWarning("[P0I] splash 不可配置: " + e.Message); }

            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[P0I] 场景已保存: " + ScenePath + " (空场景, 由 P0BMapWalk 的 AfterSceneLoad 引导自建)");

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            Debug.Log("[P0I] === BuildPipeline.BuildPlayer START === " + ApkPath);
            var s = BuildPipeline.BuildPlayer(opts);

            Debug.Log("[P0I] RESULT = " + s.summary.result);
            Debug.Log("[P0I] TOTAL_ERRORS = " + s.summary.totalErrors);

            bool ok = s.summary.result == BuildResult.Succeeded && File.Exists(ApkPath);
            if (ok)
            {
                Debug.Log("[P0I] APK_BYTES = " + new FileInfo(ApkPath).Length);
                Debug.Log("[P0I] APK_MD5 = " + Md5Of(ApkPath));

                // ★★ 出包后回验：从**产物 APK** 里把清单抠出来查 adjustResize。
                //   只验源文件在不在没用 —— 已被「写了文件但没被采用」坑过一次，
                //   必须验最终字节，否则等于自欺。
                string dump = DumpManifestFromApk(ApkPath);
                bool apkResize = dump != null && dump.Contains("windowSoftInputMode");
                Debug.Log("[P0I] 产物清单回验 windowSoftInputMode 出现=" + apkResize);
                if (dump != null)
                {
                    foreach (var ln in dump.Split('\n'))
                    {
                        string t2 = ln.Trim();
                        if (t2.Contains("softInput") || t2.Contains("windowSoftInput"))
                            Debug.Log("[P0I] 产物清单节点: " + t2);
                    }
                }
                if (!apkResize)
                {
                    Debug.LogError("[P0I] ★ 产物APK清单里没有 windowSoftInputMode —— adjustResize 没进包，判失败");
                    ok = false;
                }
            }
            Debug.Log("[P0I] APK_EXISTS = " + File.Exists(ApkPath));

            for (int i = 0; i < Frozen.Length; i++)
            {
                string now = Md5Of(Frozen[i]);
                bool same = now == frozenBefore[i];
                Debug.Log("[P0I] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建后 MD5 = " + now +
                          " 未改动=" + same);
                if (!same) { Debug.LogError("[P0I] ★已交付产物被改动: " + Frozen[i]); ok = false; }
            }

            Debug.Log("[P0I] VERDICT = " + (ok ? "SUCCESS" : "FAILURE"));
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0I] BUILD EXCEPTION: " + e);
            EditorApplication.Exit(3);
        }
    }
}
