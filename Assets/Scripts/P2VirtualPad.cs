// P2 虚拟操作层 —— 手机 MOBA 式半透明摇杆 + 攻击键。
//
// 用户 2026-10-28 的原话：
//   「可以借鉴一下王者荣耀，还有撸啊撸。lol的那种手机版的，比如说有那种虚拟按键，
//     然后做成那种透明式的，然后来操作。」
// 设计约束（PLAN-NEXT.md 里已定稿）：
//   · 左下 8 向摇杆，按下位置动态跟随（手指落点即摇杆中心），中心死区约 16%
//   · 右下主攻击键（最大），半透明 alpha 0.28~0.45，按下提到 0.7
//   · **不改 Crystal 源码里的输入层**，全部在 Unity 外壳拦截，出问题好回退
//
// 实现口径（这是关键，别改成"合成按键"）：
//   摇杆不模拟键盘方向键，而是直接写 MapControl 的三个静态字段：
//       MapControl.MouseLocation   —— 手指指向哪一格
//       MapControl.MapButtons      —— 此刻按着左键
//       MirControl.MouseControl    —— 事件归 MapControl
//   GameScene.CheckInput() 每个逻辑周期读的正是这三个，这就是原版「按住鼠标走」的入口。
//   走这条路口的好处：A* 寻路、撞墙转向、门、地形判定、跑步全部自动跟着原版走，
//   一行都不需要重写。
//
// 攻击键同理：原版 CheckInput 顶部有一段「TargetObject 非空且贴身 → 自动 Attack1」，
// 所以这里只负责「锁定最近的怪」，锁上之后每个周期都会自动挥刀；远处则朝它走。
// 再按一次取消锁定。
using System;
using Client.MirObjects;
using Client.MirScenes;
using UnityEngine;

public static class P2VirtualPad
{
    // ── 视觉与手感常量（设备像素，GUI 坐标：左上原点、Y 向下）──
    //
    // ★ 2026-11-10 统一网格重排 —— 这是用户「错位」「太丑」投诉的根治。
    //   重排前从设备截图（2616x1140）逐像素量出来的真实数据，三条都是硬伤：
    //     1) 右栏外列圆心 y = 114.5 / 264.5 / 414.5 / 664.5 / 914.5
    //        ⇒ 相邻缝 19 / 19 / 111 / 85 像素。四个缝四个数，眼睛一看就是「没排齐」。
    //     2) 半径混用 46 / 62 / 66 / 74 / 92 共五档，同一列上大小跳来跳去。
    //     3) 左栏对话键 (235,665) r=74 底边 739，摇杆盘顶边 715 —— **重叠 24 像素**，
    //        实机上看就是「按钮长在盘子上」。
    //
    //   重排规则（一条模数管到底）：
    //     · 功能键**只有两个尺寸**：主攻键 r=100，其余全部 r=76。
    //     · 相邻两键**圆边之间**恒留 64 的缝 —— 位置全部由这个缝推出来，
    //       不再有手写的 725 / 875 / 1025 那种「一个个试出来的数」。
    //     · 右栏两列：外列 = 攻击 + 4 个功能键（等距），内列 = 拾取 + 出招键（同一竖线）。
    //     · 左栏只有摇杆盘和对话键，盘心贴屏幕底，对话键落在盘顶上方。
    //     · 绘制和点选**共用 GetCenters()** 这一份坐标，不可能再「画在这儿、点在那儿」。
    // ══════════════════════════════════════════════════════════════
    //  ★ 2026-11-26：整套按键尺寸改成**可缩放**。
    //
    //  起因是 GitHub Issue #1（用户 army-yan）：「游戏里面的按钮超大无法修改」。
    //  原来这些全是 const，界面上没有任何入口能改 —— 他说的是实情。
    //
    //  做法：所有以设备像素计的几何量都乘同一个系数 _S（见 SetScaleStep）。
    //    · 半径、间距、距边距、NPC 选项坐标 —— 全乘，整体等比缩放；
    //    · 无量纲的比例（DeadZone / StickKnobRatio / FillRatio）不乘；
    //    · 字号**不乘**：按键缩小时文字保持可读，宁可撑满键面。
    //  等比缩放的好处是「画」和「点」永远共用同一套数（GetCenters / CastCenter），
    //  改一个 _S 两边同时生效，不可能出现「画在这儿、点在那儿」。
    // ══════════════════════════════════════════════════════════════
    static float _S = 1f;           // 全局缩放系数（0.66 ~ 1.30，五档）
    static int _scaleStep = 2;      // 档位下标：0=超小 1=小 2=标准 3=大 4=特大
    static int _alphaStep = 1;      // 按键浓淡档位：0=淡 1=标准 2=明显

    public static float UiScale { get { return _S; } }

    /// <summary>档位→系数。标准档必须正好是 1.0（否则等于把现有布局整体改了）。</summary>
    static readonly float[] ScaleTable = { 0.66f, 0.80f, 1.00f, 1.15f, 1.30f };
    static readonly string[] ScaleName = { "超小", "小", "标准", "大", "特大" };
    static readonly float[] IdleAlphaTable = { 0.34f, 0.45f, 0.60f };
    static readonly string[] AlphaName = { "淡", "标准", "明显" };
    /// <summary>各档愿意把键缝收紧到原值的几分之几（见 FitScale）。小档不用收，越大越要收。</summary>
    static readonly float[] GapShrinkK = { 1.00f, 1.00f, 1.00f, 0.55f, 0.28f };

    static float HotRadius { get { return 225f * _S; } }   // 摇杆热区半径（手指落在这个圆里就算按到摇杆）
    static float StickRadius { get { return 118f * _S; } } // 摇杆头最大行程
    const float DeadZone = 0.16f;   // 死区：小于这个比例不动（0.16 × 118 ≈ 19px）
    // ★ 2026-11-26：Gap 不再只跟 _S 等比，而是 FitScale() 按屏幕高度算出来的实际缝。
    //  理由见 FitScale 的注释：右栏 5 个键竖向排，光靠等比缩放，放大档在这个屏上
    //  物理装不下（「大」和「特大」会缩成同一个数，档位形同虚设）。
    //  先把缝收窄再谈放大 —— 键变大、缝变窄，才是「按钮调大」的真效果。
    static float _gap = 64f;
    static float Gap { get { return _gap; } }   // 相邻两键**圆边之间**的标准缝（唯一间距模数）

    static float UtilR { get { return 76f * _S; } }        // 功能键统一半径
    static float AtkRadius { get { return 100f * _S; } }   // 攻击键（唯一的特大键，视觉焦点）

    // 半径别名：语义名全部保留（点选代码按名字用），但值只来自上面两个模数。
    static float PickRadius { get { return UtilR; } }    // 拾取
    static float PotRadius { get { return UtilR; } }     // 喝药
    static float NpcRadius { get { return UtilR; } }     // 对话
    static float BagRadius { get { return UtilR; } }     // 背包
    static float EqpRadius { get { return UtilR; } }     // 装备
    static float SkillRadius { get { return UtilR; } }   // 技能页

    // 两列中心距**屏幕右缘**（2616 宽 ⇒ 外列 2452 / 内列 2200；右留 164，左留 56）
    static float ColOuterM { get { return 164f * _S; } }
    static float ColInnerM { get { return 416f * _S; } }

    // 右栏各行的中心距**屏幕下缘**（全部由 Gap 推出来，没有手写魔数）
    static float AtkRowM { get { return 140f * _S; } }                  // 40 边距 + r100
    static float PotRowM { get { return AtkRowM + AtkRadius + Gap + UtilR; } }   // 380
    static float BagRowM { get { return PotRowM + UtilR * 2f + Gap; } }          // 596
    static float EqpRowM { get { return BagRowM + UtilR * 2f + Gap; } }          // 812
    static float SkillRowM { get { return EqpRowM + UtilR * 2f + Gap; } }        // 1028（上边留 36）

    // 左栏（黑边中线 x = 274，宽 548）
    static float LeftColM { get { return 274f * _S; } }
    static float StickRowM { get { return 240f * _S; } }   // 摇杆盘心 ⇒ y=900，可视盘半径 190 ⇒ 710..1090
    static float NpcRowM { get { return 570f * _S; } }     // 对话键 ⇒ y=570，494..646，盘顶 710 之上留 64 = Gap

    // 出招键列（右栏内列，与拾取键同一竖线）
    //   区段固定：[24, 拾取键顶边 − Gap]。键数变化时**只改半径不改间距模数**，
    //   所以同一列里永远等大、等步长，不会像旧版那样 45/53/62 一路跳。
    static float CastColTop { get { return 24f * _S; } }
    static float CastRMax { get { return UtilR; } }   // 技能 ≤5 个时与功能键同大，也是这一列的上限
    static float CastGapK { get { return 16f * _S; } }    // 同一列内相邻两圆的缝（技能簇比功能键更紧凑）

    // ── 技能出招键（2026-11-02 新增）：F1~F8 的手机版 ──
    //
    // 原版是键盘 F1~F8 触发 GameScene.UseSpell(1..8)，Ctrl+F1~F8 触发 9..16
    // （KeyBindSettings.cs:  Bar1Skill1..8 = F1..F8，Bar2Skill1..8 = F1..F8）。
    // 手机没有 F 区，而游戏里画出来的技能栏格子只有 32x30 逻辑像素（设备 47x45），
    // 手指根本点不准 —— 实测「技能学会了、栏里也显示了，就是按不出来」。
    // 外壳自己在**右侧黑边**画一排大圆键，按下去走同一条 UseSpell(key)，
    // 跟按 F1~F8 完全等价（不改源码里的任何行为）。
    //
    // 位置：右黑边 x∈[2068,2616]。主列 x=2391 已被 攻击/喝药/背包/装备/技能页 占满，
    //   只有左侧 x=2151 这一列从 y=100 到 y=841（拾取键顶边）是空的。
    //   8 个键 r=45、间距 96：圆心 110..782，上下都不越界
    //   （首个键上边 y=65；末键下边 y=827，拾取键顶边 841，余 14px）。
    //
    // ★ 2026-11-05 实机截图量出来的布局 bug（r48/间距90 → r45/间距96）：
    //   原来 r=48（直径 96）配间距 90，相邻两圆**几何上必然重叠 6px**。
    //   实机像素掩码上看得清清楚楚：两键在交界处连成一个 26px 宽的腰
    //   （/ai/tmp/scr2.png，y=197 一行）。8 个直径 96 的圆按老 top=155 排
    //   会顶到 875，压住拾取键（顶边 841），所以不能只改间距 ——
    //   半径收到 45（直径 90）、首键上移到 110、间距放到 96，留 6px 真间隙。
    // ★ 2026-11-05 第二次实机评审（按《原神》《王者》的拇指可达区）：
    //   上一版把 1 号键放在 y=110 —— 屏幕最上面，单手根本够不到；而 1 号键
    //   恰恰是最常用的那个（原版 F1）。主流手游一律把最常用的放在**拇指最近**处，
    //   次要的往外扩，所以整列改成**从下往上**排：1 号在最下（y=782，正对拇指），
    //   8 号在最上。技能只有 2 个时，两个常用键就落在 782/686，抬手就到。
    //   列的锚点因此从 CastTop 换成 CastBottom，绘制与点选都走 CastCenter，一处改全生效。
    //
    // ★ 2026-11-05 第三次实机评审（尺寸）：技能键原来固定 r=45，比旁边 r=74 的
    //   拾取/喝药键还小 —— 主流手游里技能是**最醒目**的，这里正好反了。
    //   改成**按技能个数自动定尺寸**：只有 2~4 个技能时撑到 r=74（和拾取键同大），
    //   学到 6、7、8 个才逐级缩回 45，保证整列永远装得进 y∈[60,833]。
    //   两头都靠 CastRadiusNow()/CastCenter() 算，绘制与点选共用，不会错位。
    // 旧名保留（点选/绘制还按这些名字引用），值统一来自上面的网格模数。
    static float CastRadiusMax { get { return CastRMax; } }
    static float CastMarginX { get { return ColInnerM; } }   // 与拾取键同一竖线（W-416）
    // 列底端：拾取键顶边再留一个 Gap ⇒ y = H - AtkRowM - UtilR - Gap
    // 旧常量已改成 CastBotLimitNow(H)（跟屏幕高走）。保留名字只为兼容旧引用。
    static float CastGap { get { return CastGapK; } }
    const int   CastMax = 8;          // 一排最多 8 个（= 原版 F1..F8）
    static float CastBarRadius { get { return UtilR * 0.62f; } }   // 栏位切换键（略小）
    // 栏位键位置：右栏内列从 y=24 到 860 全被出招键占满、拾取键下面也没有空档，
    // 所以翻页键放到**左栏**对话键正上方（用 Gap 推：对话键顶边 − Gap − r）。
    // 它只在技能超过 8 个时才出现，是个低频键，放远一点不影响操作；
    // 放右边反而会和出招键抢手指。
    static float CastPageX { get { return LeftColM; } }
    // 距下缘，由网格推：对话键底边 + Gap + 自己半径 ⇒ 与对话键恒留一个 Gap
    static float CastPageRowM { get { return NpcRowM + UtilR + Gap + UtilR * 0.62f; } }   // = 757.12

    // ── 换目标键（2026-11-16 新增）──
    //
    // 用户原话：「我们是不是有切换目标什么这种按键，再弄」。
    // 官方依据（全文 <本机>/tmp\mob_ctrl_research.txt 第 25 / 84 行）：
    //   Mir4 的目标模式在 List（点图标开列表）和 **Tab（在附近敌人间循环切换）**
    //   之间可切换；热血传奇手机版是「附近列表 + 滑屏目标框」。
    //   我们已经有「点怪即锁」这条主路（TryTapSelect），缺的正是**循环切换**这一条
    //   —— 怪挤在一起、或者目标跑到屏幕外时，手指点不中第二只。
    //
    // 为什么落在这个位置（每一处都按 Gap 模数算过，别随手挪）：
    //   · 右侧两列（外列 W−164 / 内列 W−416）从 y=36 排到 y=1104，没有空档；
    //   · 换怪是**边打边用**的高频键，必须留在右手拇指够得着的地方。
    //     左栏虽然空（2026-11-18 去掉挂机键之后），但放左边就要换手，打怪时会断档；
    //   · 所以放到**两列之间的空档**：圆心 (W−306, H−250) = (2310, 890)，
    //     取 NPC 选项键同款 r=62（本文件里已有的第二种尺寸，不是新造的档位）。
    //   实测圆心距（全部大于半径和，不重叠也不抢手指）：
    //     喝药 192.5（需 138）/ 攻击 179.6（需 162）/ 拾取 155.6（需 138）
    //     / 出招① 152.8（需 138，满 8 技能时 133.2 需 107）—— 最小余量 14.8px。
    static float NextColM { get { return 306f * _S; } }    // 距屏幕右缘 ⇒ x = W − 306 = 2310
    static float NextRowM { get { return 250f * _S; } }    // 距屏幕下缘 ⇒ y = H − 250 = 890
    static float NextRadius { get { return 62f * _S; } }   // 与 NPC 选项键同尺寸

    // ── 外观设置的持久化（2026-11-26 新增）──
    //
    // 存 PlayerPrefs 而不是存文件：P2NetMode 的联机设置也走 PlayerPrefs，
    // Android 上就是 SharedPreferences，卸载前一直有效，不用自己管路径和权限。
    const string PrefScale = "p2.pad.scale";   // 0..4
    const string PrefAlpha = "p2.pad.alpha";   // 0..2
    static bool _lookLoaded;

    static void LoadLook()
    {
        if (_lookLoaded) return;
        _lookLoaded = true;
        const string MigratedKey = "p2.pad.scale.v2";
        int saved = PlayerPrefs.GetInt(PrefScale, 1);
        // 2026-11-27：新增「超小」插在最前，所有旧下标（含 0=小）整体 +1，老玩家的大小不变。
        // 只迁一次并写回：否则每次启动都 +1 会漂到特大，或标记在但值没改会退回旧档。
        if (PlayerPrefs.GetInt(MigratedKey, 0) == 0)
        {
            saved += 1;
            PlayerPrefs.SetInt(PrefScale, saved);
            PlayerPrefs.SetInt(MigratedKey, 1);
            PlayerPrefs.Save();
        }
        _scaleStep = Mathf.Clamp(saved, 0, ScaleTable.Length - 1);
        _alphaStep = Mathf.Clamp(PlayerPrefs.GetInt(PrefAlpha, 1), 0, IdleAlphaTable.Length - 1);
        _S = ScaleTable[_scaleStep];
    }

    /// <summary>
    /// 按屏幕高度定**实际缩放 + 实际缝宽**。
    ///
    /// 右栏外列竖着排了 5 个键（攻击/喝药/背包/装备/技能页）。设档位 S、缝宽 g，
    /// 最上沿距屏幕下缘 = 140S +100S +g +76S + 3×(76S×2 + g) + 76S = **848S + 4g**。
    /// 旧代码 S=1、g=64 ⇒ 1104，在 1140 高的基准屏上只剩 36px 余量；
    /// 屏幕高 1080 的常见机型上 = 1104 > 1080，**技能页键早就被顶出屏幕了**
    /// （这是 Issue #1 说「屏幕小的手机按键占比例更大」的物理原因之一）。
    ///
    /// 所以这里不搞「等比放大再整体封顶」——那会把「大」和「特大」压成同一个数，
    /// 档位形同虚设。改成**先收缝、再放大**：
    ///   · 目标越大，缝越小（64 → 64 → 35 → 18），键收紧腾地方；
    ///   · 缝收到下限仍装不下，才回头压 S（此时设置页如实标出真实比例）。
    ///
    /// Step() 和 Draw() 每帧各调一次，必须同值，否则「画在这儿、点在那儿」。
    /// </summary>
    static void FitScale(float H)
    {
        const float gMin = 14f;                   // 缝的绝对下限（设备像素）：再小两键就粘成一团
        float avail = H - 16f;                    // 上下各留 8px 不贴边
        if (avail < 400f) avail = 400f;

        float want = ScaleTable[_scaleStep];
        // 缝跟着档位走：先按比例（64×S，与旧布局一致），再乘该档愿意收紧到的系数。
        // gapK 提到 static readonly：这个函数每帧调两次，数组不能每帧 new（手机 GC 会抖）。
        float g = 64f * want * GapShrinkK[Mathf.Clamp(_scaleStep, 0, GapShrinkK.Length - 1)];

        // 848S + 4g ≤ avail
        if (848f * want + 4f * g > avail)
        {
            g = Mathf.Max(gMin, (avail - 848f * want) / 4f);   // 先收缝
            if (848f * want + 4f * g > avail)
            {
                // 缝到底还装不下 ⇒ 压 S（缝保持下限不动）
                g = gMin;
                want = Mathf.Clamp((avail - 4f * g) / 848f, 0.55f, want);
            }
        }
        _S = want;
        _gap = g;
    }

    static void SaveLook()
    {
        PlayerPrefs.SetInt(PrefScale, _scaleStep);
        PlayerPrefs.SetInt(PrefAlpha, _alphaStep);
        PlayerPrefs.Save();
    }

    /// <summary>把画面上这些按钮整体放大/缩小一档（设置面板调用）。</summary>
    public static void StepScale(int dir)
    {
        LoadLook();
        _scaleStep = Mathf.Clamp(_scaleStep + dir, 0, ScaleTable.Length - 1);
        _S = ScaleTable[_scaleStep];
        FitScale(Screen.height);   // 立刻按本机屏幕封顶，日志里写的是**这一帧真实生效**的比例
        SaveLook();
        ShowToast("按键大小：" + ScaleName[_scaleStep]);
        if (Log != null) Log("[外观] 按键大小 → " + ScaleName[_scaleStep] + "（×" + _S.ToString("0.00") + "）");
    }

    /// <summary>按键浓淡切换（设置面板调用）。</summary>
    public static void StepAlpha(int dir)
    {
        LoadLook();
        _alphaStep = Mathf.Clamp(_alphaStep + dir, 0, IdleAlphaTable.Length - 1);
        SaveLook();
        ShowToast("按键浓淡：" + AlphaName[_alphaStep]);
        if (Log != null) Log("[外观] 按键浓淡 → " + AlphaName[_alphaStep]);
    }

    /// <summary>恢复默认外观（标准大小 + 标准浓淡）。设置页的「恢复默认」按钮调这个。</summary>
    public static void ResetLook()
    {
        LoadLook();
        _scaleStep = 2;
        _alphaStep = 1;
        _S = ScaleTable[_scaleStep];
        SaveLook();
        ShowToast("按键外观已恢复默认");
        if (Log != null) Log("[外观] 恢复默认（标准 / 标准）");
    }

    /// <summary>
    /// 设置页显示的大小档位。
    /// 屏幕矮放不下时如实标注 —— 否则用户点了「特大」看到的还是「标准」，
    /// 会以为按钮坏了；写明「屏幕放不下」他才知道是物理限制。
    /// </summary>
    public static string ScaleLabel
    {
        get
        {
            LoadLook();
            FitScale(Screen.height);
            float want = ScaleTable[_scaleStep];
            string nm = ScaleName[_scaleStep];
            // 只在实际被压下去超过 3% 时提示，轻微收边不值得吓人
            if (_S < want * 0.97f) return nm + "（本机最高到这）";
            return nm;
        }
    }

    public static string AlphaLabel { get { LoadLook(); return AlphaName[_alphaStep]; } }
    public static bool ScaleAtMin { get { LoadLook(); return _scaleStep <= 0; } }
    public static bool ScaleAtMax { get { LoadLook(); return _scaleStep >= ScaleTable.Length - 1; } }
    public static bool AlphaAtMin { get { LoadLook(); return _alphaStep <= 0; } }
    public static bool AlphaAtMax { get { LoadLook(); return _alphaStep >= IdleAlphaTable.Length - 1; } }

    /// <summary>
    /// 预览框的「尺子」：最大档（特大 + 它自己那档的缝）下三颗键摆一排的宽和高。
    /// 缩放系数必须按这把尺子**一次定死**、四档共用；按当前档位现算的话，
    /// 每一档都会刚好塞满框子，四档在预览里长得一模一样。
    /// 从表里取而不是写死数值 —— 以后改档位，预览会自动跟上，不会只改一半。
    /// </summary>
    static void LookPreviewBasis(out float maxW, out float maxH)
    {
        int last = ScaleTable.Length - 1;
        float SMax = ScaleTable[last];
        float GMax = 64f * SMax * GapShrinkK[last];
        maxW = 4f * 76f * SMax + 2f * 100f * SMax + 2f * GMax;
        maxH = 2f * 100f * SMax;
    }

    /// <summary>
    /// 布局层用：预览框建议的宽高比（= 最大档三颗键摆一排的宽 ÷ 高）。
    /// 框按这个比例定宽，才不会比内容宽出一大片空留白。
    /// </summary>
    public static float LookPreviewAspect
    {
        get { float w, h; LookPreviewBasis(out w, out h); return h > 1f ? w / h : 2.7f; }
    }

    /// <summary>
    /// 设置面板里的「实际比例」预览：照当前档位画三个真按键（拾取 / 攻击 / 喝药）。
    ///
    /// 为什么非要有这块（2026-11-26）：设置面板是**整屏不透明**的，玩家调大小时
    /// 根本看不见背后的按键 —— 没有预览的话，「改完立刻生效」在面板里就等于看不见，
    /// 只能关掉面板再开一次才知道调成了什么样。这块框让档位变化当场看得见。
    ///
    /// 用的是和游戏里**同一个** DrawFunc / MakePad，所以看到的就是真按键，
    /// 不是另画一套示意图（两套图迟早会漂）。
    /// </summary>
    public static void DrawLookPreview(Rect box)
    {
        if (box.width < 60f || box.height < 50f) return;
        if (_style == null) EnsureStyle();
        LoadLook();
        FitScale(Screen.height);

        float maxW, maxH;
        LookPreviewBasis(out maxW, out maxH);
        float k = 0.80f;
        if (maxH > 1f) k = Mathf.Min(k, box.height * 0.94f / maxH);
        if (maxW > 1f) k = Mathf.Min(k, box.width * 0.96f / maxW);
        if (k <= 0.05f) return;

        var oldAlign = _style.alignment;
        _style.alignment = TextAnchor.MiddleCenter;

        float a = IdleAlpha;
        float cy = box.y + box.height * 0.5f;
        float needW = 4f * UtilR + 2f * AtkRadius + 2f * Gap;
        float x0 = box.x + box.width * 0.5f - needW * k * 0.5f;
        Vector2 pickC = new Vector2(x0 + UtilR * k, cy);
        Vector2 atkC = new Vector2(x0 + (2f * UtilR + Gap + AtkRadius) * k, cy);
        Vector2 potC = new Vector2(x0 + (2f * UtilR + Gap + 2f * AtkRadius + Gap + UtilR) * k, cy);
        Color white = new Color(1f, 1f, 1f, 0.92f);

        DrawFunc(pickC, UtilR * k, a * FillRatio, a * 0.95f, 0f, 0f,
                 "pick", 0.60f, "拾取", white, true);
        DrawFunc(atkC, AtkRadius * k, a * FillRatio, a * 0.95f, 0f, 0f,
                 "attack", 0.58f, "攻击", white);
        DrawFunc(potC, UtilR * k, a * FillRatio, a * 0.95f, 0f, 0f,
                 "pot", 0.60f, "喝药", white);

        _style.alignment = oldAlign;
    }

    // ── 运行态 ──
    static float _releasedAt = -999f;   // 摇杆最后一次松手的时刻（松手 1.2 秒内不自动追怪）
    static float _autoNoPotT;       // 「没药了」提示节流（15 秒一次，别刷屏）
    static float _nextFlashT;       // 换目标键按下闪光
    static int _nextFinger = -2;    // 这一指已经处理过这个键

    // 三档浓淡（设置面板里的「淡 / 标准 / 明显」）。
    //   2026-11-26 改成 straight alpha 之后，这三个数不再是「亮度」而是**不透明度**的源头：
    //   MakePad 里 bodyA = alpha × FillRatio × 0.86，于是三档实际落到
    //     淡 0.21 / 标准 0.28 / 明显 0.37
    //   0.28 是在真实截图上四版并排给 see-image 评审选出来的（padfinal.png）：
    //   既有通透感、边缘又清楚不糊，判语是「最像现代手游、不像贴纸」。
    //   ★ 别再把它们当亮度看 —— 旧注释说「0.45 才勉强等于半透明白键」是预乘时代的换算，
    //     现在 0.45 就是 0.45 的不透明度，盘面会明显偏实。
    static float IdleAlpha { get { return IdleAlphaTable[_alphaStep]; } }  // 常态透明度（可调）
    const float PressAlpha = 0.70f; // 按下时
    const float KnobAlpha = 0.62f;

    // ★ 2026-10-29 第三处定案（填充与描边的**亮度配比**）—— 结论仍然有效，理由变了：
    //   当年是为了把「一圈细线 + 一片几乎全黑的圆」救回来（fill 0.34 → 0.72）。
    //   现在 fill 是 alpha 不是亮度，0.72 的含义是「盘身比外环淡」——
    //   半透明模型下这依然成立且必须成立：外环要能压住盘面才看得见边界。
    //   把它改成 1.0 会让盘面和环一样实，圆就糊成一坨（padfinal.py 里试过）。
    // 摇杆的「视觉」尺寸（与上面的热区/行程是两套东西，2026-11-01 拆开）
    //   热区 HotRadius=235 是手指落点判定，做大一点更好按；
    //   视觉环 190 才是画出来的大小。两者不相等是手游的标准做法。
    static float StickBaseRadius { get { return 190f * _S; } }  // 底盘空心环半径
    const float StickKnobRatio = 0.44f;  // 摇杆头半径 = StickRadius * 该比例 ≈ 52
    // ★ 2026-11-01 改：0.52 → 1.0。原来球只走手指一半，配合「底盘跟手漂移」，
    //   视觉上分不清是球在动还是盘在动，整体像一张贴图。底盘钉死之后球必须
    //   1:1 跟手才有操纵感（真机推到底 = 球贴住环内缘），推到 StickRadius 就钳位。
    const float StickVisRatio = 1.0f;    // 手指行程 → 球位移 的缩放（1.0 = 完全跟手）

    const float FillRatio = 0.72f;   // 填充 / 描边 的亮度比
    const float KnobFillRatio = 0.80f;

    // ── 摇杆运行态 ──
    static bool _active;
    static int _touchId = -1;
    static Vector2 _origin;         // ★ 摇杆底盘中心 —— 固定死在左下角，不跟手指跑（2026-11-01 改）
    static Vector2 _anchor;         // 手指按下的起点，只用来算「推了多远」，不参与画图
    static Vector2 _knob;           // 摇杆头当前位置
    static bool _hasDir;
    static MirDirection _dir;

    /// <summary>MapButtons 的左键是不是「本层按下去的」——只清自己置的那一位。</summary>
    static bool _padHeld;

    // ── 攻击键运行态 ──
    static bool _atkPressed;
    static float _atkFlashT;        // 按下回弹动画剩余时间
    static int _atkFinger = -2;     // 已经算过这一下的手指（-2 = 没有）；防 Began+Ended 各算一次

    // ── 攻击键长按 = 强制攻击（2026-11-13 新增）──
    // 原版 PC 端「按住 Shift 点怪」才是强制攻击，手机上没有 Shift 键，
    // 实测 CMain.Shift 在整个 Unity 外壳里零写入 ⇒ 玩家/红名/守卫永远打不了。
    // 这里用「按住攻击键 0.8 秒」当手机的 Shift：按住时置位，抬起就清。
    const float ForceAtkHoldSec = 0.8f;  // 按住多久算长按（原版是 Shift 键，手机上用长按代替）
    static float _atkHoldT;         // 已经按住了多久（秒）
    static bool _atkHoldFired;      // 这一次按住是否已经触发过强制攻击
    static bool _shiftApplied;      // CMain.Shift 是不是「本层置的」——只清自己置的
    static int _atkHoldFinger = -2; // 正在长按的那根手指（-2 = 没在按）

    // ── 方向锥形锁定（2026-11-13 新增）──
    // 摇杆推着某个方向时按攻击键 ⇒ 只锁「那个方向 ±45° 内最近的怪」，
    // 这就是用户问的「我要打右前方那只，怎么操控」。
    // 摇杆没推（松手/死区）⇒ 退回全局最近，和原来一样。
    static MirDirection _lastDir;   // 最后一次推杆方向
    static float _dirSeenT = -99f;  // 最后一次「推出方向」的时刻（unscaledTime）
                                    // 初值给负：开局时 unscaledTime 只有零点几秒，
                                    // 留 0 会让「刚进游戏点攻击」误判成「有方向」
    const float DirStickySec = 0.6f; // 松手后这么久内点的攻击键，仍按这个方向算
    // ★ 2026-11-13 补：方向锁定要「粘住」，不能只靠 0.6 秒窗口。
    //   实测（安卓 2616x1140）：`input swipe …900` 推下再点攻击，第一下进了方向分支，
    //   第二下（230ms 后）就掉回「全局最近」——两次点击之间的手感被打断，
    //   用户要的「同一个方向连点换下一只」根本连不上。
    //   手游的实际做法是：锁定后**记住这个方向**，方向不变就一直在这个扇区里轮换，
    //   只有玩家把摇杆推到别的方向、或者这个扇区打空了，才重新判定。
    static MirDirection _lockDir;   // 当前锁定是按哪个方向挑的
    static bool _lockDirValid;      // true = 后续点攻击键仍按 _lockDir 这个扇区轮流换
    // 扇形挑怪用的复用表：每帧都可能调，避免每次都 new List 给 GC 添垃圾
    static readonly System.Collections.Generic.List<MapObject> _coneBuf =
        new System.Collections.Generic.List<MapObject>(32);

    // ── 点怪即锁（2026-11-15 新增，这是「控制重做」的主路）──
    //
    // 用户原话：「攻击它会选择什么，某一个方向的其中一个」。
    // 病根就是这个扇面锁定：按攻击键时按摇杆方向筛一个 90° 锥，
    // 锥里谁最近打谁 —— 玩家根本不想要这个，他要的是「我点哪只就打哪只」。
    //
    // 原版端游就是这么干的（GameScene.cs:11926 OnMouseDown → 12013
    //   `MapObject.TargetObjectID = MapObject.MouseObject.ObjectID`），
    // 而 MouseObject 的命中是**像素级**的（MonsterObject.cs:4471
    //   用 BodyLibrary.VisiblePixel 判精灵的透明位）。
    // 手游版（热血传奇手机版）同样是「点击怪物模型即可锁定」。
    //
    // 这里用 DisplayRectangle + 手指容差做命中：像素级在 1024 宽的画布上
    // 太苛刻（一只稻草人只有 48x40），手指误差 20~30 逻辑像素很正常。
    const int TapSelectSlack = 26;  // 手指容差（画布像素）
    static int _tapSelFinger = -2;  // 这一指已经做过点选判定，别在 Began/Ended 里跑两遍
    static bool _tapLocked;         // 当前锁定是「点出来的」（攻击键不许覆盖它）
    static uint _tapLockedId;       // 点选锁定的那个 ObjectID

    // ── 拾取 / 喝药运行态 ──
    static float _pickFlashT, _potFlashT;
    static int _pickFinger = -2, _potFinger = -2;
    static string _toast;            // 屏幕上闪一下的提示（"捡起 金创药" / "没有可捡的"）
    static float _toastT;
    static float _autoPotT;          // 自动喝药节流（每 1 秒最多试一次）
    static float _autoPickT;         // 自动拾取节流（每 0.4 秒查一次脚下）

    // ── 自动走去捡：点了拾取键但东西不在脚下时，记住那个格子一路走过去 ──
    // 2026-10-30：原来 DoPickUp 只「朝那个方向推一帧」，人只走一格就停，
    //   实测日志 `拾取键 → 附近有 金币 @(641,604)，先走过去` 之后角色原地不动，
    //   金币永远捡不到。改成状态机：每帧朝目标格走，站上去就发包。
    static bool _pickHasTarget;
    static int _pickX, _pickY;
    static string _pickName;
    static float _pickDeadline;      // 超过这个时间还没走到就放弃（防卡死）

    // ── 对话键运行态：走过去 → 贴身 → 发 C.CallNPC ──
    static float _npcFlashT;
    static int _npcFinger = -2;
    static bool _npcHasTarget;
    static int _npcX, _npcY;
    static uint _npcId;
    static string _npcName;
    static float _npcDeadline;
    static float _npcRepathT;        // 逐格推不动时重新寻路的节流
    static int _npcLastX = -1, _npcLastY = -1;

    // ── 背包 / 装备 / 技能键 ──
    static float _bagFlashT, _eqpFlashT, _skillFlashT;
    static int _bagFinger = -2, _eqpFinger = -2, _skillFinger = -2;

    // ── 出招键运行态（2026-11-02）──
    //  _castName16 / _castFull16 按下标 (key-1) 存，key 就是原版 UseSpell 的键位：
    //    0..7   = 键位 1..8   （Bar1，原版 F1..F8）
    //    8..15  = 键位 9..16  （Bar2，原版 Ctrl+F1..F8）
    //  _castPage 决定这一屏画哪一半；服务端只发了 1..8 的号就自动隐藏翻页键。
    static readonly string[] _castName16 = new string[16];   // 完整中文名（toast / 日志 / 图标缺失时的降级标签）
    static readonly string[] _castFull16 = new string[16];   // 同 _castName16（保留旧名，别处还在用）
    static readonly bool[]   _castPassive16 = new bool[16];  // 被动技能：按下去原版直接 return，画淡一点
    // ★ 2026-11-05 新增：技能图标帧号（= ClientMagic.Icon * 2，原版未按下态）。
    //   数据源是 Bridge.SkillSlots() 的第 4 字段（MirCoreBridge.cs:1367 的 key|cn|en|Icon|level），
    //   帧图由 <本机>/tmp\mk_magicon_rgba.py 从 Data_HOLD/MagIcon.Lib 离线导出到 Data/UI/mag/。
    //   0 表示没拿到（老版本服务端不带这个字段）⇒ 退回白色星星图标。
    static readonly int[]    _castIcon16 = new int[16];
    static readonly float[]  _castFlashT = new float[CastMax];
    static bool _castBar2Exists;      // 有没有键位 ≥9 的技能（没有就不画翻页键）
    static int  _castPage;            // 0 = 键位 1..8，1 = 键位 9..16
    static float _castPollT;          // 清单轮询节流（0.5 秒）
    static float _castPageFlashT;
    static int   _castFinger = -2;
    static string _castSig;           // 上一次的清单签名，变了才重排 + 记日志

    // ── NPC 对话选项（2026-10-31 新增）──
    //
    // 为什么必须有（设备实测）：原版 NPC 对话框的链接是 14x16 逻辑像素的小黄字
    // （日志原文 `"买"@{X=26,Y=36} {Width=14, Height=16}`），
    // 设备上只有 21x24 像素 —— 手指点不准，玩家就是「对话框弹出来了但啥也点不了」。
    // 而且对话框固定贴在逻辑 (0,0)，手机上有半张被摇杆压着。
    //
    // 做法：对话框一出现，就把里面的链接文字搬到**右侧黑边**，竖着排一排大按钮
    // （宽 460 高 132 设备像素 ≈ 3 倍手指直径），点一下调 ClickNpcButton(文字)。
    // 黑边宽 548，按钮居中在 x = W - 230，正好不挡游戏画面。
    static float NpcOptRadius { get { return 62f * _S; } }
    const int   NpcOptMax = 6;          // 最多显示 6 个（再多就不适合用手指点了）

    static string[] _npcOpts = new string[0];   // 当前要显示的链接文字
    static bool _npcDlgOpen;                    // NPC 对话框是否开着（决定要不要画关闭键）
    static float _npcOptPollT;                  // 拉取链接清单的节流（每 0.3 秒一次）
    static int _npcOptFinger = -2;
    static float _npcOptFlashT;
    static int _npcOptFlashIdx = -1;

    static bool DialogMode { get { return _npcDlgOpen; } }

    /// <summary>
    /// 第 i 个对话选项按钮的中心（设备像素，GUI 坐标）：**左侧黑边**里 2 列 3 行。
    ///
    /// 为什么放左边不放右边（2026-10-31 排版实测）：
    ///   右侧黑边已经被 拾取/喝药/背包/装备 四个键占满（y=265/415/665/915），
    ///   6 个选项塞进去会互相压。左侧黑边除了对话键（y=665）和摇杆（y=905）之外全是空的，
    ///   2 列 3 行落在 y=70/215/360，最下一个底边 422 &lt; 对话键顶边 591，一格不碰。
    /// </summary>
    static Vector2 NpcOptCenter(int i, float W)
    {
        int col = i % 2, row = i / 2;
        return new Vector2(NpcOptColXTable[col] * _S, NpcOptRowYTable[row] * _S);
    }

    static readonly float[] NpcOptColXTable = { 140f, 400f };
    static readonly float[] NpcOptRowYTable = { 70f, 215f, 360f };

    /// <summary>对话关闭键的中心：左侧黑边最下面一行，正好在对话键上方。</summary>
    static Vector2 NpcCloseCenter(float W) { return new Vector2(270f * _S, 505f * _S); }

    // ────────────────────────────────────────────────────────────
    //  出招键（F1~F8 的手机版）
    // ────────────────────────────────────────────────────────────

    /// <summary>这一屏实际学了几个技能（决定键多大、整列排多高）。</summary>
    static int CastCountNow()
    {
        int n = 0;
        for (int i = 0; i < CastMax; i++)
            if (!string.IsNullOrEmpty(_castName16[CastKeyOf(i) - 1])) n++;
        return n;
    }

    /// <summary>
    /// 第 i 个格子（键位下标 0..7）在屏幕上的**排位**（0 起，自下而上）。
    /// 没学的格子不占位 —— 否则学到「基本剑术」「攻杀剑术」之外的 3 号技能时，
    /// 1、2 号中间会空出一大块，看着像按钮丢了。
    /// </summary>
    static int CastSlotOf(int i)
    {
        int slot = 0;
        for (int j = 0; j < i; j++)
            if (!string.IsNullOrEmpty(_castName16[CastKeyOf(j) - 1])) slot++;
        return slot;
    }

    /// <summary>
    /// 出招列的下边界：拾取键顶边再留一个 Gap。跟着屏幕高走，不写死分辨率。
    /// </summary>
    static float CastBotLimitNow(float H)
    {
        return H - AtkRowM - UtilR - Gap;
    }

    /// <summary>
    /// 出招键半径：技能越少越大。≤5 个时给满（和功能键同大），
    /// 学到 6/7/8 个才逐级收小，保证整列永远落在
    /// [CastColTop, 拾取键顶边−Gap] 之间，既够不到顶也不压拾取键。
    /// 同一列里等大等步长 —— 不会像旧版那样 45/53/62 一路跳。
    /// </summary>
    static float CastRadiusNow(float H)
    {
        int n = Mathf.Max(1, CastCountNow());
        float span = CastBotLimitNow(H) - CastColTop;
        float r = (span - (n - 1) * CastGap) / (2f * n);
        // ★ 2026-11-26：下限不能再硬夹到「44×S」那种按档位等比放大的值。
        //  这一列的可用高度并不随 S 等比涨（下边界是按键半径、上边界是屏幕高），
        //  放大档再学满 8 个技能时几何上只装得下 r≈42，硬夹回更大的值
        //  会让整列顶出屏幕上沿两百多像素。宁可键略小，也绝不能出屏 ——
        //  上限照旧由 CastRadiusMax 管；12px 只是防负数/退化的安全网，
        //  正常机型永远碰不到。
        return Mathf.Clamp(r, 12f, CastRadiusMax);
    }

    /// <summary>
    /// ★ 所有按键坐标的**唯一来源**（2026-11-10 统一网格）。
    /// Step()（点选）和 Draw()（绘制）都调这一个函数，几何不可能再漂。
    /// 返回顺序固定：热区 / 攻击 / 拾取 / 喝药 / 对话 / 背包 / 装备 / 技能页。
    /// </summary>
    static void GetCenters(float W, float H,
                           out Vector2 hot, out Vector2 atk, out Vector2 pick, out Vector2 pot,
                           out Vector2 npc, out Vector2 bag, out Vector2 eqp, out Vector2 skill)
    {
        hot   = new Vector2(LeftColM,             H - StickRowM);
        npc   = new Vector2(LeftColM,             H - NpcRowM);
        atk   = new Vector2(W - ColOuterM,        H - AtkRowM);
        pick  = new Vector2(W - ColInnerM,        H - AtkRowM);
        pot   = new Vector2(W - ColOuterM,        H - PotRowM);
        bag   = new Vector2(W - ColOuterM,        H - BagRowM);
        eqp   = new Vector2(W - ColOuterM,        H - EqpRowM);
        skill = new Vector2(W - ColOuterM,        H - SkillRowM);
    }

    /// <summary>第 i 个出招键的圆心（i 是**这一屏**的下标 0..7，不是键位）。</summary>
    static Vector2 CastCenter(int i, float W, float H)
    {
        float r = CastRadiusNow(H);
        float step = 2f * r + CastGap;
        return new Vector2(W - CastMarginX, CastBotLimitNow(H) - r - step * CastSlotOf(i));
    }

    /// <summary>栏位翻页键：左栏对话键正上方（只在技能 >8 时出现，低频键放远处）。</summary>
    static Vector2 CastPageCenter(float W, float H)
    {
        return new Vector2(CastPageX, H - CastPageRowM);
    }

    /// <summary>换目标键：右栏两列之间的空档（见 NextColM/NextRowM 的注释）。</summary>
    static Vector2 NextCenter(float W, float H)
    {
        return new Vector2(W - NextColM, H - NextRowM);
    }

    /// <summary>这一屏第 i 个格子对应的原版键位（1..16）。</summary>
    static int CastKeyOf(int i) { return _castPage * 8 + i + 1; }

    /// <summary>
    /// 拉一次已学技能清单，填进 _castName16 / _castFull16 / _castPassive16。
    ///
    /// 数据源是 Bridge.SkillSlots()（服务端下发的 UserObject.Magics），
    /// 中文名由 Bridge.SpellCnName() 从语言包取 —— ClientMagic.Name 是英文。
    /// 只在**清单变了**的时候打日志，免得每 0.5 秒刷一行。
    /// </summary>
    static void PollCastSlots()
    {
        string raw = "";
        try { raw = Client.MirCore.Bridge.SkillSlots(); }
        catch { raw = ""; }
        if (raw == null) raw = "";
        if (raw == _castSig) return;
        _castSig = raw;

        for (int i = 0; i < 16; i++) { _castName16[i] = null; _castFull16[i] = null; _castPassive16[i] = false; _castIcon16[i] = 0; }
        _castBar2Exists = false;

        int shown = 0;
        var sb = new System.Text.StringBuilder();
        if (raw.Length > 0)
        {
            var segs = raw.Split(';');
            for (int s = 0; s < segs.Length; s++)
            {
                if (string.IsNullOrEmpty(segs[s])) continue;
                var f = segs[s].Split('|');
                if (f.Length < 3) continue;
                int key, lvl = 0, icon = 0;
                if (!int.TryParse(f[0], out key) || key < 1 || key > 16) continue;
                if (f.Length > 4) int.TryParse(f[4], out lvl);
                if (f.Length > 3) int.TryParse(f[3], out icon);

                string cn = f[1];
                string en = f[2];
                if (string.IsNullOrEmpty(cn)) cn = en;

                // ★ 2026-11-18：被动技能**不占技能栏**。
                //
                //   依据（原版源码逐行）：GameScene.cs:1034-1042 这一整串 case 直接 return ——
                //   Fencing(基本剑术) / Slaying(攻杀剑术) / FatalSword / MPEater /
                //   Hemorrhage / SpiritSword(精神战法) / Focus / Meditation。
                //   按下去屏幕上**什么都不会发生**（不是"效果很弱"，是字面意义的 return）。
                //
                //   而服务端按「学到的先后」自动发键位（HumanObject.cs:2333-2355 NextFreeMagicKey），
                //   1.76 里最先学到的恰好就是这些被动技 ⇒ 1、2 号槽（拇指最近的两颗）
                //   长期被两颗死键占着。战士 6 门里占 2 门、道士 13 门里占 1 门。
                //   代价不只是"白占"：CastRadiusNow（:397）是按**键数**算半径的，
                //   6 个时 r=63、8 个时 r=45，比拾取键(76)小一半还多。
                //
                //   处理：名字不写进 _castName16 ⇒ CastCountNow 不数它 ⇒ 键不画、位置不留空，
                //   半径自动涨回来。全是被动技时整列出招键消失（本来也没东西可按）。
                //   被动技仍然记进 _castPassive16，技能页里照常看得见、照常生效。
                bool passive = IsPassiveSpell(en);
                _castFull16[key - 1] = cn;
                _castPassive16[key - 1] = passive;

                if (passive)
                {
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append(key).Append(':').Append(cn).Append("(被动·不入栏)");
                    continue;
                }

                _castName16[key - 1] = cn;
                _castIcon16[key - 1] = icon;
                if (key > 8) _castBar2Exists = true;

                if (sb.Length > 0) sb.Append("  ");
                sb.Append(key).Append(':').Append(cn).Append("(Lv").Append(lvl).Append(')');
                shown++;
            }
        }
        if (_castPage == 1 && !_castBar2Exists) _castPage = 0;

        if (Log != null)
            Log("出招键 → 可放 " + shown + " 个" +
                (shown > 0 ? "：" + sb : (sb.Length > 0 ? "（只有被动技，不出键）：" + sb : "（一个都没有，等 NPC 传功）")));
    }

    /// <summary>
    /// 被动技能按下后原版 UseSpell 直接 return（GameScene.cs:1016 那一串 case），
    /// 屏幕上不会有任何反应 —— 画淡一点、按下去提示「被动」，免得以为没做出来。
    /// </summary>
    static bool IsPassiveSpell(string en)
    {
        switch (en)
        {
            case "Fencing":      // 基本剑术
            case "Slaying":      // 攻杀剑术
            case "FatalSword":
            case "MPEater":
            case "Hemorrhage":
            case "SpiritSword":
            case "Focus":
            case "Meditation":
                return true;
            default:
                return false;
        }
    }

    /// <summary>圆键里能放下的字数：中文 4 字以内原样，再长就截 4 字。</summary>
    static string FitLabel(string s)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        if (s.Length <= 4) return s;
        return s.Substring(0, 4);
    }

    /// <summary>
    /// 这一指是否落在出招键 / 翻页键上；是就执行（每个 fingerId 只算一次）。
    /// 返回 true 表示已经被消费，外层不要再把它当点击投给场景。
    /// </summary>
    static bool HitCastKey(Vector2 p, float W, float H, int fingerId)
    {
        // 翻页键先判（它在拾取键下方，和 8 个竖排圆键不重叠，但顺序上先判更省事）
        if (_castBar2Exists && (p - CastPageCenter(W, H)).magnitude <= CastBarRadius)
        {
            if (_castFinger != fingerId)
            {
                _castFinger = fingerId;
                _castPage = _castPage == 0 ? 1 : 0;
                _castPageFlashT = 0.14f;
                if (Log != null) Log("出招键 → 翻到 Bar" + (_castPage + 1) + "（键位 " + (_castPage * 8 + 1) + ".." + (_castPage * 8 + 8) + "）");
            }
            return true;
        }

        for (int i = 0; i < CastMax; i++)
        {
            if (string.IsNullOrEmpty(_castName16[CastKeyOf(i) - 1])) continue;  // 空槽没有键
            if ((p - CastCenter(i, W, H)).magnitude > CastRadiusNow(H)) continue;
            if (_castFinger != fingerId)
            {
                _castFinger = fingerId;
                DoCast(i);
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// 屏幕第 slot 格（1 起）对应哪个技能键位 —— 给 P2DevCmd 的 vcast 做验收用，
    /// 保证「命令里说的第几格」和「手指点第几格」算的是同一个键。
    /// </summary>
    public static int CastKeyForSlot(int slot)
    {
        if (slot < 1 || slot > CastMax) return 0;
        return CastKeyOf(slot - 1);
    }

    /// <summary>
    /// 从外壳外面「按一下第 slot 格出招键」，走和手指点击完全相同的 DoCast。
    /// 只给 P2DevCmd 验收用（真机上玩家走触摸事件进 HitCastKey）。
    /// </summary>
    public static void CastBySlot(int slot)
    {
        if (slot < 1 || slot > CastMax) return;
        DoCast(slot - 1);
    }

    /// <summary>当前有没有挂着一个「走过去再放」的远程技能（验收用）。</summary>
    public static bool HasPendingCast { get { return _castPendingKey != 0; } }

    /// <summary>待放技能的状态摘要（验收用）。</summary>
    public static string PendingCastInfo
    {
        get
        {
            if (_castPendingKey == 0) return "无";
            return _castPendingName + " 键位=" + _castPendingKey + " 目标id=" + _castPendingId;
        }
    }

    /// <summary>
    /// 放第 i 格（这一屏）的技能：走 Bridge.UseSpell(键位)，和按 F1~F8 完全同一条路。
    ///
    /// ★ 2026-11-15 加「远处目标自动接近」（用户原话：火球术、雷电术这些远处的应该怎么弄）。
    ///
    /// 原版是怎么处理的（已逐行取证）：
    ///   GameScene.cs:12596
    ///     if ((magic.Range != 0) && (!Functions.InRange(actor.CurrentLocation, location, magic.Range)))
    ///     { …弹一句「目标距离太远」… actor.ClearMagic(); return; }
    ///   ⇒ 原版**只管够不够得着，够不着就放弃**，绝不自己走过去。
    ///     电脑上这没问题（玩家自己拿鼠标点路），手机上目标在 9 格外时
    ///     玩家只会看到「按了技能键，没反应」。
    ///
    /// 所以外壳补这一段，分三种情况：
    ///   ① 近战技能（Range=0）→ 维持原样，交给 TryChaseTarget 贴身
    ///   ② 远程技能 + 目标在射程内 → 直接放
    ///   ③ 远程技能 + 目标在射程外 → 记住「要放哪个键」，边走边判断，进了射程自动放
    ///
    /// 第③种是**一个持续状态**，不是一帧的事：走好几格要好几百毫秒，
    /// 这里只负责记下意图，真正每帧推进的是 TickPendingCast()。
    /// </summary>
    static void DoCast(int i)
    {
        int key = CastKeyOf(i);
        string name = _castName16[key - 1];
        if (string.IsNullOrEmpty(name))
        {
            ShowToast("键位 " + key + " 还没学技能");
            if (Log != null) Log("出招键 → 键位 " + key + " 空（未学）");
            return;
        }

        _castFlashT[i] = 0.12f;

        bool passive = _castPassive16[key - 1];
        if (passive)
        {
            ShowToast(name + "（被动）");
            if (Log != null)
                Log("出招键 → 键位 " + key + " " + _castFull16[key - 1] +
                    " ⇒ 被动技能，原版 UseSpell 直接 return，画面上不会有反应（正常）");
            return;
        }

        // ── 判「这个技能是不是远程、目标离多远」──
        int range = -1;
        bool ranged = false;
        try { range = Client.MirCore.Bridge.SpellRange(key); } catch { }
        try { ranged = Client.MirCore.Bridge.SpellIsRanged(key); } catch { }

        var tgt = MapObject.TargetObject;
        var user = MapObject.User;

        // 远程技能 + 有活目标 + 够不着 ⇒ 记下意图，交给 TickPendingCast 走过去
        if (ranged && range > 0 && tgt != null && !tgt.Dead && user != null)
        {
            int dist = Functions.MaxDistance(user.CurrentLocation, tgt.CurrentLocation);
            if (dist > range)
            {
                _castPendingKey = key;
                _castPendingId = tgt.ObjectID;
                _castPendingName = name;
                // ★ 2026-11-15 实机改 12 → 25 秒。
                //   战士走路是「一格一格的排队动作」，每格要等一个动作周期；
                //   模拟器掉到 FPS≈1 时 13 格能走 15 秒以上，12 秒的硬超时
                //   会先把人掐死（实机 `寻路 距离=13 路径=12 步` → `超时取消`，
                //   而人其实一直在朝怪走）。卡死另有 8 秒「没更靠近」判定兜着，
                //   这条只是防无限挂着，放宽到 25 秒不会漏掉真卡死。
                _castPendingT = Time.unscaledTime + 25f;
                _castPendingRepathT = 0f;
                _castPendingRepathGate = 0f;
                _castPendingStallT = 0f;
                _castBestDist = int.MaxValue;
                _castPendingLastX = user.CurrentLocation.X;
                _castPendingLastY = user.CurrentLocation.Y;
                _castMoveStarted = true;

                // 点了技能键 = 玩家接管移动，命令行遗留的 walk/talknpc 必须让位。
                // 不掐的话：WalkTo 覆盖了它的路线，人朝怪走了几格；等这条 A* 走完，
                // StepWalk 会照旧目标每 0.5 秒重新寻路，把角色往反方向拽回去
                // （实机 `寻路=剩196步 AutoPath=True 终点=(320,270)`）。
                P2DevCmd.CancelCmdWalk();

                ShowToast(name + " → 走近到 " + range + " 格再放");
                if (Log != null)
                    Log("出招键 → 键位 " + key + " " + _castFull16[key - 1]
                        + " 是远程（射程 " + range + " 格），目标 " + tgt.Name
                        + " 距离 " + dist + " 格 ⇒ 先走过去");
                return;
            }

            // 在射程内：把目标写进 MagicObject 再放（手机上 MouseObject 恒为 null）
            try { Client.MirCore.Bridge.SetMagicTarget(tgt.ObjectID); } catch { }
        }

        bool ok = false;
        try { ok = Client.MirCore.Bridge.UseSpell(key); }
        catch { ok = false; }

        ShowToast("放 " + name);
        if (Log != null)
        {
            string mp = "?";
            try
            {
                var u = MapObject.User;
                mp = u == null ? "?" : (u.MP + "/" + u.Stats[Stat.MP]);
            }
            catch { }
            Log("出招键 → 键位 " + key + " " + _castFull16[key - 1] +
                " 投递=" + ok + " 蓝=" + mp
                + " 远程=" + ranged + " 射程=" + range
                + " 目标=" + (tgt == null ? "无" : tgt.Name));
        }
    }

    // ────────────────────────────────────────────────────────────
    //  远程技能：目标在射程外 → 走过去 → 进射程自动放
    //
    //  这是用户 2026-11-15 问的「远处的目标，火球术、雷电术这些应该怎么弄」。
    //  原版不管这件事（够不着就弹一句话放弃，见 DoCast 注释），
    //  所以手游外壳必须补：主流传奇手游点技能键打远处的怪，人都会自己走过去。
    // ────────────────────────────────────────────────────────────

    static int    _castPendingKey;       // 要放的键位（0 = 没有待放技能）
    static uint   _castPendingId;        // 锁定的是哪只怪
    static string _castPendingName = "";
    static float  _castPendingT;         // 到这个时刻还没走到就放弃
    static float  _castPendingRepathT;   // 重寻路节流
    static float  _castPendingStallT;    // 「离怪没再靠近」累计（卡住检测）
    static int    _castBestDist = int.MaxValue; // 走到过的最短距离（判有没有进展）
    static int    _castPendingLastX = -1, _castPendingLastY = -1;
    static bool   _castMoveStarted;      // 已经开始移动（用来只打一条「开始走」的日志）

    /// <summary>
    /// 把「本层按下去的鼠标左键」松开。
    ///
    /// ★ 2026-11-15 实机定案：不松手 = 自动寻路永远不生效。
    ///   原版 MapControl.CheckInput 的结构是
    ///     if (MouseControl == this) { switch (MapButtons) { case Left: …走路…; return; } }
    ///     if (AutoPath) { …A* 逐格走… }
    ///   手一按住（MapButtons=Left），switch 那个分支就把这一帧 return 掉了，
    ///   **AutoPath 那段根本跑不到**。于是 Bridge.WalkTo 算出来的 19 步 A* 路线
    ///   一直挂在 CurrentPath 里没人读，人只会按上次 SetPointerToDirection 留下的
    ///   旧方向一格格蹭：实机日志 `寻路=剩19步 AutoPath=True 终点=(628,640)`
    ///   连续四帧不变，User 在 (614,646)↔(615,646) 之间来回，最后 12 秒超时。
    ///   走通的那条之所以行，是因为它第一次就寻路成功、从头没按过左键。
    /// </summary>
    static void ReleaseHold()
    {
        // ★ 这里**不**加 `if (_padHeld)`：实机日志里 MapButtons=Left 但没走到
        //   「自己按下去」那次记录的路径也存在（旧方向指针 MapLocation 留着上一轮的值）。
        //   待放技能是「全自动走过去」的场合，左键按着只会坏事，一律清掉。
        //   清完如果这一帧还要直线推，ApplyDirection 会重新按下去，不影响兜底。
        try { MapControl.MapButtons &= ~System.Windows.Forms.MouseButtons.Left; } catch { }
        _padHeld = false;
    }

    /// <summary>待放技能状态复位。</summary>
    static void ClearPendingCast()
    {
        if (_castPendingKey != 0 && Log != null && _castPendingName.Length > 0)
            Log("待放技能 → 取消 " + _castPendingName);
        _castPendingKey = 0;
        _castPendingId = 0;
        _castPendingName = "";
        _castPendingT = 0f;
        _castPendingRepathT = 0f;
        _castPendingStallT = 0f;
        _castBestDist = int.MaxValue;
        _castPendingLastX = -1;
        _castPendingLastY = -1;
        _castMoveStarted = false;
        try { Client.MirCore.Bridge.StopWalk(); } catch { }
    }

    static float  _castPendingRepathGate; // 重寻路的节流闸门（0.5 秒一次，和对话键同节奏）

    /// <summary>
    /// 每帧推进「待放技能」。返回 true = 这一帧的移动被它接管。
    ///
    /// 走法复用已验证的链路（Bridge.WalkTo 走原版 A*）：
    /// 直线推方向会在墙/树后面顶着墙原地踏步，实机已经踩过一次
    /// （日志连着 9 条「走不到（距离 3，0.75 秒原地不动）」）。
    /// </summary>
    static bool TickPendingCast(MapControl map)
    {
        if (_castPendingKey == 0) return false;

        var user = MapObject.User;
        if (user == null || user.Dead) { ClearPendingCast(); return false; }

        if (Time.unscaledTime > _castPendingT)
        {
            ShowToast("走不过去，技能取消");
            if (Log != null) Log("待放技能 → 超时取消 " + _castPendingName);
            _castPendingKey = 0;
            try { Client.MirCore.Bridge.StopWalk(); } catch { }
            return false;
        }

        // 目标死了 / 丢了 → 收工
        var t = MapObject.TargetObject;
        if (t == null || t.Dead || t.ObjectID != _castPendingId)
        {
            ClearPendingCast();
            return false;
        }

        int range = -1;
        try { range = Client.MirCore.Bridge.SpellRange(_castPendingKey); } catch { }
        if (range <= 0) { ClearPendingCast(); return false; }

        int dist = Functions.MaxDistance(user.CurrentLocation, t.CurrentLocation);
        if (dist <= range)
        {
            // ── 进射程了：停脚 → 把目标写进 MagicObject → 放 ──
            try { Client.MirCore.Bridge.StopWalk(); } catch { }
            ReleaseHold();

            int key = _castPendingKey;
            string nm = _castPendingName;
            _castPendingKey = 0;      // 先清，避免 UseSpell 同步失败时卡住重入

            bool set = false;
            try { set = Client.MirCore.Bridge.SetMagicTarget(t.ObjectID); } catch { }
            bool ok = false;
            try { ok = Client.MirCore.Bridge.UseSpell(key); } catch { }

            ShowToast("放 " + nm);
            if (Log != null)
                Log("待放技能 → 到位（距离 " + dist + " ≤ 射程 " + range + "）放 " + nm
                    + " 锁目标=" + set + " 投递=" + ok);
            return true;
        }

        // ── 卡住检测：离怪的「最近距离」8 秒没再缩短，才判定走不过去 ──
        //
        // ★ 2026-11-15 两次实机修正，两次都是判据写错：
        //   第一版「1.2 秒位置没变」→ **永远不会触发**：卡在两格之间来回蹭时坐标每帧
        //     都在变（614,646 ↔ 615,646），moved 恒为 true，于是走满 12 秒超时才取消。
        //   第二版「3 秒没能更靠近」→ **太急**：怪自己会动，实机 `最近距离 11 现在 12`，
        //     就差这 1 格，3 秒一到立刻判死，明明人还在往它那边走。
        //   现在：距离 **≤ 历史最好** 就算有进展（怪原地抖 11↔12 时 11 的这一帧
        //   会把计时清零，不会被抖动骗），连续 8 秒都没刷新最好成绩才放弃。
        //   真走不过去（墙对面/被围死）8 秒足够暴露，硬超时 20 秒兜底。
        if (dist <= _castBestDist)
        {
            _castBestDist = dist;
            _castPendingStallT = 0f;
            _castPendingLastX = user.CurrentLocation.X;
            _castPendingLastY = user.CurrentLocation.Y;
        }
        else
        {
            _castPendingStallT += Time.unscaledDeltaTime;
            if (_castPendingStallT > 8f)
            {
                ShowToast("走不过去，技能取消");
                if (Log != null) Log("待放技能 → " + t.Name + " 走不到（8 秒没能更靠近，"
                    + "最近距离 " + _castBestDist + " 现在 " + dist + "），取消");
                _castPendingKey = 0;
                try { Client.MirCore.Bridge.StopWalk(); } catch { }
                return false;
            }
        }

        // ── 走过去：A* 绕墙，路线跟着怪刷新 ──
        //
        // ★★ 必须先松手再寻路（2026-11-15 实机定案，见 ReleaseHold 注释）：
        //    MapButtons 只要还按着 Left，CheckInput 就走 switch 那个分支并 return，
        //    后面 `if (AutoPath)` 整段 A* 逐格走路的代码一句都跑不到。
        //    这就是「寻路=剩19步 AutoPath=True」却一步不动的真根因。
        ReleaseHold();

        bool hasRoute = Client.MirCore.Bridge.WalkRemaining() > 0;
        bool stale = !hasRoute || Time.unscaledTime >= _castPendingRepathT;

        if (stale && Time.unscaledTime >= _castPendingRepathGate)
        {
            _castPendingRepathGate = Time.unscaledTime + 0.5f;
            _castPendingRepathT = Time.unscaledTime + 2f;   // 怪会动，2 秒把终点挪一次
            int steps = Client.MirCore.Bridge.WalkTo(t.CurrentLocation.X, t.CurrentLocation.Y);
            if (steps > 0)
            {
                if (_castMoveStarted && Log != null)
                {
                    _castMoveStarted = false;
                    Log("待放技能 → 寻路 " + t.Name + " 距离=" + dist
                        + " 路径=" + steps + " 步 →(" + t.CurrentLocation.X + "," + t.CurrentLocation.Y + ")");
                }
                return true;
            }
        }

        // 还有路在走就继续跟着走（哪怕这一帧刚过 2 秒还没到重算闸门）。
        // ★ 2026-11-15 修：原来这一支是 `else if`，一旦 stale 成立又没到闸门，
        //   就会掉到下面的直线兜底去推一帧方向，把 A* 的节奏打断。
        if (hasRoute) return true;

        // 无路可走兜底：直线推一格（这一步才需要按住左键）
        ApplyDirection(map, Functions.DirectionFromPoint(user.CurrentLocation, t.CurrentLocation));
        return true;
    }

    /// <summary>
    /// 把 NPC 对话框里的链接文字抄一份过来（对话框一出现就抄，变了就换）。
    /// 只抄，不点 —— 点击留给 Draw 出来的大按钮。
    /// </summary>
    static void PollNpcOptions()
    {
        bool open = false;
        string[] texts = null;
        try
        {
            open = Client.MirCore.Bridge.NpcDialogVisible();
            if (open) texts = Client.MirCore.Bridge.NpcButtonTexts();
        }
        catch { open = false; }

        if (!open)
        {
            if (_npcDlgOpen) { _npcDlgOpen = false; _npcOpts = new string[0]; }
            return;
        }

        _npcDlgOpen = true;
        if (texts == null) texts = new string[0];

        bool same = _npcOpts.Length == texts.Length;
        if (same)
            for (int i = 0; i < texts.Length; i++)
                if (_npcOpts[i] != texts[i]) { same = false; break; }
        if (same) return;

        if (texts.Length > NpcOptMax)
        {
            var cut = new string[NpcOptMax];
            System.Array.Copy(texts, cut, NpcOptMax);
            texts = cut;
        }
        _npcOpts = texts;
        if (Log != null)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < texts.Length; i++)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append('"').Append(texts[i]).Append('"');
            }
            Log("对话选项 → " + texts.Length + " 个: " + sb);
        }
    }

    /// <summary>点了某个对话选项：直接点原版那个小链接（ClickNpcButton 内部会发 C.CallNPC）。</summary>
    static void DoNpcOption(int i)
    {
        if (i < 0 || i >= _npcOpts.Length) return;
        string text = _npcOpts[i];
        int bx = 0, by = 0;
        bool ok = false;
        try { ok = Client.MirCore.Bridge.ClickNpcButton(text, out bx, out by); }
        catch { ok = false; }
        _npcOptFlashT = 0.16f;
        _npcOptFlashIdx = i;
        if (ok) ShowToast("选 " + text);
        if (Log != null)
            Log("对话选项 → 点 \"" + text + "\" " + (ok ? "已投递 @(" + bx + "," + by + ")" : "没点到（链接可能已变）"));
        PollNpcOptions();          // 立刻刷新，别等 0.3 秒
    }

    /// <summary>关掉 NPC 对话框。</summary>
    static void DoCloseNpcDialog()
    {
        try { Client.MirCore.Bridge.CloseNpcDialog(); } catch { }
        _npcDlgOpen = false;
        _npcOpts = new string[0];
        if (Log != null) Log("对话选项 → 已关闭对话框");
    }

    // ── 当前画面对应的视口（letterbox 后的 4:3 区域，GUI 坐标）──
    static Rect _viewport;

    // ★ 2026-11-26 订正（这条推翻了 2026-10-29 的两次「定案」，以下是实测后的真因）：
    //
    //   旧结论说「Tuanjie 的 IMGUI 把 A 通道整条链丢掉」。**这是误诊。**
    //   反证在同目录 P2StickArt.cs:95-100：摇杆底盘从 2026-11-11 起就是
    //     ApplyDiscAlpha(...) 把盘外 A 写成 0 + DrawTexture(..., alphaBlend:true)，
    //   实机可见底盘外透出草地 —— A 通道一直是生效的。
    //
    //   那么 2026-10-29 那两次实测看到的是什么？
    //     证据一说「GUI.color.a=0.34 无效」—— 对，但原因是**当时贴图的 A 是 255**，
    //       GUI.color 的 alpha 在 IMGUI 里对 DrawTexture 只做 RGB 乘算（tint），
    //       它本来就**不是**一个"整体不透明度"旋钮。不是 A 被丢。
    //     证据二说「把 alpha 写进纹理 A 也不变」—— 因为那次纹理是预乘的，
    //       且盘内 A 仍是 255，A=0 只出现在圆外那块方角上，看起来当然"没变化"。
    //
    //   ⇒ 真正的问题从头到尾只有一个：**MakePad 自己把 alpha 预乘进 RGB、把 A 钉死 255**，
    //     于是每个按键是一张不透明圆贴纸。用户 2026-11-26 原话「各种虚拟按键还不是透明的，
    //     太丑了」就是这个。现在 MakePad 改成 straight alpha（RGB 本色 + A 覆盖率），
    //     盘面半透明、背景真的透出来。
    //
    // ★ 2026-10-29 第二处定案仍然有效（这条是真 bug，别再拆开画）：
    //   一个按键只能有**一次** DrawTexture。填充、描边、内环全部烘进同一张贴图 ——
    //   不是因为"A 被丢、圆外是黑的"，而是因为**分两张画会在接缝处互相削边**：
    //   环那张贴图圆内的填充是半透明的，叠在填充圆上会把圆心又压暗一遍，
    //   于是"圆心比环暗"的假象就出现了（旧实测圆心 (0,0,0) / 环 (120,130,142)
    //   正是两层半透明叠加的结果，不是刷黑）。
    static readonly System.Collections.Generic.Dictionary<string, Texture2D> _padCache
        = new System.Collections.Generic.Dictionary<string, Texture2D>();
    static GUIStyle _style;

    // ★ 2026-11-10 字体定案 —— 用户原话「就跟那两年级的小学生写的字一样」。
    //   根因：这一层一直在用 _style = new GUIStyle(GUI.skin.label)，
    //   也就是 Unity 内置的 Arial。Arial **没有汉字**，中文全靠系统字体回退，
    //   而 Android 的回退字体在小字号 + Bold 下笔画是歪的（这就是「小学生字」）。
    //   工程里明明有 chatfont.ttf（聊天 / HUD / 通知三层都在用，见 P0HChat.cs:1232、
    //   P0HHud.cs:329、P0HNotify.cs:598），这一层漏了，所以它和游戏内文字两套字形。
    //   现在改用同一套：chatfont → 系统中文字体 → GUI.skin.font 三级回退。
    static Font _font;
    static bool _fontTried;

    static void EnsureStyle()
    {
        if (!_fontTried)
        {
            _fontTried = true;
            _font = Resources.Load<Font>("chatfont");
            if (_font == null)
            {
                string[] cands = { "Noto Sans CJK SC", "Noto Sans SC", "Droid Sans Fallback", "Arial Unicode MS" };
                try { _font = Font.CreateDynamicFontFromOSFont(cands, 32); }
                catch (Exception) { _font = null; }
            }
        }
        _style = new GUIStyle(GUI.skin.label);
        if (_font != null) _style.font = _font;
        // ★ 2026-11-11 去掉 Bold（这是「字发虚/粗糙」的一个真根因）：
        //   chatfont.ttf 是 SimHei，**没有粗体面**。Unity 动态字体遇到 Bold 会走
        //   FreeType 的合成加粗（轮廓膨胀 1px），小字号下笔画会连成糊块，
        //   实机评审原话「偏小且发虚，字体比较粗糙」。SimHei 本身就是黑体，
        //   常规字重已经够粗，不需要再合成加粗。
        _style.fontStyle = FontStyle.Normal;
        _style.alignment = TextAnchor.MiddleCenter;
        _style.clipping = TextClipping.Overflow;
        _style.wordWrap = false;
        _style.padding = new RectOffset(0, 0, 0, 0);
        _style.margin = new RectOffset(0, 0, 0, 0);
    }

    /// <summary>外壳挂进来的日志出口（方向变化时打一条，用来在 adb 侧验收）。</summary>
    public static System.Action<string> Log;

    static MirDirection _loggedDir;
    static bool _loggedActive;

    /// <summary>
    /// 每帧由外壳告知视口。视口外是黑边，落在那里但不在热区里的触摸直接吃掉，
    /// 免得它被换算成逻辑坐标 (0,0) 一带，在画面边缘乱点。
    /// </summary>
    public static void SetViewport(Rect vp) { _viewport = vp; }

    /// <summary>离开 GameScene（登出/回选角）时清干净，否则 MapButtons 会一直卡着。</summary>
    public static void Reset()
    {
        Release();
        _hasDir = false;
        _atkPressed = false;
        _atkFlashT = 0f;
        _atkFinger = -2;
        _atkHoldFinger = -2;
        _atkHoldT = 0f;
        _atkHoldFired = false;
        if (_shiftApplied) ClearForceAttack();
        _dirSeenT = -99f;
        _lockDirValid = false;
        _tapLocked = false;
        _tapLockedId = 0;
        _tapSelFinger = -2;
        _coneBuf.Clear();
        _pickFlashT = _potFlashT = _npcFlashT = 0f;
        _pickFinger = _potFinger = _npcFinger = -2;
        _bagFlashT = _eqpFlashT = _skillFlashT = 0f;
        _bagFinger = _eqpFinger = _skillFinger = -2;
        _nextFlashT = 0f;
        _nextFinger = -2;
        for (int i = 0; i < CastMax; i++) _castFlashT[i] = 0f;
        for (int i = 0; i < 16; i++) { _castName16[i] = null; _castFull16[i] = null; _castIcon16[i] = 0; }
        _castBar2Exists = false;
        _castPage = 0;
        _castPollT = 0f;
        _castPageFlashT = 0f;
        _castFinger = -2;
        _castSig = null;
        _toast = null;
        _toastT = 0f;
        _autoPotT = 0f;
        _autoPickT = 0f;
        _pickHasTarget = false;
        _npcHasTarget = false;
        _npcOpts = new string[0];
        _npcOptPollT = 0f;
        _npcOptFinger = -2;
        _npcOptFlashT = 0f;
        _npcOptFlashIdx = -1;
    }

    static void Release()
    {
        _active = false;
        _touchId = -1;
        // ★ 松手立刻把球收回盘心、方向清掉（2026-11-01 加）。
        //   旧版没清 _knob：虽然画图时 _active=false 会画在中心，但 _hasDir 还留着 true，
        //   下一帧若 _active 又为真（连按），球会从上次的位置起跳，看着像瞬移。
        _knob = _origin;
        _hasDir = false;
        // ★ 2026-11-18 开会定案（三刀·A1）：记下松手时刻。
        //   原来松手后下一帧就掉进 TryChaseTarget，人立刻掉头走回刚才在躲的那只怪 ——
        //   玩家体感是「想跑跑不掉，这摇杆是假的」。松手 = 明确的收手信号，1.2 秒内不自动追。
        _releasedAt = Time.unscaledTime;
        if (_padHeld)
        {
            try { MapControl.MapButtons &= ~System.Windows.Forms.MouseButtons.Left; } catch { }
            _padHeld = false;
        }
    }

    // ────────────────────────────────────────────────────────────
    //  每帧驱动
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 处理虚拟按键。返回 true 表示这一帧的触摸已经被消费，外面不要再转给原版场景
    /// （否则摇杆按住的同时还会「点地面走路」，两套输入打架）。
    /// </summary>
    public static bool Step(GameScene gs, Rect viewport)
    {
        LoadLook();          // 首次调用读一次外观设置（缩放/浓淡），之后是空转
        FitScale(Screen.height);   // 每帧按实际屏幕高度封顶，调大不会把键顶出屏幕
        _viewport = viewport;

        if (gs == null) { Reset(); return false; }
        if (_atkFlashT > 0f) _atkFlashT -= Time.unscaledDeltaTime;
        if (_pickFlashT > 0f) _pickFlashT -= Time.unscaledDeltaTime;
        if (_potFlashT > 0f) _potFlashT -= Time.unscaledDeltaTime;
        if (_npcFlashT > 0f) _npcFlashT -= Time.unscaledDeltaTime;
        if (_bagFlashT > 0f) _bagFlashT -= Time.unscaledDeltaTime;
        if (_eqpFlashT > 0f) _eqpFlashT -= Time.unscaledDeltaTime;
        if (_skillFlashT > 0f) _skillFlashT -= Time.unscaledDeltaTime;
        if (_nextFlashT > 0f) _nextFlashT -= Time.unscaledDeltaTime;
        if (_toastT > 0f) _toastT -= Time.unscaledDeltaTime;

        var map = gs.MapControl;
        if (map == null || MapObject.User == null) { Reset(); return false; }

        float W = Screen.width, H = Screen.height;
        Vector2 hotCenter, atkCenter, pickCenter, potCenter, npcCenter, bagCenter, eqpCenter, skillCenter;
        GetCenters(W, H, out hotCenter, out atkCenter, out pickCenter, out potCenter,
                   out npcCenter, out bagCenter, out eqpCenter, out skillCenter);

        // ── 自动拾取：走到哪格，脚下有东西就顺手捡 ──
        // 2026-10-30 加。原版 PC 端靠「鼠标点自己脚下那格」捡东西，
        //   手机上没有这个动作，实测连打 8 条日志 `金币=0` 一动不动，
        //   于是商店一件也买不了。手机上标准做法就是走过去自动捡。
        // 节流 400ms：服务端 PickUp 一次只捡一件（PlayerObject.PickUp 里捡完就 return），
        //   同格堆了几件时需要连发几次，太快只会排空包。
        _autoPickT -= Time.unscaledDeltaTime;
        if (_autoPickT <= 0f)
        {
            _autoPickT = 0.4f;
            try
            {
                string atFeet = Client.MirCore.Bridge.ItemUnderFeet();
                if (!string.IsNullOrEmpty(atFeet))
                {
                    Client.MirCore.Bridge.PickUp();
                    ShowToast("捡起 " + atFeet);
                    if (Log != null) Log("自动拾取 → " + atFeet);
                }
            }
            catch { }
        }

        // ── 自动喝药：血/蓝见底就自己喝，玩家不用管 ──
        // 节流 1 秒一次：原版 UseItem 有 200ms 的发包节流，问得太勤只会白费。
        _autoPotT -= Time.unscaledDeltaTime;
        if (_autoPotT <= 0f)
        {
            _autoPotT = 1f;
            try
            {
                string what = Client.MirCore.Bridge.AutoPotion();
                if (!string.IsNullOrEmpty(what)) { ShowToast("自动喝药 " + what); _potFlashT = 0.12f; }
                else
                {
                    // ★ 2026-11-18 开会定案（四刀·没药提示）：论文 E 1.2。
                    //   AutoPotion 找不到药时静默返回空串，玩家什么都不知道，血一直掉到死。
                    //   血低于 70%（就是 AutoPotion 自己的触发线）还没药可喝 ⇒ 每 15 秒提示一次。
                    int hpPct, mpPct, hp, maxHp;
                    Client.MirCore.Bridge.GetVitals(out hpPct, out mpPct, out hp, out maxHp);
                    if (maxHp > 0 && hp * 10 < maxHp * 7 && Time.unscaledTime >= _autoNoPotT)
                    {
                        _autoNoPotT = Time.unscaledTime + 15f;
                        ShowToast("没药了（血 " + hpPct + "%）");
                        if (Log != null) Log("自动喝药 → 没药了，血量 " + hp + "/" + maxHp);
                    }
                }
            }
            catch { }
        }

        // ── NPC 对话选项：每 0.3 秒拉一次对话框里的链接清单 ──
        // 轮询而不是事件通知：Bridge 那边没有回调口子，而对话框是服务端包驱动的，
        // 0.3 秒的延迟在手机上完全感觉不到。清单没变就不动，避免每帧重建按钮。
        _npcOptPollT -= Time.unscaledDeltaTime;
        if (_npcOptPollT <= 0f)
        {
            _npcOptPollT = 0.3f;
            PollNpcOptions();
        }
        if (_npcOptFlashT > 0f) _npcOptFlashT -= Time.unscaledDeltaTime;

        for (int i = 0; i < CastMax; i++)
            if (_castFlashT[i] > 0f) _castFlashT[i] -= Time.unscaledDeltaTime;
        if (_castPageFlashT > 0f) _castPageFlashT -= Time.unscaledDeltaTime;

        // ── 出招键清单：每 0.5 秒拉一次（技能是 NPC 传功时当场学会的，得跟着变）──
        _castPollT -= Time.unscaledDeltaTime;
        if (_castPollT <= 0f)
        {
            _castPollT = 0.5f;
            PollCastSlots();
        }

        bool consumed = false;

        for (int i = 0; i < Input.touchCount; i++)
        {
            var t = Input.GetTouch(i);
            Vector2 p = ToGui(t.position);

            if (t.phase == TouchPhase.Began)
            {
                // ── NPC 对话期间：选项键优先（它盖在右侧黑边上，和拾取/喝药同一带）──
                // 顺序放在最前：对话框开着的时候玩家最想点的就是这几颗，
                // 挡到下面的拾取/喝药键也不会误触（那几个键此时没什么用）。
                if (_npcDlgOpen)
                {
                    bool hitOpt = false;
                    for (int k = 0; k < _npcOpts.Length; k++)
                    {
                        if ((p - NpcOptCenter(k, W)).magnitude > NpcOptRadius) continue;
                        hitOpt = true;
                        if (_npcOptFinger != t.fingerId)
                        {
                            _npcOptFinger = t.fingerId;
                            DoNpcOption(k);
                        }
                        break;
                    }
                    if (!hitOpt && (p - NpcCloseCenter(W)).magnitude <= NpcOptRadius)
                    {
                        hitOpt = true;
                        if (_npcOptFinger != t.fingerId)
                        {
                            _npcOptFinger = t.fingerId;
                            DoCloseNpcDialog();
                        }
                    }
                    if (hitOpt) { consumed = true; continue; }
                }

                if (!_active && (p - hotCenter).magnitude <= HotRadius)
                {
                    // ★ 底盘钉死在 hotCenter，不跟着手指跑（2026-11-01 改，用户明确指出的问题）：
                    //   旧代码 _origin = p（手指落点即摇杆中心），于是整块底盘会随手指漂移，
                    //   看着就是「一张贴图跟着手指走」，跟王者荣耀/撸啊撸差得远。
                    //   真实手游的做法是：底盘**永远固定在屏幕左下角**，只有摇杆头跟手；
                    //   方向从底盘中心算，手指推得够远才触发（死区），松手回弹。
                    //   手指落在热区里任何位置都算按下，落点只作为行程起点。
                    _active = true;
                    _touchId = t.fingerId;
                    _anchor = p;
                    _origin = hotCenter;     // 底盘中心恒等于左下角锚点
                    // ★ 球从盘心起步，不等于手指落点。
                    //   否则手指点在大热区边缘（HotRadius=235 比视觉环 190 还大），
                    //   一按球就"啪"地出现在环外缘 —— 看着不像摇杆，像鼠标瞬移。
                    _knob = hotCenter;
                    consumed = true;
                }
                else if ((p - atkCenter).magnitude <= AtkRadius)
                {
                    if (_atkFinger != t.fingerId)
                    {
                        _atkFinger = t.fingerId;
                        _atkHoldFinger = t.fingerId;
                        _atkHoldT = 0f;
                        _atkHoldFired = false;
                        ToggleAttackLock();
                        _atkPressed = true;
                        _atkFlashT = 0.12f;
                    }
                    consumed = true;
                }
                else if ((p - pickCenter).magnitude <= PickRadius)
                {
                    if (_pickFinger != t.fingerId)
                    {
                        _pickFinger = t.fingerId;
                        DoPickUp();
                        _pickFlashT = 0.12f;
                    }
                    consumed = true;
                }
                else if ((p - potCenter).magnitude <= PotRadius)
                {
                    if (_potFinger != t.fingerId)
                    {
                        _potFinger = t.fingerId;
                        DoDrinkPot();
                        _potFlashT = 0.12f;
                    }
                    consumed = true;
                }
                else if ((p - npcCenter).magnitude <= NpcRadius)
                {
                    if (_npcFinger != t.fingerId)
                    {
                        _npcFinger = t.fingerId;
                        DoTalkNpc();
                        _npcFlashT = 0.12f;
                    }
                    consumed = true;
                }
                else if ((p - bagCenter).magnitude <= BagRadius)
                {
                    if (_bagFinger != t.fingerId)
                    {
                        _bagFinger = t.fingerId;
                        DoToggleBag();
                        _bagFlashT = 0.12f;
                    }
                    consumed = true;
                }
                else if ((p - eqpCenter).magnitude <= EqpRadius)
                {
                    if (_eqpFinger != t.fingerId)
                    {
                        _eqpFinger = t.fingerId;
                        DoEquipAll();
                        _eqpFlashT = 0.12f;
                    }
                    consumed = true;
                }
                else if ((p - skillCenter).magnitude <= SkillRadius)
                {
                    if (_skillFinger != t.fingerId)
                    {
                        _skillFinger = t.fingerId;
                        DoToggleSkill();
                        _skillFlashT = 0.12f;
                    }
                    consumed = true;
                }
                else if ((p - NextCenter(W, H)).magnitude <= NextRadius)
                {
                    // 换目标键：连按 = 由近到远循环切下一只（见 CycleTarget）
                    if (_nextFinger != t.fingerId)
                    {
                        _nextFinger = t.fingerId;
                        CycleTarget();
                        _nextFlashT = 0.12f;
                    }
                    consumed = true;
                }
                else if (HitCastKey(p, W, H, t.fingerId))
                {
                    // 出招键（含翻页键）：HitCastKey 内部已经执行过 UseSpell
                    consumed = true;
                }
                else if (viewport.Contains(p) && TryTapSelect(p, t.fingerId, "落下"))
                {
                    // ★ 2026-11-15：点在地图区域、且点到怪/人 ⇒ 锁定它（主路）。
                    //   吃掉这一指，不再当「点地面走路」处理 —— 否则人会朝那只怪
                    //   自己走一格，镜头一平移，锁定的怪在屏幕上就跳了。
                    consumed = true;
                }
                else if (viewport.Contains(p) && MapObject.TargetObject != null)
                {
                    // ★ 2026-11-15：点空地 = 取消锁定，然后照常走这一格。
                    //
                    //   原版是「点空地 TargetObjectID = 0」（GameScene.cs:12017-12018
                    //   的 else 分支），然后正常走路。上一版这里写着「点空地不清锁定，
                    //   手滑一下丢目标很灾难」—— 那条顾虑在端游成立（鼠标精确），
                    //   在手机上反过来了：只有再点**同一只怪**才能解锁，而怪一直在动，
                    //   玩家根本点不中第二次 ⇒ 锁了就再也解不掉，这比误触可怕得多。
                    //   所以按原版来：点空地就解锁，解锁后不 return，让原版
                    //   PressAt 照常处理这一指的走路。
                    MapObject.TargetObjectID = 0;
                    MapObject.MagicObjectID = 0;
                    _lockDirValid = false;
                    _tapLocked = false;
                    _tapLockedId = 0;
                    // 待放的远程技能也一起取消：目标都没了，没有「走到 9 格内放火球」这回事
                    if (_castPendingKey != 0) ClearPendingCast();
                    ShowToast("解除锁定");
                }
                else if (!viewport.Contains(p))
                {
                    // 黑边里、又不在任何按键上 —— 不是游戏，也不是按键，吃掉。
                    consumed = true;
                }
            }
            else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
            {
                // ★ 抬起必须在这里一次判清，不能只靠下面的 else-if：
                //   2026-10-29 实测，Ended 分支插在前面后，摇杆手指抬起时进的是这里，
                //   而 Release() 在后面的分支里 → 永远执行不到 → _active 一直为真，
                //   手指松开人还在自己走。这种「松手不停」是最难查的一类 bug。
                if (_active && t.fingerId == _touchId)
                {
                    Release();
                    consumed = true;
                }
                // 极快的一次点击（手指只碰了 1~2 帧，Unity 直接报到 Ended）：
                // Began 分支根本没跑过，攻击键会「点了没反应」。adb 的 input tap 就是这个特征。
                // ★ 对话选项也必须在这里补一遍：`adb shell input tap` 实测只报 Ended，
                //   只在 Began 里判的话，点选项的验收会全是「没点到」。
                bool optHit = false;
                if (_npcDlgOpen && _npcOptFinger != t.fingerId)
                {
                    for (int k = 0; k < _npcOpts.Length; k++)
                    {
                        if ((p - NpcOptCenter(k, W)).magnitude > NpcOptRadius) continue;
                        optHit = true;
                        _npcOptFinger = t.fingerId;
                        DoNpcOption(k);
                        break;
                    }
                    if (!optHit && (p - NpcCloseCenter(W)).magnitude <= NpcOptRadius)
                    {
                        optHit = true;
                        _npcOptFinger = t.fingerId;
                        DoCloseNpcDialog();
                    }
                    if (optHit) consumed = true;
                }

                if (optHit)
                {
                    // 这一下已经算在对话选项上，别再落到底下的按键
                }
                else if ((p - atkCenter).magnitude <= AtkRadius && _atkFinger != t.fingerId)
                {
                    _atkFinger = t.fingerId;
                    ToggleAttackLock();
                    _atkPressed = true;
                    _atkFlashT = 0.12f;
                    // adb 的 input tap 只报 Ended，长按判断就此结束：
                    // 这一下最多算「短按」，绝不能把 Shift 留在按下状态。
                    _atkHoldFinger = -2;
                    _atkHoldT = 0f;
                    _atkHoldFired = false;
                    if (_shiftApplied) ClearForceAttack();
                    consumed = true;
                }
                else if ((p - pickCenter).magnitude <= PickRadius && _pickFinger != t.fingerId)
                {
                    _pickFinger = t.fingerId;
                    DoPickUp();
                    _pickFlashT = 0.12f;
                    consumed = true;
                }
                else if ((p - potCenter).magnitude <= PotRadius && _potFinger != t.fingerId)
                {
                    _potFinger = t.fingerId;
                    DoDrinkPot();
                    _potFlashT = 0.12f;
                    consumed = true;
                }
                else if ((p - npcCenter).magnitude <= NpcRadius && _npcFinger != t.fingerId)
                {
                    _npcFinger = t.fingerId;
                    DoTalkNpc();
                    _npcFlashT = 0.12f;
                    consumed = true;
                }
                else if ((p - bagCenter).magnitude <= BagRadius && _bagFinger != t.fingerId)
                {
                    _bagFinger = t.fingerId;
                    DoToggleBag();
                    _bagFlashT = 0.12f;
                    consumed = true;
                }
                else if ((p - eqpCenter).magnitude <= EqpRadius && _eqpFinger != t.fingerId)
                {
                    _eqpFinger = t.fingerId;
                    DoEquipAll();
                    _eqpFlashT = 0.12f;
                    consumed = true;
                }
                else if ((p - skillCenter).magnitude <= SkillRadius && _skillFinger != t.fingerId)
                {
                    _skillFinger = t.fingerId;
                    DoToggleSkill();
                    _skillFlashT = 0.12f;
                    consumed = true;
                }
                else if ((p - NextCenter(W, H)).magnitude <= NextRadius && _nextFinger != t.fingerId)
                {
                    // Ended 补判，理由同上（adb input tap 只报 Ended）
                    _nextFinger = t.fingerId;
                    CycleTarget();
                    _nextFlashT = 0.12f;
                    consumed = true;
                }
                else if (HitCastKey(p, W, H, t.fingerId))
                {
                    // 出招键（含翻页键）：HitCastKey 内部已经执行过 UseSpell
                    consumed = true;
                }
                else if (viewport.Contains(p) && TryTapSelect(p, t.fingerId, "抬起"))
                {
                    // ★ 点怪即锁的 Ended 补判。`adb shell input tap` 与很多机型的
                    //   极快点击只报 Ended（Began 分支根本不跑），只在 Began 判的话
                    //   实机验收会全是「没点到」。_tapSelFinger 保证同一次点击不判两遍。
                    consumed = true;
                }

                // 这根手指这一下已经算完，放掉标记，下次按下还能再触发
                _atkFinger = -2;
                _pickFinger = -2;
                _potFinger = -2;
                _npcFinger = -2;
                _bagFinger = -2;
                _eqpFinger = -2;
                _skillFinger = -2;
                _nextFinger = -2;
                _npcOptFinger = -2;
                _castFinger = -2;
                // ★ 必须清：fingerId 会被系统复用（同一根手指再次按下还是 0），
                //   不清的话第二次点怪会被「这一指判过了」直接挡掉。
                _tapSelFinger = -2;
            }
            else if (_active && t.fingerId == _touchId
                     && (t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary))
            {
                consumed = true;

                // ★ 2026-11-18 开会定案（三刀·A3）：行程与方向都从**按下点**算，不是从盘心。
                //   热区 225 比画出来的盘 190 大 35px，外面那圈什么都不画；
                //   拇指又习惯性落在偏左一点的位置 —— 旧版从盘心算，一按下去就已经有
                //   200px 位移 = 满速朝左跑，想「先按住再决定方向」根本做不到。
                //   改成从按下点算：手指不动 = 位移 0 = 不走；往哪边推才往哪边走。
                //   盘本身仍然钉死在左下角（球不跟盘跑），这是之前定稿的手感，不动。
                Vector2 d = p - _anchor;

                // 摇杆头：钉在盘心为原点，跟手但不跑出外圈
                _knob = d.magnitude > StickRadius ? _origin + d.normalized * StickRadius : _origin + d;

                float mag = Mathf.Clamp01(d.magnitude / StickRadius);
                if (mag >= DeadZone) { _dir = DirFrom(d); _hasDir = true; NoteDirection(_dir); }
                else { _hasDir = false; }
            }
        }

        // ★ 摇杆手指凭空消失也必须放开（2026-10-29 实测定案）：
        //   有些机型的 Ended 事件会被系统吞掉（切后台、来电、手势导航），
        //   那时 touchCount 直接变小，没有任何一根手指报 Ended。
        //   不补救的话 _active 永远为真 ⇒ 「松手了人还在走」。
        if (_active && _touchId >= 0)
        {
            bool stillDown = false;
            for (int i = 0; i < Input.touchCount; i++)
                if (Input.GetTouch(i).fingerId == _touchId) { stillDown = true; break; }
            if (!stillDown) Release();
        }

        // 鼠标（编辑器里调试用）：按住左键拖 = 摇杆
        if (Input.touchCount == 0)
        {
            Vector2 mp = ToGui(Input.mousePosition);
            if (Input.GetMouseButtonDown(0) && (mp - hotCenter).magnitude <= HotRadius)
            {
                _active = true; _touchId = -1; _anchor = mp; _origin = hotCenter; _knob = hotCenter; consumed = true;
            }
            else if (Input.GetMouseButtonUp(0) && _active && _touchId == -1)
            {
                Release(); consumed = true;
            }
            if (_active && _touchId == -1)
            {
                Vector2 d = mp - _anchor;   // 三刀·A3：同触屏，从按下点算
                _knob = d.magnitude > StickRadius ? _origin + d.normalized * StickRadius : _origin + d;
                if (d.magnitude / StickRadius >= DeadZone) { _dir = DirFrom(d); _hasDir = true; NoteDirection(_dir); }
                else _hasDir = false;
            }
        }

        // ── 攻击键长按 = 强制攻击（等价于 PC 端按住 Shift）──
        // 判据：这一下按在攻击键上超过 ForceAtkHoldSec 秒、手指还在屏幕上，
        //       就把 Client.CMain.Shift 置 true；手指一离开立刻清回 false。
        // 为什么需要它：原版 CheckInput 三处自动攻击/追击都写着
        //   `CMain.Shift || Settings.NoShiftAttack`（GameScene.cs:12039 / 12139 / 12348），
        //   而整个 Unity 外壳从来没有写过 CMain.Shift（全仓零写入），
        //   于是「打玩家/红名/守卫」这条路在手机上是彻底断的。
        // ★ 只清自己置的那一位：MirAmountBox.cs:262 / MirTextBox.cs:403 也会把它置 false，
        //   别的地方（比如原版对话框）可能正需要它。
        UpdateAttackHold();

        // ── 把摇杆状态写进原版输入字段（这是走路的唯一入口）──
        if (_active && _hasDir)
        {
            // ★ 手动推杆 = 最高优先级，必须掐掉还在跑的自动寻路（2026-11-15 实测加）。
            //   原版 MapControl.CheckInput 里 AutoPath 那段排在 `switch (MapButtons)`
            //   **之后**：摇杆指向的方向能走时确实先走一格就走了；但指向墙/水/树
            //   （CanWalk 为 false）时会掉到 AutoPath 分支，于是玩家只想往左靠一下，
            //   人却被拽着朝锁定的怪绕过去 —— 手感上就是「摇杆失灵」。
            //   这一处同时管住两条自动走路：对话键寻路 / 拾取键寻路。
            if (map.AutoPath) Client.MirCore.Bridge.StopWalk();
            ApplyDirection(map, _dir);
            _pickHasTarget = false;   // 玩家自己推杆了，放弃自动走去捡
            _npcHasTarget = false;    // 同理，放弃自动走向 NPC
            // ★ 待放的远程技能也一起取消：玩家把摇杆推到底，意思就是「我自己走」，
            //   这时候还挂着一个「走到 9 格内自动放火球」会突然抢走操控。
            if (_castPendingKey != 0) ClearPendingCast();
        }
        else if (!_active && TickPendingCast(map))
        {
            // 远程技能点了技能键、目标在射程外 —— 已经记下要放的键，这一帧在走过去。
            // ★ 排在 NPC/拾取之后，是因为按技能键是玩家明确的一次动作。
            //   排在 NPC/拾取之后：那两条也是玩家点出来的，而且 NPC/掉落不会
            //   和「我要打那只怪」同时发生（真同时发生时先让走路类动作做完，
            //   技能意图还在 _castPendingKey 里，下一帧继续）。
        }
        else if (!_active && TryWalkToNpc(map))
        {
            // 点了对话键、NPC 不在贴身范围 —— 一路走到它旁边
        }
        else if (!_active && TryWalkToPick(map))
        {
            // 点了拾取键、东西不在脚下 —— 一路走到它那格
        }
        else if (!_active && TryChaseTarget(map))
        {
            // 锁着怪、摇杆又没推 —— 朝怪走（LoL 手游的「点一下自动贴上去」）
        }
        else if (_padHeld)
        {
            // ★ 只清「自己按下去的那一次」。摇杆从没按过就别去碰 MapButtons：
            //   原版 PressAt（点地面、点 NPC）也会置这一位，无脑清会把那些输入一起废掉。
            MapControl.MapButtons &= ~System.Windows.Forms.MouseButtons.Left;
            _padHeld = false;
        }

        // 方向变化打一行，adb 侧据此验收（不刷屏：只在变化时打）
        if (_active != _loggedActive || (_active && _hasDir && _dir != _loggedDir))
        {
            _loggedActive = _active;
            _loggedDir = _dir;
            if (Log != null)
                Log("摇杆 " + (_active ? (_hasDir ? _dir.ToString() : "按住·死区") : "松开")
                    + "  目标=" + (MapObject.TargetObject != null ? MapObject.TargetObject.Name : "无")
                    + "  地图=(" + MapObject.User.CurrentLocation.X + "," + MapObject.User.CurrentLocation.Y + ")");
        }

        return consumed;
    }

    /// <summary>
    /// 真正把「朝这个方向走」写进原版字段。
    /// MouseControl 必须指到 MapControl：CheckInput 里整段走路逻辑都在
    /// `if (MouseControl == this)` 里面，不指就是一潭死水（2026-10-29 实测）。
    /// </summary>
    static void ApplyDirection(MapControl map, MirDirection dir)
    {
        Client.MirControls.MirControl.MouseControl = map;
        SetPointerToDirection(map, dir);
        MapControl.MapButtons |= System.Windows.Forms.MouseButtons.Left;
        _padHeld = true;
    }

    /// <summary>
    /// 锁了目标但目标不在贴身距离 → 朝它走一格；贴身了就交回原版自动攻击。
    /// 返回 true 表示这一帧的移动已经被这里接管。
    /// </summary>
    static bool TryChaseTarget(MapControl map)
    {
        var target = MapObject.TargetObject;
        var user = MapObject.User;
        if (target == null || target.Dead || user == null) return false;
        if (Functions.InRange(target.CurrentLocation, user.CurrentLocation, 1)) return false;
        // ★ 2026-11-18 开会定案（三刀·A1）：松手 1.2 秒内不自动追。
        //   锁着怪往反方向躲，手指一松人立刻掉头走回怪 —— 原版端游是鼠标点怪自动走近，
        //   鼠标不会「松手」；手机摇杆松手是明确的收手信号，不该等于「交回自动追怪」。
        if (Time.unscaledTime - _releasedAt < 1.2f) return false;

        ApplyDirection(map, Functions.DirectionFromPoint(user.CurrentLocation, target.CurrentLocation));
        return true;
    }

    /// <summary>
    /// 拾取键点了之后「一路走到掉落物那格，站上去就捡」。
    /// 返回 true = 这一帧的移动被它接管。
    ///
    /// 2026-10-30 为什么要有这个：原来 DoPickUp 只推一帧方向就撒手，
    /// 而 P2VirtualPad 每帧重新计算 —— 推杆状态一断，MapButtons 立刻被清，
    /// 人只走一格。实测日志：点拾取 → `走向 金币 (4) @(641,604)` → 角色停在 (641,606) 不动。
    /// </summary>
    static bool TryWalkToPick(MapControl map)
    {
        if (!_pickHasTarget) return false;

        var user = MapObject.User;
        if (user == null) { _pickHasTarget = false; return false; }

        if (Time.unscaledTime > _pickDeadline)
        {
            _pickHasTarget = false;
            ShowToast("走不过去，放弃");
            if (Log != null) Log("拾取键 → 超时放弃 " + _pickName);
            return false;
        }

        // 目标格不在了（被别人捡走）就收工。找不到任何掉落物就说明真没了。
        int x, y; uint id; string name;
        if (!Client.MirCore.Bridge.FindNearestItem(12, out x, out y, out id, out name))
        {
            _pickHasTarget = false;
            return false;
        }

        // 走到同一格 → 直接捡
        if (user.CurrentLocation.X == _pickX && user.CurrentLocation.Y == _pickY)
        {
            string atFeet = Client.MirCore.Bridge.ItemUnderFeet();
            // ★ 同一格可能站着别的东西；再核一次脚下到底是不是掉落物
            if (!string.IsNullOrEmpty(atFeet))
            {
                Client.MirCore.Bridge.PickUp();
                ShowToast("捡起 " + atFeet);
                if (Log != null) Log("拾取键 → 走到 (" + _pickX + "," + _pickY + ")，已发包捡 " + atFeet);
            }
            _pickHasTarget = false;
            return false;
        }

        ApplyDirection(map, Functions.DirectionFromPoint(user.CurrentLocation, new System.Drawing.Point(_pickX, _pickY)));
        return true;
    }

    // ────────────────────────────────────────────────────────────
    //  背包键 / 装备键
    //
    //  原版电脑上靠 MainDialog 的两个小按钮或 F9/F10 开背包和角色页。
    //  手机上这两样全没有 —— 于是玩家看不到背包、更穿不上装备，
    //  穿不上装备就一直打不动怪。这两个键补的就是这个断点。
    // ────────────────────────────────────────────────────────────

    /// <summary>背包键：开/关背包面板。</summary>
    static void DoToggleBag()
    {
        try
        {
            bool on = Client.MirCore.Bridge.ToggleInventory();
            ShowToast(on ? "背包 开" : "背包 关");
            if (Log != null) Log("背包键 → " + (on ? "开" : "关"));
        }
        catch (System.Exception ex)
        {
            if (Log != null) Log("背包键异常 " + ex.Message);
        }
    }

    /// <summary>
    /// 技能键：开/关角色页里的技能页（原版 F11）。
    /// 技能页里能看到当前会的技能、等级、熟练度；技能栏（底栏上方那条）则是出招的地方。
    /// </summary>
    static void DoToggleSkill()
    {
        try
        {
            bool on = Client.MirCore.Bridge.ToggleSkillPage();
            int n = 0;
            try { n = GameScene.User == null ? 0 : GameScene.User.Magics.Count; } catch { }
            ShowToast(on ? ("技能 开（" + n + " 个）") : "技能 关");
            if (Log != null)
            {
                string bar0 = "", bar1 = "";
                try { bar0 = Client.MirCore.Bridge.SkillBarIcons(0); bar1 = Client.MirCore.Bridge.SkillBarIcons(1); } catch { }
                Log("技能键 → " + (on ? "开" : "关") + "  已学=" + n + " ｜ Bar1[" + bar0 + "] Bar2[" + bar1 + "]");
                var sb = new System.Text.StringBuilder();
                try
                {
                    foreach (var m in GameScene.User.Magics)
                        sb.Append(m.Spell).Append("(键").Append(m.Key).Append(") ");
                }
                catch { }
                if (sb.Length > 0) Log("技能键 → 清单 " + sb);
            }
        }
        catch (System.Exception ex)
        {
            if (Log != null) Log("技能键异常 " + ex.Message);
        }
    }

    /// <summary>
    /// 装备键：开/关**装备面板**（＝PC 版 F10 那个窗口）。
    ///
    /// ★ 2026-11-07 改（用户投诉「装备那一栏点开什么都没有，那个功能应该是 F10」）：
    ///   旧实现是「一键把背包里能穿的都穿上」（Bridge.EquipAllFromInventory），
    ///   面板根本不打开 —— 玩家按了只看到一句 toast，看不到人形和 13 个装备格，
    ///   自然判定为「点开什么都没有」。
    ///   现在走 GameScene 里 F10 的原始分支（GameScene.cs:583-591 KeybindOptions.Equipment）：
    ///   没开或不在装备页 → Show + ShowCharacterPage；开着且在装备页 → 关。
    ///   穿衣改成面板内正常交互（点背包格 → 点装备格），不再需要这个一键捷径。
    /// </summary>
    static void DoEquipAll()
    {
        try
        {
            bool on = Client.MirCore.Bridge.ToggleCharacter();
            ShowToast(on ? "装备 开" : "装备 关");
            if (Log != null)
            {
                string eq = "";
                try { eq = Client.MirCore.Bridge.EquippedIds(); } catch { }
                Log("装备键 → " + (on ? "开" : "关") + "  身上[" + eq + "]");
            }
        }
        catch (System.Exception ex)
        {
            if (Log != null) Log("装备键异常 " + ex.Message);
        }
    }

    // ────────────────────────────────────────────────────────────
    //  对话键：找最近的 NPC → 走过去 → 贴身了发 C.CallNPC
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 按下对话键。视野里有 NPC 就开始走过去，贴身之后才喊话
    /// （原版 GameScene.cs:11528 也是贴身才发 `C.CallNPC`，服务端 CheckNPC 会校验距离）。
    /// </summary>
    // ── 视野内看不见 NPC 时的兜底：本图已知的常驻 NPC 坐标 ──
    // ★ 为什么要这张表（2026-10-31 实测）：
    //   Bridge.FindNearestNpc 只遍历 MapControl.ObjectsList，而服务端只下发**视野内**的对象。
    //   角色一出生在银杏新人村 (644,601)，传送小姐固定在比奇城 (329,266)，相距约 470 格，
    //   永远不会出现在视野里 → 没有这张表，在出生点按「对话键」永远只有「附近没有 NPC」，
    //   也就永远回不了城、换不了地图。坐标取自服务端 NPC 脚本
    //   Build/Server/Release/Envir/NPCs/传送员_比奇省传送员.txt 与实测日志「传送小姐(329,266)」。
    static readonly string[] KnownNpcMap = { "比奇省" };
    static readonly string[] KnownNpcName = { "传送小姐" };
    static readonly int[] KnownNpcX = { 329 };
    static readonly int[] KnownNpcY = { 266 };

    /// <summary>当前地图里已知的常驻 NPC 坐标；没有就返回 false。</summary>
    static bool FindKnownNpc(out string name, out int x, out int y)
    {
        name = null; x = y = 0;
        var mc = Client.MirScenes.GameScene.Scene == null ? null : Client.MirScenes.GameScene.Scene.MapControl;
        string title = mc == null ? null : mc.Title;
        if (string.IsNullOrEmpty(title)) return false;
        for (int i = 0; i < KnownNpcMap.Length; i++)
        {
            if (title.IndexOf(KnownNpcMap[i], System.StringComparison.Ordinal) < 0) continue;
            name = KnownNpcName[i]; x = KnownNpcX[i]; y = KnownNpcY[i];
            return true;
        }
        return false;
    }

    static void DoTalkNpc()
    {
        var user = MapObject.User;
        if (user == null) return;

        int x, y; uint id; string name;
        if (!Client.MirCore.Bridge.FindNearestNpc(16, out x, out y, out id, out name))
        {
            // 视野里没有 → 查本图已知地标，有就直接寻路过去，到了再喊话。
            string kn; int kx, ky;
            if (FindKnownNpc(out kn, out kx, out ky))
            {
                int kd = Functions.MaxDistance(user.CurrentLocation, new System.Drawing.Point(kx, ky));
                if (kd <= 1)
                {
                    // 已经站在它脚下但对象还没进 ObjectsList（极端情况），只能提示
                    ShowToast("到了 " + kn + " 但还没看到人");
                    if (Log != null) Log("对话键 → 已在 " + kn + " 坐标但视野里没有对象");
                    return;
                }
                int steps = Client.MirCore.Bridge.WalkTo(kx, ky);
                _npcHasTarget = true; _npcX = kx; _npcY = ky; _npcId = 0; _npcName = kn;
                _npcDeadline = Time.unscaledTime + 20f;
                _npcLastX = -1; _npcLastY = -1;
                _npcRepathT = 0f;
                ShowToast("去 " + kn + "（" + steps + " 步）");
                if (Log != null) Log("对话键 → 视野无 NPC，寻路去已知地标 " + kn + " 距离=" + kd + " 路径=" + steps + " 步 @(" + kx + "," + ky + ")");
                return;
            }
            ShowToast("附近没有 NPC");
            if (Log != null) Log("对话键 → 视野内没有 NPC");
            return;
        }

        int d = Functions.MaxDistance(user.CurrentLocation, new System.Drawing.Point(x, y));
        if (d <= 1)
        {
            // 已经贴身，直接喊话
            Client.MirCore.Bridge.CallNpc(id, "[@Main]");
            ShowToast("和 " + name + " 说话");
            if (Log != null) Log("对话键 → 直接对话 " + name + " @(" + x + "," + y + ")");
            return;
        }

        _npcHasTarget = true;
        _npcX = x; _npcY = y; _npcId = id; _npcName = name;
        _npcDeadline = Time.unscaledTime + 8f;
        _npcLastX = -1; _npcLastY = -1;   // 让第一帧必定续一次命
        _npcRepathT = 0f;

        // ★ 只要能寻路就寻路，别「近就硬冲」（2026-10-31 第二次实测改）。
        //   原来门槛是 d > 3，结果 d=2 的屠夫走「逐格推进」：方向是 DownRight，
        //   中间那格 Back=0x20000387（bit29 阻挡）⇒ 一步都迈不出去，
        //   日志 `对话键 → 走向 屠夫 距离=2 @(649,591)` → 8 秒后 `卡住放弃`。
        //   逐格推进是一条直线，不绕墙；只要 A* 能给出路径就必须用 A*。
        if (d > 1)
        {
            int steps = Client.MirCore.Bridge.WalkTo(x, y);
            if (steps > 0)
            {
                ShowToast("寻路 " + name + " " + steps + " 步");
                if (Log != null) Log("对话键 → WalkTo " + name + " 距离=" + d + " 路径=" + steps + " 步 @(" + x + "," + y + ")");
                return;
            }
            if (Log != null) Log("对话键 → WalkTo 无路可走，退回逐格推进 " + name + " 距离=" + d);
        }

        ShowToast("走向 " + name);
        if (Log != null) Log("对话键 → 走向 " + name + " 距离=" + d + " @(" + x + "," + y + ")");
    }

    /// <summary>
    /// 对话目标还在路上 → 朝它走一格。走到贴身就停下发 C.CallNPC。
    /// 返回 true = 这一帧的移动被它接管（和 TryWalkToPick 同一套路）。
    /// </summary>
    static bool TryWalkToNpc(MapControl map)
    {
        if (!_npcHasTarget) return false;

        var user = MapObject.User;
        if (user == null) { _npcHasTarget = false; return false; }

        // ★ 超时必须是「卡住超时」，不是「总耗时超时」（2026-10-31 实测）。
        //   模拟器上只有 1.2 FPS，一格要走 4 秒，12 格的直线距离得 50 秒 ——
        //   固定 12 秒的死线会把正常走路判成失败（实测走到 (315,291) 就被掐断）。
        //   改成：只要还在往前挪，就续命；连续 8 秒原地不动才算卡住。
        var here = user.CurrentLocation;
        if (here.X != _npcLastX || here.Y != _npcLastY)
        {
            _npcLastX = here.X; _npcLastY = here.Y;
            _npcDeadline = Time.unscaledTime + 8f;
        }

        if (Time.unscaledTime > _npcDeadline)
        {
            Client.MirCore.Bridge.StopWalk();
            _npcHasTarget = false;
            ShowToast("走不过去，放弃");
            if (Log != null) Log("对话键 → 卡住放弃 " + _npcName + " 停在(" + here.X + "," + here.Y + ")");
            return false;
        }

        int d = Functions.MaxDistance(user.CurrentLocation, new System.Drawing.Point(_npcX, _npcY));
        if (d <= 1)
        {
            Client.MirCore.Bridge.StopWalk();
            _npcHasTarget = false;

            // ★ 已知地标过来的目标没有 ObjectID（对象当时不在视野里）。
            //   走到跟前它才被服务端下发进 ObjectsList，这时才拿得到真 ID，
            //   所以贴身这一刻重新问一次，问到才喊话 —— 用 0 去 CallNPC 服务端会当垃圾包丢掉。
            uint id = _npcId;
            if (id == 0)
            {
                int nx, ny; uint nid; string nname;
                if (Client.MirCore.Bridge.FindNearestNpc(4, out nx, out ny, out nid, out nname))
                {
                    id = nid;
                    if (!string.IsNullOrEmpty(nname)) _npcName = nname;
                }
            }
            if (id == 0)
            {
                ShowToast("到了 " + _npcName + " 跟前，但还没看到人");
                if (Log != null) Log("对话键 → 走到 " + _npcName + " 坐标，视野里仍无对象，未发包");
                return false;
            }
            Client.MirCore.Bridge.CallNpc(id, "[@Main]");
            ShowToast("和 " + _npcName + " 说话");
            if (Log != null) Log("对话键 → 走到贴身，已发 CallNPC " + _npcName + " id=" + id);
            return false;
        }

        // ★ AutoPath 已接管就不用再逐格推方向盘（2026-10-31）。
        //   原版 MapControl.CheckInput 每次 Process 都会读 CurrentPath 自己走，
        //   我们再 ApplyDirection 会把它拽回摇杆方向，等于互相打架。
        if (Client.MirCore.Bridge.WalkRemaining() > 0)
        {
            return true;
        }

        // ★ 路径走完/被 AutoPath 丢掉，但人还没贴身 —— 重新寻一次路（2026-10-31 第二次实测加）。
        //   原来这里直接退回「逐格推方向盘」，而方向盘走的是直线：目标与自己之间隔一堵墙就
        //   永远迈不出去，只能等 8 秒卡住超时。实测就是这样卡死在 (648,589)，
        //   对话框里的「沃玛森林」一次都没点到。
        //   现在每 0.5 秒重算一次 A*；真的无路可走（返回 0）才退回逐格推。
        if (Time.unscaledTime >= _npcRepathT)
        {
            _npcRepathT = Time.unscaledTime + 0.5f;
            int re = Client.MirCore.Bridge.WalkTo(_npcX, _npcY);
            if (re > 0)
            {
                if (Log != null) Log("对话键 → 重新寻路 " + _npcName + " 距离=" + d + " 路径=" + re + " 步");
                return true;
            }
            if (Log != null) Log("对话键 → 无路可走 " + _npcName + " 距离=" + d + " 目标=(" + _npcX + "," + _npcY + ")");
        }

        // 真无路可走才逐格推（至少试一下相邻格）
        ApplyDirection(map, Functions.DirectionFromPoint(user.CurrentLocation, new System.Drawing.Point(_npcX, _npcY)));
        return true;
    }

    /// <summary>
    /// 让 MapControl.MouseLocation 正好指向「角色当前格 + 这个方向」，
    /// 于是 MapLocation == CurrentLocation + delta（非零，不会触发拾取），
    /// MouseDirection() 也必然返回同一个方向（delta 在 ±1 内，走 InRange 的精确分支）。
    /// </summary>
    static void SetPointerToDirection(MapControl map, MirDirection dir)
    {
        Vector2Int d = DirToDelta(dir);
        MapControl.MouseLocation = new System.Drawing.Point(
            (d.x + MapControl.OffSetX) * MapControl.CellWidth + MapControl.CellWidth / 2,
            (d.y + MapControl.OffSetY) * MapControl.CellHeight + MapControl.CellHeight / 2);

        // 摇杆指着地面，就别让鼠标悬停在 NPC / 别的玩家 / AI70 的怪上，
        // 否则 CheckInput 的 Left 分支会因为这些类型直接 break，人就不走了。
        MapObject.MouseObjectID = 0;
    }

    /// <summary>MirDirection 转格偏移（逻辑坐标：X 右为正、Y 下为正）。</summary>
    static Vector2Int DirToDelta(MirDirection dir)
    {
        switch (dir)
        {
            case MirDirection.Up: return new Vector2Int(0, -1);
            case MirDirection.UpRight: return new Vector2Int(1, -1);
            case MirDirection.Right: return new Vector2Int(1, 0);
            case MirDirection.DownRight: return new Vector2Int(1, 1);
            case MirDirection.Down: return new Vector2Int(0, 1);
            case MirDirection.DownLeft: return new Vector2Int(-1, 1);
            case MirDirection.Left: return new Vector2Int(-1, 0);
            default: return new Vector2Int(-1, -1);
        }
    }

    /// <summary>
    /// 手指位移 → 8 方向。做法是把位移投到 8 个单位向量上取点积最大的那个，
    /// 比按角度分段少一次 atan2，也不会在扇区边界上抖。
    /// GUI 坐标 Y 向下，正好和逻辑坐标一致，不需要翻。
    /// </summary>
    static MirDirection DirFrom(Vector2 d)
    {
        if (d.sqrMagnitude < 0.0001f) return MirDirection.Down;
        d.Normalize();
        float best = -2f;
        MirDirection bd = MirDirection.Down;
        for (int i = 0; i < 8; i++)
        {
            var dir = (MirDirection)i;
            var v = DirToDelta(dir);
            var n = new Vector2(v.x, v.y).normalized;
            float dot = Vector2.Dot(d, n);
            if (dot > best) { best = dot; bd = dir; }
        }
        return bd;
    }

    static Vector2 ToGui(Vector3 screenPos)
    {
        // Unity 触摸坐标是左下原点、Y 向上；GUI 是左上原点、Y 向下。
        return new Vector2(screenPos.x, Screen.height - screenPos.y);
    }

    // ────────────────────────────────────────────────────────────
    //  攻击键：锁定最近的怪
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 拾取键：脚下有东西就捡；没有就自动走向最近的掉落物再捡。
    /// 手机上不可能让玩家精确点到地上那个小格子，所以整条链路都替他想好。
    /// </summary>
    static void DoPickUp()
    {
        try
        {
            string atFeet = Client.MirCore.Bridge.ItemUnderFeet();
            if (!string.IsNullOrEmpty(atFeet))
            {
                Client.MirCore.Bridge.PickUp();
                ShowToast("捡起 " + atFeet);
                if (Log != null) Log("拾取键 → 脚下有 " + atFeet + "，已发包");
                return;
            }

            int x, y; uint id; string name;
            if (Client.MirCore.Bridge.FindNearestItem(12, out x, out y, out id, out name))
            {
                // 不在脚下：记住这一格，交给 TryWalkToPick 每帧走过去（原来只推一帧，人走一格就停）
                MapObject.TargetObjectID = 0;
                _pickHasTarget = true;
                _pickX = x; _pickY = y; _pickName = name;
                _pickDeadline = Time.unscaledTime + 20f;   // 20 秒走不到就放弃
                ShowToast("走向 " + name);
                if (Log != null) Log("拾取键 → 附近有 " + name + " @(" + x + "," + y + ")，自动走过去");
            }
            else
            {
                _pickHasTarget = false;
                ShowToast("附近没有掉落物");
                if (Log != null) Log("拾取键 → 12 格内没有掉落物");
            }
        }
        catch (System.Exception ex)
        {
            if (Log != null) Log("拾取键异常 " + ex.Message);
        }
    }

    /// <summary>
    /// 喝药键：优先喝红的，没红喝蓝的（原版快捷栏 D1~D6 = 腰带 6 格）。
    ///
    /// ★ 2026-11-02 加「自动上药」：手机玩家拿红药以后**没有任何办法把它放到快捷栏**
    ///   （原版是鼠标拖拽，触屏映射不了），所以腰带永远空着、喝药键永远提示"没药"。
    ///   这里先看腰带是不是全空，全空就先从背包把药水/卷轴搬进去，再喝。
    ///   搬的是原版正规 MoveItem 协议（MirItemCell.cs:574），服务端把它当正常拖拽处理。
    /// </summary>
    static void DoDrinkPot()
    {
        try
        {
            // ① 腰带空了先自动上药
            string names = "";
            try { names = Client.MirCore.Bridge.BeltNames(); } catch { }
            bool beltEmpty = true;
            if (!string.IsNullOrEmpty(names))
                foreach (var s in names.Split(';')) if (!string.IsNullOrEmpty(s)) { beltEmpty = false; break; }

            if (beltEmpty)
            {
                string diag = "";
                int put = Client.MirCore.Bridge.AutoFillBelt(out diag);
                if (Log != null) Log("喝药键 → 腰带空，自动上药 " + put + " 件 ｜ " + diag);
                if (put > 0) System.Threading.Thread.Sleep(180);   // 等服务端把物品挪完
            }

            // ② 按原版 D1..D6 顺序找能喝的
            for (int i = 0; i < 6; i++)
            {
                if (!Client.MirCore.Bridge.UseBeltSlot(i)) continue;
                ShowToast("使用快捷栏 " + (i + 1));
                if (Log != null) Log("喝药键 → 快捷栏 " + (i + 1) + "（腰带=" + Client.MirCore.Bridge.BeltNames() + "）");
                return;
            }
            ShowToast("快捷栏里没药");
            if (Log != null) Log("喝药键 → 腰带 1~6 全空 ｜ 腰带内容=" + Client.MirCore.Bridge.BeltNames());
        }
        catch (System.Exception ex)
        {
            if (Log != null) Log("喝药键异常 " + ex.Message);
        }
    }

    static void ShowToast(string msg)
    {
        _toast = msg;
        _toastT = 1.6f;
        if (Log != null) Log("提示 " + msg);
    }

    /// <summary>
    /// 记下「玩家最后一次把摇杆推到了哪个方向」。
    /// 攻击键抬手时摇杆常常已经松了，所以方向要留 0.6 秒的记忆，
    /// 否则「推右前 → 松手 → 点攻击」永远只锁到背后那只。
    /// </summary>
    static void NoteDirection(MirDirection d)
    {
        // 玩家把摇杆推到了**另一个**方向 ⇒ 之前粘着的扇区作废，
        // 否则「先锁正下、再推右上、松手点攻击」还会去打正下那批，方向感就乱了。
        if (_lockDirValid && d != _lockDir) _lockDirValid = false;
        _lastDir = d;
        _dirSeenT = Time.unscaledTime;
    }

    /// <summary>
    /// 攻击键短按。
    ///   · 已经锁着目标 → 保持不动（原版 CheckInput 自己会贴身挥刀，远处的由
    ///     TryChaseTarget 朝它走）—— **连按多少次都不变**
    ///   · 什么都没锁   → 锁最近的一只活怪
    ///   · 取消锁定     → 点空地 或 再点同一只怪（不挂在攻击键上）
    ///
    /// ★ 方向完全不参与选怪。真正的选怪入口只有一个：手指点屏幕上的那只怪
    ///   （TryTapSelect）。这是原版传奇的唯一方式（GameScene.cs:11926 OnMouseDown），
    ///   也是唯一不会让玩家「不知道自己在打谁」的方式。
    /// </summary>
    static void ToggleAttackLock()
    {
        var user = MapObject.User;
        if (user == null) return;

        // ★ 2026-11-15 重排优先级（用户投诉「攻击它会选择什么，某一个方向的其中一个」）。
        //
        //   旧优先级是：1) 摇杆推着方向 2) _lockDirValid 粘着 3) 松手 0.6 秒内 4) 全局最近。
        //   问题出在 2) 和 3)：只要刚才推过一次杆，之后**每一按**都还在那个扇区里挑，
        //   而且挑的是「扇区里最近的那只」—— 玩家完全不知道自己会打到谁。
        //
        //   ★ 2026-11-15 第二次修订：彻底删掉「按摇杆方向挑怪」。
        //
        //   上一版保留了一条「摇杆此刻正推着方向 → 打那个扇区里最近的一只」，
        //   理由写的是「移动中打身前，ARPG 常规手感」。**这个理由是错的**：
        //   传奇不是 ARPG，是站桩锁定输出。原版端游（GameScene.cs:11926 OnMouseDown）
        //   从头到尾只有一条选怪路径 —— 鼠标点到谁就打谁，键盘方向键只负责走路。
        //   把方向掺进选怪，玩家推着杆按攻击，就变成「系统在我朝的方向里自己挑一只」，
        //   正是用户投诉的「攻击它会选择什么，某一个方向的其中一个」。
        //
        //   现在的语义只有一条，和原版一致：**点哪只打哪只**（TryTapSelect，
        //   挂在手指落下/抬起里）。攻击键只做两件事：
        //     1) 已经锁着目标 → 不换（要让原版 CheckInput 自己挥刀/追击）
        //     2) 什么都没锁   → 锁最近的一只（给个合理的默认，不用先点）
        //   取消锁定走「点空地 / 再点同一只怪」。方向完全不参与选怪。
        var cur = MapObject.TargetObject;
        if (cur != null && !cur.Dead)
        {
            // ★ 2026-11-15 第三次修订：**攻击键永不取消锁定**。
            //
            //   上一版这里还是「再按一次 = 取消」（从老的开关语义继承下来的）。
            //   玩家打怪的真实动作是**连按攻击键**，那样第二下就把刚锁上的目标清掉、
            //   第三下再锁回来 —— 屏幕上的选中框和角色动作一起闪，看起来就是
            //   「攻击键坏了 / 它自己乱选」。
            //
            //   取消锁定本来就有两个符合原版的入口，不需要挂在攻击键上：
            //     · 点空地 → TargetObjectID = 0（GameScene.cs:12017-12018 原版 else 分支）
            //     · 再点同一只怪 → 取消（TryTapSelect）
            //   所以这里只做一件事：已经锁着活的，就不动它，交给原版 CheckInput
            //   自己贴身挥刀、远处的交给 TryChaseTarget 走过去。
            if (Log != null) Log("攻击键 → 保持目标 " + cur.Name);
            return;
        }

        _tapLocked = false;

        MapObject best = null;
        int bestD = int.MaxValue;
        var list = MapControl.ObjectsList;
        for (int i = 0; i < list.Count; i++)
        {
            var ob = list[i];
            if (ob == null || ob == user) continue;
            // 统一判定：守卫 / 宠物 / 不可选怪 一律不锁（见 IsHostileMonster）
            if (!IsHostileMonster(ob)) continue;

            int dist = Functions.MaxDistance(user.CurrentLocation, ob.CurrentLocation);
            if (dist < bestD) { bestD = dist; best = ob; }
        }
        if (best != null)
        {
            LockOn(best, bestD, "最近");
        }
        else if (Log != null)
        {
            Log("攻击键 → 附近没有可打的怪（视野内 Race==Monster 数量=0）");
        }
    }

    /// <summary>
    /// 方向锥形锁定：在「摇杆指向 ±45°」这个扇区里挑怪，由近到远。
    /// 已经锁着这个扇区里的一只时，再按一次换下一只（循环），
    /// 所以「右前方三只，我要挑里面那只」也不用点屏幕。
    /// 扇区里一只都没有 ⇒ 清掉锁定并提示，免得人物回头去打背后那只。
    /// </summary>
    static void ToggleAttackLockInDirection(MapObject user, MirDirection dir)
    {
        var buf = _coneBuf;
        buf.Clear();

        var list = MapControl.ObjectsList;
        for (int i = 0; i < list.Count; i++)
        {
            var ob = list[i];
            if (ob == null || ob == user) continue;
            if (!IsHostileMonster(ob)) continue;   // 守卫 / 宠物 / 不可选怪
            // ★ 扇区判定必须用**真实角度**，不能用 Functions.DirectionFromPoint。
            //   实测坑：DirectionFromPoint（Shared/Functions/Functions.cs:180）只看符号，
            //   只要 dx>0 且 dy>0 就返回 DownRight —— 一条 1 格右、10 格下的怪也被算成
            //   「右下」，于是右下这个类实际覆盖了整个 90° 象限；再叠加 ±45° 容差，
            //   扇形张开到 180°，「打右前方那只」会先把正下方几只全算进来。
            //   这里改成 atan2 分 8 向：每类正好 45°，±45° 容差 ⇒ 真正的 90° 扇面。
            MirDirection od = DirectionFromVector(
                ob.CurrentLocation.X - user.CurrentLocation.X,
                ob.CurrentLocation.Y - user.CurrentLocation.Y);
            if (AngleSteps(dir, od) > 1) continue;
            buf.Add(ob);
        }

        if (buf.Count == 0)
        {
            MapObject.TargetObjectID = 0;
            _lockDirValid = false;      // 这个扇区空了，别再粘着它
            string ds = DirName(dir);
            ShowToast(ds + "没有可打的怪");
            if (Log != null) Log("攻击键 → 方向 " + ds + " 扇形内没有怪，已清空锁定");
            return;
        }

        buf.Sort(DistanceCompare);

        // 已经在打这个扇区里的某一只 ⇒ 换下一只（到末尾回第一只）
        int start = 0;
        var cur = MapObject.TargetObject;
        if (cur != null && !cur.Dead)
        {
            int idx = buf.IndexOf(cur);
            if (idx >= 0 && buf.Count > 1) start = (idx + 1) % buf.Count;
        }

        // 粘住方向：下一次点攻击键，只要玩家没换方向，仍按这个扇区轮换
        _lockDir = dir;
        _lockDirValid = true;

        var pick = buf[start];
        int d = Functions.MaxDistance(user.CurrentLocation, pick.CurrentLocation);
        LockOn(pick, d, DirName(dir) + " 第" + (start + 1) + "/" + buf.Count + "只");
    }

    /// <summary>锁一只怪，顺带把法术目标也指过去（法师/道士的火符类技能靠它选目标）。</summary>
    static void LockOn(MapObject ob, int dist, string why)
    {
        // 扇面/最近锁上来了 ⇒ 之前那个「点选」标记作废，否则攻击键会一直护着旧目标
        _tapLocked = false;
        _tapLockedId = 0;
        MapObject.TargetObjectID = ob.ObjectID;
        MapObject.MagicObjectID = ob.ObjectID;
        if (Log != null)
            Log("攻击键 → 锁定 " + ob.Name + "（" + why + "）距离=" + dist
                + " 位置=(" + ob.CurrentLocation.X + "," + ob.CurrentLocation.Y + ")");
    }

    // ────────────────────────────────────────────────────────────
    //  换目标键（2026-11-16 新增）
    // ────────────────────────────────────────────────────────────
    //
    // 语义（和 Mir4 的 Tab 模式一致，见 <本机>/tmp\mob_ctrl_research.txt 第 25 行）：
    //   · 附近一只都没锁 → 锁最近的一只
    //   · 已锁着        → 按「由近到远」的顺序换到**下一只**，到底了回到第一只
    //   · 附近没活怪    → 清掉锁定 + 提示
    //
    // 为什么不用「屏幕上最近的那只」而用**全局由近到远**排序：
    //   用户要的场景是「打远处的怪」。远处的怪常常在屏幕外/被 UI 压住，
    //   手指根本点不到，而列表是按距离排的，连按几下就能一路点到远处那只。
    //   同时距离排序也让「换目标」的行为可预测：每按一下就往后挪一格。
    //
    // 范围限制：只认视野内 MapControl.ObjectsList 里的怪（服务端下发了才存在），
    // 这和点选、攻击键两条路完全一致，不会出现「换到一只看不见的怪」。

    /// <summary>
    /// 换目标键：在附近活怪之间循环切换（由近到远，跳过守卫）。
    /// 返回 true = 换成功（或首次锁定），false = 附近没有可打的怪。
    /// 命令行 `next` 也走这里，验收路径与手指按键完全相同。
    /// </summary>
    public static bool CycleTarget()
    {
        var user = MapObject.User;
        if (user == null)
        {
            ShowToast("还没进游戏");
            return false;
        }

        var buf = _coneBuf;
        buf.Clear();
        var list = MapControl.ObjectsList;
        for (int i = 0; i < list.Count; i++)
        {
            var ob = list[i];
            if (ob == null || ob == user) continue;
            if (!IsHostileMonster(ob)) continue;   // 守卫 / 宠物 / 不可选怪
            buf.Add(ob);
        }

        if (buf.Count == 0)
        {
            MapObject.TargetObjectID = 0;
            MapObject.MagicObjectID = 0;
            _tapLocked = false;
            _tapLockedId = 0;
            _lockDirValid = false;
            if (_castPendingKey != 0) ClearPendingCast();
            ShowToast("附近没有可打的怪");
            if (Log != null) Log("换目标 → 视野内 Race==Monster 且非守卫 的活怪数量=0，已清空锁定");
            return false;
        }

        buf.Sort(CycleCompare);   // 由近到远；同距离按 ObjectID 定序，保证连按一定往后走

        // 当前锁着的那只如果在表里 ⇒ 取下一只；否则从第一只（最近）开始
        int start = 0;
        var cur = MapObject.TargetObject;
        if (cur != null && !cur.Dead)
        {
            int idx = buf.IndexOf(cur);
            if (idx >= 0) start = (idx + 1) % buf.Count;
        }

        var pick = buf[start];
        int d = Functions.MaxDistance(user.CurrentLocation, pick.CurrentLocation);
        LockOn(pick, d, "换目标 " + (start + 1) + "/" + buf.Count);

        // 换了目标 ⇒ 之前挂着「走过去再放」的那个技能作废。
        //   不作废的话：先点雷电术（目标在 13 格外 → 开始走），再按换目标，
        //   人还在朝旧那只走，新锁的目标反而没人管 —— 手感上就是按键失灵。
        if (_castPendingKey != 0) ClearPendingCast();

        ShowToast("目标 " + pick.Name + " " + d + "格 (" + (start + 1) + "/" + buf.Count + ")");
        return true;
    }

    // ────────────────────────────────────────────────────────────
    //  点怪即锁（主路）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 手指点在地图区域时先做一次「点到怪了吗」的判定，命中就锁它。
    ///
    /// 返回 true = 这一指已经被点选吃掉（调用方不要再当走路的输入处理）。
    ///
    /// 为什么主路必须是点选（而不是扇面）——
    /// 用户 2026-11-15 原话：「攻击它会选择什么，某一个方向的其中一个」，
    /// 也就是说扇面挑怪挑出来的**不是他想打的那只**，而且他失去了选择权。
    /// 原版端游/热血传奇手机版都是「点哪只打哪只」：
    ///   · GameScene.cs:11926 `OnMouseDown` → 12013 写 TargetObjectID
    ///   · 命中靠 MonsterObject.MouseOver（BodyLibrary.VisiblePixel，像素级）
    /// 这里用 DisplayRectangle ± 容差代替像素级（手指精度远低于鼠标）。
    ///
    /// 取消锁定有两个入口，都跟原版一致：
    ///   · 点空地 → TargetObjectID = 0（调用方 Began 分支里做，原版 12017 的 else）
    ///   · 再点同一只 → 这里清掉（下面 2026 行那一段）
    /// 攻击键**不参与**取消 —— 玩家连按攻击是不会想「把目标丢掉」的。
    /// </summary>
    static bool TryTapSelect(Vector2 guiPoint, int fingerId, string from)
    {
        if (_tapSelFinger == fingerId) return false;   // 这一指判过了
        _tapSelFinger = fingerId;

        var user = MapObject.User;
        if (user == null) return false;

        // GUI 坐标 → 画布坐标（和 P2Core.ToLogic 同一套换算，MapControl.MouseLocation
        // 和 MapObject.DisplayRectangle 都在画布坐标系里）。
        var vp = _viewport;
        if (vp.width <= 0f || vp.height <= 0f) return false;
        int cw = Client.MirCore.Bridge.CanvasWidth;
        int ch = Client.MirCore.Bridge.CanvasHeight;
        float nx = (guiPoint.x - vp.x) / vp.width;
        float ny = (guiPoint.y - vp.y) / vp.height;
        int cx = Mathf.RoundToInt(nx * cw);
        int cy = Mathf.RoundToInt(ny * ch);

        MapObject hit = null;
        int hitD = int.MaxValue;
        var list = MapControl.ObjectsList;
        for (int i = 0; i < list.Count; i++)
        {
            var ob = list[i];
            if (ob == null || ob == user || ob.Dead) continue;
            // 只锁能点中的：怪 + 人。掉落物/NPC 交给原版点击流程（对话、捡东西）。
            // 这里**不套 IsHostileMonster**：原版是「点到谁就选中谁」，选中 ≠ 发招，
            // 发不发招由 CheckInput 按 Shift / 名字括号再判一次（GameScene.cs:12062）。
            // 玩家点自己的宝宝是合法意图（要给它加血/看它状态），不该被这里拦掉。
            if (ob.Race != ObjectType.Monster && ob.Race != ObjectType.Player) continue;
            if (ob.Race == ObjectType.Monster && ob.AI == 64) continue;   // 不可见怪，无从点起

            // 手写边界判断而不是 Rectangle.Inflate/Contains：
            // Assets/Scripts 这一侧只引用 MirClientCore.dll，System.Drawing 的
            // 扩展方法不一定被带过来（InputBridge 能用是因为它编进 dll 内部）。
            //
            // ★ 2026-11-15 改用 SpriteRectangle（= DisplayRectangle + 精灵偏移，
            //   即 FinalDrawLocation 口径）。之前用 DisplayRectangle，它的原点不含
            //   mi.X/mi.Y 偏移，而怪实际画的位置上移了几十像素 —— 玩家点在怪身上，
            //   这个矩形根本没盖住那个点，于是「点哪只打哪只」在真机上永远点不中，
            //   只能退回攻击键锁最近的。这跟红框画偏是同一个根因。
            var r = ob.SpriteRectangle;
            if (r.Width <= 0 || r.Height <= 0) continue;
            if (cx < r.X - TapSelectSlack || cx > r.Right + TapSelectSlack) continue;
            if (cy < r.Y - TapSelectSlack || cy > r.Bottom + TapSelectSlack) continue;

            // 命中多个时取离落点最近的（不是离玩家最近的 —— 玩家点的就是那个位置）
            int d = Math.Abs(cx - (r.X + r.Width / 2)) + Math.Abs(cy - (r.Y + r.Height / 2));
            if (d < hitD) { hitD = d; hit = ob; }
        }

        if (hit == null) return false;   // 没点中 → 交回原来的走路/点地面流程

        // 再点一次同一只 = 取消锁定（和攻击键的 toggle 语义一致，不用另找入口）
        if (MapObject.TargetObject == hit)
        {
            MapObject.TargetObjectID = 0;
            MapObject.MagicObjectID = 0;
            _lockDirValid = false;
            _tapLocked = false;
            _tapLockedId = 0;
            ShowToast("取消锁定");
            if (Log != null) Log("点选(" + from + ") → 再点一次，取消锁定 " + hit.Name);
            return true;
        }

        int dist = Functions.MaxDistance(user.CurrentLocation, hit.CurrentLocation);
        // 点选 = 显式选择，扇面状态必须作废，否则下一按攻击键又按旧扇区跑掉
        _lockDirValid = false;
        MapObject.TargetObjectID = hit.ObjectID;
        MapObject.MagicObjectID = hit.ObjectID;
        _tapLocked = true;
        _tapLockedId = hit.ObjectID;
        ShowToast("锁定 " + hit.Name);
        if (Log != null)
            Log("点选(" + from + ") → 锁定 " + hit.Name
                + "（画布点=(" + cx + "," + cy + ") 框=" + hit.SpriteRectangle
                + " 距离=" + dist + " 格=(" + hit.CurrentLocation.X + "," + hit.CurrentLocation.Y + ")）");
        return true;
    }

    /// <summary>
    /// 换目标用的排序：先比距离，**再比 ObjectID**。
    ///
    /// 为什么必须加第二级（2026-11-16 实机测出来的）：
    ///   只用距离排，同距离的几只（怪挤成一堆时 3~5 只都是「1 格」）顺序由
    ///   List.Sort 的不稳定排序决定，**每次调用都可能不一样**。实机连按三次
    ///   `next` 的日志是 1/19 → 3/18 → 4/18 —— 序号确实在走，但玩家看到的是
    ///   「同一只钉耙猫」来回跳（三只都叫钉耙猫、都在 1 格），看着像按键没反应。
    ///   加上 ObjectID 这个恒定键之后，同距离的相对次序在两次按键之间固定，
    ///   「下一只」就真的换成表里下一个位置。
    /// </summary>
    static int CycleCompare(MapObject a, MapObject b)
    {
        int c = DistanceCompare(a, b);
        if (c != 0) return c;
        return a.ObjectID.CompareTo(b.ObjectID);
    }

    static int DistanceCompare(MapObject a, MapObject b)
    {
        var u = MapObject.User;
        if (u == null) return 0;
        int da = Functions.MaxDistance(u.CurrentLocation, a.CurrentLocation);
        int db = Functions.MaxDistance(u.CurrentLocation, b.CurrentLocation);
        return da.CompareTo(db);
    }

    /// <summary>
    /// 这只怪「能不能当普通怪自动锁上去」—— 三个**自动**选怪入口
    /// （攻击键 ToggleAttackLock / 换怪键 CycleTarget）必须统一走这里，
    /// 不能各写各的。
    ///
    /// 手指**点选**（TryTapSelect）不在这里，因为原版的语义是「点到谁就选中谁」，
    /// 选中 ≠ 发招：发不发招由 CheckInput 按 Shift / 名字括号再判一次
    /// （GameScene.cs:12062-12063）。玩家点自己的宝宝是合法意图（要看状态/加血），
    /// 不该被自动索敌的规则拦掉。
    ///
    /// ★ 2026-11-16 补：原来三处各写一遍 `AI == 6 || 58 || 113`，只看守卫，
    ///   漏掉了**宠物和召唤物**。原版客户端的判据是名字（GameScene.cs:12062-12063）：
    ///       MapObject.TargetObject.Name.EndsWith(")")
    ///   服务端起名就是 `游戏名(主人名)`（Server/MirObjects/MonsterObject.cs:520
    ///   `string.Format("{0}({1})", Info.GameName, Master.Name)`），收尾必带一个右括号。
    ///   原版据此区分「自己的宝宝」和「野怪」：名字带括号的要按住 Shift 才打。
    ///
    ///   我们之前没这条 ⇒ 道士带骷髅、法师带宝宝的时候，攻击键/换怪键
    ///   会把**自己的宝宝**当成最近的怪锁上去，然后原版 CheckInput 里
    ///   `!Name.EndsWith(")") && is MonsterObject` 不成立 ⇒ 不发招，
    ///   表现就是「按了攻击键，人站着不动」——正是 11-15 用户投诉的那类毛病。
    ///   当前角色是战士（没宝宝）所以还没暴露，但道士/法师一旦上手就会踩。
    ///
    /// 另外排除 AI == 64：MonsterObject.cs:18 注释说明这类怪不阻挡、不可见、
    /// 不进格子（原版 Probe/隐身类），GameScene.cs:10809-10812 也是每帧
    /// 主动把当目标清掉 —— 我们没必要先锁上再被清。
    /// </summary>
    static bool IsHostileMonster(MapObject ob)
    {
        if (ob == null || ob.Dead) return false;
        if (ob.Race != ObjectType.Monster) return false;
        if (ob.AI == 64) return false;                                  // 不可见/不可选
        if (ob.AI == 6 || ob.AI == 58 || ob.AI == 113) return false;     // 守卫三兄弟
        if (ob.Name != null && ob.Name.EndsWith(")")) return false;      // 别人的宝宝/自己宝宝
        return true;
    }

    /// <summary>
    /// 由「目标相对玩家的位移」算出 8 向，每一类正好 45°。
    /// 坐标系：X 向右、Y 向下（Mir2 地图/屏幕同向，Down 就是 Y+1）。
    ///
    /// 为什么不用 Functions.DirectionFromPoint（Shared/Functions/Functions.cs:180）：
    /// 那个函数只比较符号 —— 只要 dx>0 且 dy>0 就返回 DownRight，
    /// 于是「右边 10 格、下边 1 格」和「右边 1 格、下边 10 格」是同一类，
    /// 而 Right 这一类只剩下 dy==0 那一条射线。结果就是推「正右」几乎选不到怪，
    /// 推「右下」却把整个右下象限都吞进来。
    /// </summary>
    static MirDirection DirectionFromVector(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return MirDirection.Down;
        // atan2(dy,dx)：Y 向下为正 ⇒ 角度顺时针增大，正好对上 MirDirection 的排列
        // （Right=2 在 0°，DownRight=3 在 45°，Down=4 在 90° …）
        double deg = System.Math.Atan2(dy, dx) * 180.0 / System.Math.PI;
        int k = (int)System.Math.Round(deg / 45.0, System.MidpointRounding.AwayFromZero);
        int idx = ((k + 2) % 8 + 8) % 8;
        return (MirDirection)idx;
    }

    /// <summary>8 向序号的最小夹角步数（0=同向，1=45°，最大 4=180°）。</summary>
    static int AngleSteps(MirDirection a, MirDirection b)
    {
        const int N = 8;
        int d = ((int)a - (int)b + N) % N;
        return d <= N / 2 ? d : N - d;
    }

    static string DirName(MirDirection d)
    {
        switch (d)
        {
            case MirDirection.Up: return "正上";
            case MirDirection.UpRight: return "右前";
            case MirDirection.Right: return "正右";
            case MirDirection.DownRight: return "右下";
            case MirDirection.Down: return "正下";
            case MirDirection.DownLeft: return "左下";
            case MirDirection.Left: return "正左";
            default: return "左上";
        }
    }

    /// <summary>
    /// 攻击键长按 = 强制攻击（PC 端是按住 Shift 打）。
    /// 每帧调一次：到点就置 Client.CMain.Shift 并锁一个「非普通怪」的目标；
    /// 手指离开立刻清回 false。只清自己置的那一位。
    /// </summary>
    static void UpdateAttackHold()
    {
        if (_atkHoldFinger == -2)
        {
            if (_shiftApplied) ClearForceAttack();
            return;
        }

        bool down = false;
        for (int i = 0; i < Input.touchCount; i++)
        {
            var t = Input.GetTouch(i);
            if (t.fingerId != _atkHoldFinger) continue;
            if (t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled) down = true;
            break;
        }
        if (!down)
        {
            _atkHoldFinger = -2;
            _atkHoldT = 0f;
            _atkHoldFired = false;
            if (_shiftApplied) ClearForceAttack();
            return;
        }

        if (_atkHoldFired) return;

        _atkHoldT += Time.unscaledDeltaTime;
        if (_atkHoldT < ForceAtkHoldSec) return;

        _atkHoldFired = true;
        ApplyForceAttack();
    }

    static void ApplyForceAttack()
    {
        try { Client.CMain.Shift = true; _shiftApplied = true; } catch { }

        // ★ 2026-11-16 修「点 A 打 B」（安卓实机复现，不是推测）。
        //
        //   现象：手指点了远处那只「多钩猫」→ 日志 `点选(落下) → 锁定 多钩猫 距离=11`，
        //   战斗栏 `目标=多钩猫 距离=11`；接着按住攻击键 0.8 秒，
        //   日志变成 `攻击键长按 → 强制攻击 Shift=true 目标=稻草人 距离=2` ——
        //   目标被换成了脚边的稻草人，玩家点的那只丢了。
        //
        //   根因就在这个函数：它**无条件**遍历 ObjectsList 重新挑最近的一只并
        //   `MapObject.TargetObjectID = best.ObjectID`。上面那句注释写着
        //   「点过就锁着，长按也不会换人」，代码却每次长按都把玩家选的目标覆盖掉。
        //
        //   语义修正：长按只是「按住 Shift」这一件事（让原版 CheckInput 肯对
        //   宝宝/玩家/守卫发招），**不负责选怪**。选怪的唯一入口是玩家点谁。
        //   只有在什么都没锁的时候，才给个最近的当默认。
        var cur = MapObject.TargetObject;
        if (cur != null && !cur.Dead)
        {
            ShowToast("强制攻击：" + cur.Name);
            if (Log != null)
                Log("攻击键长按 → 强制攻击 Shift=true 保持目标=" + cur.Name
                    + " 类型=" + cur.Race
                    + " 距离=" + Functions.MaxDistance(MapObject.User.CurrentLocation, cur.CurrentLocation));
            return;
        }

        // 强制攻击的目标不只是怪：玩家、红名、守卫都要能打。
        // 这里不按 Race 过滤，只排除掉落物/NPC 这类点不动的。
        var user = MapObject.User;
        MapObject best = null;
        int bestD = int.MaxValue;
        if (user != null)
        {
            var list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
            {
                var ob = list[i];
                if (ob == null || ob == user || ob.Dead) continue;
                if (ob.Race == ObjectType.Item || ob.Race == ObjectType.Merchant || ob.Race == ObjectType.Spell) continue;
                // ★ 2026-11-15：长按也不按方向挑（和短按统一）。理由同 ToggleAttackLock：
                //   方向掺进选怪 = 玩家不知道自己在打谁，正是用户投诉的那句
                //   「攻击它会选择什么，某一个方向的其中一个」。
                int dist = Functions.MaxDistance(user.CurrentLocation, ob.CurrentLocation);
                if (dist < bestD) { bestD = dist; best = ob; }
            }
        }
        if (best != null)
        {
            MapObject.TargetObjectID = best.ObjectID;
            MapObject.MagicObjectID = best.ObjectID;

            // ★ 2026-11-13：光有 Shift 还打不了玩家 —— 服务端还有一道攻击模式门。
            //   Server/MirObjects/PlayerObject.cs:4842 `IsAttackTarget(HumanObject attacker)`
            //   在 `attacker.AMode == AttackMode.Peace` 时直接 return false，
            //   而新角色的 AMode 默认就是 Peace（AttackMode 枚举 Peace=0，Shared/Enums.cs:990）。
            //   手机外壳从来没有切模式的入口（AttackMode/ChangeAMode 在 Assets/Scripts 零命中），
            //   所以「按住 Shift 打玩家」在服务端一定会被拒。
            //   只有当**真的选中了玩家**时才升一级（Peace→Group，Group 下无队伍即人人可打），
            //   不选中玩家就不动它 —— 别无缘无故改玩家的 PK 设置。
            if (best.Race == ObjectType.Player)
            {
                try
                {
                    var sc = Client.MirScenes.GameScene.Scene;
                    if (sc != null && sc.AMode == AttackMode.Peace)
                    {
                        sc.ChangeAttackMode();   // GameScene.cs:950，内部循环 Peace→Group→…
                        ShowToast("和平模式 → 组队模式（这样才能打到人）");
                        if (Log != null) Log("攻击键长按 → 目标是人，攻击模式 Peace→Group");
                    }
                }
                catch (Exception ex)
                {
                    if (Log != null) Log("攻击键长按 → 切攻击模式失败 " + ex.GetType().Name);
                }
            }

            ShowToast("强制攻击：" + best.Name);
            if (Log != null)
                Log("攻击键长按 → 强制攻击 Shift=true 目标=" + best.Name
                    + " 类型=" + best.Race + " 距离=" + bestD);
        }
        else
        {
            ShowToast("强制攻击已开（这个方向没人）");
            if (Log != null) Log("攻击键长按 → 强制攻击 Shift=true，视野内没有可选目标");
        }
    }

    static void ClearForceAttack()
    {
        try { Client.CMain.Shift = false; } catch { }
        _shiftApplied = false;
        if (Log != null) Log("攻击键长按 → 松开，强制攻击关闭");
    }

    // ────────────────────────────────────────────────────────────
    //  画（OnGUI 最上层）
    // ────────────────────────────────────────────────────────────

    public static void Draw()
    {
        LoadLook();
        FitScale(Screen.height);   // 和 Step 必须同一套比例，否则「画在这儿、点在那儿」
        if (_style == null) EnsureStyle();
        // ★ 2026-11-11 0.0195→0.021：理由同 DrawFunc，去掉合成粗体后整体放大一号
        //   （1140 高 ⇒ 24px；NPC 选项键 / 提示条都用这个基准）
        _style.fontSize = Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.021f, 16f, 36f));
        _style.fontStyle = FontStyle.Normal;   // 理由见 EnsureStyle：SimHei 无粗体面，合成加粗=糊
        _style.alignment = TextAnchor.MiddleCenter;
        _style.clipping = TextClipping.Overflow;

        float W = Screen.width, H = Screen.height;
        Vector2 hotCenter, atkCenter, pickCenter, potCenter, npcCenter, bagCenter, eqpCenter, skillCenter;
        GetCenters(W, H, out hotCenter, out atkCenter, out pickCenter, out potCenter,
                   out npcCenter, out bagCenter, out eqpCenter, out skillCenter);

        float txtH = _style.fontSize + 8f;

        // ── 摇杆：离线 3D 渲染的真美术（2026-11-02）──
        // 用户原话：「你那个摇杆，现在他妈的纯贴图，和那个王者荣耀的差距百倍」。
        // 之前的 DrawPad 是逐像素手画的径向渐变 + 一圈高光弧 —— 没有法线、没有环境光、
        // 没有接触阴影，本质上仍是 2D 涂鸦，所以一定「像贴图」。
        // 现在整块换成 <本机>/tmp\proto14.py 离线渲染出来的两张图（P2StickArt.cs 负责合成）：
        //   底盘 = 解析高度场（含真的刻进去的四向凹槽）→ 数值法线 → 半球环境光 + Blinn-Phong + 菲涅尔；
        //   球头 = 真球面法线的琥珀金属球，双高光 + 边缘菲涅尔；
        //   接触阴影按解析函数在运行时算，光在左上 ⇒ 影落右下，球才「坐」在盘上而不是浮着。
        // ★ 底盘恒在 hotCenter，不随手指移动；只动摇杆头（2026-11-01 已定，别退回去）。
        Vector2 c = hotCenter;
        Vector2 knobRaw = _active ? _knob : hotCenter;
        Vector2 knob = _active ? c + (knobRaw - c) * StickVisRatio : hotCenter;
        // ★ 2026-11-26：多传一个 _S —— 摇杆美术以前是固定 480 贴死的，
        //   设置里改按键大小时热区/行程跟着缩，屏幕上的盘面却不缩，手感和眼睛会脱节。
        bool artOk = P2StickArt.Draw(c, knob.x - c.x, knob.y - c.y, _S);

        if (!artOk)
        {
            // 美术缺失时退回手绘版本（只能出现在素材没拷进来的情况下，正常不会走到）
            float a = _active ? PressAlpha : IdleAlpha;
            float baseR = StickBaseRadius;
            DrawPad(c, baseR * (_active ? 1.04f : 1f),
                    0f, a, _active ? a * 0.45f : 0f, 0f, KindRing);
            float kr = StickRadius * StickKnobRatio;
            float knobA = _active ? KnobAlpha + 0.20f : KnobAlpha;
            DrawPad(knob, kr, knobA, knobA * 0.92f, knobA * 0.50f, 0f, KindKnob);
        }

        Color white = new Color(1f, 1f, 1f, 0.92f);

        // ── 功能键：全部改成「图标 + 纯白描边圆」（2026-11-05，对标原神/王者）──
        //   图标名对应 Data/UI/ic_<name>.rgba；素材缺失时自动退回文字标签，
        //   不会出现「按了但屏幕上没东西」。
        //   布局：主列（攻击/喝药/背包/装备/技能）在右黑边靠里，出招列在它左边一列，
        //   拾取键与出招列同列最下方 —— 两列之间留 240px，不挤。

        // ── 攻击键（最大、永远最醒目）──
        // 三态：强制攻击（长按中，红）/ 已锁定（金）/ 待机（白）。
        // 长按读条用外圈亮度表示：按住越久外圈越实，到 0.8 秒就切到红色「强制」。
        bool locked = MapObject.TargetObject != null && !MapObject.TargetObject.Dead;
        bool force = _shiftApplied;
        float ap = Flash01(_atkFlashT);
        float holdK = _atkHoldFinger >= 0 && !_atkHoldFired
            ? Mathf.Clamp01(_atkHoldT / ForceAtkHoldSec) : 0f;
        float aa = (_atkPressed && ap > 0f) ? PressAlpha : (force ? 0.62f : (locked ? 0.52f : IdleAlpha));
        float ringK = force ? 0.85f : (locked ? 0.50f + 0.35f * ap : holdK * 0.45f);
        Color atkCol = force ? new Color(1f, 0.46f, 0.38f, 0.98f)
                    : locked ? new Color(1f, 0.86f, 0.55f, 0.95f) : white;
        string atkLabel = force ? "强制" : (locked ? "攻击中" : "攻击");
        // ★ 2026-11-13：tint 必须传。攻击键有图标 ⇒ DrawFunc 走图标分支、不画文字，
        //   旧的 atkCol 只染文字 ⇒ 金/红两态在屏幕上根本看不见（实机截图像素差 <3%）。
        //   传 tint 后整张贴图按状态着色：白待机 / 金锁定 / 红强制。
        DrawFunc(atkCenter, AtkRadius * (1f - 0.075f * ap),
                 aa * FillRatio, aa * 0.95f, ringK, 0f,
                 "attack", 0.58f, atkLabel, atkCol,
                 false, force || locked ? atkCol : (Color?)null);

        // ── 拾取键（脚下的东西 / 走过去捡）──
        float pp = Flash01(_pickFlashT);
        float pa = pp > 0f ? PressAlpha : IdleAlpha;
        // 拾取键写「拾取」两个字：图形方案三轮评审都输给了「下载」的联想，
        // 见 DrawFunc 的 useText 注释。文字在 r=74 的圆里占 ~62% 直径，够醒目。
        DrawFunc(pickCenter, PickRadius * (1f - 0.075f * pp),
                 pa * FillRatio, pa * 0.95f, pa * 0.30f * pp, 0f, "pick", 0.60f, "拾取", white,
                 true);

        // ── 喝药键 ──
        float dp = Flash01(_potFlashT);
        float da = dp > 0f ? PressAlpha : IdleAlpha;
        DrawFunc(potCenter, PotRadius * (1f - 0.075f * dp),
                 da * FillRatio, da * 0.95f, da * 0.30f * dp, 0f, "pot", 0.60f, "喝药", white);

        // ── 对话键（左黑边，与喝药键左右对称）──
        float np = Flash01(_npcFlashT);
        float na = np > 0f ? PressAlpha : IdleAlpha;
        DrawFunc(npcCenter, NpcRadius * (1f - 0.075f * np),
                 na * FillRatio, na * 0.95f, na * 0.30f * np, 0f, "npc", 0.60f, "对话", white);

        // ── 背包键 / 装备键 / 技能页（右侧黑边，喝药键上方三档）──
        float bp = Flash01(_bagFlashT);
        float ba = bp > 0f ? PressAlpha : IdleAlpha;
        DrawFunc(bagCenter, BagRadius * (1f - 0.075f * bp),
                 ba * FillRatio, ba * 0.95f, ba * 0.30f * bp, 0f, "bag", 0.60f, "背包", white);

        float ep = Flash01(_eqpFlashT);
        float ea = ep > 0f ? PressAlpha : IdleAlpha;
        // 装备键写「装备」：盾牌被读成医疗包/定位标，见 DrawFunc 的 useText 注释。
        DrawFunc(eqpCenter, EqpRadius * (1f - 0.075f * ep),
                 ea * FillRatio, ea * 0.95f, ea * 0.30f * ep, 0f, "eqp", 0.60f, "装备", white,
                 true);

        float sp = Flash01(_skillFlashT);
        float sa = sp > 0f ? PressAlpha : IdleAlpha;
        // 技能页写「技能」：四芒星和摊开书都被评审判为不可辨，见 DrawFunc 的 useText 注释。
        DrawFunc(skillCenter, SkillRadius * (1f - 0.075f * sp),
                 sa * FillRatio, sa * 0.95f, sa * 0.30f * sp, 0f, "skill", 0.60f, "技能", white,
                 true);

        // ── 换目标键（右栏两列之间的空档，r=62）──
        //   写「换怪」两个字：全排按键都是 2 字，3 字在圆里会顶出边。
        //   有活目标时整张贴图染暖金色 —— 和攻击键「已锁定」同一套语汇，
        //   玩家一眼就能看出「现在锁着东西」，不必等提示条。
        Vector2 nextC = NextCenter(W, H);
        var tgtNow = MapObject.TargetObject;
        bool hasTgt = tgtNow != null && !tgtNow.Dead;
        float nfp = Flash01(_nextFlashT);
        float nfa = nfp > 0f ? PressAlpha : (hasTgt ? 0.56f : IdleAlpha);
        Color nextCol = hasTgt ? new Color(1f, 0.86f, 0.55f, 0.95f) : white;
        DrawFunc(nextC, NextRadius * (1f - 0.075f * nfp),
                 nfa * FillRatio, nfa * 0.95f,
                 hasTgt ? 0.50f + 0.30f * nfp : nfa * 0.30f * nfp, 0f,
                 null, 0.60f, "换怪", nextCol, true,
                 hasTgt ? nextCol : (Color?)null);

        // 目标名条：换怪键**左边**，常驻显示「当前锁的是谁、还有多远」。
        // 用户会直接问「你现在是哪只怪」—— 屏幕上得有一行常驻答案，
        // 不能只靠 1.6 秒就消失的提示条。
        //
        // 为什么放左边而不是上方：上方 y∈[836,900] 只有 64px 高、x 被喝药键左缘(2376)
        // 和出招列右缘(2276)夹成 100px 宽，塞不下「半兽人 13格」。
        // 左边这条带子是真空的 —— 出招列的下边界恒为 CastBotLimitNow(H)=860，
        // 拾取键顶边 924，所以 y∈[866,906] 整条都是空的。
        if (hasTgt && MapObject.User != null)
        {
            int oldSize = _style.fontSize;
            var oldAlign = _style.alignment;
            _style.fontSize = Mathf.RoundToInt(Mathf.Clamp(NextRadius * 0.38f, 16f, 28f));
            float lh = _style.fontSize + 12f;
            int td = Functions.MaxDistance(MapObject.User.CurrentLocation, tgtNow.CurrentLocation);
            _style.alignment = TextAnchor.MiddleRight;
            LabelAt(new Rect(nextC.x - NextRadius - 8f - 210f, nextC.y - lh * 0.5f, 210f, lh),
                    tgtNow.Name + " " + td + "格",
                    new Color(1f, 0.90f, 0.62f, 0.92f));
            _style.alignment = oldAlign;
            _style.fontSize = oldSize;
        }

        // ── 出招键：右黑边左列，竖排 8 个（原版 F1~F8）──
        //
        // 只在**这一格真有技能**时才画：刚出生的小号什么都没有，
        // 画 8 个空圆环反而挡事。NPC 传功学到一个就冒出来一个。
        // 图标用原版 MagIcon 的未按下帧（帧号 = Icon*2）；那一帧没导出来时
        // MakePad 会自动退回白色星星，不会留空圆。
        for (int i = 0; i < CastMax; i++)
        {
            int key = CastKeyOf(i);
            string nm = _castName16[key - 1];
            if (string.IsNullOrEmpty(nm)) continue;

            Vector2 cc = CastCenter(i, W, H);
            float fp = Flash01(_castFlashT[i]);
            bool passive = _castPassive16[key - 1];
            float ca = fp > 0f ? PressAlpha : (passive ? 0.42f : 0.58f);

            // ★ 2026-11-05 第四次实机评审：**出招键彻底不用 MagIcon 了，改写技能名**。
            //   原版 MagIcon 每帧只有 32x30，塞进 r=74（直径 148px）的圆要放大 4.6 倍，
            //   实机原分辨率放大图上两次评审的原话是「极低，非常模糊」「糊成一团」——
            //   玩家根本认不出是哪个技能。这和拾取/装备/技能三个键的理由完全一样
            //   （见 DrawFunc 的注释）：小圆键上这类语义图形本身就不成立。
            //   原版 MagIcon 只在 32x30 的格子里是清楚的，手机上是硬拉大，注定糊。
            //   用户的原始授权就是「上面有图标，**或者说是字体**」，所以这里写字：
            //   技能名（基本剑术 / 攻杀剑术）一眼就懂，缩到多小都认得。
            float cr = CastRadiusNow(H);
            DrawPadIcon(cc, cr * (1f - 0.075f * fp),
                        ca * FillRatio, ca * 0.95f, ca * 0.30f * fp, 0f, KindButton,
                        null, -1, 0.60f);
            DrawCastLabel(cc, cr, nm,
                          passive ? new Color(0.78f, 0.84f, 0.92f, 0.90f) : white);
        }

        // 翻页键（Bar2 存在时才画）：把 9..16 号键位换到这一排
        if (_castBar2Exists)
        {
            Vector2 pc = CastPageCenter(W, H);
            float pf = Flash01(_castPageFlashT);
            float pra = pf > 0f ? PressAlpha : IdleAlpha;
            DrawFunc(pc, CastBarRadius * (1f - 0.075f * pf),
                     pra * FillRatio, pra * 0.95f, pra * 0.30f * pf, 0f,
                     "page", 0.62f, "栏" + (_castPage + 1), new Color(1f, 0.92f, 0.7f, 0.95f));
        }

        // ── NPC 对话选项：左侧黑边 2 列 3 行 + 一个关闭键 ──
        //
        // 为什么非得外壳自己画一遍（2026-10-31 实测）：
        //   原版链接是小黄字 14x16 逻辑像素（设备 21x24），手指点不准；
        //   对话框本身还固定贴在逻辑 (0,0)，手机上左半张被摇杆压着。
        //   这里把文字搬成 3 倍大的圆键，点一下走同一套 ClickNpcButton → C.CallNPC。
        // ★ 选项键上**必须留文字**（"买"/"卖"/"修理"），换成图标就看不出点的是哪个。
        if (_npcDlgOpen)
        {
            for (int k = 0; k < _npcOpts.Length; k++)
            {
                Vector2 cc = NpcOptCenter(k, W);
                bool fl = _npcOptFlashT > 0f && _npcOptFlashIdx == k;
                float oa = fl ? PressAlpha : 0.62f;
                DrawPad(cc, NpcOptRadius * (fl ? 0.94f : 1f),
                        oa * FillRatio, oa * 0.95f, oa * 0.30f, 0f, KindButton);
                LabelAt(new Rect(cc.x - NpcOptRadius, cc.y - txtH * 0.5f, NpcOptRadius * 2, txtH),
                        _npcOpts[k], new Color(1f, 0.95f, 0.6f, 0.95f));
            }

            // 关闭键：× 图标（扁平风里「叉」比「关闭」两个字更直观）
            Vector2 xc = NpcCloseCenter(W);
            DrawFunc(xc, NpcOptRadius, 0.30f * FillRatio, 0.62f, 0f, 0f,
                     "close", 0.60f, "关闭", new Color(1f, 0.72f, 0.62f, 0.95f));
        }

        // ── 提示条：屏幕中上方，1.6 秒后自动消失 ──
        if (_toastT > 0f && !string.IsNullOrEmpty(_toast))
        {
            _style.alignment = TextAnchor.MiddleCenter;
            LabelAt(new Rect(0, H * 0.18f, W, txtH * 1.6f), _toast,
                    new Color(1f, 0.95f, 0.55f, Mathf.Clamp01(_toastT)));
        }
    }

    /// <summary>
    /// 按下回弹进度：刚按下 1，弹完 0（_atkFlashT 等计时器初值 0.12）。
    /// ★ 量化到 5 档 —— 每个不同的值都会生成一张 192x192 贴图（约 147KB），
    ///   不量化的话回弹那 0.12 秒能刷出几十张，白吃显存。
    /// </summary>
    static float Flash01(float t)
    {
        if (t <= 0f) return 0f;
        return Mathf.Round(Mathf.Clamp01(t / 0.12f) * 5f) / 5f;
    }

    /// <summary>
    /// 带黑色投影的文字（IMGUI 没有描边，画两遍最省事；实机上小字靠这个才看得清）。
    ///
    /// ★ 2026-11-11 投影改成「按字号缩的整数像素」：
    ///   原来固定 1.5px —— 出招键上字号只有 29px，1.5px 的半像素偏移等于让整串字
    ///   被重采样一次，笔画边沿多出一圈 30%~60% 的灰过渡带，肉眼就是「发虚」。
    ///   现在小字只偏 1px（整像素不重采样），大字偏 2px；透明度也收小到 0.55，
    ///   投影只负责把字从底色里「拎出来」，不再糊第二层字。
    /// </summary>
    static void LabelAt(Rect rc, string s, Color col)
    {
        int off = _style.fontSize >= 34 ? 2 : 1;
        _style.normal.textColor = new Color(0f, 0f, 0f, 0.55f);
        GUI.Label(new Rect(rc.x + off, rc.y + off, rc.width, rc.height), s, _style);
        _style.normal.textColor = col;
        GUI.Label(rc, s, _style);
    }

    /// <summary>
    /// 出招键里写技能名：中文名一般 2~4 字（火球术 / 基本剑术 / 灵魂火符…）。
    ///
    /// ★ 2026-11-11 字号重定（评审原话：「偏小且发虚，字体比较粗糙」）：
    ///   旧版 4 字走 0.46r 两行（r=63 ⇒ 29px），实机量出来整块字只占圆直径
    ///   45%×47%，四周空一大圈 —— 在 126px 的圆里就是一小撮，第一眼抓不住。
    ///
    ///   圆的几何上限是内接正方形：2 行 2 列、每格边长 F，半对角线 F·√2 ≤ r。
    ///   取 F = 0.62r（r=63 ⇒ 39px），半对角线 56px，距圆边仍留 7px（亮环在 0.94r），
    ///   字块 78×80 占到直径 62%×63%，比原来大近 30%。行高同时从 F+3 收到 F+1：
    ///   方块字堆紧一点才像「招牌」，散开就像掉在圆里的几个字。
    ///
    ///   3 字也走「上 2 下 1」，不再缩字号单行放：三个键并排时字号必须一样大，
    ///   否则一排里一大一小更难看（旧版 3 字被压到 0.40r，比 4 字的还小一圈）。
    /// </summary>
    static void DrawCastLabel(Vector2 center, float radius, string name, Color col)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (name.Length > 4) name = name.Substring(0, 4);   // 圆里最多放 4 字

        int oldSize = _style.fontSize;
        var oldAlign = _style.alignment;
        _style.alignment = TextAnchor.MiddleCenter;
        _style.fontSize = Mathf.RoundToInt(Mathf.Clamp(radius * 0.62f, 22f, 46f));

        if (name.Length <= 2)
        {
            float h1 = _style.fontSize + 6f;
            LabelAt(new Rect(center.x - radius, center.y - h1 * 0.5f, radius * 2f, h1), name, col);
        }
        else
        {
            float lineH = _style.fontSize + 1f;
            LabelAt(new Rect(center.x - radius, center.y - lineH, radius * 2f, lineH),
                    name.Substring(0, 2), col);
            LabelAt(new Rect(center.x - radius, center.y, radius * 2f, lineH),
                    name.Substring(2), col);
        }
        _style.fontSize = oldSize;
        _style.alignment = oldAlign;
    }

    /// <summary>
    /// 画一个功能键：**默认有图标就只画图标**（主流手游的做法，屏幕上没有一个是写字的）；
    /// 素材没下发、或 useText=true 时在圆里写中文。
    ///
    /// ★ 2026-11-05 useText 的由来（别再回头抠图形）：
    ///   拾取 / 装备 / 技能 这三个白描图标连续三轮离线评审都没过关 ——
    ///     拾取：宝箱→「投币口」，箭头+托盘→「下载」，张开的手→「点击/交互」；
    ///     装备：胸甲→「蝴蝶结」，头盔→「柜子」，盾牌+大十字→「医疗包」，纯盾→「定位标」；
    ///     技能：四芒星→「太抽象」，摊开的书→「面罩/护目镜」。
    ///   44~74px 的圆键上，这三类语义**图形本身就不成立**（全世界手游也都规避，
    ///   所以原神那三个键下面是配文字的）。用户原话允许「图标，或者说是字体」，
    ///   所以这三键直接写汉字：一眼就懂，缩到多小都认得。
    /// </summary>
    static void DrawFunc(Vector2 center, float radius, float fill, float ring,
                         float inner, float bg, string iconName, float glyphScale,
                         string fallbackLabel, Color labelColor, bool useText = false,
                         Color? tint = null)
    {
        bool hasIcon = !useText && P2IconArt.Mask(iconName) != null;
        DrawPadIcon(center, radius, fill, ring, inner, bg, KindButton,
                    hasIcon ? iconName : null, -1, glyphScale, tint);
        if (!hasIcon && !string.IsNullOrEmpty(fallbackLabel))
        {
            int oldSize = _style.fontSize;
            // 圆里写字：字号按半径算（r=76 ⇒ 39px，两个字合起来约占直径 51%），
            // 和旁边按 0.60 半径铺开的图标视觉重量相当，不会一大一小。
            // ★ 2026-11-11 0.46→0.52：去掉合成粗体后字变细，同步放大一号补回来。
            _style.fontSize = Mathf.RoundToInt(Mathf.Clamp(radius * 0.52f, 18f, 46f));
            float txtH = _style.fontSize + 8f;
            LabelAt(new Rect(center.x - radius, center.y - txtH * 0.5f, radius * 2, txtH),
                    fallbackLabel, labelColor);
            _style.fontSize = oldSize;
        }
    }

    // ────────────────────────────────────────────────────────────
    //  按键贴图：现代扁平风 + 内嵌图标（2026-11-05 重做）
    // ────────────────────────────────────────────────────────────
    //
    // 用户 2026-11-05 原话：「把你那个操控这些摇杆按钮…参照比较主流的那种游戏，
    // 比如说《原神》…然后上面有图标，或者说是字体…就按照你的设计感，你去弄。」
    //
    // 上一版是「立体玻璃」（径向渐变 + 高光弧 + 内外棱），离线评审的原话是
    // 「非常典型的早期拟物化 UI（加重的高光、厚重的斜面倒角、强烈的立体阴影）」。
    // 这一版换成原神/王者那一套**扁平**语言，全部烘进一张贴图
    // （每键只能一次 DrawTexture —— 理由不是"A 通道会被丢掉"（那是旧误诊，见
    //  _padCache 处的订正），而是分两张画会在接缝处互相削边）：
    //
    //   ① 底：深暖黑本色 + 一层**极弱**的竖向渐变（上亮下暗）—— A 走真半透明，
    //      背景透得进来。扁平风靠「边界清晰」而不是靠高光
    //   ② 边：1 圈暖白细环（约 5% 半径）—— 半透明盘面全靠它收边
    //   ③ 环内侧压 1px 暗缝 —— 让圆「有厚度但不像拟物」，这是扁平风的常见做法
    //   ④ 图标：纯白实心，只带一层很淡的下沉暗影提升可读性
    //
    // 三种形态：
    //   KindRing   —— 摇杆底盘，空心环，圆心全黑（摇杆真美术缺失时的降级）
    //   KindKnob   —— 摇杆头，实心
    //   KindButton —— 功能键，实心+很淡的底
    //
    // 图标烘进贴图的理由：一层 DrawTexture 刷不透明矩形，画两层会把下层圆角切掉。

    const int KindRing = 0, KindKnob = 1, KindButton = 2;

    // 按键本色 —— **straight alpha**（2026-11-26 第三次定案）
    //
    //   盘身 = 深暖黑：半透明状态下它负责「压暗背景」。必须是深色 ——
    //     浅色盘身即使只给 30% 不透明度，叠在明亮草地上也会糊成一片白雾。
    //   外环 = 暖白：明亮细边，让圆盘在任何底色上都找得到边界。
    //     离线在真实截图上四版并排评审（padfinal.png，see-image 裁决）：
    //     「半透明深盘 + 清晰暖白细边」胜出，评语是「不像贴纸、最像原神/王者」。
    //
    // ★ 这和 2026-11-11 那版「两段暖色」不是一回事。那版把颜色和亮度乘在一起
    //   （rgb = v × lerp(Dark,Light,v)），v 常态只有 0.32，三个通道被压到最多差 8%，
    //   设备实测落到屏上是纯中性灰 (75,75,74)。现在颜色是**本色**，明暗由 alpha
    //   和光照明暗各自管，互不干扰。
    const float DarkR = 0.205f, DarkG = 0.165f, DarkB = 0.130f;   // 盘身：深暖黑
    const float LitR  = 1.000f, LitG  = 0.945f, LitB  = 0.855f;   // 外环：暖白

    // 旧口径 → 新口径换算。
    //   调用点传进来的 fill / ring 一直是「亮度」（常态 alpha × FillRatio × 亮度放大），
    //   新模型里它们要变成**不透明度**，逐处改调用点风险太大，所以在这里统一换算：
    //     IdleAlpha 0.45 × FillRatio 0.72 = 0.324  → bodyA 0.279（评审选中的「标准」档）
    //     IdleAlpha 0.45 × 0.95           = 0.4275 → ringA 0.80
    //   三档浓淡随之线性落在 0.21 / 0.28 / 0.37，无需另设表。
    const float BodyAlphaK = 0.86f;
    const float RingAlphaK = 1.87f;

    /// <summary>没有图标的按键（NPC 选项/关闭键等由调用方直接传图标名，这里给降级口）。</summary>
    static void DrawPad(Vector2 center, float radius, float fill, float ring,
                        float inner, float bg, int kind)
    {
        // magFrame 必须传 -1（= 不要图标）；传 0 会去取 MagIcon 第 0 帧，白白在圆上糊一个技能图
        DrawPadIcon(center, radius, fill, ring, inner, bg, kind, null, -1, 0f);
    }

    /// <summary>
    /// 画一个按键。iconName 是白描图标（Data/UI/ic_&lt;name&gt;.rgba），
    /// magFrame &gt;= 0 时改用原版技能图标（Data/UI/mag/&lt;帧号&gt;.rgba）。
    /// 两者都没有就只画圆 —— 素材没下发时不会崩，只是没图标。
    /// </summary>
    static void DrawPadIcon(Vector2 center, float radius, float fill, float ring,
                            float inner, float bg, int kind,
                            string iconName, int magFrame, float glyphScale)
    {
        DrawPadIcon(center, radius, fill, ring, inner, bg, kind, iconName, magFrame,
                    glyphScale, null);
    }

    /// <summary>
    /// 同上，额外用 tint 给整张贴图**染色**。
    ///
    /// ★ 2026-11-13 加（攻击键三态颜色原本看不见）：
    ///   旧实现把「已锁定=金 / 强制攻击=红」只写进 atkCol，而 atkCol 只用于
    ///   fallbackLabel 那行文字。可攻击键有图标（ic_attack 存在），DrawFunc 一旦
    ///   走图标分支就**不画文字** ⇒ 金/红两态在屏幕上完全没有体现，三态退化成
    ///   「0.45 和 0.52 两种透明度」——实机截图像素差异 <3%，肉眼分不出。
    ///   贴图颜色是本色（straight alpha），GUI.color 对 RGB 做乘算就能染色，
    ///   不必重做贴图，也不进 _padCache（着色是每帧只作用于攻击键一次）。
    /// ★ 2026-11-26：tint 只动 RGB、不动 A，所以染色后**透明度不变** ——
    ///   攻击键金/红两态只换颜色，不会顺带变成不透明或变透明。
    /// </summary>
    static void DrawPadIcon(Vector2 center, float radius, float fill, float ring,
                            float inner, float bg, int kind,
                            string iconName, int magFrame, float glyphScale, Color? tint)
    {
        // ★ 2026-11-26：分辨率阈值改成**按未缩放的基准半径**判。
        //  以前这里是 `radius >= 80f`，radius 是写死的基准值（76 / 100）；
        //  现在 radius 乘了 _S，UtilR=76 在「大」档变成 87.4 会跨过 80 ⇒ 同一颗键
        //  只是调了一下大小，贴图分辨率就从 128 跳到 192，既多吃显存、图标描边
        //  粗细也跟着变。除回 _S 就永远按原始尺寸判，换档只改显示大小、不改贴图。
        float baseR = _S > 0.001f ? radius / _S : radius;
        int n = baseR >= 80f ? 192 : 128;   // 小键降到 128，省显存（8 个出招键各有独立贴图）
        int kf = Q(fill), kr = Q(ring), ki = Q(inner), kb = Q(bg), kg = Q(glyphScale);
        var tex = GetPad(n, kf, kr, ki, kb, kind, iconName, magFrame, kg);
        // GUI.color 是乘算：tint=(1,0.86,0.55) ⇒ 盘面偏金，图标白处也一起偏金。
        // 不传 tint 时保持纯白（乘 1），和改动前逐字节一致。
        GUI.color = tint ?? Color.white;
        GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2, radius * 2),
            tex, ScaleMode.StretchToFill, true);
        GUI.color = Color.white;
    }

    static int Q(float v) { return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * 64f), 0, 64); }

    static Texture2D GetPad(int n, int kf, int kr, int ki, int kb, int kind,
                            string iconName, int magFrame, int kg)
    {
        string key = n + "_" + kind + "_" + kf + "_" + kr + "_" + ki + "_" + kb
                   + "_" + (iconName == null ? "-" : iconName) + "_" + magFrame + "_" + kg;
        Texture2D t;
        if (_padCache.TryGetValue(key, out t) && t != null) return t;
        t = MakePad(n, kf / 64f, kr / 64f, ki / 64f, kb / 64f, kind,
                    iconName, magFrame, kg / 64f);
        _padCache[key] = t;
        return t;
    }

    /// <summary>
    /// 生成一个按键贴图。**straight alpha：RGB 存本色、A 存覆盖率**（2026-11-26 重做）。
    /// 圆内 A = bodyA / ringA，圆外 A = 0，贴图按 AlphaBlend 画 ⇒ 背景真的能透出来。
    ///
    /// ★ 为什么这次能成（前面两次都是误诊，见 _padCache 那段注释的订正）：
    ///   Tuanjie 的 IMGUI **没有**丢 A 通道 —— P2StickArt 的摇杆底盘从 2026-11-11 起
    ///   就是「ApplyDiscAlpha 写 A=0 + DrawTexture(alphaBlend:true)」透出地图的，实机成立。
    ///   真正的问题一直是 MakePad 自己把 alpha 预乘进 RGB、把 A 钉死 255，
    ///   于是每个按键都成了一张不透明圆贴纸。
    ///
    /// 公式与 <本机>/tmp\padfinal.py 的 render() **逐行对应** —— 视觉是先在 Python 里
    /// 出图、在真实截图上四版并排给 see-image 评审过才搬过来的，改这里请同步改那边。
    ///
    /// bg 参数已失去意义（旧模型用它给圆外填不透明底色），保留形参只为不动 5 处调用点。
    /// </summary>
    static Texture2D MakePad(int n, float fill, float ring, float inner, float bg, int kind,
                             string iconName, int magFrame, float glyphScale)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Bilinear;
        var px = new Color32[n * n];
        float r = n * 0.5f;

        // 旧口径（亮度）→ 新口径（不透明度）。换算比例见 BodyAlphaK / RingAlphaK 的注释。
        float bodyA = Mathf.Clamp01(fill * BodyAlphaK);
        float ringA = Mathf.Clamp01(ring * RingAlphaK);

        // 细外环 / 环内细缝（扁平风的关键：边界清楚，不要拟物高光）
        float rimW = Mathf.Max(2f, r * 0.052f);
        float inW = Mathf.Max(1.2f, r * 0.014f);
        float haloR = r * 0.055f;

        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - r, dy = y + 0.5f - r;
                float d = Mathf.Sqrt(dx * dx + dy * dy);

                // ① 盘身：深暖黑本色 + 极弱的竖向光照（上略亮）。
                //    注意只调**颜色**、不调 alpha —— 半透明下 alpha 上深下浅会像脏了。
                float lit = Mathf.Clamp01(0.5f - dy / (r * 2.2f));
                float grad = 0.92f + 0.16f * lit;
                float bcR = DarkR * grad, bcG = DarkG * grad, bcB = DarkB * grad;

                // 圆盘遮罩：1 在盘内，到外沿 1px 线性降到 0（抗锯齿）
                float disc = Mathf.Clamp01(r - d);
                float aBody = bodyA * disc;

                // ② 外环：暖白细边 —— 半透明盘面全靠它收边
                float rimK = Mathf.Clamp01((d - (r - rimW)) / rimW) * disc;
                float aRing = ringA * rimK;
                float rcR = LitR, rcG = LitG, rcB = LitB;

                // ③ 环内侧细缝：把「盘面」和「环」分开，硬边就没有了
                float inDark = Mathf.Clamp01(1f - Mathf.Abs(d - (r - rimW - inW)) / inW) * disc;
                if (inDark > 0f)
                {
                    aBody *= (1f - 0.70f * inDark);
                    aRing *= (1f - 0.55f * inDark);
                }

                if (kind == KindRing)
                {
                    // 空心环（摇杆降级态）：只留一圈带 + 极淡的一层玻璃
                    float ringW = r * 0.030f;
                    float ringR = r - rimW;
                    aBody = 0.08f * disc;
                    aRing = ringA * Mathf.Clamp01(1f - Mathf.Abs(d - ringR) / (ringW * 0.5f)) * disc;
                }
                else if (inner > 0f)
                {
                    // ④ 锁定内环（原 inner 语义保留：锁定目标时亮一圈）
                    float inR = r * 0.62f;
                    float av = Mathf.Clamp01(1f - Mathf.Abs(d - inR) / (r * 0.075f)) * disc;
                    aRing = Mathf.Max(aRing, Mathf.Clamp01(inner) * av);
                }

                // 「环 over 身」的 straight-alpha 合成 —— 和 padfinal.py 的 render() 同一式
                float ao = aBody + aRing * (1f - aBody);
                float oR = 0f, oG = 0f, oB = 0f;
                if (ao > 0.002f)
                {
                    float inv = 1f / ao;
                    oR = (bcR * 255f * aBody + rcR * 255f * aRing * (1f - aBody)) * inv;
                    oG = (bcG * 255f * aBody + rcG * 255f * aRing * (1f - aBody)) * inv;
                    oB = (bcB * 255f * aBody + rcB * 255f * aRing * (1f - aBody)) * inv;
                }

                // ⑤ 圆外：先 5.5% 半径的柔光，再往外彻底透明。
                //    柔光也走 alpha（旧版是往 RGB 里加常数提亮），这样底下再亮
                //    也不会在圆边糊出一圈白边。
                if (d > r)
                {
                    if (d < r + haloR)
                    {
                        float g = 1f - (d - r) / haloR;
                        ao = g * g * 0.10f;
                        oR = oG = oB = 255f;
                    }
                    else
                    {
                        ao = 0f;
                    }
                }

                px[y * n + x] = ao <= 0f
                    ? new Color32(0, 0, 0, 0)
                    : new Color32(ClampB(oR), ClampB(oG), ClampB(oB), ClampB(ao * 255f));
            }

        // ⑥ 图标：白色 SDF 遮罩（3x3 超采样）或原版 MagIcon 帧（bicubic + 裁圆）。
        //    两个 Blend 都已改成 straight alpha 的 over 合成（见 P2IconArt），
        //    图标本身变成不透明白、盘面保持半透明。
        //
        //   ★ 2026-11-05 出招键改法（离线评审定案，见 <本机>/tmp\castmock.py）：
        //     技能帧是整块不透明的小方块，所以这里走「暗盘 + 原画盖满圆 + 亮环压边」，
        //     和下面功能键的白描图标是两条路，不要合并。
        //     （现状：出招键已改用文字标签，magFrame 恒传 -1，这条分支是留给旧素材的。）
        float gs = glyphScale > 0.01f ? glyphScale : 0.62f;
        if (magFrame >= 0)
        {
            var mag = P2IconArt.Mag(magFrame);
            if (mag != null)
            {
                // 原画是彩色的，浅底会把它冲淡 ⇒ 先把盘面压暗（只压颜色，alpha 不动）
                for (int i = 0; i < px.Length; i++)
                {
                    Color32 c0 = px[i];
                    px[i] = new Color32((byte)(c0.r * 0.22f), (byte)(c0.g * 0.22f),
                                        (byte)(c0.b * 0.22f), c0.a);
                }

                // 记下压暗后的盘面，画完原画再按圆遮罩合回去 ——
                // 既裁掉圆外的方角，圆边那 1px 也天然是抗锯齿的
                var under = (Color32[])px.Clone();
                P2IconArt.BlendMag(px, n, mag, gs, 1f);

                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = x + 0.5f - r, dy = y + 0.5f - r;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float m = Mathf.Clamp01(r - d);           // 1 圆内 → 0 圆外
                        int i = y * n + x;
                        Color32 u = under[i], c = px[i];
                        px[i] = new Color32(
                            (byte)(u.r + (c.r - u.r) * m),
                            (byte)(u.g + (c.g - u.g) * m),
                            (byte)(u.b + (c.b - u.b) * m),
                            (byte)(u.a + (c.a - u.a) * m));
                    }

                // 最上层压一道细亮环：原画铺满后边缘会糊，靠环把圆「收」住。
                //
                // ★ 2026-11-05 实机截图抓到的方块底图 bug（别再犯）：
                //   上一版这里的 rim **没有乘圆遮罩**。d >= r 时 rim 恒为 1、
                //   rv = ring 恒为正，于是圆外那一整块方角全被抬到 ring 亮度 ——
                //   实机截图上出招键有个 96x96 的完整正方形，四角 RGB=(88,95,103)
                //   完全一致（纯黑底上本该是 0）。前面 ①~④ 段都乘了 disc，唯独这段漏了。
                //   修法：再乘一道 clip(r+0.5-d) 把亮环收进圆内，圆外保持透明。
                //   离线复现/对照见 <本机>/tmp\castfix.py。
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = x + 0.5f - r, dy = y + 0.5f - r;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float rim = Mathf.Clamp01((d - (r - rimW)) / rimW)
                                  * Mathf.Clamp01(r + 0.5f - d);
                        if (rim <= 0f) continue;
                        int i = y * n + x;
                        Over(ref px[i], LitR * 255f, LitG * 255f, LitB * 255f, ringA * rim);
                    }
            }
            else
            {
                // 帧号对不上（这个技能没导出图标）⇒ 退回通用星星，别留一个空圆
                var fallback = P2IconArt.Mask("skill");
                if (fallback != null) P2IconArt.BlendMask(px, n, fallback, gs, 0.95f);
            }
        }
        else if (!string.IsNullOrEmpty(iconName))
        {
            var mask = P2IconArt.Mask(iconName);
            if (mask != null) P2IconArt.BlendMask(px, n, mask, gs, 0.95f);
        }

        // ★ 2026-11-05 修的行序 bug：
        //   上面整个循环都在**屏幕坐标**里工作（y=0 = 上方，和 GUI 一致），
        //   而 Unity 纹理的 SetPixels32 第 0 行是**下方**。
        //   上一版没翻，于是「上亮下暗」的渐变和图标全都上下颠倒 ——
        //   圆环是上下对称的看不出来，换成剑/药瓶这种有方向的图标就一眼穿帮。
        //   翻转在这里统一做一次，上面的公式就能和 padmock.py 逐行对齐。
        //   用 Array.Copy 而不是 Buffer.BlockCopy —— Color32 不是基元类型，
        //   BlockCopy 会对它抛 ArgumentException（"Object must be an array of primitives"）。
        var dst = new Color32[n * n];
        for (int y = 0; y < n; y++)
            Array.Copy(px, y * n, dst, (n - 1 - y) * n, n);

        t.SetPixels32(dst);
        t.Apply();
        return t;
    }

    /// <summary>0..255 浮点 → Color32 分量（clamp + 四舍五入）。</summary>
    static byte ClampB(float v)
    {
        if (v <= 0f) return 0;
        if (v >= 255f) return 255;
        return (byte)(v + 0.5f);
    }

    /// <summary>
    /// straight-alpha 的「over」：把颜色 (sr,sg,sb) 以不透明度 sa 叠到 dst 上面。
    /// 用来在半透明的盘面上盖不透光的图标 / 亮环 —— 结果是 alpha 变高、颜色趋白，
    /// 而不是像旧版那样把 alpha 直接写成 255（那样半透明就没了）。
    /// </summary>
    static void Over(ref Color32 dst, float sr, float sg, float sb, float sa)
    {
        sa = Mathf.Clamp01(sa);
        if (sa <= 0f) return;
        float da = dst.a / 255f;
        float ao = sa + da * (1f - sa);
        if (ao <= 0.002f) { dst = new Color32(0, 0, 0, 0); return; }
        float inv = 1f / ao;
        float r = (sr * sa + dst.r * da * (1f - sa)) * inv;
        float g = (sg * sa + dst.g * da * (1f - sa)) * inv;
        float b = (sb * sa + dst.b * da * (1f - sa)) * inv;
        dst = new Color32(ClampB(r), ClampB(g), ClampB(b), ClampB(ao * 255f));
    }
}
