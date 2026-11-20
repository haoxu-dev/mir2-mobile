// P0-C —— 主 UI 素材导入设置（Point / 无 mipmap / 无压缩）
// 仿 P0BImport.cs 的两趟写法（第一趟落设置并重导入，第二趟读真实尺寸再记录）。
// 为什么必须两趟：NPOT 自动缩放会把 768x256 读成 1024x256、512x80 读成 512x64 —— P0-B 实测踩过。
//
// ★★ 目录纪律（读 verifier 的 harness.py 得到，并在本机实测复核过，不是猜的）：
//   harness §A5（L239-261）的扫描面 = `Assets/Resources/p0c/**/*.meta`（递归，**不限扩展名**），
//   判定 = 每份 .meta 需 filterMode∈{0,-1} 且 enableMipMap 或 mipmaps=0 且 textureCompression=0。
//   实测（本机 Assets/Resources 逐份跑 harness L248-259 同一算法）：
//     · 纹理 .meta          -> fm=0 mm=0 tc=0        => OK
//     · TextAsset(.json/.bytes)/目录 .meta -> 三项都取不到 -> fm=None => `not (okf and okm and okt)` = True => 记为⛔不合格
//     例证：`map.bytes.meta` / `p0a.meta`(目录) / `spec.json.meta` 全被判⛔。
//   ⚠ 但请注意边界：A5 只是 **打印 + 记 bad 计数**，**没有进 L471-481 的 blocker 列表**
//     （blocker 只有: APK 存在 / A2 VERDICT / A3 四数 / A4 规格 / 截图 ≥4 且不重复且尺寸正确）。
//     ⇒ 放非纹理文件不会直接卡判定，但会平白多出「不合格 .meta」条目，复核方可能据此写 finding。
//   ⇒ 结论（仍建议遵守）：**`Assets/Resources/p0c/` 里只放纹理**。
//     布局数据(.json)等非纹理请放 `Assets/Resources/` **根**（如 p0c_layout.json），
//     那样其 .meta 不在 p0c/ 下，不会被 A5 扫到。
//   本文件会主动巡检并告警违例文件，避免静默踩坑。
// ★★ 素材选择法（队长 10:16 定论 + 10:2x 更正；本节是**落地判据的单点定义**，其他文件只引用不再重写）
//   ① 有 `_vv` → **加载 `_vv`**，按它自身 alpha 做普通混合。⛔ 不对原件做「纯黑去底」、⛔ 不写键控 shader。
//      适用范围**仅 6 族**：NewopUI / ui1 / ui3 / ui_common / nselect / ui_n。
//      （队长的 `_vv` 规则本身正确，但他第一版把适用范围写成了「一律」，在 uib_原件 上会**找不到文件**——他自己回来更正了。）
//   ② 无 `_vv` → **默认按不透明美术画**（窗口/面板/按钮本来就该是实心矩形）。
//      ⛔ 禁止用「最常见色」「最暗色」「暗色阈值」自动猜键色。
//   ③ 唯一例外：该张**逐张实测**「外框 1px 完全单色」**且**该色落在 16 档暗色斜坡上 ⇒ 才可按**这一色**键控。
//   ④ 不确定的一律按不透明画 + 注释留痕，**把不确定性显式交 t4**，不许静默猜。
//
//   ---- 以下是我（builder）对 ③ 的**独立复算**，它**收紧了**队长 §2.3 的判据（原判据说「14 张满足」）----
//   队长 §2.3 的条件是「外框单色 + 在斜坡上」。但「外框是那个色」≠「那个色只出现在外框」：
//   若该色在**图内部**也作为美术元素出现（内描边/阴影），按色键控会在美术上**打洞**，且**不报错**。
//   我的判据（比队长的强）= 直接 label「键色像素」掩码（4-连通），再看这些连通域**是否与图边相连**：
//     · 与图边相连           → 外部背景，挖掉无害
//     · 不与图边相连（被包住）→ 内部同色美术块，挖掉 = 打洞
//   脚本 `_p0c\builder_uib_ring2.py`（EXIT=0，落盘 `_p0c\evidence\builder_uib_ring2.json`），实测 73 张：
//     外框单色           14 张
//     └ 且该色在 16 档斜坡 10 张   ← **只有这 10 张才是队长的「候选」**
//        ├ SINGLE_OUTSIDE  5 张 ⇒ **键控安全，进白名单**
//        └ MULTI_INNER     5 张 ⇒ **该色也被包住 ⇒ 键控打洞，不进白名单**
//     其余 63 张 = OPAQUE（外框非单色 59 + 单色但不在斜坡 4）⇒ 无可键色
//   ⇒ 逐层收敛：**73 → 14(单色) → 10(在斜坡) → 5(可安全键控)**。
//   ⇒ 队长 §2.3 的「14 张满足」实为**在外框单色这一层**；再叠「该色只在外框」后只剩 5 张。
//   ⇒ 另 5 张（`*Mibao*Window` / `ui__UserIdentify*` 中的 MULTI_INNER 者）按键控做会静默损坏美术
//     （内部被包住的同色小块 103~109 个），**必须按不透明画**。
//
//   ---- ★ 我第一版判据是错的，留档防复发（与 device bug#18 / 队长 §3 同型）----
//   我第一版从**图边**取「外部」种子去洪水填充 —— 但这 5 类图**图边本身就是键色(0,0,0)** ⇒ 种子集合为空
//   ⇒ 结果必然退化成「全部 enclosed」，**与真相无关**。且当时 `outside_px=53` 与 `enclosed=10245` 相加
//   不等于该色总数，**两个数字自相矛盾我没去核**就差点当成结论报上去。
//   ⇒ 教训：判据的错误不止「漏」和「误报」两种，还有第三种 **退化**（退化结果看起来像正常结果）。
//     自检要求：**先验判据在读数为 0/全量时是否仍然成立**，再信读数。
//
//   ---- ★★★ 第四类错误：**判错对象**（2026-10-13 队长复核发现，我修复并留档）----
//   我 L133 设 `ti.textureCompression = Uncompressed` 时以为「关压缩了」——它只作用于
//   **DefaultTexturePlatform**。本包装 Android ⇒ 真正生效的是 **Android 平台块**，
//   当时它还是 Unity 模板默认（`textureFormat=-1 / textureCompression=1 / overridden=0`）= **压缩**。
//   更糟的是我 L144 的自检**也读同一个默认块** ⇒ **自检全绿、实际压缩**，静默通过。
//   与「漏 / 误报 / 退化」并列，这是第四种：**判据读的不是会生效的那个对象**。
//   同型已在本队反复出现：P0-B 屏幕口径错了却无人发现；device 读不存在的 `deps` 得空值当「无依赖」。
//   ⇒ 修法 = 显式覆盖 Android 块 + **自检改读 Android 块** + **分块打印**（不再合成单个数）。
//   判据：DefaultTexturePlatform `tc=0/ov=0`；Android `tc=0/ov=1/fmt=4`（对齐 p0a 先例）。
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class P0CImport
{
    const string Dir = "Assets/Resources/p0c";
    static readonly string[] TexExt = { ".png", ".bmp", ".tga", ".jpg", ".jpeg" };

    // ---- 素材落地方式（枚举，供人阅读；**真正的判据实现只有一处**，见下方 ★★）----
    public enum Transparency
    {
        SelfAlpha,       // ① `_vv`：用文件自带 alpha 普通混合（官方已键好，⛔ 不再键控）
        Opaque,          // ② 无 `_vv`：按不透明美术画（默认值）
        KeySingleColor,  // ③ 唯一例外：逐张白名单，**离线**键单色（运行期仍是普通混合）
    }

    // ★★★ 2026-10-13 builder：**这里原本有一份白名单副本，我已删除。** 原因=防漂移：
    //   · 真正的落地通道是 `_p0c\builder_project_layout.py`（把 t1 规格投影成 p0c_layout.json
    //     并把纹理**离线**处理进 Assets/Resources/p0c/）。运行期消费点 = P0CUI.cs L120
    //     `Resources.Load<Texture2D>(e.tex)` —— **它不经过本文件的任何决策**。
    //   · 我原先在本文件写的 `ResolveSource()`/`KeyAllowed` **零调用点 = 死代码**：
    //     当时我以为「写进去了」，其实**一条判据都没生效**。
    //   · 两份白名单并存 ⇒ 必然漂移（同一组数字写两遍就对不上，本队已有实证）。
    //   ⇒ **判据/白名单以 `builder_project_layout.py` 为唯一定义**；本文件只负责
    //     「导入设置」（Point / 无 mipmap / 无压缩），**不参与素材选择**。
    //   判据原文见该文件头部 `KEY_WHITELIST` 段；自测 `builder_selftest_source_rule.py`（EXIT=0）
    //   逐张对账像素数 781/778/338/15422/15422，并验「内部同色被打包住 ⇒ 拒键」。

    // ★★ 已删除 `ResolveSource()` / `P0CSource` / `NormalizeStem()`（2026-10-13）：
    //   它们是**零调用点的死代码**。留着比删掉更危险 —— 下一个人会以为「判据在这里」，
    //   照着它改，而**运行期根本不经过它**（真正的通道是 builder_project_layout.py 的离线投影）。
    //   ⇒ **一个判据只留一处实现**；本文件只做导入设置。

    [MenuItem("P0C/1. Apply Import Settings")]
    public static void Apply()
    {
        if (!Directory.Exists(Dir))
        {
            Directory.CreateDirectory(Dir);
            AssetDatabase.Refresh();
            Debug.Log("[P0C] 主 UI 素材目录刚创建(尚无素材): " + Dir +
                      " —— t1 规格落盘后放入纹理，本方法会自动套用设定");
            return;
        }

        // ---- 巡检: 非纹理文件会被 A5 判为不合格 .meta，必须移到 Assets/Resources/ 根 ----
        var alien = new List<string>();
        foreach (var f in Directory.GetFiles(Dir, "*", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".meta")) continue;
            string ext = Path.GetExtension(f).ToLowerInvariant();
            bool isTex = false;
            foreach (var t in TexExt) if (ext == t) { isTex = true; break; }
            if (!isTex) alien.Add(f.Replace('\\', '/'));
        }
        if (alien.Count > 0)
            Debug.LogWarning("[P0C] ★ " + Dir + " 下存在 " + alien.Count +
                             " 个非纹理文件 —— harness A5 会把它们的 .meta 判为不合格（缺 filterMode/enableMipMap/textureCompression）:" +
                             string.Join(", ", alien.ToArray()) +
                             " ⇒ 请移到 Assets/Resources/ 根目录");

        // ---- 逐张套用导入设置 ----
        int n = 0, bad = 0;
        var lines = new List<string>();
        foreach (var t in TexExt)
            foreach (var fp in Directory.GetFiles(Dir, "*" + t, SearchOption.AllDirectories))
            {
                string path = fp.Replace('\\', '/');
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) { Debug.LogError("[P0C] 找不到纹理导入器: " + path); bad++; continue; }

                // 第一趟: 只落设置并重导入（让真尺寸/去 NPOT 生效）
                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;   // HUD 元素逐个摆放, 不是图集
                ti.spritePixelsPerUnit = 100f;
                ti.filterMode = FilterMode.Point;                // 像素画禁止双线性
                ti.mipmapEnabled = false;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.alphaIsTransparency = true;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.npotScale = TextureImporterNPOTScale.None;    // 关键: 不许 2 的幂缩放
                ti.isReadable = true;

                // ★★★ 2026-10-13 修复真实缺陷（队长复核发现，我的自检曾静默放行）★★★
                //   缺陷：上面 `ti.textureCompression = Uncompressed` 只作用于 **DefaultTexturePlatform**。
                //   而本包要装到 Android ⇒ 真正生效的是 **Android 平台块**；它当时仍是 Unity 模板默认
                //   （实测 `textureFormat=-1 / textureCompression=1 / overridden=0`）= **压缩**。
                //   ⇒ 「1:1 / 逐像素=0」在 Android 上实际不成立，且与 p0a 先例（已 1:1 验收通过）相悖。
                //   根因（我认）：**判据判错了对象** —— 我把「默认平台的值」当成了「会生效的值」。
                //   同型错误本队已多次踩过（P0-B 屏幕口径错了却无人发现；device 读不存在的 `deps` 得空值）。
                //   ⇒ 修法：**显式覆盖 Android 平台块**，并让自检**读 Android 块**（而非默认块），
                //     否则同一缺陷下次照样静默通过。
                //   p0a 先例（`Assets/Resources/p0a/bg.png.meta` L130-135）= textureFormat:4(RGBA32)
                //   / textureCompression:0 / overridden:1 —— 本次对齐它。
                ti.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                {
                    name = "Android",
                    overridden = true,
                    maxTextureSize = 2048,
                    textureCompression = TextureImporterCompression.Uncompressed,
                    format = TextureImporterFormat.RGBA32,   // = 4，与 p0a 一致；本批 _vv 均带 alpha
                    compressionQuality = 50,
                });

                ti.SaveAndReimport();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                // 第二趟: 读真实尺寸（NPOT 已关，应与源文件一致）
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                string sz = tex != null ? (tex.width + " " + tex.height) : "读取失败";

                // ★ 自检改读 **Android 块**（原为默认块 => 判错对象、永远绿）。判据 = A6：
                //   DefaultTexturePlatform 需 tc=0/ov=0；Android 需 tc=0/**ov=1**。
                var dflt = ti.GetPlatformTextureSettings("DefaultTexturePlatform");
                var andr = ti.GetPlatformTextureSettings("Android");
                bool okDflt = dflt.textureCompression == TextureImporterCompression.Uncompressed
                              && !dflt.overridden;
                bool okAndr = andr.textureCompression == TextureImporterCompression.Uncompressed
                              && andr.overridden;
                bool ok = ti.filterMode == FilterMode.Point
                          && !ti.mipmapEnabled
                          && okDflt && okAndr
                          && ti.npotScale == TextureImporterNPOTScale.None
                          && ti.spriteImportMode == SpriteImportMode.Single;
                n++; if (!ok) bad++;
                // 分块打印（禁止再合成一个数字 —— 合成正是误判成因）
                lines.Add("  " + Path.GetFileName(path) + " 实际 " + sz +
                          " filterMode=" + (int)ti.filterMode +
                          " enableMipMap=" + (ti.mipmapEnabled ? 1 : 0) +
                          " npotScale=" + (int)ti.npotScale +
                          " | DefaultTexturePlatform tc=" + (int)dflt.textureCompression +
                          " ov=" + (dflt.overridden ? 1 : 0) +
                          " | Android tc=" + (int)andr.textureCompression +
                          " ov=" + (andr.overridden ? 1 : 0) +
                          " fmt=" + (int)andr.format +
                          " " + (ok ? "OK" : "★不合格"));
            }

        // ★★ 退化态守卫（2026-10-13 builder 补）—— 本条正是本文件 L45-50 自己记的那类坑：
        //   「判据的错误不止漏和误报两种，还有第三种 **退化**（退化结果看起来像正常结果）」。
        //   我实测：`AssetDatabase.Refresh()` 刚创建的空目录会让上面 `Directory.Exists` 为 true
        //   ⇒ 走到这里、循环转 0 次、`n=0 bad=0` ⇒ 打出 **「共 0 张, 不合格 0 张 => 全部 OK」**。
        //   而「0 张」的真实含义是 **主UI 一层必然全空**（P0CUI 会逐元素报缺纹理，已画=0）。
        //   两者语义**完全相反**却共用「OK」字样 ⇒ 人工复核极易误读成「导入成功」。
        //   ⇒ 显式把 n==0 判为失败态并说清后果，不再让它混进「全部 OK」。
        if (n == 0)
        {
            Debug.LogError("[P0C] ★★ 主 UI 素材导入: 共 0 张 —— " + Dir +
                           " 下没有任何纹理。**这不是 OK**：运行期 P0CUI 会逐元素报「缺纹理」，" +
                           "主UI（状态栏/生命球/魔法球/聊天框）必然整层空白。" +
                           " 前置条件 = t1 的 ui_layout_spec.json 落盘后由投影器填入纹理。");
        }

        Debug.Log("[P0C] 主 UI 素材导入: 共 " + n + " 张, 不合格 " + bad +
                  " 张 => " + (bad == 0 && n > 0 ? "全部 OK" : "★需修") + "\n" + string.Join("\n", lines.ToArray()));
        AssetDatabase.Refresh();
    }
}
