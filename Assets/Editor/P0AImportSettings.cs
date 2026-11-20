// t19 / P0-A —— 素材导入设置（按《14-UI一比一复刻方法》§5 硬要求）
//   1) Texture Type  = Sprite (2D and UI)
//   2) Filter Mode   = Point          （禁止线性插值：一插值就不是像素级 1:1）
//   3) Compression   = None           （有损压缩会改像素值）
//   4) Mip Maps      = off            （否则远处采样到低 mip，糊掉）
//   5) spriteMeshType= FullRect + extrude=0
//      —— 这条最容易漏：默认 Tight 网格会裁掉透明边、并把 Image 按裁剪后的
//         pivot 定位，元素会整体位移几个像素，1:1 直接废掉。
// 不 Preserve Aspect（尺寸由 RectTransform 显式给定，等于原版尺寸 x1.8）。
// 素材原件（ChrSel.wil / proguse 背景）只读，本文件只改工程内 .meta 导入设置。
using UnityEditor;
using UnityEngine;

public static class P0AImportSettings
{
    const string Dir = "Assets/Resources/p0a";

    // [2026-10-24 重写] 补齐 P0-A 人物选择界面的全部素材：
    //   room.png   ChrSel#22  房间背景
    //   plate.png  Prguse#65  底板（两壁龛已抠成透明窗）
    //   ch_a/ch_b  角色立绘（彩色/灰度 _g/白色剪影 _w 三态）
    //   fx_00..13  ChrSel#4..#17 传送光柱 14 帧
    // 老的 bg/slot/txt_* 一并保留，让旧场景不至于丢引用。
    static readonly string[] Files = {
        // ---- 本轮 P0-A 主用 ----
        "room.png", "plate.png",
        "ch_a.png", "ch_a_g.png", "ch_a_w.png",
        "ch_b.png", "ch_b_g.png", "ch_b_w.png",
        "fx_00.png", "fx_01.png", "fx_02.png", "fx_03.png",
        "fx_04.png", "fx_05.png", "fx_06.png", "fx_07.png",
        "fx_08.png", "fx_09.png", "fx_10.png", "fx_11.png",
        "fx_12.png", "fx_13.png",
        // ---- 旧素材（保留） ----
        "bg.png", "slot.png",
        "txt_kaishi.png", "txt_chuangjian.png", "txt_shanchu.png",
        "txt_jieshu.png", "txt_copyright.png",
        "glyphs.png"
    };

    [MenuItem("P0A/1. Apply Import Settings")]
    public static void Apply()
    {
        int ok = 0;
        for (int i = 0; i < Files.Length; i++)
        {
            string path = Dir + "/" + Files[i];
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) { Debug.LogError("[P0A-IMP] MISSING " + path); continue; }

            imp.textureType = TextureImporterType.Sprite;
            imp.spriteImportMode = SpriteImportMode.Single;
            imp.spritePixelsPerUnit = 100f;
            imp.filterMode = FilterMode.Point;
            imp.mipmapEnabled = false;
            imp.alphaIsTransparency = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.maxTextureSize = 2048;
            imp.sRGBTexture = true;
            imp.isReadable = false;

            var ss = new TextureImporterSettings();
            imp.ReadTextureSettings(ss);
            ss.spriteMeshType = SpriteMeshType.FullRect;
            ss.spriteExtrude = 0;
            ss.spriteGenerateFallbackPhysicsShape = false;
            ss.filterMode = FilterMode.Point;
            imp.SetTextureSettings(ss);

            var an = imp.GetPlatformTextureSettings("Android");
            an.overridden = true;
            an.format = TextureImporterFormat.RGBA32;
            an.maxTextureSize = 2048;
            an.textureCompression = TextureImporterCompression.Uncompressed;
            imp.SetPlatformTextureSettings(an);

            imp.SaveAndReimport();

            var t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Debug.Log(string.Format(
                "[P0A-IMP] {0} {1}x{2} filter={3} comp={4} mip={5} mesh=FullRect extrude=0 androidFmt={6} srgb={7}",
                Files[i],
                t != null ? t.width : -1, t != null ? t.height : -1,
                imp.filterMode, imp.textureCompression, imp.mipmapEnabled,
                imp.GetPlatformTextureSettings("Android").format, imp.sRGBTexture));
            ok++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[P0A-IMP] DONE ok=" + ok + "/" + Files.Length);
    }
}
