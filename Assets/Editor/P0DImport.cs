// P0-D —— M2 主 UI 素材导入设置（Point / 无 mipmap / 无压缩 / NPOT 不缩放）
//
// 为什么另开一个文件而不改 P0CImport.cs：
//   P0CImport.cs 在 t43 的 **outOfScope[4]** 里，且它把 P0-C 的 `Assets/Resources/p0c/` 当唯一目录。
//   M2 的素材落在**新目录** `Assets/Resources/p0d/`，两份互不干涉（P0-C 的包与素材零改动）。
//
// ★★ 三条实测得来的纪律（照抄 P0CImport.cs 的教训，不靠猜）：
//   ① 只作用 **Android 平台块**。P0-C 实测踩过：`ti.textureCompression = Uncompressed` 只写
//      DefaultTexturePlatform，真正生效的是 Android 块 ⇒「1:1」在设备上不成立且**自检全绿**。
//      故本文件显式 `SetPlatformTextureSettings("Android")`，且自检**读 Android 块**。
//   ② NPOT 必须关：否则 800x251 这类非 2 幂尺寸会被缩放，元素尺寸判据（V9）直接失真。
//   ③ `n==0` 必须判失败：空目录会让循环转 0 次、打出「共 0 张 不合格 0 张 => 全部 OK」，
//      而真实含义是「主 UI 必然整层空白」—— 语义相反却共用 OK 字样（P0-C 实测踩过）。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class P0DImport
{
    // ⚠ 目录名带 `p0d`，与 P0-C 的 `p0c` **并列**，不复用、不覆盖。
    const string Dir = "Assets/Resources/p0d";

    static readonly string[] TexExt = { ".png", ".tga", ".bmp", ".jpg", ".jpeg" };

    [MenuItem("P0D/1. Apply UI Import Settings")]
    public static void Apply()
    {
        // ---- 前置：目录里不许有非纹理（harness A5 会把它们的 .meta 判为不合格）----
        if (!Directory.Exists(Dir))
        {
            Debug.LogError("[P0D] ★★ 素材目录不存在: " + Dir +
                           " —— M2 主UI 一层必然整层空白（这不是 OK）");
            return;
        }
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
            Debug.LogWarning("[P0D] ★ " + Dir + " 下存在 " + alien.Count +
                             " 个非纹理文件（harness A5 会判其 .meta 不合格）: " +
                             string.Join(", ", alien.ToArray()));

        int n = 0, bad = 0;
        var lines = new List<string>();
        foreach (var t in TexExt)
            foreach (var fp in Directory.GetFiles(Dir, "*" + t, SearchOption.AllDirectories))
            {
                string path = fp.Replace('\\', '/');
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) { Debug.LogError("[P0D] 找不到纹理导入器: " + path); bad++; continue; }

                // ---- 第一趟：落设置并重导入（让去 NPOT / 真尺寸生效）----
                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;
                ti.spritePixelsPerUnit = 100f;
                ti.filterMode = FilterMode.Point;          // ★ 像素画禁双线性：整数倍放大必须 NEAREST
                ti.mipmapEnabled = false;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.alphaIsTransparency = true;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.npotScale = TextureImporterNPOTScale.None;   // ★ 关键：不许 2 的幂缩放
                ti.isReadable = true;

                // ---- ★ 同一条纪律：显式覆盖 **Android 块**（默认块不生效）----
                ti.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                {
                    name = "Android",
                    overridden = true,
                    maxTextureSize = 2048,
                    textureCompression = TextureImporterCompression.Uncompressed,
                    format = TextureImporterFormat.RGBA32,     // = 4，与 p0a 先例一致；本批均带 alpha
                    compressionQuality = 50,
                });

                ti.SaveAndReimport();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                // ---- 第二趟：读真实尺寸（NPOT 已关，应与源文件一致）----
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                string sz = tex != null ? (tex.width + " " + tex.height) : "读取失败";

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
                lines.Add("  " + Path.GetFileName(path) + " 实际 " + sz +
                          " filterMode=" + (int)ti.filterMode +
                          " enableMipMap=" + (ti.mipmapEnabled ? 1 : 0) +
                          " npotScale=" + (int)ti.npotScale +
                          " | Default tc=" + (int)dflt.textureCompression +
                          " ov=" + (dflt.overridden ? 1 : 0) +
                          " | Android tc=" + (int)andr.textureCompression +
                          " ov=" + (andr.overridden ? 1 : 0) +
                          " fmt=" + (int)andr.format +
                          " " + (ok ? "OK" : "★不合格"));
            }

        if (n == 0)
            Debug.LogError("[P0D] ★★ M2 主 UI 素材导入: 共 0 张 —— " + Dir +
                           " 下没有任何纹理。**这不是 OK**：运行期 P0DUI 会逐元素报「缺纹理」，" +
                           "主UI 一层必然整块空白。");

        Debug.Log("[P0D] M2 主 UI 素材导入: 共 " + n + " 张, 不合格 " + bad +
                  " 张 => " + (bad == 0 && n > 0 ? "全部 OK" : "★需修") + "\n" +
                  string.Join("\n", lines.ToArray()));
        AssetDatabase.Refresh();
    }
}
