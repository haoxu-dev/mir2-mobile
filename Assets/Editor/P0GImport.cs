// P0-G —— M2 位图层素材导入设置（Point / 无 mipmap / 无压缩 / NPOT 不缩放）
//
// 为什么另开一份而不改 P0DImport.cs：
//   P0DImport 的目录常量写死 `Assets/Resources/p0d`，且它属于 P0-D 已被验收的证据链。
//   P0-G 的素材落在**并列新目录** `Assets/Resources/p0g/`，两份互不干涉
//   （P0-D 的 0039 HUD 素材与其 .meta 一个字节都不动）。
//
// ★★ 三条纪律照抄 P0DImport（都是实测踩出来的，不是照抄格式）：
//   ① 只作用 Android 平台块不成立 —— 必须显式 SetPlatformTextureSettings("Android")，
//      自检也要读 **Android 块**；只写 Default 会「自检全绿但设备上不 1:1」。
//   ② NPOT 必须关：316x236 / 72x24 / 48x25 全是非 2 幂，开了会被缩放到 2 的幂，
//      P0GArt 的尺寸判据当场失真。
//   ③ n==0 必须判失败：空目录会让循环转 0 次并打出「共 0 张 不合格 0 张」，
//      而真实含义是「背包位图层整层空白」——语义相反却共用 OK 字样。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class P0GImport
{
    const string Dir = "Assets/Resources/p0g";

    static readonly string[] TexExt = { ".png", ".tga", ".bmp", ".jpg", ".jpeg" };

    [MenuItem("P0G/1. Apply Art Import Settings")]
    public static void Apply()
    {
        if (!Directory.Exists(Dir))
        {
            Debug.LogError("[P0G] ★★ 素材目录不存在: " + Dir +
                           " —— 背包位图层必然整层空白（这不是 OK）。请先跑 m2spec/gen_m2_art.py");
            return;
        }

        // ★ 必须先 Refresh：否则新拷进来/新删除 .meta 的贴图还没被 AssetDatabase 注册，
        //   下面 AssetImporter.GetAtPath() 会一律返回 null（历史 bug：共 0 张、不合格 10 张）。
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

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
            Debug.LogWarning("[P0G] ★ " + Dir + " 下存在 " + alien.Count + " 个非纹理文件: " +
                             string.Join(", ", alien.ToArray()));

        int n = 0, bad = 0;
        var lines = new List<string>();
        foreach (var t in TexExt)
            foreach (var fp in Directory.GetFiles(Dir, "*" + t, SearchOption.AllDirectories))
            {
                string path = fp.Replace('\\', '/');
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null)
                {
                    // 兜底：还没进库就强制同步导入一次再取。
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    ti = AssetImporter.GetAtPath(path) as TextureImporter;
                }
                if (ti == null) { Debug.LogError("[P0G] 找不到纹理导入器: " + path); bad++; continue; }

                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;
                ti.spritePixelsPerUnit = 100f;
                ti.filterMode = FilterMode.Point;              // 像素画：整数倍放大必须 NEAREST
                ti.mipmapEnabled = false;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.alphaIsTransparency = true;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.npotScale = TextureImporterNPOTScale.None;  // ★ 关键：不许 2 的幂缩放
                ti.isReadable = true;

                ti.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                {
                    name = "Android",
                    overridden = true,
                    maxTextureSize = 2048,
                    textureCompression = TextureImporterCompression.Uncompressed,
                    format = TextureImporterFormat.RGBA32,
                    compressionQuality = 50,
                });

                ti.SaveAndReimport();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

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
                          && ti.spriteImportMode == SpriteImportMode.Single
                          && tex != null;
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
            Debug.LogError("[P0G] ★★ M2 位图层素材导入: 共 0 张 —— " + Dir +
                           " 下没有任何纹理。**这不是 OK**：运行期 P0GArt 会报「缺纹理」，背包位图层必然空白。");

        Debug.Log("[P0G] M2 位图层素材导入: 共 " + n + " 张, 不合格 " + bad +
                  " 张 => " + (bad == 0 && n > 0 ? "全部 OK" : "★需修") + "\n" +
                  string.Join("\n", lines.ToArray()));
        AssetDatabase.Refresh();
    }
}
