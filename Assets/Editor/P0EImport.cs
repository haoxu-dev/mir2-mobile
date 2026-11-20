// P0-E —— 图集/白名单导入设置（Point / 无 mipmap / **不压缩** / NPOT 不缩放）
//
// ★ 为什么需要它（三个已实测过的坑，全部照抄 P0BImport / P0DImport 的教训，不靠猜）：
//   ① **NPOT 必须关**：`p0e_smtiles_atlas.png` 是 768x512，2048 上限内没问题，但
//      `p0e_tiles_atlas.png` 是 2048x2048；若被缩放，Sprite.Create 的格子坐标全错位，
//      表现为"地图花掉"而日志全绿 —— 这正是 P0-B 实测踩过的 768x256→1024x256（切出 40 块）。
//   ② **压缩必须关**：真彩帧是 pixel art，任何有损压缩都会毁色卡；且 A7 判的是"彩色帧"，
//      介入了压缩器就没法证明颜色来自源素材。
//   ③ **只作用 Android 平台块**：P0-C 实测踩过 `ti.textureCompression` 只写 DefaultTexturePlatform，
//      真正生效的是 Android 块 ⇒「1:1」在设备上不成立且**自检全绿**。故显式 SetPlatformTextureSettings。
//
// ★ 本文件**不碰** P0-B/P0-C/P0-D 的素材（atlas_tiles.png / atlas_walk.png / map.bytes / p0d/*），
//   只处理 P0-E 新增的 3 个文件。
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class P0EImport
{
    const string Res = "Assets/Resources/";

    [MenuItem("P0E/1. Apply Atlas Import Settings")]
    public static void Apply()
    {
        // 图集按「整张当一张纹理」导入（不切 Sprite 网格）：切格由运行期 Sprite.Create 做，
        // 这样图集索引表改版不需要重新导入纹理。
        Sheet("p0e_tiles_atlas.png", 2048);
        Sheet("p0e_smtiles_atlas.png", 2048);
        // ★ 2026-10-25 新增：前景层图集（Objects/Objects4/Objects6 货架装箱，多页）。
        //   必须走同一套 Point/无mipmap/不压缩/NPOT-none，否则 pixel art 会被双线性糊掉。
        for (int i = 0; i < 8; i++)
        {
            string objPath = Res + "p0e_obj_atlas_" + i + ".png";
            if (!File.Exists(objPath)) break;
            Sheet("p0e_obj_atlas_" + i + ".png", 2048);
        }
        Text("p0e_atlas_index.txt");
        Text("p0e_whitelist.txt");
        Text("0.map.bytes");
        AssetDatabase.Refresh();
    }

    static void Sheet(string file, int maxSize)
    {
        string path = Res + file;
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) { Debug.LogError("[P0E] 找不到纹理导入器: " + path); return; }

        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Single;   // ★ 整张当一张；切格在运行期
        ti.spritePixelsPerUnit = 100f;
        ti.filterMode = FilterMode.Point;
        ti.mipmapEnabled = false;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.alphaIsTransparency = true;
        ti.maxTextureSize = maxSize;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.isReadable = false;                            // 运行期只取样、不读回像素

        // ★ 关键：显式落 Android 平台块（Default 块的设置不会自动生效）
        var ps = new TextureImporterPlatformSettings
        {
            name = "Android",
            overridden = true,
            maxTextureSize = maxSize,
            format = TextureImporterFormat.RGBA32,
            textureCompression = TextureImporterCompression.Uncompressed,
            crunchedCompression = false,
            allowsAlphaSplitting = false
        };
        ti.SetPlatformTextureSettings(ps);
        ti.SaveAndReimport();

        // ★ 自检**读 Android 块**（不是读 Default 块）—— 否则就是"看着在检、实际什么都没检"
        var back = AssetImporter.GetAtPath(path) as TextureImporter;
        var bp = back.GetPlatformTextureSettings("Android");
        bool ok = bp.overridden &&
                  bp.format == TextureImporterFormat.RGBA32 &&
                  bp.textureCompression == TextureImporterCompression.Uncompressed &&
                  back.npotScale == TextureImporterNPOTScale.None &&
                  back.mipmapEnabled == false &&
                  back.filterMode == FilterMode.Point;
        Debug.Log("[P0E] " + file + " Android块 overridden=" + bp.overridden +
                  " format=" + bp.format + " compression=" + bp.textureCompression +
                  " npot=" + back.npotScale + " mip=" + back.mipmapEnabled +
                  " filter=" + back.filterMode + " maxSize=" + bp.maxTextureSize +
                  " => " + (ok ? "OK" : "★不合格"));
    }

    static void Text(string file)
    {
        string path = Res + file;
        var ta = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
        if (ta == null) { Debug.LogError("[P0E] " + path + " 不是 TextAsset（Unity 可能没导入该扩展名）"); return; }
        Debug.Log("[P0E] " + file + " TextAsset 字节数=" + ta.bytes.Length +
                  " sha16=" + Sha16(ta.bytes));
    }

    static string Sha16(byte[] b)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            var h = sha.ComputeHash(b);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
