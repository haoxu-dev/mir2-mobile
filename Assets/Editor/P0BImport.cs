// t20 / P0-B —— 素材导入设置(图集不压缩 / Point 过滤 / 多块精灵)
// 注意: 必须两趟 —— 先把导入设置(textureType=Sprite, npotScale=None)落盘并重新导入,
// 再读真实像素尺寸切块。第一趟就切会踩 NPOT 自动缩放: 768x256 被读成 1024x256(切出 40 块),
// 512x80 被读成 512x64(切出 0 块)。2026-10-12 首轮构建日志实测到这两个数, 故改两趟。
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class P0BImport
{
    const string Res = "Assets/Resources/";

    [MenuItem("P0B/1. Apply Import Settings")]
    public static void Apply()
    {
        Sheet("atlas_tiles.png", 96, 64);
        Sheet("atlas_walk.png", 64, 80);
        Text("map.bytes");
        AssetDatabase.Refresh();
    }

    static void Sheet(string file, int frameW, int frameH)
    {
        string path = Res + file;
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) { Debug.LogError("[P0B] 找不到纹理导入器: " + path); return; }

        // 第一趟: 只落导入设置
        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Multiple;
        ti.spritePixelsPerUnit = 100f;
        ti.filterMode = FilterMode.Point;
        ti.mipmapEnabled = false;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.alphaIsTransparency = true;
        ti.maxTextureSize = 2048;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.isReadable = true;
        ti.SaveAndReimport();                      // 关键: 先让真尺寸生效
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        // 第二趟: 读真实尺寸再切块
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (tex == null) { Debug.LogError("[P0B] 加载失败: " + path); return; }
        int cols = tex.width / frameW, rows = tex.height / frameH;
        var list = new List<SpriteMetaData>();
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                list.Add(new SpriteMetaData
                {
                    name = Path.GetFileNameWithoutExtension(file) + "_" + (y * cols + x),
                    // Unity 精灵 rect 原点在左下; 资源行序从上到下
                    rect = new Rect(x * frameW, tex.height - (y + 1) * frameH, frameW, frameH),
                    alignment = (int)SpriteAlignment.Custom,
                    pivot = new Vector2(0.5f, 0f),
                    border = Vector4.zero
                });

        ti = AssetImporter.GetAtPath(path) as TextureImporter;   // 重新取, 上一趟后实例已失效
        ti.spritesheet = list.ToArray();
        EditorUtility.SetDirty(ti);
        ti.SaveAndReimport();

        var check = AssetDatabase.LoadAllAssetsAtPath(path);
        int ok = 0;
        foreach (var o in check) if (o is Sprite) ok++;
        Debug.Log("[P0B] " + file + " 真实尺寸=" + tex.width + "x" + tex.height +
                  " 网格=" + cols + "x" + rows + " 每格 " + frameW + "x" + frameH +
                  " 写入块数=" + list.Count + " 回读Sprite数=" + ok +
                  " => " + (ok == list.Count && list.Count > 0 ? "OK" : "异常") +
                  " | 期望 " + (file.StartsWith("atlas_walk") ? "8" : "25有效/32格") +
                  " npotScale=" + ti.npotScale + " compression=" + ti.textureCompression);
    }

    static void Text(string file)
    {
        string path = Res + file;
        var ta = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
        if (ta == null) { Debug.LogError("[P0B] " + path + " 不是 TextAsset"); return; }
        Debug.Log("[P0B] " + file + " TextAsset 字节数=" + ta.bytes.Length +
                  " 期望=" + (48 * 48 * 2) + " => " + (ta.bytes.Length == 48 * 48 * 2 ? "匹配" : "不匹配"));
    }
}
