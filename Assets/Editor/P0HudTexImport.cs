// P0-HUD —— 球体贴图导入设置的**唯一出口**（AssetPostprocessor，导入即生效）
//
// ★★ 为什么必须用 import 回调，而不是再写一个「点一下菜单」的批量脚本：
//   2026-10-24 实测事故：p0h_hud 下 8 个旧 .meta 的 guid 长度异常（44/24 位），
//   构建日志原文
//     "The .meta file ... does not have a valid GUID and its corresponding Asset
//      file will be ignored"
//   ⇒ 资源**根本没进包**（dump globalgamemanagers 里 p0h_hud/ 路径全集缺 orb_liq_hp），
//     Resources.Load 返回 null，运行时左球一点液体都不画。
//   手工写 .meta 也翻车过一次（脚本拼出来的 guid 是 41/56 位非法串）。
//   ⇒ 结论：**.meta 不许由脚本生成，必须让 Tuanjie 自己写；导入设置由本回调统一施加。**
//
// ★ 三条纪律（前两条照抄 P0HImport 的实测教训）：
//   ① 只写 Default 平台块不成立 —— 必须显式 SetPlatformTextureSettings("Android")。
//   ② NPOT 必须关：92×90 非 2 幂，开了会被缩放到 2 的幂，逐像素液面判据当场失真。
//   ③ filterMode 必须 Point(0)：92×90 的图要放大到 184×180 画，双线性会把
//      原版像素边缘糊掉，那正是「看着像贴上去的假东西」的来源之一。
using UnityEditor;
using UnityEngine;

public class P0HudTexImport : AssetPostprocessor
{
    const string Dir = "Assets/Resources/p0h_hud/";
    // ★ 2026-10-24 扩到 p0d/：底栏两张原图也必须走同一套设置。
    //   病根：p0d/hud_middle.png 原文件是 **P(调色板)+tRNS**，Unity 不把 tRNS 转成 alpha，
    //   导入后整张变不透明 ⇒ 中段本该透出地图的 70% 像素被涂成纯黑（设备实测 87.9% 像素=(0,0,0)、
    //   逐像素 std=0，而同一 y 带地图区有 831 种颜色 ⇒ 不是地图没渲染，是黑块盖住了）。
    //   已在磁盘上把它转成真 RGBA(逐像素校验一致)，这里再把导入口径钉死，防止换图回退。
    static readonly string[] Dirs = { Dir, "Assets/Resources/p0d/" };

    void OnPreprocessTexture()
    {
        string p = assetPath.Replace('\\', '/');
        bool hit = false;
        foreach (var d in Dirs) { if (p.StartsWith(d)) { hit = true; break; } }
        if (!hit) return;

        var ti = (TextureImporter)assetImporter;

        ti.textureType        = TextureImporterType.Default;
        ti.filterMode         = FilterMode.Point;      // ③ 像素风不许插值
        ti.mipmapEnabled      = false;
        ti.alphaIsTransparency = true;
        ti.npotScale          = TextureImporterNPOTScale.None;   // ②
        ti.maxTextureSize     = 2048;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.isReadable         = false;

        // ① Android 块必须单独写，否则「自检全绿但设备上不 1:1」
        var android = ti.GetPlatformTextureSettings("Android");
        android.overridden        = true;
        android.maxTextureSize    = 2048;
        android.format            = TextureImporterFormat.RGBA32;
        android.textureCompression = TextureImporterCompression.Uncompressed;
        android.compressionQuality = 100;
        ti.SetPlatformTextureSettings(android);
    }
}
