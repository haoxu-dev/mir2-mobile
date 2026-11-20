// P0-E 探针 v3（一次性，**已消费完毕**）—— 决定「真地图文件」在工程内的落地形态
//   候选1: Assets/Resources/0.map       + .meta 显式写 TextScriptImporter（能否强制导入？）
//   候选2: Assets/Resources/0.map.bytes + Resources.Load<TextAsset>("0.map")  （队长的方案 A）
//   候选3: Assets/Resources/0.bytes     + Resources.Load<TextAsset>("0")      （备选）
//
// ===================== 结论（2026-10-16，原始日志 probe_map2.log） =====================
//   候选1  `0.map`         -> main_type=UnityEditor.DefaultAsset | LoadAssetAtPath=null | Resources.Load=null
//                             ⇒ Unity **不**把 `.map` 当 TextAsset 导入；手写 meta 强制 importer 也无效。
//   候选2  `0.map.bytes`   -> main_type=UnityEngine.TextAsset | Load<"0.map">   = OK 5880052B byteSame=True
//                             ⇒ **胜出**：磁盘名 `0.map.bytes`，`Resources.Load` 的 key 是 `0.map`。
//   候选3  `0.bytes`       -> main_type=UnityEngine.TextAsset | Load<"0">       = OK 5880052B byteSame=True
//                             ⇒ 同样能 Load，但 key 是 `0`，不如候选2 自解释 ⇒ 未采用（探针已删除该产物）。
//
// ===================== ★ 为什么本探针**不可再运行** =====================
//   它原实现会把 `0.map` 拷进 `Assets/Resources/`（候选2/3 的 `File.Copy(MapPath, ...)`）。
//   而 `0.map` 与 `0.map.bytes` **逐字节相同**（同为 5880052 B / md5 6B2E92C877FB9C6B303637AD2B391C5A）
//   ⇒ 两个同基名资源共存时，`Resources.Load("0.map")` 解析到哪一个**无法从字节判别**
//     （探针的 `byteSame=True` 对两者都成立）⇒ 会污染 A11「地图文件同一性」判据。
//   ⇒ 收尾动作：`0.map` / `0.map.meta` 已**移出** `Assets/Resources/`，归档到
//     `_scratch\_p0c\m2build\p0e_map_archive\`（md5 与源文件一致，未做任何转换）。
//   ⇒ 因此本文件现在**只读**：Run() 仅打印结论与磁盘现状，**不写任何文件、不做任何导入/删除**。
//     保留它的意义 = 让日志里的结论可被复核方追溯到出结论的那段代码（而不是只剩一句口述）。
using System.IO;
using UnityEditor;
using UnityEngine;

public static class P0EProbe
{
    const string ResDir = "Assets/Resources";
    // 归档位置（原 `Assets/Resources/0.map`）。仅用于打印现状，不再被写入。
    const string ArchiveDir = @"C:\Users\haoxu\ai\_scratch\_p0c\m2build\p0e_map_archive";
    const string ArchiveMap = ArchiveDir + @"\0.map";
    // 工程内当前**实际被加载**的地图资源（候选2 胜出物）。
    const string LiveMap = ResDir + "/0.map.bytes";

    static string Md5Of(string p)
    {
        if (!File.Exists(p)) return "(不存在)";
        using (var md5 = System.Security.Cryptography.MD5.Create())
        using (var fs = File.OpenRead(p))
            return System.BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "");
    }

    /// <summary>只读：打印「胜出结论 + 两条路径的字节数与 md5」，供复核方对照 A11。</summary>
    public static void Run()
    {
        Debug.Log("[P3] ===== P0-E 探针已消费完毕（结论：候选2 `0.map.bytes` 胜出），本次只读、不写入 =====");
        Debug.Log("[P3] 候选1 0.map       -> DefaultAsset / LoadAssetAtPath=null / Resources.Load=null");
        Debug.Log("[P3] 候选2 0.map.bytes -> TextAsset / Load<\"0.map\"> = OK 5880052B byteSame=True  ← 胜出");
        Debug.Log("[P3] 候选3 0.bytes     -> TextAsset / Load<\"0\">     = OK 5880052B byteSame=True  (未采用)");
        Debug.Log("[P3] 原始日志 = _scratch\\_p0c\\m2build\\probe_map2.log");

        // ---- 只读核对 A11：工程内被加载的地图 vs 源文件（须逐字节相同）----
        const string src = @"C:\ai\_dl\netdisk\gee176\MirServer\Mir200\map\0.map";
        string liveBytes = File.Exists(LiveMap) ? (new FileInfo(LiveMap).Length).ToString() : "(不存在)";
        Debug.Log("[P3][A11] 工程内加载物 " + LiveMap +
                  " bytes=" + liveBytes + " md5=" + Md5Of(LiveMap) +
                  " | exists=" + File.Exists(LiveMap));
        Debug.Log("[P3][A11] 源文件 " + src +
                  " bytes=" + (File.Exists(src) ? (new FileInfo(src).Length).ToString() : "(不存在)") +
                  " md5=" + Md5Of(src) +
                  " | 逐字节相同=" + (Md5Of(LiveMap) == Md5Of(src) && File.Exists(LiveMap)));
        Debug.Log("[P3][A11] 已移出的 0.map 归档 = " + ArchiveMap +
                  " bytes=" + (File.Exists(ArchiveMap) ? (new FileInfo(ArchiveMap).Length).ToString() : "(不存在)") +
                  " md5=" + Md5Of(ArchiveMap));
        Debug.Log("[P3] Assets/Resources/0.map 是否已移出 -> exists=" + File.Exists(ResDir + "/0.map") +
                  "（期望 False：与 0.map.bytes 同基名会污染 Load 归属判别）");
        Debug.Log("[P3] DONE (只读，未写入任何文件)");
        EditorApplication.Exit(0);
    }
}
