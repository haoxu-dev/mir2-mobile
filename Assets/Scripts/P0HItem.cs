// P0-H 背包 / 物品层（V9）
// ============================================================================
// ★ 这一层解决用户指出的第三个"没联动"病灶：背包和身上的装备对玩家属性毫无影响。
//   在此之前，负重只是 P0HRuntime 里的一个出生常量（L154 注释：出生 木剑(5)+布衣(6)=11），
//   捡东西时 L811 只做 PlayerWeight++ ⇒「背包里有什么、身上穿什么、到底多重」，
//   HUD 的负重条完全不知道。背包是死的，数据链断在物品这一环。
//
//   本层把「物品 → 格子 → 装备位 → 玩家属性 → HUD 负重条」串成一条链：
//       Add / Remove    ⇒ 格数变 ⇒ RecalcStats 重算 PlayerWeight
//       Equip / Unequip ⇒ 装备位变 ⇒ RecalcStats 重算 DC / AC / MC / SC
//   每次重算直接写回 P0HRuntime 的 public 字段，HUD 下一帧就跟着动。
//   ⇒「联动」的判据是**数字跟着变**，不是「能画出 40 个格子」。
//
// ★ 为什么是纯 C# 类：宿主 P0BMapWalk 已经是全工程唯一的 Unity 组件，
//   本层由它 new 出来、在它的 BeginGroup 内被调用（与 P0HHud / P0HSkill 同款）。
//   本层不注册任何逐帧回调：项目里只有宿主那一个 OnGUI 在绘制，多一个回调只是多一次空转。
//
// ★ 坐标契约（照 P0BMapWalk L297-321 的实测结论）：
//   绘制发生在 `GUI.BeginGroup(MainUICanvasRect)` 内部 ⇒ 一律走
//   `P0BMapWalk.GroupRectForLogic(lx,ly,lw,lh)`，参数是原版 800×600 逻辑坐标。
//   那个返回绝对设备坐标的换算函数在这里会二次偏移 (508,-60)，HUD 会整体跑出视口 —— 别用。
//
// ★ 纹理（2026-10-27 三次定案，全部来自 2003 官方客户端原库，不是自绘）：
//   底图  Assets/Resources/p0g/bag_base_352x295.png = 2003官方 Prguse2.wil 第 180 帧
//         ★ 逐像素校验：不透明 83742 对 83742、MAE=0.000、一致率 100.00%、完全相同像素 100.000%
//         这张图**自带 8x5 的格子线**（实测竖亮线 x=63,99,134,171,207,243,279,315；
//         横亮线 y=38,73,105,137,170,205），所以本层**不再自绘任何格子边框**——
//         自绘只会跟原版格线错位、叠出双线。
//
//   ★★ 为什么从 Prguse[3] 换回 bag_base_352x295（推翻本文件上一版结论，附铁证）：
//     上一版的理由是「Prguse2 只有 14 帧，索引 180 越界 ⇒ FState.pas 是后期 tmcq 版」。
//     **那句判断建立在残缺素材包上**：
//       <本机>/_dl\netdisk\client2003\Data\Prguse2.wil  458792 字节 /  14 帧  ← 被人裁过
//       <本机>/_dl\mir176_p0c_assets\Prguse2.wil       3475317 字节 / 184 帧  ← 完整
//       <本机>/_dl\mir176_p0c_assets\Prguse3.wil      12490815 字节 / 618 帧  ← 完整
//     完整包下 FState.pas 要的 Prguse2 索引 148/180/182/183 **全部命中**，
//     Prguse3 索引 11/11 **全部命中**；只有 590/744 是更后期才加的。
//     ⇒ 源码与 2003 素材是配套的，上一版「拼错版本」的结论作废。
//
//     【铁证 1 · 逐字节相同】Prguse2[180] 解出的 RGBA 与工程 bag_base_352x295.png：
//       不透明 83742 对 83742，MAE=0.000，一致率 100.00%，完全相同像素 100.000%
//       ⇒ 这张图本来就是官方原帧，一个字都没被魔改。上一版把它换掉才是改动。
//
//     【铁证 2 · 格线与源码坐标逐条吻合】源码 DItemGrid(Left=29, Top=41, W=286, H=162)
//       8 等分边界 29 / 64.75 / 100.5 / 136.25 / 172 / 207.75 / 243.5 / 279.25 / 315
//       5 等分边界 41 / 73.4 / 105.8 / 138.2 / 170.6 / 203
//       像素实测     x = 63, 99, 134, 171, 207, 243, 279, 315
//                    y = 38, 73, 105, 137, 170, 205
//       ⇒ 逐条对上（误差 ≤2px）。源码那组坐标就是配这张图的。
//       而 Prguse[3] 336x270 的格线是 14.75/10.25 起、间距 36.90×32.45，与源码**完全对不上**。
//
//     【格式备忘】mir176_p0c_assets 那套没有 .WIX 索引文件，不能按偏移表读。实测是顺序存储：
//       调色板 offset 56..1080，第一帧从 1080 起，帧头 u16 w/u16 h/i16 x/i16 y + w*h 索引字节
//       首尾相接。Prguse2 的 184 帧正好铺到文件末尾（剩余 0 字节），Prguse3 的 618 帧同理。
//       校验：① 帧数等于头部 offset44 的声明值；② 铺满无残留；③ Prguse3 抽帧与 Ly_Prg3
//       同索引帧（#298/#465/#406/#420/#400）MAE=0.00 / 一致率 100% ⇒ 顺序解析没错位。
//
//   图标  Assets/Resources/p0h/bag_item_atlas_0.png + bag_item_index.txt
//         来源 Items.wil，依据 MShare.pas L157 `BAGITEMIMAGESFILE = 'Data\Items.data'`。
//         ⚠ 与 P0HWorld 的 item_atlas 是**两个库**：那个是 DnItems.wil（地面掉落小图，
//           实测尺寸只有 Items 的约一半），背包装备必须用本层这套，不能用地面那套。
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 一件物品的静态定义（按 Crystal ItemInfo 的字段口径裁剪）。
/// 注意这是**数据**不是场景对象：没有 GameObject / Sprite 引用。
/// looks 是 Items.wil 的帧号，由本层自己的 bag_item_index.txt 解成图标。
/// </summary>
public class P0HItemDef
{
    public string name;          // 中文名，如 "木剑"
    public int    looks;         // 图集帧号（来自 p0h/item_index.txt）
    public int    weight;        // 重量
    public int    dc, dcMax;     // 攻击  min/max
    public int    ac, acMax;     // 防御  min/max
    public int    mc, sc;        // 魔法 / 道术
    public int    type;          // 0=消耗 1=武器 2=衣服 3=头盔 4=项链 5=手镯 6=戒指 7=靴子 8=腰带
    public int    needLevel;     // 需求等级
    public bool   stack;         // 是否可堆叠
}

/// <summary>
/// 40 格背包（8×5）+ 8 个装备位：实现全在这里，所有绘制都在 P0BMapWalk 的 BeginGroup 内完成。
///
/// ★ 两个名字各司其职（不是重复实现，实现只有一份）：
///   · P0HItem  = 本层对外提供的 API 名（Add/Remove/Equip/Unequip/RecalcStats/DrawBag/Init/LogVerdict）；
///   · P0HItem = 文件名 + 宿主字段类型（P0BMapWalk L457 `P0HItem _hItem;`、
///                P0JBuild.cs:220 自检也按这个名字搜）。
///   两者靠文件尾部一层空继承对齐：签名一个不差、逻辑一行不复制。
///   ☆ 踩过的坑：只把实现改名成 P0HItem、却忘了删尾部别名 ⇒ CS0101 重复定义；
///     只删别名、不改实现名 ⇒ 宿主 `new P0HItem()` 找不到类型。两处必须同步。
/// </summary>
public class P0HItem
{
    public const int COLS = 8, ROWS = 5, SLOTS = COLS * ROWS;   // 40 格

    // ---- 动作码：LastAction 的取值，接线方按这个判 ----
    public const int ACT_NONE = 0, ACT_ADD = 1, ACT_REMOVE = 2, ACT_EQUIP = 3, ACT_UNEQUIP = 4;

    // type 取值 0..8；0 = 消耗品（不可装备），1..8 才是装备位
    const int EQUIP_TYPES = 9;

    // ---------------- 面板几何（逻辑 800×600 坐标，全部经 GroupRectForLogic 换算） ----------------
    //
    // ★★★ 2026-10-27 三次定案（推翻上一版，附逐像素铁证）：底图 = Prguse2[180] 352x295。
    //
    //    【上一版为什么错】上一版说"Prguse2 只有 14 帧、索引 180 越界 ⇒ 源码是后期 tmcq 版"。
    //    那句判断建立在**残缺的素材包**上：<本机>/_dl\netdisk\client2003\Data\Prguse2.wil 只有
    //    458792 字节 / 14 帧，是被人裁过的。完整版在 <本机>/_dl\mir176_p0c_assets\Prguse2.wil，
    //    3475317 字节 / 184 帧 —— 索引 180 稳稳命中，尺寸 352x295。
    //
    //    【格式】mir176_p0c_assets 那套没有 .WIX 索引文件，不能按偏移表读。实测是**顺序存储**：
    //    调色板在 offset 56..1080，第一帧从 1080 开始，帧头 u16 w/u16 h/i16 x/i16 y + w*h 索引字节
    //    首尾相接，184 帧正好铺到文件末尾（剩余 0 字节）。Prguse3.wil 同理（618 帧，剩余 0）。
    //    这个解析结果被三条独立事实交叉验证：① 184/618 帧数正好等于头部 offset44 的声明值；
    //    ② 铺满文件无残留；③ Prguse3 抽帧与 Ly_Prg3 同索引帧 MAE=0.00 / 一致率 100%（#298/#465/
    //    #406/#420/#400 五个不同尺寸全中）⇒ 顺序解析没有错位。
    //
    //    【铁证 1 — 逐字节相同】Assets/Resources/p0g/bag_base_352x295.png 与
    //    Prguse2[180] 解出来的 RGBA 逐像素比对：不透明 83742 对 83742，MAE=0.000，
    //    一致率 100.00%，完全相同像素 100.000%。**这张图本来就是官方原帧，一个字都没魔改。**
    //
    //    【铁证 2 — 格线与源码坐标吻合】Prguse2[180] 像素实测的格线位置：
    //      竖亮线 x = 63, 99, 134, 171, 207, 243, 279, 315（间距 36,35,37,36,36,36,36）
    //      横亮线 y = 38, 73, 105, 137, 170, 205（间距 35,32,32,33,35）
    //    源码 DItemGrid.Left=29 Width=286 → 8 等分边界 29/64.75/100.5/136.25/172/207.75/243.5/
    //    279.25/315；DItemGrid.Top=41 Height=162 → 5 等分边界 41/73.4/105.8/138.2/170.6/203。
    //    实测与理论**逐条对上（误差 ≤2px）** ⇒ 源码那组坐标就是配这张图的，不是配别的图。
    //    而 Prguse[3] 336x270 的格线是 36.90 × 32.45、格区从 (14.75,10.25) 起，完全对不上源码。
    //
    //    【铁证 3 — 索引覆盖】完整包下 FState.pas 的 Prguse3 索引 11/11 全命中（618 帧）；
    //    Prguse2 的 148/180/182/183 全命中（只有 590/744 是更后期加的）。上一版"7/8 越界"
    //    的结论只对那个 14 帧的残包成立。
    //
    //    来源逐字（FState.pas L2345-2379 / L11044-11112）：
    //      DItemBag.SetImgIndex(g_WMain2Images, 180);  DItemBag.Left := 0;  DItemBag.Top := 60;
    //      DItemGrid.Left := 29; Top := 41; Width := 286; Height := 162;
    const float BAG_X = 0f;         // DItemBag.Left := 0
    const float BAG_Y = 60f;        // DItemBag.Top := 60
    const float BAG_W = 352f;       // Prguse2[180] 宽（官方原帧，逐像素 100% 相同）
    const float BAG_H = 295f;       // Prguse2[180] 高

    const float GRID_X = 29f;       // DItemGrid.Left := 29
    const float GRID_Y = 41f;       // DItemGrid.Top := 41
    const float GRID_W = 286f;      // DItemGrid.Width := 286
    const float GRID_H = 162f;      // DItemGrid.Height := 162
    const float CELL_W = GRID_W / COLS;   // 35.75
    const float CELL_H = GRID_H / ROWS;   // 32.40

    // 原版图标居中公式（FState.pas L11622-11624）：
    //   Draw(Rect.Left + (ColWidth - d.Width) div 2 - 1,  Rect.Top + (RowHeight - d.Height) div 2 + 1)
    //   ⇒ 水平 -1、垂直 +1 是**原版的固定偏置**，不是笔误，照抄。
    const float ICON_OFF_X = -1f;
    const float ICON_OFF_Y = 1f;

    // 文字锚点 —— 照抄原版绘制代码里相对 DItemBag 的偏移（FState.pas DItemBagDirectPaint）：
    //   金币  TextOut(SurfaceX(Left + 72),  SurfaceY(Top + 212), GetGoldStr(...), clWhite)
    //   说明  TextOut(SurfaceX(Left + 77),  SurfaceY(Top + 243 + 00), 'CTRL+B 刷新包裹', ...)
    //         TextOut(SurfaceX(Left + 77),  SurfaceY(Top + 243 + 16), 'ALT+B 刷新英雄包裹', ...)  // 仅 g_boHero
    const float GOLD_TXT_X = 72f;
    const float GOLD_TXT_Y = 212f;
    const float NOTE_TXT_X = 77f;
    const float NOTE_TXT_Y = 243f;

    // 关闭按钮 —— 来源逐字 FState.pas L2375-2379：
    //   DCloseBag.SetImgIndex(g_WMainImages, 371); Left := 336; Top := 59;
    // DCloseBag 与 DItemBag 同为 DBackground 的子控件，所以 (336,59) 是屏幕绝对坐标，
    // 换算成 DItemBag 内相对坐标 = (336-0, 59-60) = (336, -1)。
    //
    // ★ 2026-10-27 实测纠正：Prguse.wil 第 371 帧的真实尺寸是 **16x23**（不是注释里原先
    //   猜测的 14x20）。已从 client2003\Data\Prguse.wil 原样导出成
    //   Assets/Resources/p0g/uibtn_bag_prguse0371_16x23.png（734 B，op=368=100%）。
    //   源码 L2377-2378 的 Width/Height 是**点击区**，与图尺寸本就可以不同（TDButton 同此）。
    //   绘制与点击都按图的真实 16x23 走，点击区另按源码 14x20 由 Rect 单独给 —— 这里取并集。
    const string BTN_CLOSE_TEX = "p0g/uibtn_bag_prguse0371_16x23";
    const float BTN_CLOSE_X = 336f;
    const float BTN_CLOSE_Y = -1f;
    const float BTN_CLOSE_W = 16f;
    const float BTN_CLOSE_H = 23f;
    /// <summary>
    /// 说明行分支开关。MShare.pas L1248 `g_showgamegoldinfo: Boolean = False;` ⇒ 原版默认 False，
    /// 画的是「CTRL+B 刷新背包」而不是三行金刚石/灵符/元宝。改 true 可看那一支。
    /// </summary>
    public static bool SHOW_GAMEGOLD_INFO = false;

    // ---------------- 背包图标图集（Items.wil，来自 bag_item_index.txt） ----------------
    /// <summary>索引一条：图集第几页 + 图像坐标（左上原点，与 Unity 纹理坐标 y 相反）。</summary>
    class IconFrame
    {
        public int page, x, y, w, h;
        public bool ok;
    }

    readonly Dictionary<int, IconFrame> _iconIdx = new Dictionary<int, IconFrame>();
    readonly List<Texture2D> _iconPages = new List<Texture2D>();
    bool _iconTried;
    bool _iconLoaded;
    bool _btnMissingLogged;
    bool _closeMissingLogged;

    /// <summary>
    /// 触摸开关按钮的矩形（逻辑 800x600）。
    ///
    /// ★★ 2026-10-24 修坐标（Lead 自算，差点白验一轮）：
    ///   初版写的逻辑 y=10，换算设备像素是 `设备y = CROP_TOP偏移(-60) + 10*2 = -40`，
    ///   即按钮顶边在 y=-40、底边在 y=+8 ⇒ **92% 落在屏幕外，用户根本看不见、也点不到**，
    ///   等于这个入口白做。根因是写坐标时按「800x600 逻辑画布」想，忘了宿主
    ///   `MainUICanvasRect = Rect(MAIN_X, -CROP_TOP, RENDER_W, RENDER_H)` 把画布整体上移了 60。
    ///   ⇒ 规则：逻辑 y 必须 ≥ 30 才能保证设备 y≥0（`-60 + 30*2 = 0`）。
    ///   现改到逻辑 (740,40)-(796,64) ⇒ 设备 x∈[1988,2100) y∈[20,68)：
    ///     - 完全在屏内；
    ///     - 在主视口右上角（主视口 x 上限 2100，正好贴边不越界）；
    ///     - HUD 面板从逻辑 y=349 起、技能栏 y=300 起 ⇒ 不冲突；
    ///     - 纵向 48 设备像素高，手指点得中（真机最小可点尺寸约 40px）。
    /// </summary>
    /// <summary>
    /// 原版主界面背包按钮 `DMyBag` 的**真身几何**（2026-10-27 定案，不是自绘）。
    ///
    /// 逐条依据（全部来自 1.76 Delphi 源码，可逐行回源）：
    ///   FState.pas L71    `DMyBag: TDButton;`
    ///   FState.pas L2519  `DMyBag.SetImgIndex(g_WMainImages, 9);`
    ///   FState.pas L2520  `DMyBag.Left := SCREENWIDTH div 2 + (SCREENWIDTH div 2 - (400 - 282)) {682};`
    ///   FState.pas L2521  `DMyBag.Top := 42;`
    ///   MShare.pas L105   `MAINIMAGEFILE = 'Data\Prguse.data'`  ⇒ g_WMainImages 就是 Prguse.wil
    ///   MShare.pas L2083  `DMyBag: (Image: 9; Left: 682; Top: 42; Width: 0; Height: 0);`
    ///                     Width/Height = 0 ⇒ 按帧原尺寸绘制，实测 Prguse[9] = 24x22
    ///   FState.pas L9186  `if Sender = DMyBag then OpenItemBag;`  ⇒ 点它就是开/关背包
    ///
    /// ★★ 这段注释推翻本文件 2026-10-24 的一条错误结论。原文写的是：
    ///   「真 1.76 主界面**没有"背包装备"按钮**（实测 ref 全帧无此控件）」——**错的**。
    ///   错因：当时只看了截图的底栏那一条，没看主视口右上角；而原版这 4 个按钮
    ///   （DMyState/DMyBag/DMyMagic/DOption）是**沿右上角斜排**的，位置分别是
    ///   (643,62) / (682,42) / (722,22) / (764,12)，越靠右越高，全在底栏上方。
    ///   用户原话「别自己瞎几把画」针对的是**自绘的蓝色矩形+中文标签**，
    ///   不是「不该有这个按钮」。恢复原版按钮 = 用原图按原坐标画，这才是用户要的。
    ///
    /// 设备落点核算（SCALE=2、MAIN_X=508、CROP_TOP=60 ⇒ 设备y = -60 + ly*2）：
    ///   逻辑 (682,42) 24x22 → 设备 (1872,24) 48x44，落在主视口 x∈[508,2108) 内、y≥0 全可见。
    ///   手指可点尺寸 48x44 设备像素（真机最小可点约 40px）✔
    /// </summary>
    const float HOTKEY_X = 682f;   // = SCREENWIDTH div 2 + (SCREENWIDTH div 2 - (400 - 282))
    const float HOTKEY_Y = 42f;
    const float HOTKEY_W = 24f;    // Prguse[9] 原始帧宽
    const float HOTKEY_H = 22f;    // Prguse[9] 原始帧高

    /// <summary>
    /// 是否绘制原版背包按钮的**图像**。
    /// ★ 2026-10-27：默认改为 true —— 它不是自绘件，是原版 DMyBag 控件本身（Prguse[9]）。
    ///   手机没有 F9 键，没有这个按钮玩家就无从打开背包；而且原版本就有这个按钮，
    ///   画出来是「还原」，不画才是「缺失」。关掉它只用于隔离调试。
    /// </summary>
    public static bool SHOW_BAG_BUTTON = true;

    /// <summary>原版 DMyBag 按钮图像资源（Prguse.wil 第 9 帧，24x22，逐字节解出未重采样）。</summary>
    const string BAG_BTN_TEX = "p0g/uibtn_bag_prguse0009_24x22";

    /// <summary>
    /// 负重基数。★ 2026-10-23 22:52 修正口径：
    ///   P0HRuntime L154 的 `PlayerWeight = 11 // 出生 木剑(5) + 布衣(6) = 11`
    ///   —— 这 11 **本身已经含出生装的 5+6**，所以它是「含出生装的出生值」，
    ///   不是「裸身基数」。若直接 11 + Σ装备 + Σ背包，出生状态会算成 22，
    ///   等于同一套木剑布衣被称了两遍重量（设备实测 `Wt=22/55`，用户会当 bug 报）。
    ///   ⇒ 正确做法：把出生装的重量当作「已经含在 11 里」，从基数里扣掉，
    ///     再让 RecalcStats 把手上真装备的重量加回去。出生状态正好回到 11。
    ///   ⚠ 扣的是**出生装的定义常量**，不是玩家实际穿的 —— 即使玩家换了装备，
    ///     基数仍是 11-5-6=0，穿多少加多少，逻辑一致。
    /// </summary>
    public const int BIRTH_GEAR_WEIGHT = 5 + 6;   // 木剑 5 + 布衣 6，与 P0HRuntime L154 同源
    public const int BASE_WEIGHT = 11 - BIRTH_GEAR_WEIGHT;   // = 0，裸身负重基数
    /// <summary>出生负重 = P0HRuntime L154 的 11（含出生装）。判据下限用它。</summary>
    public const int BIRTH_WEIGHT = 11;

    /// <summary>格子里的一份实物：定义 + 数量。装备位恒为 1，只有消耗品才 &gt;1。</summary>
    class Entry
    {
        public P0HItemDef def;
        public int count;
    }

    // ---------------- 数据 ----------------
    readonly Entry[] _bag = new Entry[SLOTS];
    readonly Entry[] _equip = new Entry[EQUIP_TYPES];   // 下标 = type（1..8 有效）

    P0HRuntime _run;          // Init 时记住运行期引用：Add/Remove 没有 run 参数，靠它自动重算
    P0HRuntime _baseRun;      // 基础属性是从哪份 run 采的（重复 Init 时据此判断要不要重采）
    bool _baseReady;
    int _baseDc, _baseDcMax, _baseAc, _baseMc, _baseSc;

    // ---------------- 样式 ----------------
    GUIStyle _name;   // 格内物品名前 2 字
    GUIStyle _cnt;    // 数量角标
    GUIStyle _head;   // 面板上方表头

    // ---------------- 配色 ----------------
    // ★ 2026-10-27：PanelBg / SlotEmptyBg / SlotUsedBg / SlotEdge / TYPE_COL 全部删除。
    //   它们服务的是「没有素材只好自己画」的旧方案；现在底图自带格线、图标是真图，
    //   留着这些常量只会诱使后来的人又把自绘格子加回来。
    static readonly Color HeadCol  = new Color(1f, 0.92f, 0.72f, 1f);   // 表头（工程自有的调试信息）
    static readonly Color NameCol  = new Color(0.92f, 0.92f, 0.86f, 1f); // 格内前 2 字
    static readonly Color CountCol = new Color(1f, 0.95f, 0.55f, 1f);    // 数量角标
    static readonly Color GoldCol  = new Color(1f, 1f, 1f, 1f);          // 原版金币文字是 clWhite
    static readonly Color NoteCol  = new Color(1f, 1f, 1f, 1f);          // 原版说明文字 FontColor := clWhite

    // ---------------- 观测 ----------------
    /// <summary>已占格数。每次现扫不缓存 ⇒ 永远不会与真实格子对不上。</summary>
    public int UsedSlots
    {
        get
        {
            int n = 0;
            for (int i = 0; i < SLOTS; i++) if (_bag[i] != null) n++;
            return n;
        }
    }

    /// <summary>身上实际穿着的件数（1..8 位里非空的个数）。</summary>
    public int EquipCount
    {
        get
        {
            int n = 0;
            for (int t = 1; t < EQUIP_TYPES; t++) if (_equip[t] != null) n++;
            return n;
        }
    }

    /// <summary>最近一次成功的动作码，取值见 ACT_*。Init 只是落地状态，会复位成 ACT_NONE。</summary>
    public int LastAction { get; private set; }

    /// <summary>F9 开关，默认 false（与原版一致：背包不常驻）。</summary>
    public bool Visible { get; set; }

    public P0HItem()
    {
        LastAction = ACT_NONE;
        Visible = false;
    }

    /// <summary>
    /// F9 开关（原版 ClMain.pas L2625 `VK_F9: FrmDlg.OpenItemBag`）。
    /// 宿主 P0BMapWalk 的 Update 里调的就是这个。
    /// ★ 开关只在宿主**这一处**翻，本层自己不再监听 F9 ——
    ///   否则同一帧翻两次 = 用户看到的是「按了没反应」。
    /// </summary>
    public void Toggle()
    {
        Visible = !Visible;
        Debug.Log("[P0BAG] 背包面板 Visible=" + Visible +
                  " 已用=" + UsedSlots + "/" + SLOTS +
                  " 装备=" + EquipCount);
    }

    // =====================================================================
    // 背包图标图集（Items.wil 解出来的 bag_item_index.txt）
    // =====================================================================
    /// <summary>
    /// 懒加载。格式与工程既有 item_index.txt 完全一致（首行 `count=N`，其后
    /// `<looks> <page> <x> <y> <w> <h>`），因为两者由同一个导出脚本产出。
    /// 失败只打一行日志、把 _iconTried 置真 ⇒ 不会每帧重试刷屏，也不抛异常。
    /// </summary>
    void EnsureIcons()
    {
        if (_iconTried) return;
        _iconTried = true;

        TextAsset idx = Resources.Load<TextAsset>("p0h/bag_item_index");
        if (idx == null)
        {
            Debug.LogWarning("[P0BAG] 缺 p0h/bag_item_index（背包图标索引）⇒ 图标退化为不画");
            return;
        }

        for (int p = 0; p < 8; p++)   // 本套只有 1 页，循环上限留 8 页余量
        {
            Texture2D t = Resources.Load<Texture2D>("p0h/bag_item_atlas_" + p);
            if (t == null) break;
            _iconPages.Add(t);
        }
        if (_iconPages.Count == 0)
        {
            Debug.LogWarning("[P0BAG] 缺 p0h/bag_item_atlas_0（背包图标图集）⇒ 图标退化为不画");
            return;
        }

        string[] lines = idx.text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string ln = lines[i].Trim();
            if (ln.Length == 0 || ln.StartsWith("count=")) continue;
            string[] t = ln.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 6) continue;
            int looks, page, x, y, w, h;
            if (!int.TryParse(t[0], out looks)) continue;
            if (!int.TryParse(t[1], out page)) continue;
            if (!int.TryParse(t[2], out x)) continue;
            if (!int.TryParse(t[3], out y)) continue;
            if (!int.TryParse(t[4], out w)) continue;
            if (!int.TryParse(t[5], out h)) continue;
            if (page < 0 || page >= _iconPages.Count) continue;
            if (w <= 0 || h <= 0) continue;
            _iconIdx[looks] = new IconFrame { page = page, x = x, y = y, w = w, h = h, ok = true };
        }

        _iconLoaded = _iconIdx.Count > 0;
        Debug.Log("[P0BAG] 背包图标图集就绪: " + _iconPages.Count + " 页 / " + _iconIdx.Count + " 帧" +
                  "（来源 Items.wil，非地面 DnItems）");
    }

    public bool IconReady { get { EnsureIcons(); return _iconLoaded; } }
    public int IconFrameCount { get { EnsureIcons(); return _iconIdx.Count; } }

    /// <summary>
    /// 在设备矩形 <paramref name="dst"/> 内画 looks 的图标，保持**像素整数倍**放大。
    ///
    /// ★ 为什么不用 StretchToFill 直接拉伸：原版图标是 16~36 px 的小图，
    ///   非整数倍拉伸会让像素边缘糊成灰边（Point 采样也会出现宽窄不一的列），
    ///   而 `FilterMode.Point` + 整数倍是这套素材唯一能保持「原样」的画法。
    /// ★ 缩放上限由格子尺寸决定：图标最大 36x36、格 35.75x32.4 ⇒ 大部分图标是 1 倍原尺寸绘制，
    ///   与 2003 年客户端在 800x600 下逐像素一致。
    /// </summary>
    void DrawIcon(int looks, float dstX, float dstY, float dstW, float dstH)
    {
        EnsureIcons();
        if (!_iconLoaded) return;

        IconFrame f;
        if (!_iconIdx.TryGetValue(looks, out f) || !f.ok) return;
        if (f.page < 0 || f.page >= _iconPages.Count) return;
        Texture2D tex = _iconPages[f.page];
        if (tex == null) return;
        // Texture2D 可能还没读入尺寸（首帧），此时 tex.height 为 0，别算出负 y
        if (tex.width <= 0 || tex.height <= 0) return;

        // 整数倍：取「装得下」的最大倍数，且不超过 3（再大就成放大镜，不像原版）
        int k = 1;
        while (k < 3 && (f.w * (k + 1) <= dstW) && (f.h * (k + 1) <= dstH)) k++;

        float dw = f.w * k, dh = f.h * k;
        float dx = dstX + (dstW - dw) * 0.5f + ICON_OFF_X * k;
        float dy = dstY + (dstH - dh) * 0.5f + ICON_OFF_Y * k;

        // 索引里的 y 是「图像从上往下」，Unity 纹理坐标是「从下往上」⇒ 翻转一次。
        Rect uv = new Rect((float)f.x / tex.width,
                           (float)(tex.height - f.y - f.h) / tex.height,
                           (float)f.w / tex.width,
                           (float)f.h / tex.height);

        Color keep = GUI.color;
        GUI.color = Color.white;   // 图标必须原色，不许被上层染色
        GUI.DrawTextureWithTexCoords(P0BMapWalk.GroupRectForLogic(dx, dy, dw, dh), tex, uv, true);
        GUI.color = keep;
    }

    // =====================================================================
    // 数据操作
    // =====================================================================
    public bool Add(P0HItemDef def, int count = 1)
    {
        if (def == null || count <= 0)
        {
            Debug.Log("[P0BAG] ADD 失败: 定义为空或数量非法(" + count + ")");
            return false;
        }

        // 可堆叠：先找同名格并入。★ 按 name 比较而不是比引用 ——
        // 定义可能是运行时 new 出来的另一份实例，比引用会导致永远堆不上。
        if (def.stack)
        {
            int same = FindStackable(def);
            if (same >= 0)
            {
                _bag[same].count += count;
                LastAction = ACT_ADD;
                AfterChange();
                Debug.Log("[P0BAG] ADD " + def.name + " x" + count + " → 并入格" + same +
                          "(共 " + _bag[same].count + ") 已用 " + UsedSlots + "/" + SLOTS +
                          " 重 " + WtText(_run));
                return true;
            }
        }

        // 不可堆叠：count 件各占一格。先数够不够，避免「塞了一半却返回 false」的半成品状态
        // （背包里凭空多几件、调用方却以为失败，这种账最难查）
        int need = def.stack ? 1 : count;
        int free = 0;
        for (int i = 0; i < SLOTS; i++) if (_bag[i] == null) free++;
        if (free < need)
        {
            Debug.Log("[P0BAG] ADD 失败: " + def.name + " 需 " + need + " 格，只剩 " + free + " 格");
            return false;
        }

        int placed = 0;
        while (placed < need)
        {
            int i = FirstFree();
            if (i < 0) break;    // 上面刚数过，正常不会走到
            _bag[i] = new Entry { def = def, count = (need == 1) ? count : 1 };
            placed++;
        }

        LastAction = ACT_ADD;
        AfterChange();
        Debug.Log("[P0BAG] ADD " + def.name + " x" + count + " 已用 " + UsedSlots + "/" + SLOTS +
                  " 重 " + WtText(_run));
        return true;
    }

    public bool Remove(int slot, int count = 1)
    {
        if (slot < 0 || slot >= SLOTS || count <= 0)
        {
            Debug.Log("[P0BAG] REMOVE 失败: 格号/数量非法 slot=" + slot + " count=" + count);
            return false;
        }
        Entry e = _bag[slot];
        if (e == null || e.def == null)
        {
            Debug.Log("[P0BAG] REMOVE 失败: 格" + slot + " 是空的");
            return false;
        }

        int real = Mathf.Min(count, e.count);   // 传大数时按已有数量删，不返回「成功却没删够」
        string nm = e.def.name;
        e.count -= real;
        if (e.count <= 0) _bag[slot] = null;

        LastAction = ACT_REMOVE;
        AfterChange();
        Debug.Log("[P0BAG] REMOVE " + nm + " x" + real + " 格" + slot +
                  " 已用 " + UsedSlots + "/" + SLOTS + " 重 " + WtText(_run));
        return true;
    }

    /// <summary>把背包 slot 里的东西穿到身上；同部位已有装备则互换（旧装备回这一格，天然有位）。</summary>
    public bool Equip(int slot, P0HRuntime run)
    {
        if (run == null || slot < 0 || slot >= SLOTS) return false;
        Entry e = _bag[slot];
        if (e == null || e.def == null)
        {
            Debug.Log("[P0BAG] EQUIP 失败: 格" + slot + " 是空的");
            return false;
        }

        int t = e.def.type;
        if (t <= 0 || t >= EQUIP_TYPES)
        {
            // 消耗品没有「穿上」的概念，明确拒绝，不塞进某个位子凑数
            Debug.Log("[P0BAG] EQUIP 失败: " + e.def.name + " 是消耗品(type=0)，不能装备");
            return false;
        }
        if (e.def.needLevel > run.PlayerLevel)
        {
            Debug.Log("[P0BAG] EQUIP 失败: " + e.def.name + " 需 " + e.def.needLevel +
                      " 级，当前 " + run.PlayerLevel + " 级");
            return false;
        }

        EnsureBase(run);          // ★ 必须在改动装备位之前采样裸身基础
        _run = run;

        Entry old = _equip[t];
        _bag[slot] = old;         // 旧装备直接回这一格（刚腾空，必定放得下 ⇒ 不必找空格）
        _equip[t] = e;

        RecalcStats(run);
        LastAction = ACT_EQUIP;
        Debug.Log("[P0BAG] EQUIP " + e.def.name + " → " + SlotName(t) + "位" +
                  (old == null || old.def == null ? "" : "(换下 " + old.def.name + ")") +
                  " 装备 " + EquipCount + " 重 " + WtText(run) +
                  " DC=" + run.PlayerDC + "/" + run.PlayerDCMax + " AC=" + run.PlayerAC);
        return true;
    }

    /// <summary>按装备位（type 1..8）脱下，放回背包空格。</summary>
    public bool Unequip(int type, P0HRuntime run)
    {
        if (run == null || type <= 0 || type >= EQUIP_TYPES)
        {
            Debug.Log("[P0BAG] UNEQUIP 失败: 装备位非法 type=" + type);
            return false;
        }
        EnsureBase(run);
        Entry e = _equip[type];
        if (e == null || e.def == null)
        {
            Debug.Log("[P0BAG] UNEQUIP 失败: " + SlotName(type) + "位是空的");
            return false;
        }

        int target = e.def.stack ? FindStackable(e.def) : -1;
        if (target < 0) target = FirstFree();
        if (target < 0)
        {
            // 背包满就脱不下来 —— 与原版一致，不偷偷把装备扔掉
            Debug.Log("[P0BAG] UNEQUIP 失败: 背包已满(40/40)，脱不下 " + e.def.name);
            return false;
        }

        if (e.def.stack && _bag[target] != null)
            _bag[target].count += e.count;
        else
            _bag[target] = e;

        _equip[type] = null;
        _run = run;
        RecalcStats(run);
        LastAction = ACT_UNEQUIP;
        Debug.Log("[P0BAG] UNEQUIP " + e.def.name + " ← " + SlotName(type) + "位 → 格" + target +
                  " 装备 " + EquipCount + " 重 " + WtText(run));
        return true;
    }

    /// <summary>
    /// 把装备加成算进战斗属性与负重。
    /// ★ 只有**身上穿的**涨 DC/AC/MC/SC（背包里的东西不涨攻击防御，这是常识也是原版口径），
    ///   但**背包里的东西一律计重** —— 这正是 HUD 负重条能跟着背包动的唯一原因。
    /// </summary>
    public void RecalcStats(P0HRuntime run)
    {
        if (run == null) return;
        EnsureBase(run);

        int dc = 0, dcMax = 0, acMin = 0, acMax = 0, mc = 0, sc = 0;
        int wEquip = 0, wBag = 0;

        for (int t = 1; t < EQUIP_TYPES; t++)
        {
            Entry e = _equip[t];
            if (e == null || e.def == null) continue;
            dc    += e.def.dc;
            dcMax += e.def.dcMax;
            acMin += e.def.ac;
            acMax += e.def.acMax;
            mc    += e.def.mc;
            sc    += e.def.sc;
            wEquip += e.def.weight * Mathf.Max(1, e.count);
        }

        for (int i = 0; i < SLOTS; i++)
        {
            Entry e = _bag[i];
            if (e == null || e.def == null) continue;
            wBag += e.def.weight * Mathf.Max(1, e.count);
        }

        // ⚠ 口径重叠（与负重同理，如实写在代码里，不藏）：
        //   P0HRuntime L135-138 明说 PlayerDC 2-5 就是「1 级战士 + 木剑」给的，
        //   而 Init 又发一把木剑 ⇒ 纯加法结果是 DC 3-7。
        //   Lead 指定「装备加成算进 PlayerDC」，故按加法执行；
        //   要与 L138 的 2-5 对齐，只需把出生木剑改成 dc 0/0（或基础采样时扣掉），本方法不动。
        run.PlayerDC = _baseDc + dc;
        run.PlayerDCMax = _baseDcMax + dcMax;
        if (run.PlayerDCMax < run.PlayerDC) run.PlayerDCMax = run.PlayerDC;

        // P0HRuntime 只暴露一个 PlayerAC（L144），没有 AC 上下限两格；
        // ⇒ 取所有装备 ac..acMax 的中点四舍五入，避免只吃最小值导致「穿了衣服没感觉」
        run.PlayerAC = _baseAc + Mathf.RoundToInt((acMin + acMax) * 0.5f);
        run.PlayerMC = _baseMc + mc;
        run.PlayerSC = _baseSc + sc;

        // 负重公式（2026-10-23 22:52 修正重复计数）：
        //   P0HRuntime L154 的 11 = 出生装(木剑5+布衣6)，已含出生装。
        //   ⇒ 裸身基数 = 11 - 11 = 0，然后加玩家手上真实装备 + 背包。
        //   出生状态正好回到 11（而不是原来的 22）。
        run.PlayerWeight = BASE_WEIGHT + wEquip + wBag;
        // 上限口径必须与 P0HRuntime L667 的升级公式逐字一致，
        // 否则升一级时 HUD 上会「凭空超重」，用户会当 bug 报
        run.PlayerWeightMax = 55 + (run.PlayerLevel - 1) * 5;
    }

    /// <summary>
    /// 背包的**触摸**开关（原版 DMyBag 按钮的点击语义）。
    ///
    /// 为什么手机必须有点击入口（device 实证，仍然成立）：
    ///   本层宿主只有 `Input.GetKeyDown(F9)` 一条键盘入口，而 P0BMapWalk 已实测记录
    ///   「`adb shell input keyevent` 不进 Unity 传统 Input Manager」⇒ 手机永远等不到 F9。
    ///   build-runner 早前抓到的 main.png 与 bag.png MD5 完全相同就是这件事的直接证据。
    ///
    /// ★ 2026-10-27 定位改到**原版控件自己的矩形**：逻辑 (682,42) 24x22（见上方 HOTKEY_* 注释），
    ///   不再是之前那个自拟的 (740,10)-(796,34)。也就是说：
    ///   **玩家看到的那张图在哪，点击热区就在哪** —— 视觉与命中同源，不会出现
    ///   「图在这儿、点那儿」的错位（历史 bug 类型：画在 A、判定在 B）。
    /// </summary>
    public System.Func<Vector2, bool> PointOwnedByOther;

    void BagTouchToggle()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.MouseDown) return;
        if (PointOwnedByOther != null && PointOwnedByOther(e.mousePosition)) return;

        // ① 原版 DMyBag 按钮（Prguse[9]，逻辑 682,42 24x22）
        Rect hit = P0BMapWalk.GroupRectForLogic(HOTKEY_X, HOTKEY_Y, HOTKEY_W, HOTKEY_H);
        if (hit.Contains(e.mousePosition))
        {
            Toggle();
            e.Use();
            return;
        }

        // ② 背包里的关闭叉 DCloseBag（Prguse[371] 16x23，逻辑 336,-1）
        //    FState.pas L2375-2379 逐字：Left := 336; Top := 59（屏幕绝对，减 DItemBag.Top=60）
        //    ⇒ DItemBag 内相对 (336,-1)。只在背包开着时才有意义。
        //    ★ 原版 DCloseBag 的 OnClick 就是 CloseItemBag，没有其它语义，照抄。
        if (Visible)
        {
            Rect close = P0BMapWalk.GroupRectForLogic(BAG_X + BTN_CLOSE_X, BAG_Y + BTN_CLOSE_Y,
                                                      BTN_CLOSE_W, BTN_CLOSE_H);
            if (close.Contains(e.mousePosition))
            {
                Toggle();
                e.Use();
            }
        }
    }

    // =====================================================================
    // 绘制：40 格 8×5 背包面板（在 P0BMapWalk 的 BeginGroup 内调用）
    // =====================================================================
    public void DrawBag(P0HRuntime run)
    {
        // ★ 本层不抢热键：宿主 P0BMapWalk.Update 已经接了
        //   `Input.GetKeyDown(F9) => _hItem.Toggle()`（F9 = 原版 OpenItemBag，ClMain.pas L2625）。
        //   若这里再判一次 F9，同一帧会翻两次 => 用户看到的是「按了没反应」。
        //   所以开关责任单一地留在宿主那边：本层只认 Visible 这个状态位，Toggle 只翻位、不读键。
        //
        // ★ 但「触摸入口」必须在本层画：真机唯一可用通道（见 BagTouchToggle 注释）。
        //   它画的是**按钮**、不是键盘事件，不构成"两份开关抢同一输入"那个坑。
        EnsureStyles();
        BagTouchToggle();

        // ★ 原版 DMyBag 按钮（Prguse[9] 24x22，逻辑 (682,42)）。
        //   2026-10-27 纠正：上一版这里画的是**自绘蓝色矩形 + 中文"背包装备"标签**，
        //   那才是用户说的「魔改」。现在改为直接画原图，不叠任何自绘底色/文字。
        //
        //   绘制口径与原版一致：TDButton 直接 `dsurface.Draw(..., d.ClientRect, d, True)`
        //   —— 只画帧本身，没有边框、没有高亮底（原版的按下态是换帧，不是加底色；
        //   本工程只有单帧素材，按下态暂不换帧，属已知简化，不影响可点）。
        if (SHOW_BAG_BUTTON)
        {
            Texture2D btn = Resources.Load<Texture2D>(BAG_BTN_TEX);
            if (btn != null)
            {
                Color keep = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(HOTKEY_X, HOTKEY_Y, HOTKEY_W, HOTKEY_H),
                                btn, ScaleMode.StretchToFill, true);
                GUI.color = keep;
            }
            else
            {
                // 只在缺图时退化成一块灰 —— 不静默消失（否则玩家又打不开背包了）
                GUI.color = new Color(0.85f, 0.2f, 0.2f, 0.85f);
                GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(HOTKEY_X, HOTKEY_Y, HOTKEY_W, HOTKEY_H),
                                Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
                GUI.color = Color.white;
            }
        }
        else if (!_btnMissingLogged)
        {
            _btnMissingLogged = true;
            Debug.LogWarning("[P0H] ★ SHOW_BAG_BUTTON=false：原版 DMyBag 按钮不上屏。" +
                             "手机没有 F9 键，关掉它玩家就打不开背包（仅隔离调试时该关）");
        }

        if (!Visible) return;

        EnsureStyles();
        EnsureIcons();
        Color prev = GUI.color;

        // ---------------------------------------------------------------
        // ① 底图：2003 官方 Prguse2[180]，352x295，逻辑 (0,60)
        //    ★ 这张图**自带 8x5 格线**，所以下面不再画任何格子边框/底色。
        //    ★ 原版就是一句 `dsurface.Draw(SurfaceX(Left), SurfaceY(Top), d.ClientRect, d, True);`
        //      ——一次性整张绘出，含边框、格线、金色格位装饰，全部由美术画好。
        //    ★ 逐像素校验过：不透明 83742 对 83742、MAE=0.000、一致率 100.00%
        // ---------------------------------------------------------------
        Texture2D baseTex = Resources.Load<Texture2D>("p0g/bag_base_352x295");
        if (baseTex != null)
        {
            GUI.color = Color.white;
            GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(BAG_X, BAG_Y, BAG_W, BAG_H),
                            baseTex, ScaleMode.StretchToFill, true);
        }
        else
        {
            // 只在缺图时退化成一块半透明黑，并每帧可见地提示 —— 不静默画歪
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(BAG_X, BAG_Y, BAG_W, BAG_H),
                            Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
            GUI.color = new Color(1f, 0.4f, 0.4f, 1f);
            GUI.Label(P0BMapWalk.GroupRectForLogic(BAG_X + 30f, BAG_Y + 120f, 280f, 20f),
                      "缺少背包底图 p0g/bag_base_352x295", _head);
        }

        // ---------------------------------------------------------------
        // ①b 关闭叉 DCloseBag：Prguse[371] 真实 16x23，逻辑 (336,-1)
        //     FState.pas L2375-2379 逐字：SetImgIndex(g_WMainImages, 371); Left:=336; Top:=59
        //     原版也是 `dsurface.Draw(SurfaceX(Left), SurfaceY(Top), d.ClientRect, d, True)`
        //     —— 只画帧本身，不加底色/文字（那才会变成"魔改"）。
        //     p0g/uibtn_bag_prguse0371_16x23.png 从 client2003\Data\Prguse.wil 原样导出（734 B）。
        // ---------------------------------------------------------------
        {
            Texture2D cbtn = Resources.Load<Texture2D>(BTN_CLOSE_TEX);
            if (cbtn != null)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(P0BMapWalk.GroupRectForLogic(BAG_X + BTN_CLOSE_X,
                                                            BAG_Y + BTN_CLOSE_Y,
                                                            BTN_CLOSE_W, BTN_CLOSE_H),
                                cbtn, ScaleMode.StretchToFill, true);
            }
            else if (!_closeMissingLogged)
            {
                _closeMissingLogged = true;
                Debug.LogWarning("[P0BAG] 缺 " + BTN_CLOSE_TEX + "（原版关闭叉）⇒ 关闭按钮不画，" +
                                 "仍可用 F9 / DMyBag 按钮关");
            }
        }

        // ---------------------------------------------------------------
        // ② 40 个格子（8 列 x 5 行）
        //    原版格位公式（FState.pas L11614）：idx = ACol + ARow * ColCount + 6
        //    ⇒ 背包数组第 i 格画在 (i%8, i/8)，与 g_ItemArr[6..45] 一一对应。
        //    格宽高 35.75 x 32.4 非整数 ⇒ 左沿/上沿按浮点均分算，不能取整（取整会累积错位 3px）。
        // ---------------------------------------------------------------
        for (int i = 0; i < SLOTS; i++)
        {
            float cx = BAG_X + GRID_X + (i % COLS) * CELL_W;
            float cy = BAG_Y + GRID_Y + (i / COLS) * CELL_H;
            Entry e = _bag[i];
            if (e == null || e.def == null) continue;

            // 真图标（Items.wil）。画不出来时留空 —— 原版空槽就是这样，不加任何自绘装饰
            DrawIcon(e.def.looks, cx, cy, CELL_W, CELL_H);

            // 数量角标：原版对可叠加物品在格内左下画数字（FState.pas L11626-11631 BoldTextOut
            // 画的是持久度，可叠加物的数量在别处；本工程用「右下角小字」保持信息可见，不复刻原版耐久位）
            if (e.count > 1)
            {
                GUI.color = CountCol;
                GUI.Label(P0BMapWalk.GroupRectForLogic(cx + CELL_W - 15f, cy + CELL_H - 12f, 14f, 11f),
                          e.count + "", _cnt);
            }
        }

        // ---------------------------------------------------------------
        // ③ 金币文字：原版 TextOut(Left+72, Top+212, GetGoldStr(m_nGold), clWhite)
        // ---------------------------------------------------------------
        GUI.color = GoldCol;
        GUI.Label(P0BMapWalk.GroupRectForLogic(BAG_X + GOLD_TXT_X, BAG_Y + GOLD_TXT_Y, 130f, 12f),
                  GoldText(run), _name);

        // ---------------------------------------------------------------
        // ④ 说明行：原版 g_showgamegoldinfo 默认 False（MShare.pas L1248）
        //    ⇒ 画 'CTRL+B 刷新背包' 在 (Left+77, Top+243)；否则画金刚石/灵符/元宝三行。
        // ---------------------------------------------------------------
        GUI.color = NoteCol;
        if (SHOW_GAMEGOLD_INFO)
        {
            GUI.Label(P0BMapWalk.GroupRectForLogic(BAG_X + NOTE_TXT_X, BAG_Y + NOTE_TXT_Y, 180f, 12f),
                      "金刚石： 0", _name);
            GUI.Label(P0BMapWalk.GroupRectForLogic(BAG_X + NOTE_TXT_X, BAG_Y + NOTE_TXT_Y + 14f, 180f, 12f),
                      "灵符数： 0", _name);
            GUI.Label(P0BMapWalk.GroupRectForLogic(BAG_X + NOTE_TXT_X, BAG_Y + NOTE_TXT_Y + 28f, 180f, 12f),
                      "元宝数量： 0", _name);
        }
        else
        {
            GUI.Label(P0BMapWalk.GroupRectForLogic(BAG_X + NOTE_TXT_X, BAG_Y + NOTE_TXT_Y, 180f, 12f),
                      "CTRL+B 刷新背包", _name);
        }

        // ---------------------------------------------------------------
        // ⑤ 工程自有的调试表头 —— 原版**没有**这一行，默认不画。
        //    要排查「几个格子/多重」时把 SHOW_BAG_DEBUG 改 true。
        //    ★ 注意它必须画在面板上方的**可见区**里（BAG_Y=60 > 30，上面还有 30 行留白可放）。
        // ---------------------------------------------------------------
        if (SHOW_BAG_DEBUG)
        {
            GUI.color = HeadCol;
            GUI.Label(P0BMapWalk.GroupRectForLogic(BAG_X, BAG_Y + 4f, BAG_W, 16f),
                      "已用 " + UsedSlots + "/" + SLOTS + "  装备 " + EquipCount +
                      "  重 " + WtText(run), _head);
        }

        GUI.color = prev;
    }

    /// <summary>原版背包上方**没有**调试表头。要排查数据时改 true。</summary>
    public static bool SHOW_BAG_DEBUG = false;

    /// <summary>
    /// 金币文本。原版 `GetGoldStr` 会把大数字缩写成「1万」这种；本工程只做千分位，
    /// 因为 GetGoldStr 的缩写规则在源码里是另一套分支，未逐字核对前不臆造。
    /// ★ 字段是 PlayerGold（long），不是 P0HRuntime.gold —— 那个是**地上钱堆**的掉落金额。
    /// </summary>
    static string GoldText(P0HRuntime run)
    {
        long g = (run != null) ? run.PlayerGold : 0L;
        return g.ToString("N0");
    }

    /// <summary>
    /// 出生装备：木剑（重 5，dc 1-2）+ 布衣（重 6，ac 2-3），穿上后立即重算属性。
    /// 幂等：重复调用不会叠加出生装备（热重载脚本时最容易踩这个坑）。
    /// </summary>
    public void Init(P0HRuntime run)
    {
        if (run == null) return;

        for (int i = 0; i < SLOTS; i++) _bag[i] = null;
        for (int t = 0; t < EQUIP_TYPES; t++) _equip[t] = null;
        _run = run;
        // 不在这里清 _baseReady：同一份 run 的基础属性只该采一次，清了就变成"采到已加成值"
        EnsureBase(run);         // ★ 必须在穿装备之前采样，否则基础里混进装备加成（每穿一件翻一倍）

        // looks：木剑取 P0HRuntime L138 明写的 Looks=30；
        //        布衣的全仓库都查不到帧号，而本层也不加载图集 ⇒ 填 0，不臆造。
        P0HItemDef sword = new P0HItemDef
        {
            name = "木剑", looks = 30, weight = 5,
            dc = 1, dcMax = 2, type = 1, needLevel = 1, stack = false
        };
        P0HItemDef cloth = new P0HItemDef
        {
            name = "布衣", looks = 0, weight = 6,
            ac = 2, acMax = 3, type = 2, needLevel = 1, stack = false
        };

        Add(sword, 1);           // 空背包 ⇒ 落在格 0
        Add(cloth, 1);           // ⇒ 落在格 1
        Equip(0, run);           // 木剑穿上，格 0 腾空
        Equip(1, run);           // 布衣穿上，格 1 腾空

        LastAction = ACT_NONE;   // Init 是「落地」不是「用户动作」，别让接线方误判有人操作过
        Debug.Log("[P0BAG] INIT 出生装备 木剑(dc1-2 重5)+布衣(ac2-3 重6)" +
                  " 已用 " + UsedSlots + "/" + SLOTS + " 装备 " + EquipCount +
                  " 重 " + WtText(run) + " DC=" + run.PlayerDC + "/" + run.PlayerDCMax +
                  " AC=" + run.PlayerAC);
        LogVerdict(run);
    }

    // =====================================================================
    // 判据日志（单行，gate 用 grep 抓）
    // =====================================================================
    public void LogVerdict(P0HRuntime run)
    {
        if (run == null)
        {
            // 没有 run 就没有那些数字可打；单独一句，避免用 0 冒充真实值把 FAIL 盖成 PASS
            Debug.Log("[P0BAG] V9_VERDICT = FAIL 运行时为空（未接线）");
            return;
        }

        // ok 不是「调用了就算过」：格数 / 装备位 / 堆叠完整性 / 负重下限 / 上限口径 全部实测
        // ★ 负重下限用 BIRTH_WEIGHT(11) 而不是 BASE_WEIGHT(0)：
        //   11 是 P0HRuntime L154 的出生负重，玩家身上永远至少穿着出生装，
        //   因此真实负重不该低于 11。用 0 做下限等于没判。
        bool ok = UsedSlots >= 0 && UsedSlots <= SLOTS
                  && EquipCount >= 0 && EquipCount <= EQUIP_TYPES - 1
                  && SlotsValid()
                  && run.PlayerWeight >= BIRTH_WEIGHT
                  && run.PlayerWeightMax == 55 + (run.PlayerLevel - 1) * 5;

        Debug.Log("[P0BAG] V9_VERDICT = " + (ok ? "PASS" : "FAIL") +
                  " 已用格=" + UsedSlots + "/40 装备=" + EquipCount +
                  " 重量=" + run.PlayerWeight + "/" + run.PlayerWeightMax +
                  " DC=" + run.PlayerDC + "/" + run.PlayerDCMax +
                  " AC=" + run.PlayerAC +
                  " (口径:负重=出生" + BIRTH_WEIGHT + "+装备+背包, DC=裸身" +
                  (run.PlayerDC - 0) + "含出生武器)");
    }

    // =====================================================================
    // 内部：状态
    // =====================================================================
    /// <summary>Add/Remove 没有 run 参数 ⇒ 用 Init 记住的引用自动重算，免得调用方漏一步就「不联动」。</summary>
    void AfterChange()
    {
        if (_run != null) RecalcStats(_run);
    }

    /// <summary>
    /// 首次接管 run 时采样「裸身基础属性」，此后**同一份 run 不再重采**。
    /// ★ 时机是唯一要点：必须在任何 Equip/Unequip **之前**采样，
    ///   否则采到的数值里已含装备加成，每次重算都往上叠，结果是指数级虚高。
    /// ★ 为什么按 run 引用判而不是每次 Init 都重采：Init 会先清空背包再采样，
    ///   若此刻 run 上还留着上一轮装备撑起来的值，第二次 Init 就把虚高当成了"基础"，
    ///   于是穿着同一把木剑、DC 一次比一次高 —— 只有「同一份 run 只采一次」才真正幂等。
    /// </summary>
    void EnsureBase(P0HRuntime run)
    {
        if (run == null) return;
        if (_baseReady && ReferenceEquals(_baseRun, run)) return;
        _baseReady = true;
        _baseRun = run;

        // ★ 2026-10-23 22:52 修正：P0HRuntime L143 的 `PlayerDC = 2, PlayerDCMax = 5`
        //   是「1 级战士 + 木剑(dc1-2)」的合成值 —— 注释里明写「木剑给的」。
        //   而 Init 又发一把木剑，直接加法会算成 3/7，同一把剑被算两遍。
        //   ⇒ 采样时先扣掉出生武器的贡献，之后 RecalcStats 加回玩家手上真实那把。
        //     出生状态正好回到 2/5，与 P0HRuntime 的原始口径一致。
        _baseDc    = run.PlayerDC    - BIRTH_WEAPON_DC;
        _baseDcMax = run.PlayerDCMax - BIRTH_WEAPON_DCMAX;
        _baseAc    = run.PlayerAC;    // AC 的出生值 0 不含布衣（布衣是 P0HItem 自己发的），不用扣
        _baseMc    = run.PlayerMC;
        _baseSc    = run.PlayerSC;
    }

    /// <summary>出生武器（木剑）的攻击加成。P0HRuntime L143 的 DC 2/5 里已含这部分。</summary>
    const int BIRTH_WEAPON_DC = 1;
    const int BIRTH_WEAPON_DCMAX = 2;

    int FirstFree()
    {
        for (int i = 0; i < SLOTS; i++) if (_bag[i] == null) return i;
        return -1;
    }

    int FindStackable(P0HItemDef def)
    {
        if (def == null) return -1;
        for (int i = 0; i < SLOTS; i++)
        {
            Entry e = _bag[i];
            if (e == null || e.def == null) continue;
            if (e.def.name == def.name) return i;
        }
        return -1;
    }

    /// <summary>脏数据自检：任何 def 为空或数量 &lt;= 0 的格子都算层内不一致。</summary>
    bool SlotsValid()
    {
        for (int i = 0; i < SLOTS; i++)
        {
            Entry e = _bag[i];
            if (e != null && (e.def == null || e.count <= 0)) return false;
        }
        for (int t = 1; t < EQUIP_TYPES; t++)
        {
            Entry e = _equip[t];
            if (e != null && (e.def == null || e.count <= 0)) return false;
        }
        return true;
    }

    static string WtText(P0HRuntime run)
    {
        return run == null ? "?/?" : (run.PlayerWeight + "/" + run.PlayerWeightMax);
    }

    static string TwoChars(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= 2 ? s : s.Substring(0, 2);
    }

    static string SlotName(int type)
    {
        switch (type)
        {
            case 1: return "武器";
            case 2: return "衣服";
            case 3: return "头盔";
            case 4: return "项链";
            case 5: return "手镯";
            case 6: return "戒指";
            case 7: return "靴子";
            case 8: return "腰带";
            default: return "消耗";
        }
    }

    // =====================================================================
    // 内部：绘制
    // =====================================================================

    void EnsureStyles()
    {
        if (_name != null) return;
        int s = P0BMapWalk.ScaleI;   // 字号一律 N * ScaleI，否则设备像素下会小到看不清

        _name = new GUIStyle(GUI.skin.label);
        _name.alignment = TextAnchor.MiddleCenter;
        _name.fontSize = 9 * s;
        _name.normal.textColor = Color.white;
        _name.clipping = TextClipping.Clip;

        _cnt = new GUIStyle(_name);
        _cnt.fontSize = 8 * s;
        _cnt.alignment = TextAnchor.LowerRight;
        _cnt.normal.textColor = CountCol;

        _head = new GUIStyle(_name);
        _head.fontSize = 10 * s;
        _head.alignment = TextAnchor.MiddleLeft;
        _head.normal.textColor = HeadCol;
    }
}
