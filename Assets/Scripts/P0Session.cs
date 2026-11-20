// P0-K —— 一个进程内的「会话事实」单点定义（2026-10-24）
//
// 为什么需要这个文件（接口口径必须单点定义）：
//   P0-A（人物选择）与 P0-B（地图行走 + HUD）原本是两个**互不通信**的场景：
//   P0-A 点了「选择」只是把立绘点亮，P0-B 出生时用的是 atlas_walk[0] 的默认身体，
//   于是"我选了谁"这件事在跨场景时**必然丢失**。
//   一旦两个脚本各自拿一份私有变量去表达"当前角色"，就是典型的双写漂移
//   （2026-10-13 已实测过同类事故：同一组数字在三份文档各写一遍 ⇒ 必然对不上）。
//   ⇒ 本文件是"当前会话选了哪个角色"的**唯一**事实源，两个脚本都只读它、只经它写。
//
// 用法（唯一入口，禁止绕过）：
//   写：P0Session.SetSelection(slot)       —— 只在 P0ACharacterSelect 里调
//   读：P0Session.SelectedSlot / HasSelection / Tint / CharName
//        —— P0BMapWalk 只读，不写
//   清：P0Session.Clear()                 —— 退出/重开时
//
// 口径（与 P0ACharacterSelect 的 _slots 下标严格一致）：
//   slot 0 = 左壁龛（WIN_L / BTN_L）  slot 1 = 右壁龛（WIN_R / BTN_R）
//   -1 = 未选择（进入地图后必须按-1处理，不允许静默当成 0）

using UnityEngine;

public static class P0Session
{
    /// <summary>当前选中的槽位。-1 = 未选。</summary>
    public static int SelectedSlot = -1;

    public static bool HasSelection { get { return SelectedSlot >= 0 && SelectedSlot <= 1; } }

    // ---- 每个槽位的外观/身份口径（单点定义，两边都从这里取，不再各写一份）----
    //  idx  : 0 / 1
    //  name : 用于 HUD 与日志的角色名（本期先用"男/女"这种可验证的保守口径；
    //         真正的职业/性别取自 ChrSel 素材，等 P0-L 接 DB 再替换，此处不留假数据）
    //  tint : 玩家 sprite 的相乘色（atlas_walk 是通用身体图，靠 multiply 区分两人）
    //         ⚠ 这是**近似**，不是原版染色算法；原版走 ChrSel 身体色号表。
    //         已在 ReportLayout 之外的日志里显式标注 "TINT-APPROX"，
    //         避免以后有人把"染出来的颜色"当成"还原了原版配色"。
    static readonly string[] NAMES = { "男", "女" };
    static readonly Color[] TINTS =
    {
        new Color(1.00f, 0.86f, 0.72f, 1f),   // slot 0 偏暖
        new Color(0.86f, 0.92f, 1.00f, 1f),   // slot 1 偏冷
    };

    public static string CharName(int idx)
    {
        return (idx >= 0 && idx < NAMES.Length) ? NAMES[idx] : "";
    }

    public static Color Tint(int idx)
    {
        return (idx >= 0 && idx < TINTS.Length) ? TINTS[idx] : Color.white;
    }

    // 当前选中角色的便捷读法（默认回退=白/空，但 HasSelection 才是判据）
    public static string CurrentName { get { return CharName(SelectedSlot); } }
    public static Color CurrentTint { get { return Tint(SelectedSlot); } }

    /// <summary>
    /// 记录选择结果。唯一写入口。
    /// 日志固定前缀 [P0K] SESSION —— 真机取证按这个前缀 grep，不靠猜。
    /// </summary>
    public static void SetSelection(int slot)
    {
        if (slot < 0 || slot > 1)
        {
            Debug.LogError("[P0K] SESSION 非法 slot=" + slot + "（只允许 0/1），已拒绝写入");
            return;
        }
        SelectedSlot = slot;
        Debug.Log("[P0K] SESSION selected slot=" + slot +
                  " name=" + CharName(slot) +
                  " tint=" + Tint(slot).r.ToString("0.###") + "," +
                            Tint(slot).g.ToString("0.###") + "," +
                            Tint(slot).b.ToString("0.###"));
    }

    public static void Clear()
    {
        SelectedSlot = -1;
        Debug.Log("[P0K] SESSION cleared");
    }
}
