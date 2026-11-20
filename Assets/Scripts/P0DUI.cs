// P0-D —— 主 UI 第一层（底部 HUD：生命球/魔法球/聊天框/等级条整块）运行时渲染。
//
// ★★ 2026-10-27 终局定案：底栏真值 = **Prguse3[291] 一张 800x251 整图，逻辑落点 (0,349)**。
//   证据链（全部可复算）：
//     ① 源码 FState.pas:8791 `btop := SCREENHEIGHT - 251;`（⇒ 349）
//        FState.pas:8797 `d := g_WMain3Images.Images[BOTTOMBOARD800];`
//        FState.pas:8799 `dsurface.Draw(lx, ly, d.ClientRect, d, True);` ⇒ 整图直贴，无裁切、无分件。
//     ② 到底哪个库：MShare.pas:107 `MAINIMAGEFILE3 = 'Data\Prguse3.data'`，
//        MShare.pas:2662 `g_WMain3Images := CreateImages(MAINIMAGEFILE3, ...)`
//        ⇒ g_WMain3Images 读的是 **Prguse3**，不是 Prguse。旧版取 Prguse[1] 是取错了库。
//     ③ 到底哪一帧：FState.pas:18 `BOTTOMBOARD800 = 371`，
//        ClMain.pas:9644/9649 按"有无英雄"改成 371/291。本工程单机无英雄 ⇒ **291**。
//        （291 与 371 实测 RGBA_MAE 仅 0.389，差异全在 x635..776 y103..225 的英雄能量槽那一片。）
//     ④ 工程件 `p0d/prguse1_800x251.png`（文件名历史遗留，**内容已是 Prguse3[291] 原帧**，
//        MD5 e059f46e6286226b58f8eacb633dcc7f / 154023 B），与真 WIL 解码逐像素一致。
//        Prguse[1] 已备份为 `prguse1_800x251.png.bak-prguse1-orig.png`。
//        ★ 换图风险实测极低：两者 alpha 通道 100% 相同、RGBA_MAE=2.19、只差 3.475% 像素
//          （5 个纯花纹连通块），布局/几何/透明区一模一样。
//     ⑤ 透明区实测：不透明 76.04%；23.96% 透明**几乎全在顶部 y0..97**（y0..50 全宽 87.13%），
//        聊天白板 y118..244 与底部 y244..251 透明率 **0.00%** ⇒ 上方透出地图是原版设计，不垫黑底。
//   ⇒ 已作废的方案有三套，别再回去：
//       · `P0CUI.cs` 的 5 件叠加法（0039 + 0000/0001 补板 + 0002/0003）
//       · `hud_main(800x133)` + `hud_middle(456x131)` + `hud_face(84x94)` 三件叠加
//         （实测三件都不是 prguse1 的裁片：hud_middle 与 prguse1@(200,108) alpha 100% 相同但
//          RGB MAE=202.58，是被洗白过的重绘件；且三件加起来只有 133 高，少了原版上方 118 像素）
//       · 「整条垫黑底」补丁（基于"把索引 255 的白聊天底板误当色键"的错误前提，已删）
//
// ★ 坐标只走一条路：元素坐标一律写「原版 800x600 逻辑坐标」，再经
//   `P0BMapWalk.GroupRectForLogic()` 换算到**组内相对**设备像素。
//   ⚠ 组内**必须**用 GroupRectForLogic，不能用 CanvasRectForLogic：`GUI.BeginGroup(MainUICanvasRect)`
//     已把原点平移到 (508, -60)，组内再传绝对坐标会**二次偏移**（右移 508 / 底错 60）。
//   本文件把「组内值」与「绝对式复算值」**并列打印并逐条断言相等**，把这条口径变成运行期可核验判据。
//
// ★ 为什么不继承 MonoBehaviour：与 P0CUI.cs 同理（零 MonoBehaviour 成员引用；唯一构造点是 `new`，
//   对 MonoBehaviour 用 `new` 是 Unity 已知反模式）。生命周期由 P0BMapWalk 的 OnGUI 驱动。
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>M2 主 UI 第一层：底部 HUD（按 M2_layer1_AUTHORITATIVE.json v2.0.0 只画 0039）。</summary>
public class P0DUI
{
    [Serializable]
    public class Element
    {
        public string name;
        public string tex;
        public float[] logicalRect;    // 原版 800x600 逻辑坐标
        public float[] deviceRect;     // 规格给出的设备像素（**只作核对用，不参与绘制**）
        public int[] pixelSize;        // 源纹理像素尺寸
        public int scale;
        public string source;          // 证据档：measured / original / derived / assumed
        public string evidenceClass;
        public string srcMd5;
        public string specVersion;
    }

    [Serializable]
    public class Viewport
    {
        public int[] device;
        public int[] mainViewport;
        public int originX;
        public int sideBand;
        public int scale;
        public int cropTopLogical;
    }

    [Serializable]
    public class Layout
    {
        public string spec;
        public string specVersion;
        public string generator;
        public Viewport viewport;
        public string[] doNotDraw;
        public int logicalW = 800;
        public int logicalH = 600;
        public Element[] elements;
    }

    public int DrawnCount;
    public int MissingCount;
    public int SizeMismatchCount;     // V9 判据失败计数（>0 即不合格）

    readonly List<Element> _drawn = new List<Element>();
    readonly List<string> _missingLogged = new List<string>();
    Layout _layout;
    bool _loaded;
    bool _drawErrLogged;
    bool _zeroDrawnLogged;
    bool _v9ErrLogged;

    public void LoadLayout()
    {
        _loaded = true;
        var ta = Resources.Load<TextAsset>("p0d_layout");
        if (ta == null)
        {
            Debug.LogError("[P0D] ★ M2主UI布局未装载 elements=0 已画=0 —— 未找到 " +
                           "Assets/Resources/p0d_layout.json（须由 m2build/gen_layout.py 从 " +
                           "m2spec/M2_layer1_AUTHORITATIVE.json 生成并签入工程；M2 主UI 一层将完全不渲染）");
            return;
        }
        try
        {
            _layout = JsonUtility.FromJson<Layout>(ta.text);
        }
        catch (Exception e)
        {
            Debug.LogError("[P0D] ★ M2主UI布局未装载 elements=0 已画=0 —— p0d_layout.json 解析失败: " + e.Message);
            return;
        }
        if (_layout == null || _layout.elements == null || _layout.elements.Length == 0)
        {
            Debug.LogError("[P0D] ★ M2主UI布局未装载 elements=0 已画=0 —— p0d_layout.json 无有效 elements");
            return;
        }
        Debug.Log("[P0D] M2主UI布局装载 OK: " + _layout.elements.Length + " 个元素, 逻辑画布 " +
                  _layout.logicalW + "x" + _layout.logicalH +
                  " | 权威规格 " + _layout.spec + " v" + _layout.specVersion +
                  " | 设备 " + _layout.viewport.device[0] + "x" + _layout.viewport.device[1] +
                  " 主视口 " + _layout.viewport.mainViewport[0] + "x" + _layout.viewport.mainViewport[1] +
                  " 原点x " + _layout.viewport.originX +
                  " 侧栏 " + _layout.viewport.sideBand +
                  " scale " + _layout.viewport.scale +
                  " 裁顶(逻辑) " + _layout.viewport.cropTopLogical);
        // ★ doNotDraw 显式回显：证明「作废的 4 件」确实没被画（防"静默全画"回归）
        if (_layout.doNotDraw != null && _layout.doNotDraw.Length > 0)
            Debug.Log("[P0D] doNotDraw 已遵守(" + _layout.doNotDraw.Length + " 件不画): " +
                      string.Join(", ", _layout.doNotDraw));
    }

    /// <summary>逐元素绘制。由 P0BMapWalk.OnGUI 在 MainUICanvasRect 组内调用。</summary>
    public void DrawAll()
    {
        if (!_loaded) LoadLayout();
        if (_layout == null || _layout.elements == null)
        {
            if (!_drawErrLogged)
            {
                _drawErrLogged = true;
                Debug.LogError("[P0D] ★ M2主UI布局未装载 elements=0 已画=0 —— DrawAll 无布局可画，" +
                               "M2 主UI 一层（底部 HUD）完全不渲染");
            }
            DrawnCount = 0; MissingCount = 0;
            return;
        }

        DrawnCount = 0; MissingCount = 0; SizeMismatchCount = 0;
        _drawn.Clear();
        // ★ 不再垫任何黑底（2026-10-27 删除，证据见 p0d_layout.json 的 transparency 字段）：
        //   原版底栏是 Prguse[1] 一张 800x251 整图，自身不透明率 76.0%，24% 透明像素
        //   **几乎全在顶部 y0..97**（y0..50 全宽 87.1% 透明），聊天白板 y118..244 与
        //   底部 y244..251 的透明率都是 0.00%。⇒ 底栏上方透出地图/草地是原版设计，
        //   之前那句「a0019 内部 70.2% 透明要垫黑」基于"把索引 255 白板误当色键"的错误前提。
        foreach (var e in _layout.elements)
        {
            if (e == null || e.logicalRect == null || e.logicalRect.Length < 4) continue;
            var tex = Resources.Load<Texture2D>(e.tex);
            if (tex == null)
            {
                MissingCount++;
                if (!_missingLogged.Contains(e.name))
                {
                    _missingLogged.Add(e.name);
                    Debug.LogError("[P0D] ★ M2 HUD 元素缺纹理: " + e.name + " tex=" + e.tex +
                                   "（该元素不会出现在屏幕上）");
                }
                continue;
            }

            // ★ 组内相对设备像素（**必须用 GroupRectForLogic**，理由见文件头）
            Rect r = P0BMapWalk.GroupRectForLogic(
                e.logicalRect[0], e.logicalRect[1], e.logicalRect[2], e.logicalRect[3]);
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
            DrawnCount++;
            _drawn.Add(e);

            // ★★ V9 判据（运行期，逐元素）：实测物理尺寸 == 规格逻辑尺寸 × scale，且宽高比误差 == 0
            //    "实测物理"取**三路一致**才算通过：
            //      ① 实际传入 GUI.DrawTexture 的矩形宽高（真正上屏的尺寸）
            //      ② 规格 deviceRect 的宽高
            //      ③ 源纹理像素尺寸 × scale
            //    三路任一不符 ⇒ 计入 SizeMismatchCount 并报 Error（不是警告）。
            int expW = (int)e.logicalRect[2] * e.scale;
            int expH = (int)e.logicalRect[3] * e.scale;
            int gotW = Mathf.RoundToInt(r.width);
            int gotH = Mathf.RoundToInt(r.height);
            int texW = tex.width * e.scale;
            int texH = tex.height * e.scale;
            bool specOk = (e.deviceRect != null && e.deviceRect.Length >= 4)
                          && Mathf.RoundToInt(e.deviceRect[2]) == expW
                          && Mathf.RoundToInt(e.deviceRect[3]) == expH;
            bool sizeOk = (gotW == expW) && (gotH == expH) && (texW == expW) && (texH == expH) && specOk;
            if (!sizeOk)
            {
                SizeMismatchCount++;
                Debug.LogError("[P0D] ★ V9 尺寸判据不通过: " + e.name +
                               " 逻辑(" + e.logicalRect[2] + "x" + e.logicalRect[3] + ") scale=" + e.scale +
                               " 期望物理 " + expW + "x" + expH +
                               " | 实传矩形 " + gotW + "x" + gotH +
                               " | 纹理x2 " + texW + "x" + texH +
                               " | 规格 deviceRect " + (specOk ? "一致" : "不一致"));
            }
        }

        if (DrawnCount == 0 && !_zeroDrawnLogged)
        {
            _zeroDrawnLogged = true;
            Debug.LogError("[P0D] ★ M2主UI绘制为空 已画=0 缺=" + MissingCount +
                           " —— M2 主UI 一层（底部 HUD：生命球/魔法球/聊天框）没有任何像素上屏");
        }

        // 取证窗口：首帧 + 每 300 帧（~5 秒）一次
        if (Time.frameCount < 3 || Time.frameCount % 300 == 0)
            LogEvidence();
    }

    /// <summary>★ 证据打点：逐元素打印「逻辑 -> 组内 -> 绝对」三式 + V9 判定，供报告与复核引用。</summary>
    void LogEvidence()
    {
        if (_layout == null) return;
        Debug.Log("[P0D] M2主UI绘制: 已画=" + DrawnCount + " 缺=" + MissingCount +
                  " V9不符=" + SizeMismatchCount +
                  " | 逻辑画布 " + _layout.logicalW + "x" + _layout.logicalH +
                  " | 画布原点设备(" + P0BMapWalk.MainUICanvasRectX + ", " + P0BMapWalk.MainUICanvasRectY + ")" +
                  " | 缩放 " + P0BMapWalk.ScaleI + " | HUD贴底锚 设备y " + P0BMapWalk.HudBottomDeviceY);

        bool allOk = SizeMismatchCount == 0 && DrawnCount > 0;
        foreach (var e in _drawn)
        {
            Rect rel = P0BMapWalk.GroupRectForLogic(
                e.logicalRect[0], e.logicalRect[1], e.logicalRect[2], e.logicalRect[3]);
            Rect abs = P0BMapWalk.CanvasRectForLogic(
                e.logicalRect[0], e.logicalRect[1], e.logicalRect[2], e.logicalRect[3]);
            // ★ 双式一致性断言：绝对式必须等于规格 deviceRect（这是"几何不回退"的运行期证明）
            bool absOk = (e.deviceRect != null && e.deviceRect.Length >= 4)
                         && Mathf.Approximately(abs.x, e.deviceRect[0])
                         && Mathf.Approximately(abs.y, e.deviceRect[1])
                         && Mathf.Approximately(abs.width, e.deviceRect[2])
                         && Mathf.Approximately(abs.height, e.deviceRect[3]);
            Debug.Log("[P0D] HUD画布 " + e.name +
                      " 逻辑(" + e.logicalRect[0] + "," + e.logicalRect[1] + "," +
                      e.logicalRect[2] + "," + e.logicalRect[3] + ")" +
                      " -> 组内(" + rel.x + "," + rel.y + "," + rel.width + "," + rel.height + ")" +
                      " 绝对(" + abs.x + "," + abs.y + "," + abs.width + "," + abs.height + ")" +
                      " 底 设备y " + (abs.y + abs.height) +
                      " 源尺寸(" + e.pixelSize[0] + "x" + e.pixelSize[1] + ") scale " + e.scale +
                      " 绝对式==规格 " + (absOk ? "OK" : "★不符") +
                      " | 档 " + e.source + " | md5 " + e.srcMd5);
            if (!absOk) allOk = false;
        }
        // ★ 单行机器判据（gate.py grep 它；**必须只有一行且只在全部通过时为 PASS**）
        Debug.Log("[P0D] V9_VERDICT = " + (allOk ? "PASS" : "FAIL") +
                  " 元素数=" + _layout.elements.Length + " 已画=" + DrawnCount +
                  " 缺=" + MissingCount + " 尺寸不符=" + SizeMismatchCount);
    }
}

