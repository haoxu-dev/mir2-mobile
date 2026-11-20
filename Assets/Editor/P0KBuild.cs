// P0-K —— 出包入口（产物 = Builds\p0k.apk）· 双场景 P0A -> P0B
//
// 为什么另开一份而不改 P0JBuild.cs：
//   p0j.apk 已交付，它的链路是「P0-J 三层联动」的证据。
//   改 P0JBuild 会让那份证据失去可复现性 ⇒ 复制一份，只改类名前缀、产物路径、
//   场景清单（单场景 -> 双场景）、以及冻结清单**追加 p0j.apk**（p0a..p0h 全部保持冻结）。
//
// ★★★ 本包的内容增量（相对 p0j）—— 用户 2026-10-24 指出的病灶：
//   「P0A 人物选择界面」和「P0B 地图」现在是两个孤立场景，
//   p0j.apk 只把 P0B 打进包，P0A 那张人物选择界面根本不在包里 ⇒ 装机后看不到选人。
//   ⇒ 本次把两个场景**一起**打进同一个 APK，且 P0A 在前（启动场景）、P0B 在后：
//       index 0 = Assets/Scenes/P0A.unity   ← 冷启动进这里（选人）
//       index 1 = Assets/Scenes/P0B.unity   ← SceneManager.LoadSceneAsync("P0B") 的目标
//   ★ 顺序不可颠倒：BuildPlayerOptions.scenes 的第 0 个就是启动场景。
//     颠倒 = 冷启动直接进地图，选人界面永远看不到。
//   ★ 必须在 BuildPlayer 之前把 EditorBuildSettings.scenes 也写成这两条且 enabled=true：
//     `SceneManager.LoadSceneAsync("P0B")` 只认「构建场景清单里通过校验的场景」。
//     实测症状：清单为空 / P0B 未加入 ⇒ 运行期报
//     `Scene 'P0B' couldn't be loaded because it has not been added to the build settings`，
//     LoadSceneAsync 返回 null，界面卡死在选人页。
//     ⇒ 本脚本先写 EditorBuildSettings、**回读并打印实际内容**（P0K BUILDSCENES），再 BuildPlayer。
//
// ★ 外部工具链写法、两进程阶梯、VERDICT 单空格 —— 三项照抄 P0JBuild（已被验证可出包）：
//   6 个 EditorPrefs 键 + 3 个 UseEmbedded=false；先跑 SetPathsOnly 再跑 BuildApk。
//
// ★ JDK 必须是 17，且必须**覆盖**机器级 JAVA_HOME 指向的 jdk21：
//   JavaHome / JdkPath 两个键都显式钉到 jdk-17.0.17.10-hotspot，并置 JdkUseEmbedded=false。
//
// ★ 素材导入脚本：沿用 P0HImport/P0EImport/P0DImport/P0BImport，
//   **新增 P0AImportSettings.Apply()**（P0-A 的 33 张 p0a 素材：Sprite/Point/None/无 mipmap/FullRect）。
//   少调它 ⇒ 图片按默认 Bilinear + Tight 导入 ⇒ 像素级 1:1 直接废掉，且元素整体位移。
//
// ★★ stripEngineCode 必须保持 **false**（不是可选优化，是事故结论）：
//   stripEngineCode=true 会把 UnityEngine.IMGUIModule 的 GUISkin(class 115) 整类剥掉，
//   装机后 GUIUtility:GetDefaultSkin → GUI:DoSetSkin(null) → NullReferenceException 刷屏，
//   OnGUI 整条渲染路径失效。P0-A/P0-B 两层全部走 IMGUI，是本事故的**正中心** ⇒ 必须关闭剥离。
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class P0KBuild
{
    const string SdkRoot = @"C:\Users\haoxu\android-sdk";
    const string NdkRoot = @"C:\Users\haoxu\android-sdk\ndk\23.1.7779620";
    const string JdkRoot = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot";

    const string ProjRoot = @"C:\Users\haoxu\ai\_scratch\_tjproj_and";
    const string ApkPath = ProjRoot + @"\Builds\p0k.apk";

    // ★ 双场景：index 0 = 启动场景（选人），index 1 = 进图目标。顺序不可颠倒。
    const string SceneA = "Assets/Scenes/P0A.unity";
    const string SceneB = "Assets/Scenes/P0B.unity";

    const string AppId = "com.mir2.p0k";

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
        ProjRoot + @"\Builds\p0j.apk",   // ★ P0-K 新增：p0j.apk 已交付，纳入冻结
    };

    /// <summary>文件不存在时的占位值。★ 必须与 Md5Of 的返回值逐字一致（判 ABSENT 靠它比对）。</summary>
    const string AbsentMark = "(不存在)";

    static string Md5Of(string p)
    {
        if (!File.Exists(p)) return AbsentMark;
        using (var md5 = MD5.Create())
        using (var fs = File.OpenRead(p))
            return BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "");
    }

    /// <summary>
    /// 字节流里是否含某段 ASCII（用于在 APK 内查场景名/zip 条目名，不解包）。
    /// 分块 ASCII 解码后走 Ordinal 查找 —— 比逐字节朴素扫描快一个量级
    /// （57MB 的包朴扫 4 次要几十秒，会白占构建后的主线程）。
    /// </summary>
    static bool BytesContainAscii(byte[] hay, string needle)
    {
        if (hay == null || hay.Length == 0 || string.IsNullOrEmpty(needle)) return false;
        const int Chunk = 4 * 1024 * 1024;
        int overlap = needle.Length - 1;
        for (int off = 0; off < hay.Length; off += Chunk)
        {
            int len = Math.Min(Chunk + overlap, hay.Length - off);
            string s = System.Text.Encoding.ASCII.GetString(hay, off, len);
            if (s.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
        }
        return false;
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

                    Debug.Log("[P0K] 清单 dump 工具 = " + tool + " exit=" + p.ExitCode + " 长=" + o.Length);
                    if (!string.IsNullOrEmpty(e2)) Debug.Log("[P0K] 清单 dump stderr: " + e2.Trim());
                    if (o.Length > 0) return o;
                }
            }
            Debug.LogWarning("[P0K] 找不到可用的 aapt/aapt2，跳过产物清单回验");
            return null;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[P0K] 清单 dump 异常（已吞）: " + e.Message);
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
            if (t == null) { Debug.LogError("[P0K] ★找不到 AndroidExternalToolsSettings"); return; }
            t.GetProperty("sdkRootPath").SetValue(null, SdkRoot);
            t.GetProperty("ndkRootPath").SetValue(null, NdkRoot);
            t.GetProperty("jdkRootPath").SetValue(null, JdkRoot);
            Debug.Log("[P0K] AndroidExternalToolsSettings 已写入: sdk=" + t.GetProperty("sdkRootPath").GetValue(null) +
                      " ndk=" + t.GetProperty("ndkRootPath").GetValue(null) +
                      " jdk=" + t.GetProperty("jdkRootPath").GetValue(null));
        }
        catch (Exception e)
        {
            Debug.LogError("[P0K] ★写 AndroidExternalToolsSettings 失败: " + e);
        }
    }

    /// <summary>只落偏好、不构建。必须单独一个进程先跑它（Android 扩展在加载期缓存工具路径）。</summary>
    [MenuItem("P0K/0. Set External Tool Paths Only")]
    public static void SetPathsOnly()
    {
        SetPaths();
        Debug.Log("[P0K] prefs persisted (仅落偏好，不构建)");
        Debug.Log("[P0K]   AndroidSdkRoot     = " + EditorPrefs.GetString("AndroidSdkRoot") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidSdkRoot")));
        Debug.Log("[P0K]   AndroidNdkRootR23B = " + EditorPrefs.GetString("AndroidNdkRootR23B") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("AndroidNdkRootR23B")));
        Debug.Log("[P0K]   JavaHome           = " + EditorPrefs.GetString("JavaHome") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JavaHome")));
        Debug.Log("[P0K]   JdkPath            = " + EditorPrefs.GetString("JdkPath") +
                  " exists=" + Directory.Exists(EditorPrefs.GetString("JdkPath")));
        bool ok = EditorPrefs.GetString("AndroidSdkRoot") == SdkRoot
                  && EditorPrefs.GetString("AndroidNdkRootR23B") == NdkRoot
                  && EditorPrefs.GetString("JavaHome") == JdkRoot
                  && EditorPrefs.GetString("JdkPath") == JdkRoot;
        Debug.Log("[P0K] PREFS_VERDICT = " + (ok ? "PASS" : "FAIL") + " (回读四值与源码常量逐字比对)");
        EditorApplication.Exit(ok ? 0 : 4);
    }

    /// <summary>
    /// ★ 单进程同时落偏好 + 构建。
    /// 背景：`-batchmode` 下 `SetPathsOnly` 以 `EditorApplication.Exit(0)` 早退，
    /// EditorPrefs 未 flush 到注册表；随后起第二个进程跑 `BuildApk` 时，
    /// Android 扩展在**程序集加载期**就缓存了工具路径 ⇒ 抛
    /// `UnityException: JDK not found`（实测 p0i_build4b.log:1241）。
    /// 因此把两步合成一个 executeMethod，保证 SetPaths() 与 BuildPipeline 同进程。
    /// </summary>
    [MenuItem("P0K/2. Set Paths + Build APK")]
    public static void SetPathsAndBuild()
    {
        SetPaths();
        Debug.Log("[P0K] SetPathsAndBuild: prefs 已落(A) > 立即构建");
        BuildApk();
    }

    [MenuItem("P0K/1. Build APK")]
    public static void BuildApk()
    {
        try
        {
            SetPaths();
            Debug.Log("[P0K] SDK=" + EditorPrefs.GetString("AndroidSdkRoot") + " exists=" + Directory.Exists(SdkRoot));
            Debug.Log("[P0K] NDK=" + EditorPrefs.GetString("AndroidNdkRootR23B") + " exists=" + Directory.Exists(NdkRoot));
            Debug.Log("[P0K] JDK=" + EditorPrefs.GetString("JavaHome") + " exists=" + Directory.Exists(JdkRoot));

            Debug.Log("[P0K] 几何口径[编译期常量·非实测·不可用作设备证据] 屏 2616 1140 | 逻辑 800 600 | 渲染 1600 1200" +
                      " | 主视口 1600 1140 | 侧栏 508 | 缩放 2");

            // ---- 联动层自检（照抄 P0J：HUD/Skill/Item 三层必须在，且必须已被 P0BMapWalk 接线）----
            var need = new string[][]
            {
                //  [0]=文件名  [1]=文件里必须出现的接线标记  [2]=P0BMapWalk 里必须出现的接线标记
                new[] { "P0HHud.cs",   "public void DrawAll(P0HRuntime",  "P0HHud _hHud;"   },
                new[] { "P0HSkill.cs", "public void DrawBar(P0HRuntime",  "P0HSkill _hSkill;"},
                new[] { "P0HItem.cs",  "public void DrawBag(P0HRuntime",  "P0HItem _hItem;"  },
            };
            string walkPath = ProjRoot + @"\Assets\Scripts\P0BMapWalk.cs";
            if (!File.Exists(walkPath))
            {
                Debug.LogError("[P0K] ★ 接线文件缺失: " + walkPath);
                Debug.Log("[P0K] VERDICT = FAILURE");
                EditorApplication.Exit(5);
                return;
            }
            string walkSrc = File.ReadAllText(walkPath);
            bool wired = true;
            for (int i = 0; i < need.Length; i++)
            {
                string f = need[i][0], api = need[i][1], field = need[i][2];
                string fp = ProjRoot + @"\Assets\Scripts\" + f;
                bool fileOk = File.Exists(fp);
                bool apiOk = fileOk && File.ReadAllText(fp).Contains(api);
                bool fieldOk = walkSrc.Contains(field);
                Debug.Log("[P0K] 联动层自检 " + f +
                          " 文件在=" + fileOk +
                          " 含API[" + api + "]=" + apiOk +
                          " 已接线[" + field + "]=" + fieldOk +
                          (fileOk ? " " + new FileInfo(fp).Length + "B md5=" + Md5Of(fp) : ""));
                if (!fileOk || !apiOk || !fieldOk) wired = false;
            }
            bool drawOk = walkSrc.Contains("_hHud.DrawAll(_hRun)")
                          && walkSrc.Contains("_hSkill.DrawBar(_hRun)")
                          && walkSrc.Contains("_hItem.DrawBag(_hRun)")
                          && walkSrc.Contains("_hSkill.Tick(");
            Debug.Log("[P0K] 联动层绘制调用齐全=" + drawOk +
                      " (DrawAll/DrawBar/DrawBag/Tick 四项)");
            if (!drawOk) wired = false;

            // 输入法层（P0-I 已交付，仍须在）
            string imePath = ProjRoot + @"\Assets\Scripts\P0IIme.cs";
            bool imeWired = File.Exists(imePath) && walkSrc.Contains("P0IIme _ime;");
            Debug.Log("[P0K] 输入法层仍在=" + imeWired);
            if (!imeWired) wired = false;

            // ---- P0-K 新增自检：P0-A 人物选择层必须在（本包的一半内容就是它）----
            string selPath = ProjRoot + @"\Assets\Scripts\P0ACharacterSelect.cs";
            bool selOk = File.Exists(selPath);
            bool selBoot = false, selJump = false;
            if (selOk)
            {
                string selSrc = File.ReadAllText(selPath);
                selBoot = selSrc.Contains("RuntimeInitializeOnLoadMethod");
                selJump = selSrc.Contains("LoadSceneAsync(\"P0B\")");
            }
            Debug.Log("[P0K] P0-A 选人层 文件在=" + selOk + " 含AfterSceneLoad引导=" + selBoot +
                      " 含LoadSceneAsync(\"P0B\")=" + selJump +
                      (selOk ? " " + new FileInfo(selPath).Length + "B md5=" + Md5Of(selPath) : ""));
            if (!selOk || !selBoot || !selJump) wired = false;

            if (!wired)
            {
                Debug.LogError("[P0K] ★ 联动层/选人层未接线（HUD/Skill/Item/IME/P0A 任缺）—— 出包也是空壳，中止");
                Debug.Log("[P0K] VERDICT = FAILURE");
                EditorApplication.Exit(7);
                return;
            }

            // ---- P0-K 新增自检：两个场景文件必须在 ----
            string sceneAPath = ProjRoot + @"\" + SceneA.Replace('/', '\\');
            string sceneBPath = ProjRoot + @"\" + SceneB.Replace('/', '\\');
            bool sceneAOk = File.Exists(sceneAPath);
            bool sceneBOk = File.Exists(sceneBPath);
            Debug.Log("[P0K] 双场景 A=" + SceneA + " exists=" + sceneAOk +
                      (sceneAOk ? " " + new FileInfo(sceneAPath).Length + "B md5=" + Md5Of(sceneAPath) : ""));
            Debug.Log("[P0K] 双场景 B=" + SceneB + " exists=" + sceneBOk +
                      (sceneBOk ? " " + new FileInfo(sceneBPath).Length + "B md5=" + Md5Of(sceneBPath) : ""));
            if (!sceneAOk || !sceneBOk)
            {
                Debug.LogError("[P0K] ★ 场景文件缺失（A或B）—— 无法双场景出包，中止");
                Debug.Log("[P0K] VERDICT = FAILURE");
                EditorApplication.Exit(11);
                return;
            }

            // ★ 怪物位图层数据自检（缺了怪物层会空白，但不是本层重点，只告警不中止）
            string monIndex = ProjRoot + @"\Assets\Resources\p0h\mon_index.txt";
            Debug.Log("[P0K] 怪物位图层索引 = " + monIndex + " exists=" + File.Exists(monIndex));

            // ★ 刻意**不**调用 AssetDatabase.ImportAsset(ForceUpdate) 刷新 Resources 里的纯文本资产。
            //   实测（2026-10-17，见 m2-asset-revision-1017.md §5）：只要强制重导 Resources 资产，
            //   本次构建的 UnityLinker 就会剥掉 GUISkin(class 115)，装机后 NRE 刷屏、UI 全黑。
            var frozenBefore = new string[Frozen.Length];
            for (int i = 0; i < Frozen.Length; i++)
            {
                frozenBefore[i] = Md5Of(Frozen[i]);
                Debug.Log("[P0K] 冻结产物 " + Path.GetFileName(Frozen[i]) + " 构建前 MD5 = " + frozenBefore[i]);
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
                Debug.LogError("[P0K] ★ 自定义主清单缺失: " + manifestPath + " —— 输入法会再次盖住面板，中止");
                Debug.Log("[P0K] VERDICT = FAILURE");
                EditorApplication.Exit(8);
                return;
            }
            string manSrc = File.ReadAllText(manifestPath);
            bool hasResize = manSrc.Contains("android:windowSoftInputMode=\"adjustResize\"");
            Debug.Log("[P0K] 自定义主清单 = " + manifestPath + " " + new FileInfo(manifestPath).Length +
                      " B md5=" + Md5Of(manifestPath) + " 含adjustResize=" + hasResize);
            if (!hasResize)
            {
                Debug.LogError("[P0K] ★ 主清单缺 android:windowSoftInputMode=\"adjustResize\" —— 中止（否则白出包）");
                Debug.Log("[P0K] VERDICT = FAILURE");
                EditorApplication.Exit(9);
                return;
            }
            // useCustomMainManifest 必须为真，否则上面的文件根本不会被采用
            var projSettingsPath = ProjRoot + @"\ProjectSettings\ProjectSettings.asset";
            string projSettings = File.Exists(projSettingsPath) ? File.ReadAllText(projSettingsPath) : string.Empty;
            bool flagOn = projSettings.Contains("useCustomMainManifest: 1");
            Debug.Log("[P0K] ProjectSettings.useCustomMainManifest=1 ? " + flagOn);
            if (!flagOn)
            {
                Debug.LogError("[P0K] ★ useCustomMainManifest 未开启 —— 自定义清单不会进构建，中止");
                Debug.Log("[P0K] VERDICT = FAILURE");
                EditorApplication.Exit(10);
                return;
            }

            // ---- 素材导入：P0H/P0E/P0D/P0B 沿用，★ 新增 P0A ----
            P0HImport.Apply();
            P0EImport.Apply();
            P0DImport.Apply();
            P0BImport.Apply();
            P0AImportSettings.Apply();          // ★ P0-K 新增：33 张 p0a 素材的 Sprite/Point/None/无mipmap/FullRect
            // ★ 2026-10-27 修：漏了 P0G。不跑这一条，Assets/Resources/p0g 下新加的贴图
            //   会沿用 Unity 默认导入（NPOT 放大到 2 的幂 + 压缩），实测 state_win_232x325.png
            //   被导成 256x256，P0GArt 尺寸判据直接 FAIL、角色窗底图整块不画。
            P0GImport.Apply();                  // ★ p0g 位图层：Sprite/Point/None/无mipmap/RGBA32
            Debug.Log("[P0K] 素材导入已应用: P0H + P0E + P0D + P0B + P0A + P0G(补)");

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
            Debug.Log("[P0K] stripEngineCode(false) —— 规避 GUISkin(class115) 被剥离导致 OnGUI 全挂" +
                      "（P0-A/P0-B 全走 IMGUI，是本事故正中心）");

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
            catch (Exception e) { Debug.LogWarning("[P0K] splash 不可配置: " + e.Message); }

            // ★★★ P0-K 核心：写 EditorBuildSettings.scenes（两条，都 enabled），再回读打印实际内容。
            //   顺序 = P0A(启动) 在前、P0B(进图目标) 在后，不可颠倒。
            var ebs = new EditorBuildSettingsScene[2];
            ebs[0] = new EditorBuildSettingsScene(SceneA, true);
            ebs[1] = new EditorBuildSettingsScene(SceneB, true);
            EditorBuildSettings.scenes = ebs;
            AssetDatabase.SaveAssets();

            var back = EditorBuildSettings.scenes;
            Debug.Log("[P0K] BUILDSCENES 写入完成 count=" + back.Length);
            for (int i = 0; i < back.Length; i++)
                Debug.Log("[P0K] BUILDSCENES [" + i + "] enabled=" + back[i].enabled + " path=" + back[i].path);

            bool sceneOrderOk = back.Length == 2
                                && back[0].path == SceneA && back[0].enabled
                                && back[1].path == SceneB && back[1].enabled;
            Debug.Log("[P0K] BUILDSCENES 顺序校验(A在前/B在后/均enabled) = " + sceneOrderOk);
            if (!sceneOrderOk)
            {
                Debug.LogError("[P0K] ★ EditorBuildSettings.scenes 未按 P0A,P0B 写入 —— LoadSceneAsync(\"P0B\") 会失败，中止");
                Debug.Log("[P0K] VERDICT = FAILURE");
                EditorApplication.Exit(12);
                return;
            }

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { SceneA, SceneB },     // ★ 双场景：P0A 启动，P0B 进图目标
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            Debug.Log("[P0K] === BuildPipeline.BuildPlayer START === " + ApkPath +
                      " scenes=[" + string.Join(",", opts.scenes) + "]");
            var s = BuildPipeline.BuildPlayer(opts);

            Debug.Log("[P0K] RESULT = " + s.summary.result);
            Debug.Log("[P0K] TOTAL_ERRORS = " + s.summary.totalErrors);

            bool ok = s.summary.result == BuildResult.Succeeded && File.Exists(ApkPath);
            if (ok)
            {
                Debug.Log("[P0K] APK_BYTES = " + new FileInfo(ApkPath).Length);
                Debug.Log("[P0K] APK_MD5 = " + Md5Of(ApkPath));

                // ★★ 出包后回验：从**产物 APK** 里把清单抠出来查 adjustResize。
                //   只验源文件在不在没用 —— 已被「写了文件但没被采用」坑过一次，
                //   必须验最终字节，否则等于自欺。
                string dump = DumpManifestFromApk(ApkPath);
                bool apkResize = dump != null && dump.Contains("windowSoftInputMode");
                Debug.Log("[P0K] 产物清单回验 windowSoftInputMode 出现=" + apkResize);
                if (dump != null)
                {
                    foreach (var ln in dump.Split('\n'))
                    {
                        string t2 = ln.Trim();
                        if (t2.Contains("softInput") || t2.Contains("windowSoftInput"))
                            Debug.Log("[P0K] 产物清单节点: " + t2);
                    }
                }
                if (!apkResize)
                {
                    Debug.LogError("[P0K] ★ 产物APK清单里没有 windowSoftInputMode —— adjustResize 没进包，判失败");
                    ok = false;
                }

                // ★ P0-K 出包后回验：产物包体里必须能查到**两个场景**。
                //   只验 EditorBuildSettings 没用 —— 它证明的是「编辑器里写了」，
                //   不等于「两个场景真的编进了 APK」。
                //
                //   ★★ 2026-10-24 教训（本脚本第一版在这里判了自己 FAILURE，是**假阴性**）：
                //   原实现是把整个 APK 读成字节流扫 ASCII「Assets/Scenes/P0A.unity」。
                //   但 zip 里 `assets/bin/Data/globalgamemanagers` 是 **deflate 压缩**存储的，
                //   场景路径明文只存在于**解压后**的内容里 ⇒ 整包扫字节必然找不到，包是对的却判失败。
                //   实测证据：同一份 p0k.apk 用 PowerShell 解压后，
                //     globalgamemanagers 里按顺序读出 [0] Assets/Scenes/P0A.unity、[1] Assets/Scenes/P0B.unity。
                //   ⇒ 改判据：
                //     (a) zip 条目名 `level0` / `level1` —— 中央目录里是**明文**，扫字节有效，保留；
                //     (b) 场景是否真被打进包 —— 改用 BuildReport.files（引擎自报，无需解压）；
                //     (c) 场景顺序 —— 用 EditorBuildSettings 回读（上面已打印 BUILDSCENES[i]）。
                try
                {
                    byte[] apkBytes = File.ReadAllBytes(ApkPath);
                    bool lv0 = BytesContainAscii(apkBytes, "level0");
                    bool lv1 = BytesContainAscii(apkBytes, "level1");
                    Debug.Log("[P0K] 产物包内条目回验 字节=" + apkBytes.Length +
                              " | level0=" + lv0 + " level1=" + lv1);
                    if (!lv0 || !lv1)
                    {
                        Debug.LogError("[P0K] ★ 产物包里没有 level0+level1 两个场景条目 —— 双场景没打进去，判失败");
                        ok = false;
                    }
                }
                catch (Exception e2)
                {
                    Debug.LogWarning("[P0K] 产物包内条目回验异常（已吞，不判失败）: " + e2.Message);
                }

                // ★ 用 BuildReport.GetFiles() 交叉核对两个场景是否出现在构建产物清单里。
                //
                //   ★★ 2026-10-27 —— 这条判据**降级为信息输出，不再判失败**。原因是它一直在假红：
                //   实测 build.log(08:11) 与 build3.log(08:27) 两次都报
                //     `BuildReport.GetFiles 共 455 条 | 命中 P0A=False 命中 P0B=False`
                //   但同一份 APK 的**更强证据全部通过**：
                //     · `[P0K] BUILDSCENES 顺序校验(A在前/B在后/均enabled) = True`（EditorBuildSettings 回读）
                //     · `BuildPipeline.BuildPlayer scenes=[P0A.unity, P0B.unity]`（调用参数原样打印）
                //     · `产物包内条目回验 level0=True level1=True`（zip 条目名，中央目录明文）
                //     · `RESULT = Succeeded` / `TOTAL_ERRORS = 0`
                //   引擎把场景编进 `levelN` 而不是以 `.unity` 路径出现在 files[] 里 ——
                //   所以按 `.unity` 后缀去 files[] 找，**结构上就永远命中不了**，不是"场景没进去"。
                //   而 `level0/level1` 是 zip 明文条目名，存在即证明第 0/1 号场景已被打包；
                //   配合上面的顺序校验（P0A 在前、P0B 在后、均 enabled），映射是 1:1 的。
                //   ⇒ 判据失效时不能当判决用（假红会让"真成功"被记成失败，反向地也会掩盖真问题）。
                try
                {
                    bool builtA = false, builtB = false;
                    var files = s.GetFiles();
                    if (files != null && files.Length > 0)
                    {
                        for (int i = 0; i < files.Length; i++)
                        {
                            string fp2 = files[i].path;
                            if (string.IsNullOrEmpty(fp2)) continue;
                            fp2 = fp2.Replace('\\', '/');
                            if (fp2.EndsWith(SceneA, StringComparison.OrdinalIgnoreCase)) builtA = true;
                            if (fp2.EndsWith(SceneB, StringComparison.OrdinalIgnoreCase)) builtB = true;
                        }
                        Debug.Log("[P0K] BuildReport.GetFiles 共 " + files.Length + " 条" +
                                  " | 按 .unity 路径命中 P0A=" + builtA + " 命中 P0B=" + builtB +
                                  "（引擎以 levelN 打包，故通常为 False；场景是否进包以上面 level0/level1 + BUILDSCENES 顺序为准）");
                    }
                    else
                    {
                        Debug.LogWarning("[P0K] BuildReport.GetFiles 为空，跳过场景清单交叉核对");
                    }
                }
                catch (Exception e3)
                {
                    Debug.LogWarning("[P0K] BuildReport.GetFiles 回验异常（已吞，不判失败）: " + e3.Message);
                }
            }
            Debug.Log("[P0K] APK_EXISTS = " + File.Exists(ApkPath));

            // ★ 2026-10-27 修一个「假绿」隐患：frozenBefore 与 now 对同一条路径取值，
            //   若文件本来就不存在，两边都是 "(不存在)" ⇒ same=true ⇒ 判成"未改动"通过。
            //   于是"文件被误删"会被记成"完好"，正是这条自检本应拦住的场景。
            //   ⇒ 分三态：不存在(ABSENT，只告警不判失败) / 完全一致(OK) / 变了(FAIL)。
            //     不把 ABSENT 判失败，是因为 p0a..p0j 这些旧产物本就可能被用户清理掉；
            //     但必须显式打出来，免得下游把这行当成"全部完好"。
            int frozenAbsent = 0;
            for (int i = 0; i < Frozen.Length; i++)
            {
                string now = Md5Of(Frozen[i]);
                bool absent = (now == AbsentMark);
                if (absent) frozenAbsent++;
                bool same = now == frozenBefore[i];
                Debug.Log("[P0K] 冻结产物 " + Path.GetFileName(Frozen[i]) +
                          (absent ? " 状态=ABSENT(文件不存在，无法核对)" : "") +
                          " 构建后 MD5 = " + now + " 未改动=" + (absent ? "n/a" : same.ToString()));
                if (!absent && !same) { Debug.LogError("[P0K] ★已交付产物被改动: " + Frozen[i]); ok = false; }
            }
            Debug.Log("[P0K] 冻结产物核对: 共 " + Frozen.Length + " 个，其中不存在=" + frozenAbsent +
                      "（不存在≠通过，只是无法核对；真正的失败条件是同一路径前后 MD5 不一致）");

            Debug.Log("[P0K] VERDICT = " + (ok ? "SUCCESS" : "FAILURE"));
            EditorApplication.Exit(ok ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0K] BUILD EXCEPTION: " + e);
            EditorApplication.Exit(3);
        }
    }
}
