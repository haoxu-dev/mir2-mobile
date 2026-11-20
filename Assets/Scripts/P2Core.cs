// P2-Core —— 把「真源码渲染桥」接进 Unity。
//
// 与 P1Core 的区别（这是关键）：
//   P1Core 的画是自己手写的（P1Map.cs：照着 DrawFloor 的公式重算一遍）。
//   P2Core 的画面**一个像素都不是我画的** —— 它调真源码的
//       MapControl.DrawFloor() / DrawObjects() / PlayerObject.DrawBody()
//   让 Crystal 那 4.66 万行自己把地图和角色画在一块 BGRA 缓冲上，
//   然后只做两件事：把 BGRA 搬进 Unity 纹理、竖直翻转（Unity 纹理第 0 行在底部）。
//
// 为什么这条路线是对的：
//   手写版永远会跟原版差一点（投影、裁切、层次、Alpha 混合…），
//   而真源码版天然等于原版 —— PC 端已经逐像素验收过 99.94% 一致。
//   以后加怪物、加特效、加装备外观，全都自动跟着真源码走，不用再抄一遍公式。
//
// 验收口径：
//   启动时把第一帧的原始 BGRA 落盘到 persistentDataPath/p2_frame.bgra，
//   用 adb pull 出来与 PC 基准 C:\ai\tmp\rbshot\frame.bgra 逐字节比。
//   同尺寸（1104x480）、同数据，不需要缩放，可以直接对比。
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using Client.MirCore;
using Client.MirObjects;
using Client.MirScenes;
using P2;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class P2Core : MonoBehaviour
{
    // ── 逻辑分辨率（界面坐标系）：1.76 全部 UI 的基准，永远 1024x768 ──────
    //
    // 2026-10-28 重大修正：以前这里写的是 1764x768（照手机宽高比 2.295 反推），
    // 结果真客户端自己的整套 UI 全部退化。原因是 Crystal 的 UI 是按
    // `Settings.Resolution` **枚举式**选素材的，只认四档：
    //   Client/Resolution/eSupportedResolution.cs → 1024 / 1280 / 1366 / 1920
    // 而 MainDialogs.cs:37 是这样的：
    //   Index = Settings.Resolution == 800 ? 0 : Settings.Resolution == 1024 ? 1 : 2;
    // Prguse[0]=24x22、Prguse[1]=800x251 真底板、**Prguse[2]=1x1 全透明**。
    // 所以 1764 落进 "其他" 档 → 底栏底板拿到 1x1 空图 → 真 UI 底板整块消失。
    //
    // ★ 2026-11-11 宽屏改造（这一段是那天之后的新设计，别改回去）：
    //   1024x768 现在是**界面坐标系**，不是画面尺寸。整幅画面由 Bridge.SetCanvasSize()
    //   按设备真实宽高比算出 CanvasWidth（高度恒 768），UI 整层右移 CanvasLeft 居中，
    //   左右多出来的那两条 = 真游戏地图（OffSetX 跟着变大，横向自动多画几格）。
    //   于是：
    //     · 操作逻辑永远在屏幕正中间（用户原话）
    //     · 两边不是黑边、不是拉伸，是真瓦片（用户原话「两边的边缘你给它补充好」）
    //     · 自适应：2616x1140 算出 1762 宽；折成 4:3 时算回 1024，自动回到方画面
    const int SW = 1024;
    const int SH = 768;

    /// <summary>地图真正会用到的图库（扫全图统计出来的：Back=0、Middle=1、Front=2/5/7），
    /// 加上角色衣服 0。清单硬编码是因为 Android 上 StreamingAssets 在 APK 里，不能列目录。</summary>
    static readonly string[] NEEDED = {
        // ★ 地图图库必须是**全尺寸版**（2026-10-28 第二次踩坑）：
        //   0.map 只用到 Back/Middle/Front = 图层 0/1/2/5/7，对应
        //     Tiles(0) / Smtiles(1) / Objects(2) / Objects4(5) / Objects6(7)，
        //   所以只要这 5 张 —— 但每张都必须是完整的：
        //     Tiles.Lib   22.3MB（曾经的残缺版只有 3.3MB）
        //     Objects.Lib 44.5MB（残缺 27.2MB）
        //     Objects4    20.3MB（残缺 139KB）
        //     Objects6    15.7MB（残缺 367KB）
        //   旧清单虽然文件名一样，但 Assets 里放的是截断版，**MLibrary 缺图是静默 return**，
        //   一块图都不报错 —— 表现就是「地图一片黑、只剩几个角色」。
        //   每次 uninstall 重装 persistentDataPath 会被清空，程序照这份清单重新拷贝，
        //   所以：Assets/StreamingAssets 里这 5 个文件也必须是全尺寸版，缺一不可。
        //
        // ★ 2026-11-02 补齐另外 12 个（沙巴克皇宫全黑的真正原因）：
        //   1.76 的 .map 每格前层库号 = FrontIndex 字节 + 2（Client/MirMaps/MapCode.cs:270），
        //   Crystal 在 Client/MirGraphics/MLibrary.cs:125-134 把它接成
        //     MapLibs[3]=Objects2 … MapLibs[28]=Objects27。
        //   扫全库 577 张图，实际用到的库号有 2..23 共 15 个（再加 31/32/34/35/41 各一张图）。
        //   以前只下了 Objects / Objects4 / Objects6 三张，其余 12 张 MLibrary.Initialize()
        //   对不存在的文件**静默造空库**，取图全返回 -1 ⇒ 前层一格都画不出来。
        //   沙巴克皇宫 0150.map 357 格用 Objects、264 格用 Objects3 ⇒ 一半画面直接是黑的。
        //   这 12 张由 C:/ai/tmp/mk_missing_libs.py 从 client2003 的 WIL（像素与官方 Lib
        //   逐像素一致）+ tmcq 的 WZL（Objects8 起只有这一套源）转出，只保留地图引用到的帧。
        //   实测：加进清单并 push 到设备后，沙巴克皇宫地图区非黑率 47.2% → 67.0%。
        "Data/Map/WemadeMir2/Tiles.Lib",
        "Data/Map/WemadeMir2/Smtiles.Lib",
        "Data/Map/WemadeMir2/Objects.Lib",
        "Data/Map/WemadeMir2/Objects2.Lib",
        "Data/Map/WemadeMir2/Objects3.Lib",
        "Data/Map/WemadeMir2/Objects4.Lib",
        "Data/Map/WemadeMir2/Objects5.Lib",
        "Data/Map/WemadeMir2/Objects6.Lib",
        "Data/Map/WemadeMir2/Objects7.Lib",
        "Data/Map/WemadeMir2/Objects8.Lib",
        "Data/Map/WemadeMir2/Objects9.Lib",
        "Data/Map/WemadeMir2/Objects10.Lib",
        "Data/Map/WemadeMir2/Objects13.Lib",
        "Data/Map/WemadeMir2/Objects16.Lib",
        "Data/Map/WemadeMir2/Objects18.Lib",
        "Data/Map/WemadeMir2/Objects20.Lib",
        "Data/Map/WemadeMir2/Objects22.Lib",
        "Data/CArmour/00.Lib",
        // ── 原版 UI 图库 ──
        // MirControl 整棵树（MainDialog、27 个按钮、快捷栏、聊天气泡）全靠这几张。
        // MLibrary.Draw 遇到缺图是**静默 return**，不报错、不抛异常，
        // 所以不下发就是「界面上什么都没有」，最难查。
        "Data/Prguse.Lib",
        "Data/Prguse2.Lib",
        // ★ MMap.Lib 是小地图（MiniMapDialog）的唯一素材源（2026-11-08 补齐）：
        //   MLibrary.cs 里 `MiniMap = new MLibrary(Settings.DataPath + "MMap")`，
        //   小地图底板按 1.76 Envir/MiniMap.txt 的 143 条映射取 MMap[0..217]。
        //   以前这张库只在设备上（2026-10-27 手工 adb push，8331747 字节 / 218 帧），
        //   从没进过下发清单 ⇒ 一旦 uninstall 重装或换机，小地图窗就退化成
        //   「没有小地图...」一行字，而且 MLibrary 缺文件是静默 return，日志里不会报错。
        "Data/MMap.Lib",
        // ★ Prguse3 不能漏（2026-10-28 事故）：1.76 底栏底板 BOTTOMBOARD800 = Prguse3[291]，
        //   一张 800x251 整图，不在 Prguse 里。少了它 Mir176Hud 只能退回 Prguse 取 291 号
        //   （那是完全不相干的图），安卓上表现为「底栏整块消失、只剩游戏画面」。
        "Data/Prguse3.Lib",
        // ★ 2026-11-13 补 3 张「一直在设备上、但从没进过下发清单」的库。
        //   出全量包时才暴露：这三张以前全靠历史 adb push 躺在设备上，
        //   换台手机/卸载重装就全没了，而且 MLibrary 缺文件是**静默 return**，
        //   日志一个字都不报，表现只会是「登录界面底图变回老样子 / 人物没有影子」。
        //   · WideBack.Lib   宽屏两侧延展底图（SelectScene:56，缺→两侧退回纯色黑边）
        //   · SelBack.Lib    选角界面 1024x768 加宽帧（SelectScene:125，缺→退回 Prguse[65]）
        //   · CharShadow.Lib 人物脚下的椭圆影子（Mir176CharLayer:84，缺→人物像浮在地上）
        "Data/WideBack.Lib",
        "Data/SelBack.Lib",
        "Data/CharShadow.Lib",
        // ★ Items.Lib 是 F9 背包 / F10 人物装备格里**物品图标**的唯一来源（2026-11-06 补齐）：
        //   MirItemCell.DrawControl 里 `Library = Libraries.Items`，物品图从 Items.Lib 取 Image 号。
        //   ★ 2026-11-06 之前这个文件根本没下发（母本一直在 Data_HOLD/Items.Lib，5761 帧），
        //     导致背包和装备格全部是空格子，而且 MLibrary 缺文件是静默 return，日志里一个字都没有。
        "Data/Items.Lib",
        // ★ StateItem.Lib 是 F10 人物装备页「纸娃娃」的唯一来源（2026-11-07 补齐）：
        //   CharacterDialog.DrawDoll / InspectDialog 里衣服、武器、头盔全部取
        //   StateItem[real.Image]（布衣男=60 → 80x128，乌木剑=43 → 40x94，青铜头盔=100 → 24x40），
        //   头发取 Prguse[440+Hair+性别]（441/443/445/447 才是真图）。
        //   以前这个文件没进清单，靠手工 adb push 才在设备上 —— 出正式包就会漏，
        //   而 MLibrary 缺文件是静默 return（不报错、不抛异常），表现就是「人物光着身子」。
        "Data/StateItem.Lib",
        // ★ Wui.Lib（= 1.76 的 ui1.wzl）是 F10 人物窗口的全部底图与控件（2026-11-07 补齐）：
        //   g_Wui 别名见 MShare_utf8.pas:2658 `g_wui := CreateImages('Data\ui1.data')`。
        //   FState_utf8.pas 的 DStateWin 用它取：
        //     3220 窗口底板 339x443 / 3221 男装备页 3222 女装备页 289x302
        //     3226 属性页 / 3227 技能页 / 3228 称号页 / 3283 男时装 3284 女时装
        //     3235..3241 七条页签条 23x310 / 3039 下翻 3042 上翻 27x13
        //   ★ 少这张 = 整个 340x443 人物窗口一片空白（MLibrary 缺文件是静默 return）。
        //   母本 Data_HOLD/Wui.Lib = 26779329 字节，3661 帧，由 ui1.wzl 转换而来。
        "Data/Wui.Lib",
        // ★ NewopUI 是 1.76 底栏中段真正的素材源（2026-10-28 补齐）：
        //   FState_utf8.pas L1339 BuildBottomWinSurface 用 g_opui 的 #0/#1/#2/#10..#17 拼底板，
        //   以前没有这张库，只能用 1px 白线横铺补底 ⇒ 屏幕上那一大片死白。
        //   由 C:\ai\mir_hz\tmcq\data\NewopUI.wzl 转换，见 C:\ai\tmp\ref176\make_newopui_lib.py。
        //   ★ 少这张 = 底栏中段又是白的，而且 MLibrary 缺图是静默 return，不会报错。
        "Data/NewopUI.Lib",
        "Data/UI_32bit.Lib",
        "Data/MagIcon.Lib",
        "Data/BuffIcon.Lib",
        // ── 法术特效图库（2026-11-15 补：火球/雷电从头到尾没有画面）──
        // 根因：Libraries 里 Magic / Magic2 / Magic3 / MagicC / Effect 五张库里，
        //   前两张从来没进过下发清单，设备上也没有 —— "Data/Magic.Lib" 一查是空的。
        //   MLibrary.Initialize() 对不存在的文件**静默造空库**（同怪物库那条），
        //   于是 Effect.Draw() 里 Library.DrawBlend(...) 拿到的图像索引全无效，
        //   直接 return，画面上什么都没有。表现为「技能能放、怪会掉血，就是看不见法术」。
        //
        //   缺哪几张、各管什么（Client/MirObjects/PlayerObject.cs 实证）：
        //     Magic.Lib  #0   火球术自身施法光（10 帧）  #170 火球命中爆炸（10 帧）
        //                #10  火球飞行体（CreateProjectile(10, Magic, ...)）
        //                #200 治愈术  #600 施毒  #900 灵魂火符  #1500 召唤骷髅
        //     Magic2.Lib #20  雷电术自身施法光（3 帧）   #10  雷电命中（5 帧）
        //     Effect.Lib —— 命中/受击的通用爆点，怪挨打时用
        //   Magic3 / MagicC 分别是 3.0 之后的高级技能，1.76 的 33 个技能用不到，
        //   暂不下发（省 8 MB）；将来开到高级技能再补。
        "Data/Magic.Lib",
        "Data/Magic2.Lib",
        "Data/Effect.Lib",
        // ── 怪物图库（2026-11-01 重建：144 张）──
        // 客户端按 Library.Monsters[Image] 取图，Image 就是**服务端怪物表的 Image 字段**。
        // ★ 2026-11-01 三个根因一起修掉了，这一节的历史必须记下来，不然还会踩：
        //
        //   ① 调色板红蓝颠倒 —— pal256.json 里 R/B 两个通道整个反了。
        //      用 1.76 官方 Data/Mon{N}.Lib（360 帧原始块，RGB 是真值）反解得到 254/256 色，
        //      与旧 pal256.json 的关系是「把 R 和 B 互换后逐字节完全相同」（254/254）。
        //      验收：3.44 亿像素重解，平均绝对误差 0.0000、最大单通道差 0。
        //      旧版所有怪物都是「红蓝互换」的怪颜色 —— 这就是牛看着不对的一半原因。
        //
        //   ② 服务端 Image 编号错 —— 616 只怪里有 231 只的 Image 是错的默认值
        //      （其中 168 只全写死成 165 = 牛魔王）。已按 1.76 GEE Monster.DB 的真实 appr 改正
        //      （牛 165→380、猪 165→390、祖玛雕像 50→61、祖玛卫士 72→62…），
        //      并就地写进 Build/Server/Release/Server.MirDB（改前备份 .bak-时间戳）。
        //      ★ 素材编号体系已实测确认：Data/Monster/{N}.Lib 的 N == tmcq appr，
        //        验证方法是把 Lib 的真图尺寸序列拿去 wzl 里做子序列匹配，
        //        100→Mon11@0、160→Mon17@0、165→Mon17@1800 全部命中。
        //
        //   ③ 下发清单只有 66 张，而数据库需要 144 个编号 —— 缺的编号客户端拿不到图。
        //      MLibrary 缺文件是**静默 return**，不报错，所以少一张就是那种怪完全隐形。
        //      现在按「数据库实际引用的 Image 编号」逐个列出，144 个一个不缺。
        //
        // ★ 每张都是 Crystal 原生 224 帧标准布局（Stand32/Walk48/Atk48/Struck16/Die80），
        //   帧表 frameCount 写 0，让客户端走 FrameSet.DefaultMonster，不依赖源库的怪布局。
        // ★ 目录里最大编号决定数组长度（InitLibrary 数最大数字 +1），所以 400 必须在。
        "Data/Monster/000.Lib", "Data/Monster/001.Lib", "Data/Monster/010.Lib", "Data/Monster/020.Lib", "Data/Monster/021.Lib", "Data/Monster/022.Lib",
        "Data/Monster/023.Lib", "Data/Monster/024.Lib", "Data/Monster/025.Lib", "Data/Monster/026.Lib", "Data/Monster/027.Lib", "Data/Monster/028.Lib",
        "Data/Monster/029.Lib", "Data/Monster/030.Lib", "Data/Monster/031.Lib", "Data/Monster/032.Lib", "Data/Monster/033.Lib", "Data/Monster/034.Lib",
        "Data/Monster/036.Lib", "Data/Monster/037.Lib", "Data/Monster/038.Lib", "Data/Monster/039.Lib", "Data/Monster/040.Lib", "Data/Monster/041.Lib",
        "Data/Monster/042.Lib", "Data/Monster/043.Lib", "Data/Monster/044.Lib", "Data/Monster/045.Lib", "Data/Monster/046.Lib", "Data/Monster/047.Lib",
        "Data/Monster/048.Lib", "Data/Monster/049.Lib", "Data/Monster/050.Lib", "Data/Monster/051.Lib", "Data/Monster/052.Lib", "Data/Monster/053.Lib",
        "Data/Monster/061.Lib", "Data/Monster/062.Lib", "Data/Monster/063.Lib", "Data/Monster/070.Lib", "Data/Monster/071.Lib", "Data/Monster/072.Lib",
        "Data/Monster/073.Lib", "Data/Monster/074.Lib", "Data/Monster/080.Lib", "Data/Monster/081.Lib", "Data/Monster/082.Lib", "Data/Monster/083.Lib",
        "Data/Monster/090.Lib", "Data/Monster/091.Lib", "Data/Monster/092.Lib", "Data/Monster/093.Lib", "Data/Monster/100.Lib", "Data/Monster/101.Lib",
        "Data/Monster/102.Lib", "Data/Monster/103.Lib", "Data/Monster/110.Lib", "Data/Monster/111.Lib", "Data/Monster/112.Lib", "Data/Monster/113.Lib",
        "Data/Monster/114.Lib", "Data/Monster/115.Lib", "Data/Monster/116.Lib", "Data/Monster/117.Lib", "Data/Monster/118.Lib", "Data/Monster/119.Lib",
        "Data/Monster/120.Lib", "Data/Monster/121.Lib", "Data/Monster/122.Lib", "Data/Monster/130.Lib", "Data/Monster/131.Lib", "Data/Monster/140.Lib",
        // ★ 2026-11-06 补 143/144/145/147/153：这 5 张以前只在设备上、没进 NEEDED 清单。
        //   对应怪物：143=恶灵尸王、144=骷髅锤兵、145=骷髅长枪兵、147=骷髅弓箭手、153=虹魔蝎卫。
        //   瘦包模式靠 adb push 侥幸能用，但出正式包时它们会被漏掉 ⇒ 那 5 族怪隐形。
        "Data/Monster/143.Lib", "Data/Monster/144.Lib", "Data/Monster/145.Lib", "Data/Monster/147.Lib", "Data/Monster/153.Lib",
        "Data/Monster/142.Lib", "Data/Monster/146.Lib", "Data/Monster/148.Lib", "Data/Monster/150.Lib", "Data/Monster/151.Lib", "Data/Monster/152.Lib",
        "Data/Monster/160.Lib", "Data/Monster/161.Lib", "Data/Monster/162.Lib", "Data/Monster/163.Lib", "Data/Monster/164.Lib", "Data/Monster/165.Lib",
        "Data/Monster/166.Lib", "Data/Monster/170.Lib", "Data/Monster/171.Lib", "Data/Monster/173.Lib", "Data/Monster/180.Lib", "Data/Monster/181.Lib",
        "Data/Monster/182.Lib", "Data/Monster/183.Lib", "Data/Monster/184.Lib", "Data/Monster/186.Lib", "Data/Monster/190.Lib", "Data/Monster/191.Lib",
        "Data/Monster/192.Lib", "Data/Monster/193.Lib", "Data/Monster/194.Lib", "Data/Monster/195.Lib", "Data/Monster/196.Lib", "Data/Monster/200.Lib",
        "Data/Monster/201.Lib", "Data/Monster/202.Lib", "Data/Monster/203.Lib", "Data/Monster/204.Lib", "Data/Monster/205.Lib", "Data/Monster/206.Lib",
        "Data/Monster/207.Lib", "Data/Monster/210.Lib", "Data/Monster/211.Lib", "Data/Monster/212.Lib", "Data/Monster/213.Lib", "Data/Monster/214.Lib",
        "Data/Monster/215.Lib", "Data/Monster/218.Lib", "Data/Monster/222.Lib", "Data/Monster/233.Lib", "Data/Monster/237.Lib", "Data/Monster/250.Lib",
        "Data/Monster/251.Lib", "Data/Monster/252.Lib", "Data/Monster/253.Lib", "Data/Monster/254.Lib", "Data/Monster/255.Lib", "Data/Monster/256.Lib",
        "Data/Monster/258.Lib", "Data/Monster/262.Lib", "Data/Monster/263.Lib", "Data/Monster/275.Lib", "Data/Monster/281.Lib", "Data/Monster/290.Lib",
        "Data/Monster/291.Lib", "Data/Monster/292.Lib", "Data/Monster/311.Lib", "Data/Monster/312.Lib", "Data/Monster/313.Lib", "Data/Monster/350.Lib",
        "Data/Monster/351.Lib", "Data/Monster/360.Lib", "Data/Monster/370.Lib", "Data/Monster/380.Lib", "Data/Monster/390.Lib", "Data/Monster/400.Lib",
        // ★ 418.Lib 是**空占位库**（2026-11-01）。不是漏了素材，是故意的：
        //   MLibrary.InitLibrary 数目录里最大的文件名 +1 当 Libraries.Monsters 数组长度，
        //   而客户端代码里最大会索引到 Monster.HoodedSummoner = 418
        //   （GameScene / MonsterObject 里 13 处 >400 的写法：403 PurpleFaeFlower、
        //    406 FurbolgWarrior、418 HoodedSummoner…）。
        //   可 1.76 素材只到 400，DB 里怪物 Image 最大也是 400 —— 这些编号永远不会被刷出来。
        //   没有这个文件 → 数组长度只有 401 → 代码一碰 418 就是 IndexOutOfRangeException 闪退。
        //   有它、内容全空 → 数组够长，那些编号永远不参与绘制，零代价。
        "Data/Monster/418.Lib",
        // ── 角色选择界面（用户要求的落地页）──
        // SelectScene 的底图、标题、六个按钮全在 ChrSel/Title 里；
        // 角色小人用 Hum/Hair/CArmour。
        "Data/ChrSel.Lib",
        "Data/Title.Lib",
        "Data/Hum.Lib",
        "Data/Hair.Lib",
        // ★ Weapon.Lib 2026-11-03 加：1.76 原版武器图库，76 套 × 600 帧。
        //   以前客户端走 CWeapon/ 目录（设备上是空的）⇒ 手上永远没剑，
        //   这就是用户说的「整个人物模型没连接对」的三分之一。见 PlayerObject.SetLibraries。
        "Data/Weapon.Lib",
        // ── 字体 ──
        // 原版文字走 TextRenderer.DrawText → 垫片的 TextRenderer → FontRasterizer（自研 TTF 光栅化）。
        // FontRasterizer 默认路径是相对的 "chatfont.ttf"，Android 上进程 cwd 是只读的 "/"，
        // 所以必须把字体当成素材一起下发到 PortableInit.RootPath，再显式设 FontPath。
        "Data/chatfont.ttf",
        // ── 界面文字（2026-10-29 补）──
        // 真源码所有界面文字都来自 ClientTextMap，默认是 Shared/Language.cs 里
        // **硬编码的英文**；中文只有 Client/Localization/Chinese.json 这一份，
        // 由 Settings.Load() 里那段 `./Localization/<Language>.json` 载入 ——
        // 而 Settings.Load() 只在 Program.cs（Windows 入口）里被调用，
        // 安卓这条路上根本不会执行，所以整个游戏一直是英文：
        //   「You have died, Do you want to revive in town?」
        // 这里把中文包当素材一起下发，启动时显式载入（见 InitGame 里的 LoadLanguage）。
        // 用 .tsv 而不是 .json：JSON 那条路依赖带反射的 JsonShim，在 IL2CPP 下静默失效
        // （详见 PortableLanguage.cs 头部注释与 13:55 的 logcat 证据）。
        "Localization/Chinese.tsv",
        // ── NPC 商人图库（2026-10-30 全量，84 张）──
        // 由 C:\ai\tmp\run_b\make_npc_lib.py 从 1.76 原版 npc.wzl / npc2.wzl 转出：
        //   npc.wzl  4840 格 = 69 个商人（外观 0..68），8 位索引色，调色板取 npal.idx 第 0 组
        //   npc2.wzl 1075 格 = 15 个商人（外观 69..83），16 位 ARGB1555
        // 每个商人 70 格 = 3 个朝向 x (4 站立 + 10 动作)。
        // ★ 关键：不重排像素，而是把 3 朝向的布局**写进 Lib 自带的帧表**——
        //   Client/MirObjects/NPCObject.cs:70 是 `Frames = BodyLibrary.Frames ?? FrameSet.DefaultNPC`，
        //   Lib 里有帧表就直接覆盖默认的 8 朝向 4 帧布局。
        // ★ 帧表（站立 start=0 count=4 skip=0 → 帧号 0+4*dir+i，dir0..7 占满 0..31；
        //        动作 start=32 count=10 skip=0 → 32+10*dir+i，最大 111 < 库内 112 帧）。
        //   为什么 skip 必须 0：DrawFrame = Start + (Count+Skip)*dir + i（NPCObject.cs:101），
        //   而本库每个方向只连续放 4 帧，skip 写 4 会让 dir1 跳到 8（那是 dir2 的帧）。
        // ★ 目录里最大编号决定数组长度（InitLibrary 数最大数字 +1），所以 83 必须在。
        // ★ MLibrary 缺文件是**静默 return**，不报错，少写一张 = 那个商人隐形。
        "Data/NPC/00.Lib", "Data/NPC/01.Lib", "Data/NPC/02.Lib", "Data/NPC/03.Lib", "Data/NPC/04.Lib", "Data/NPC/05.Lib", "Data/NPC/06.Lib", "Data/NPC/07.Lib",
        "Data/NPC/08.Lib", "Data/NPC/09.Lib", "Data/NPC/10.Lib", "Data/NPC/11.Lib", "Data/NPC/12.Lib", "Data/NPC/13.Lib", "Data/NPC/14.Lib", "Data/NPC/15.Lib",
        "Data/NPC/16.Lib", "Data/NPC/17.Lib", "Data/NPC/18.Lib", "Data/NPC/19.Lib", "Data/NPC/20.Lib", "Data/NPC/21.Lib", "Data/NPC/22.Lib", "Data/NPC/23.Lib",
        "Data/NPC/24.Lib", "Data/NPC/25.Lib", "Data/NPC/26.Lib", "Data/NPC/27.Lib", "Data/NPC/28.Lib", "Data/NPC/29.Lib", "Data/NPC/30.Lib", "Data/NPC/31.Lib",
        "Data/NPC/32.Lib", "Data/NPC/33.Lib", "Data/NPC/34.Lib", "Data/NPC/35.Lib", "Data/NPC/36.Lib", "Data/NPC/37.Lib", "Data/NPC/38.Lib", "Data/NPC/39.Lib",
        "Data/NPC/40.Lib", "Data/NPC/41.Lib", "Data/NPC/42.Lib", "Data/NPC/43.Lib", "Data/NPC/44.Lib", "Data/NPC/45.Lib", "Data/NPC/46.Lib", "Data/NPC/47.Lib",
        "Data/NPC/48.Lib", "Data/NPC/49.Lib", "Data/NPC/50.Lib", "Data/NPC/51.Lib", "Data/NPC/52.Lib", "Data/NPC/53.Lib", "Data/NPC/54.Lib", "Data/NPC/55.Lib",
        "Data/NPC/56.Lib", "Data/NPC/57.Lib", "Data/NPC/58.Lib", "Data/NPC/59.Lib", "Data/NPC/60.Lib", "Data/NPC/61.Lib", "Data/NPC/62.Lib", "Data/NPC/63.Lib",
        "Data/NPC/64.Lib", "Data/NPC/65.Lib", "Data/NPC/66.Lib", "Data/NPC/67.Lib", "Data/NPC/68.Lib", "Data/NPC/69.Lib", "Data/NPC/70.Lib", "Data/NPC/71.Lib",
        "Data/NPC/72.Lib", "Data/NPC/73.Lib", "Data/NPC/74.Lib", "Data/NPC/75.Lib", "Data/NPC/76.Lib", "Data/NPC/77.Lib", "Data/NPC/78.Lib", "Data/NPC/79.Lib",
        "Data/NPC/80.Lib", "Data/NPC/81.Lib", "Data/NPC/82.Lib", "Data/NPC/83.Lib",
        // ── 摇杆真美术（2026-11-02）──
        // 用户原话：「摇杆现在他妈的纯贴图，和那个王者荣耀的差距百倍」。
        // 原来是 MakePad() 逐像素画的发光空心环 + 渐变球，本质仍是 2D 手绘。
        // 现在换成**离线 3D 渲染**出来的两张图（C:\ai\tmp\proto14.py 生成）：
        //   底盘 = 解析高度场 + 数值法线 + 半球环境光 + Blinn-Phong + 菲涅尔，
        //          四向凹槽是真的刻进高度场里，靠法线自动出明暗；
        //   球头 = 真实球面法线的琥珀金属球，双高光 + 边缘菲涅尔。
        // ★ 为什么是 .rgba 原始字节而不是 .png：
        //   Tuanjie 的 Texture2D.LoadImage 依赖 ImageConversion 模块，这个工程里
        //   没有验证过；而 LoadRawTextureData 是 P2Core.Upload() 每帧都在用的成熟路径。
        //   480x480x4=921600 字节，对 387MB 的包是零代价，换来零风险。
        // ★ 字节序：文件里是**从上到下**的行序；载入时要翻成 Unity 的下到上（见 MakeTexTopDown）。
        "Data/UI/pad_base.rgba",
        "Data/UI/pad_knob.rgba",

        // ── 按键图标（2026-11-05）──
        // 用户原话：「把你那个操控这些摇杆按钮…参照比较主流的那种游戏，比如说《原神》…
        //            然后上面有图标，或者说是字体…就按照你的设计感，你去弄。」
        // 白描图标：256x256，RGB 全白、A = 覆盖率，由 C:\ai\tmp\mk_pad_icons.py 用
        // 有符号距离场生成（边缘天然抗锯齿），运行时由 P2IconArt 读成遮罩烘进按键贴图。
        "Data/UI/ic_attack.rgba",
        "Data/UI/ic_pick.rgba",
        "Data/UI/ic_pot.rgba",
        "Data/UI/ic_npc.rgba",
        "Data/UI/ic_bag.rgba",
        "Data/UI/ic_eqp.rgba",
        "Data/UI/ic_skill.rgba",
        "Data/UI/ic_close.rgba",
        "Data/UI/ic_page.rgba",

        // 技能图标：**原版 MagIcon.Lib 的未按下帧**（正版素材，不是自己画的）。
        // 帧号 = ClientMagic.Icon * 2（见 Client/MirScenes/Dialogs/MainDialogs.cs:1837），
        // 由 C:\ai\tmp\mk_magicon_rgba.py 导出成 32x30 RGBA，单张 3840 字节。
        // 只导了 Lib 里真实存在的偶数帧（1x1 占位帧已跳过）；某个技能取不到帧时
        // P2VirtualPad 会退回白色星星图标，不会留空圆。
        "Data/UI/mag/000.rgba",
        "Data/UI/mag/002.rgba",
        "Data/UI/mag/004.rgba",
        "Data/UI/mag/006.rgba",
        "Data/UI/mag/008.rgba",
        "Data/UI/mag/010.rgba",
        "Data/UI/mag/012.rgba",
        "Data/UI/mag/014.rgba",
        "Data/UI/mag/016.rgba",
        "Data/UI/mag/018.rgba",
        "Data/UI/mag/020.rgba",
        "Data/UI/mag/022.rgba",
        "Data/UI/mag/024.rgba",
        "Data/UI/mag/026.rgba",
        "Data/UI/mag/028.rgba",
        "Data/UI/mag/030.rgba",
        "Data/UI/mag/032.rgba",
        "Data/UI/mag/034.rgba",
        "Data/UI/mag/036.rgba",
        "Data/UI/mag/038.rgba",
        "Data/UI/mag/040.rgba",
        "Data/UI/mag/042.rgba",
        "Data/UI/mag/044.rgba",
        "Data/UI/mag/046.rgba",
        "Data/UI/mag/048.rgba",
        "Data/UI/mag/050.rgba",
        "Data/UI/mag/052.rgba",
        "Data/UI/mag/054.rgba",
        "Data/UI/mag/056.rgba",
        "Data/UI/mag/058.rgba",
        "Data/UI/mag/060.rgba",
        "Data/UI/mag/062.rgba",
        "Data/UI/mag/066.rgba",
        "Data/UI/mag/068.rgba",
        "Data/UI/mag/070.rgba",
        "Data/UI/mag/072.rgba",
        "Data/UI/mag/076.rgba",
        "Data/UI/mag/078.rgba",
        "Data/UI/mag/080.rgba",
        "Data/UI/mag/082.rgba",
        "Data/UI/mag/084.rgba",
        "Data/UI/mag/086.rgba",
        "Data/UI/mag/088.rgba",
        "Data/UI/mag/090.rgba",
        "Data/UI/mag/092.rgba",
        "Data/UI/mag/094.rgba",
        "Data/UI/mag/096.rgba",
        "Data/UI/mag/098.rgba",
        "Data/UI/mag/120.rgba",
        "Data/UI/mag/122.rgba",
        "Data/UI/mag/124.rgba",
        "Data/UI/mag/126.rgba",
        "Data/UI/mag/128.rgba",
        "Data/UI/mag/130.rgba",
    };

    MapControl _map;
    GameScene _scene;
    /// <summary>单机回退时的场景（联网时为空，画面来自原版 ActiveScene）。</summary>
    internal static GameScene NetDriverAutoScene;
    bool _networked;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    Client.MirControls.MirScene _lastDrawnScene;
    bool _mouseDown;

    // 进游戏后补抓一帧用的状态（见 Update 里 enteringGame 那段）
    bool _dumpedGame;
    bool _pendingGameDump;
    int _gameFrameCount;

    // 进游戏后持续落盘（每 _gameDumpMs 毫秒一版），用来做「两个时刻的画面比对」。
    const int _gameDumpMs = 4000;
    long _lastGameDumpMs;
    int _gameDumpSeq;

    /// <summary>
    /// 调试落盘开关：整帧 BGRA 每次几 MB，正式包里关掉（只留一行状态）。
    /// 要抓画面时在 Boot() 里改成 true 或加个 adb 可写的开关文件。
    /// </summary>
    static readonly bool DebugDump =
#if P2_DEBUG_DUMP
        true;
#else
        false;
#endif

    Texture2D _tex;

    // ── 黑边环境光底衬（2026-11-11 加）──
    //
    // 症状：设备 2616x1140、逻辑 1024x768（4:3）⇒ 左右各 548 设备像素的黑边，
    //   合起来占屏幕 31% 的面积，其中 79% 是纯黑 (0,0,0)。
    //   实机评审（vis_test.py）反复点名「按钮孤零零漂在黑底上，UI 与画面之间没有过渡」，
    //   用户的原话是「太丑了……没办法看」。
    //
    // 做法：把游戏画面**左右内缘**的那一条像素，按行取平均，低通滤波后
    //   横向拉伸铺满黑边、并按离接缝的距离做衰减 —— 这就是主机/模拟器常用的
    //   ambient border（PS5 主界面、RetroArch 的 ambient 边框是同一手法）。
    //   好处：黑边跟着场景的色调和亮暗一起呼吸，画面像是「溢」出去的，
    //   而不是一块死黑贴图；同时不动游戏画面本身一个像素（内缘不算进 vp）。
    //
    // 代价控制：贴图只有 32x96（12KB），每帧重建 ~1300 次采样、一次 12KB 上传，
    //   外加 96 个数的时序低通 —— 实测对 FPS 无可见影响（见 p2_boot.log 的 FPS 行）。
    //   低通是必须的：不滤的话镜头一动，黑边就跟着频闪，比纯黑更难看。
    Texture2D _ambL, _ambR;
    Color32[] _ambLpx, _ambRpx;
    float[] _ambLr, _ambLg, _ambLb;   // 左黑边每行的平滑后颜色（0..255）
    float[] _ambRr, _ambRg, _ambRb;   // 右黑边同上
    const int AmbW = 32, AmbH = 96;
    const int AmbSampleW = 14;        // 从游戏内缘取多宽的一条做采样

    bool _ready;
    string _status = "初始化中…";
    string _logPath = "";
    readonly StringBuilder _log = new StringBuilder();

    // ── 启动屏（2026-11-14 加）──
    //
    // 症状：真机装上后启动是**纯黑**，用户以为死机 —— 因为 DrawGui 在 _ready 之前
    //   直接把整屏刷黑，而进度串 [P2] _status 藏三指手势后面（_showHud 默认关）。
    //   首次启动要从 APK 的 jar: 里逐个抽出 909MB 素材写到 persistentDataPath，
    //   真机上要几分钟，这期间用户看到的是一块黑板，没有任何证据说明它还在干活。
    // 做法：_ready 之前常显启动屏 —— 标题 + 当前步骤 + 已运行秒数 + 已写字节数。
    //   带「秒数在涨」这一条，用户能自己判断是「还在跑」还是「真卡死」。
    //   字体用 Unity 内置 Arial：chatfont.ttf 要 CopyAssets 之后才存在，而本屏
    //   恰恰要在那之前显示；内置字体不含中文，所以这里全用 ASCII。
    long _bootT0;                 // Boot 协程起点的 Stopwatch 时间戳
    long _bootBytes;              // 已从 APK 抽出的素材字节数
    int _bootItems, _bootTotal;   // 已处理 / 应处理素材项数
    string _bootTitle = "CHUANQI 1.76  -  Starting";

    int _drawMs, _uploadMs, _stepMs, _guiMs;

    // ── 帧耗时 A/B 开关（2026-11-04 加）──
    //
    // 症状：状态行「帧周期=446ms 体内=71ms」—— 在 Update 里我们只干 71ms，
    //   但一帧要 446ms。缺口 375ms 不在 Update 里，只可能在三处：
    //     ① RenderFrame()  软件渲染整个 1.76 画面到内存位图（我们自己的像素活）
    //     ② Upload()       LoadRawTextureData + Apply（3MB 上传 GPU）
    //     ③ OnGUI 的 GUI.DrawTexture（把纹理 blit 到屏幕，Unity 内部走 GLES）
    //   反复改代码重打包一次 4 分钟，用运行时掩码逐个摘掉，几十秒就能分清。
    //   命令：adb shell "echo 'ab nosw' > .../p2_cmd.txt"（见 P2DevCmd）
    public const int AB_SKIP_SOFTWARE = 1;   // 不画软件帧（RenderFrame 整个跳过）
    public const int AB_SKIP_UPLOAD = 2;     // 不上传纹理
    public const int AB_SKIP_BLIT = 4;       // OnGUI 里不 blit 纹理到屏幕
    public static int AbMask;

    // 整帧拆解（2026-11-04 加，见 Update 开头的注释）
    long _lastFrameEndMs;
    int _frameGapMs, _frameBodyMs;
    Stopwatch _frameBodySw;
    byte[] _raw;
    float _fps;
    int _frames;
    float _fpsT;

    // ── 位置与行走（与 P1Core 同口径：真 A* + 镜头跟人）──
    Vector2Int _cell = new Vector2Int(200, 200);
    Vector2Int _target = new Vector2Int(600, 600);
    System.Collections.Generic.List<Vector2Int> _path = new System.Collections.Generic.List<Vector2Int>();
    int _pathIdx;
    float _stepT;
    const float StepTime = 0.30f;
    int _dir = 4;
    int _walkFrame;
    float _walkAnimT;
    const float WalkFrameTime = 0.10f;
    bool _autoWalk;

    // 启动后先站定，给「截图 vs 基准」留一个镜头停在已知坐标的窗口
    const float BootHold = 25f;
    float _bootHoldUntil;

    // ── 诊断 ──
    int _renderCount;
    bool _dumped;

    // ── 运行时换比例（折叠屏开合 / 分屏 / 旋转）──
    //
    // 画布尺寸（Bridge.CanvasWidth/Height）在启动协程里算一次就写死了，
    // 派生品（MapControl 的视野参数 / 控件 Size / 天气粒子 / 地面光照纹理）
    // 也全都是那时定的。OPPO Find N5 这类折叠机在游戏运行中展开或合拢，
    // Screen 宽高比一变，画布还是旧的 ⇒ Viewport() 只能等比缩放并留空带
    // （实测切成 2048x1536 后上下各留 448 / 248 px）。
    //
    // 这里每帧比对尺寸，变了就调 Bridge.RefitCanvas 重算并作废派生品；
    // Unity 纹理与 _raw 缓冲跟着重建（尺寸变了不重建，Upload 会整帧跳过 ⇒ 画面冻住）。
    int _lastSw, _lastSh;
    bool _sizeHooked;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        string scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!string.IsNullOrEmpty(scn) && scn != "P2")
        {
            UnityEngine.Debug.Log("[P2CORE] boot skipped, active scene = '" + scn + "'");
            return;
        }
        var go = new GameObject("P2Core");
        go.AddComponent<P2Core>();
        UnityEngine.Object.DontDestroyOnLoad(go);
        UnityEngine.Debug.Log("[P2CORE] 引导完成");
    }

    void Start()
    {
        // Unity 在 Android 上默认 Application.targetFrameRate=30，所以哪怕模拟器跑在 4060 上、
        // 单帧渲染只要 4~14ms，画面也死死卡在 30 FPS（2026-10-31 实测：FPS 恒等于 30.0，
        // 与渲染耗时无关）。这里显式要 60。
        // vSyncCount 保持 1（模拟器屏幕就是 60Hz），与 targetFrameRate=60 同向下发即为满帧。
        QualitySettings.vSyncCount = 1;
        Application.targetFrameRate = 60;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        StartCoroutine(Boot());
    }

    /// <summary>日志同时进 Logcat 和 persistentDataPath/p2_boot.log（adb pull 就能看）。</summary>
    /// <remarks>
    /// ★ 2026-11-03 必须加锁：服务端后台线程会通过 EmbeddedServer.Log 回调进到这里，
    ///   和主线程同时 AppendLine 同一个 StringBuilder。StringBuilder 不是线程安全的，
    ///   实测（23:23 那次 logcat）并发写把内部状态写坏了，p2_boot.log 里出现
    ///   「果。」「图。」「。」这种半截行，而且整行丢失 —— 「内嵌服务端就绪」
    ///   和「登录成功」都丢过，验收脚本据此误判"服务端没起来"。
    ///   锁只保护内存里的拼接，文件写入放到锁外，免得磁盘 IO 堵住渲染线程。
    /// </remarks>
    static readonly object _logLock = new object();

    // ★ 2026-11-04 落盘从「每行重写整个文件」改成「追加」。
    //
    //   原来的实现是 `File.WriteAllText(_logPath, _log.ToString())` —— 每写一行日志，
    //   就把**整份日志**重新写一遍。启动跑完时日志能到 1.8MB，那是已经崩掉的旧记录
    //   （见设备上 p2_boot.log.prev），按那个体量每行就是一次 1.8MB 的磁盘写。
    //   进游戏后每 4 秒一口气写 8~9 行，等于每 4 秒往磁盘写十几 MB。
    //
    //   这正是 stime 占 72% 的一大来源（内核态：文件写 + fsync + 页缓存）。
    //   改成 File.AppendText 后每行只写自己那几十字节，代价与日志总量无关。
    //
    //   内存里的 _log 仍然保留完整历史（状态行、验收脚本都靠它）；
    //   只是不再每次从内存整份重写磁盘。
    void L(string s)
    {
        Debug.Log("[P2CORE] " + s);

        lock (_logLock)
        {
            _log.AppendLine(s);
            if (string.IsNullOrEmpty(_logPath)) return;
            try
            {
                using (var w = new StreamWriter(_logPath, true, System.Text.Encoding.UTF8))
                {
                    w.WriteLine(s);
                    w.Flush();
                }
            }
            catch (Exception) { /* 落盘失败不影响渲染 */ }
        }
    }

    /// <summary>服务端 log4net 日志的落地点（见 Log4NetShim.Emit）。</summary>
    /// <remarks>
    /// ★ 2026-11-03：原来服务端日志走 System.Diagnostics.Trace，在 IL2CPP 下表现为
    ///   同一条消息打印两遍（完整行 + 半截尾巴，logcat 里那行孤零零的 `果。`），
    ///   而且**不写 p2_boot.log**。账号库自愈公告（MessageQueue.Enqueue）正是走这条
    ///   通道，验收脚本要读它判断自愈有没有触发 —— 半截覆盖 = 抓不到 = 假失败。
    ///   接到这里之后：单通道，和 [P2CORE] 同一把锁、同一份落盘文件。
    /// </remarks>
    void SrvLog(string line)
    {
        L("[srvlog] " + line);
    }

    IEnumerator Boot()
    {
        var sw = Stopwatch.StartNew();
        _bootT0 = Stopwatch.GetTimestamp();
        _bootTotal = 0; _bootItems = 0; _bootBytes = 0;
        _logPath = Path.Combine(Application.persistentDataPath, "p2_boot.log");

        // L() 现在是追加写（不再每行重写整份文件，见那里的注释）。
        // 追加语义下必须在这里把上一次的旧日志清掉，否则每次启动都在旧内容后面接，
        // 日志会无限长、验收脚本 grep 到的第一条永远是上一次运行的。
        try
        {
            if (File.Exists(_logPath)) File.Delete(_logPath);
            using (var w = new StreamWriter(_logPath, false, System.Text.Encoding.UTF8)) { }
        }
        catch (Exception) { /* 清不掉就退回追加，不影响运行 */ }

        // ── 0) 切进程 cwd 到可写目录 ──
        // 真源码全是相对路径（"./Data/"、"./Mir2Config.ini"），Android 上进程 cwd 是只读的 "/"。
        // 必须在碰 Client.Settings 的**任何**成员之前调用（它的静态构造里会 new InIReader）。
        bool initOk = PortableInit.Init(Application.persistentDataPath);
        L("PortableInit=" + initOk + " root=" + PortableInit.RootPath);

        // 远程命令行通道（p2_cmd.txt）。必须在 cwd 切好之后初始化，
        // 否则 Path.Combine 拿到的是只读的进程根目录。
        P2DevCmd.Init(PortableInit.RootPath);
        L("设备 " + Screen.width + "x" + Screen.height + "  dpi=" + Screen.dpi);

        // ── 1) 素材：StreamingAssets → persistentDataPath ──
        // Android 上 StreamingAssets 在 APK 内部（jar: 路径），File.Exists/FileStream 一律读不到，
        // 而 MLibrary.Initialize() 恰恰只认 FileStream。所以必须先整份拷到可写目录。
        _status = "拷贝素材…";
        yield return CopyAssets();

        // ── 1.2) 界面文字：载入中文包 ──
        // ★ 为什么这里必须显式做（2026-10-29 定位，之前整个游戏是英文）：
        //   真源码的文字全部来自 GameLanguage.ClientTextMap，它的**默认值是英文**
        //   （Shared/Language.cs L1899+ 硬编码），中文靠 Settings 里那段
        //     ./Localization/<Language>.json  → GameLanguage.LoadClientLanguage()
        //   覆盖上去。而 Settings.Load() 只在 Client/Program.cs:39（Windows 入口）被调用，
        //   安卓走 Unity，永远碰不到那段代码 ⇒ 玩家看到的标题、对话框、按键提示全是英文。
        //   证据：logcat 里死亡弹窗原文是
        //         「You have died, Do you want to revive in town?」
        //   现在中文包已随素材下发（见 NEEDED 里的 Localization/Chinese.json），
        //   在**任何控件构造之前**载入 —— MirLabel 构造时就取文字，晚了就固化成英文。
        // ★ 必须走 PortableLanguage.LoadTsv，**不能**用原版 GameLanguage.LoadClientLanguage（2026-10-29 实测）。
        //   原版内部是 System.Text.Json 的 JsonSerializer.Deserialize<TextMap>()，而
        //   netstandard2.1 里没有这个包，走的是 Shim/JsonShim.cs 那个用反射手搓的最小解析器。
        //   它在桌面 .NET 上解析这份文件完美（实测 1090 条全中），但在安卓 IL2CPP 里**静默失效**；
        //   而 LoadClientLanguage 的 catch (Exception) { } 是空的，异常被吞得干干净净。
        //   实测证据（13:55 logcat）：
        //     `界面文字=中文 …/Chinese.json  中文条目 4/1089  死亡提示=「You have died…」`
        //   —— 1089 条里只有 4 条带中文，那 4 条还是源码里本来写死的中文。
        //   TSV 是纯文本逐行赋值，不碰反射，IL2CPP 下稳。
        try
        {
            string langPath = Path.Combine(PortableInit.RootPath, "Localization", "Chinese.tsv");
            if (PortableLanguage.LoadTsv(langPath))
            {
                int cn = 0;
                foreach (var kv in GameLanguage.ClientTextMap.Text)
                {
                    // 粗略计数：只要含有非 ASCII 字符就算中文生效
                    foreach (char ch in kv.Value) { if (ch > 127) { cn++; break; } }
                }
                L("界面文字=中文  " + langPath +
                  "  覆盖 Text " + PortableLanguage.LoadedText + "/" + GameLanguage.ClientTextMap.Text.Count +
                  "  Enum " + PortableLanguage.LoadedEnum + "/" + GameLanguage.ClientTextMap.Enum.Count +
                  "  中文条目 " + cn +
                  "  死亡提示=「" + GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DiedTip) + "」");
            }
            else
            {
                L("★ 语言包没载入 " + langPath + " ⇒ 界面会是英文");
            }
        }
        catch (Exception e)
        {
            L("★ 载入语言包失败 " + e.GetType().Name + " " + e.Message);
        }
        yield return null;

        // ── 1.5) 字体路径 ──
        // FontRasterizer 默认 "chatfont.ttf" 是相对进程 cwd 的；Android cwd 是 "/"（只读），载不到。
        // 必须在**任何** UI 控件构造之前设好 —— MirLabel 构造时就会 new Font()，
        // FontRasterizer.FontPath 的 setter 会重置缓存，所以这里设一次就够了。
        // 这个提前量不能省：MirLabel 构造 → Font.Height → FontRasterizer.LineHeight → 载字体。
        string fontPath = Path.Combine(PortableInit.RootPath, "Data", "chatfont.ttf");
        bool fontExists = File.Exists(fontPath);
        FontRasterizer.FontPath = fontPath;
        L("字体 path=" + fontPath + " 存在=" + fontExists +
          " 载入=" + (FontRasterizer.Font != null) +
          " 行高(8pt)=" + FontRasterizer.LineHeight(8f));
        if (!fontExists) { _status = "★字体缺失 " + fontPath; L("★ " + _status); yield break; }
        yield return null;

        // ── 2) 逻辑分辨率 ──
        // MapControl 的构造函数按 ScreenWidth/Height 算 OffSetX/OffSetY/ViewRangeX/ViewRangeY，
        // 所以这两个值必须在 CreateMap() 之前定好。
        Bridge.SetScreenSize(SW, SH);

        // ★ 2026-11-11 宽屏改造：世界画布按设备真实宽高比算，必须排在 CreateMap() 之前
        //   —— MapControl 构造函数里 OffSetX/OffSetY/ViewRangeX/ViewRangeY 全由画布尺寸推导，
        //   晚一步算，横向就只画 1024 那么宽，两侧还是黑。
        L(Bridge.SetCanvasSize(Screen.width, Screen.height));
        L("界面坐标系 " + SW + "x" + SH + "  设备 " + Screen.width + "x" + Screen.height);
        yield return null;

        // ── 2.5) 音频初始化**延后**（2026-11-13 改）──
        //
        // 原来这里就 Create() + LoadSoundList()。问题：SoundList.LoadSoundList()
        // 读的是 ./Sound/SoundList.lst，而 Sound/ 现在是由 StreamingAssets/
        // SoundData/SoundData.pak 在**第 3.55 步**才解出来。全新安装时 2.5 步
        // Sound/ 还不存在 → 载入 0 条 → Indexes 恒空 → 全程静音且毫无报错。
        // 所以整段挪到 3.55 解包之后（见下面「[audio] 初始化」段）。
        yield return null;

        // ── 3) 真源码地图 ──
        _map = Bridge.CreateMap();

        // 先把全部 577 张地图解到 ./Map/，再读第一张。
        //
        // 为什么必须全解而不是只解一张：
        //   真源码换图走的是
        //     GameScene.MapChanged（Client/MirScenes/GameScene.cs:3970）
        //       → MapControl.FileName = Path.Combine(Settings.MapPath, p.FileName + ".map")
        //       → MapControl.LoadMap()（同文件 10437）
        //   文件名整个由**服务器下发**（S.MapChanged.FileName），客户端没法预判
        //   玩家会走到比奇省、盟重省还是某个洞穴。缺哪张，MapReader 就静默造一张
        //   1000x1000 的空图（MapCode.cs:152-164，不报错），画面全黑但日志正常 ——
        //   2026-10-28 就栽在这个静默降级上。所以一次性全解，宁可多等几秒。
        //
        // 数据源是 StreamingAssets/Data/Map/maps.pak（186.1 MB 压到 11.2 MB，
        //   .map 是大量重复格子的结构体数组，Deflate 压到 6%）。
        //   打包脚本见 C:\ai\tmp\run_b\pack_maps.py，格式说明也在那里。
        _status = "解包地图…";
        yield return UnpackMaps();

        // ── 3.5) 解服务端数据（Exvir/Configs/Server.MirDB）──
        // 手机上「自己开服务器」的第一步：服务端那些相对路径（./Envir、./Configs、
        // ./Server.MirDB）得先有文件，而 Android 上 StreamingAssets 在 APK 里读不到。
        // 所以和地图同一个套路：先解到 persistentDataPath（= 当前 cwd），再让服务端起。
        _status = "解包服务端数据…";
        yield return UnpackServerData();

        // ── 3.55) 解音效数据（Sound/ 669 个 wav + SoundList.lst）──
        // ★ 2026-11-13 加。为什么必须有：
        //   以前 Sound/ 只靠 adb push 推到设备，**没进 APK**。任何一次重装都会
        //   把 Sound/ 清掉，游戏立刻退回全程静音，日志还一切正常（查不出来）。
        //   数据源 StreamingAssets/SoundData/SoundData.pak：669 个 wav 从 124.8MB
        //   转成 22050Hz 单声道 16bit 再 raw-deflate，约 43MB。
        //   为什么必须留 16bit PCM：Shim/NAudioShim.cs 的 DecodeWav 只认
        //   fmtTag 1(PCM)/3(float)，ADPCM 解码器没实现 —— 转 ADPCM 会静音。
        //
        //   ★ 必须排在音频初始化（下面第 2.5 步）之前才会有声音，但这里已经在
        //     第 3.5 步之后了，所以音频那步改成延时到解包完成再 Create()。
        //     实际做法：把音频初始化整体挪到 3.55 之后（见下面的 "[audio]" 段）。
        _status = "解包音效…";
        yield return UnpackSoundData();

        // ── 3.57) [audio] 初始化（从 2.5 步挪来，必须在 Sound/ 就位之后）──
        //
        // 为什么必须显式调用：Crystal 的 SoundManager.Create() 只在
        // Client/Forms/CMain.cs:93 被调，而 Forms/ 整个目录被 csproj 排除在编译外。
        // 安卓上 mixer 因此**从来不存在** —— 这是「100% 静音」的第一层根因。
        // 第二层是垫片 AudioFileReader.Read() 恒返回 0（已改成真解码，见 NAudioShim.cs）。
        //
        // P2Audio 负责把垫片算出来的采样交给 Unity 的音频设备，
        // 并补一个场景里没有的 AudioListener（P2.unity 实测 0 个）。
        P2Audio.Log = L;
        P2Audio.Ensure(gameObject);
        try
        {
            Client.MirSounds.SoundManager.Create();
            Bridge.SetAudioVolume(100, 100);
            Client.MirSounds.SoundManager.Vol = 100;
            L("[audio] SoundManager.Create() 完成  音乐/音效音量=100");

            // ★ 必须显式载 lst：原版在 Forms/CMain.cs 里调，Forms/ 不进编译 ⇒ 安卓上没人调
            //   ⇒ Indexes 恒空 ⇒ 所有 PlaySound 查不到文件名 ⇒ 全程静音。
            int n = Bridge.LoadSoundList();
            L("[audio] SoundList.lst 载入 " + n + " 条");

            // 声音文件本身在不在（目录为空时 lst 载入成功也照样静音）
            try
            {
                string sndDir = PortableInit.Resolve("Sound");
                int wavCount = 0;
                if (Directory.Exists(sndDir))
                {
                    var caps = Directory.GetFiles(sndDir, "*.wav");
                    wavCount = caps == null ? 0 : caps.Length;
                }
                L("[audio] Sound/ 里 wav 文件 " + wavCount + " 个  (" + sndDir + ")");
            }
            catch (Exception e) { L("[audio] 数 Sound/ 失败 " + e.GetType().Name); }
        }
        catch (Exception e)
        {
            L("★音频初始化失败 " + e.GetType().Name + ": " + e.Message);
        }
        L("[audio] " + Bridge.AudioDiag());
        yield return null;

        // ── 3.6) 起内嵌服务端 ──
        // ★ 用户 2026-11-01 的原话：「我需要的就是手机上自己开服务器，自己弄，
        //   全部都结合到一起。」以前是电脑跑 Server.exe + adb reverse 隧道，
        //   玩家一离开电脑就玩不了，隧道一断就「连接超时」。
        //   现在服务端就跑在这个 Unity 进程里（Server.MirEnvir.Envir.Main），
        //   和客户端同进程、走 127.0.0.1 的真 TCP + Crystal 协议。
        //   服务端代码一行没改 ⇒ 刷怪/掉落/NPC/技能/经验全按 1.76 源码规范来。
        //
        // 启动是**异步**的（Envir.Start() 立刻返回，480 张地图在后台线程加载，
        // 实测电脑上约 5 秒），所以这里只负责点火，就绪轮询在下面第一步之后。
        _status = "启动内嵌服务端…";
        EmbeddedServer.Log = s => L("[srv] " + s);
        // 服务端内部 log4net 的系统消息（自愈公告、加载统计）也接到同一份日志里。
        log4net.LogManager.Sink = SrvLog;
        EmbeddedServer.Start();
        L("内嵌服务端已点火，等待其就绪…");
        yield return null;

        // 解包后读一张作初始图（进游戏前单机回退用）。优先 0.map（比奇省）。
        string startMapPath = Path.Combine(PortableInit.RootPath, "Map", "0.map");
        if (!File.Exists(startMapPath))
        {
            _status = "★缺 ./Map/0.map";
            L("★ " + _status);
            yield break;
        }

        bool mapOk = Bridge.LoadMapFromFile(_map, startMapPath);
        int mw, mh;
        Bridge.GetSize(_map, out mw, out mh);
        L("地图 ok=" + mapOk + " 尺寸 " + mw + "x" + mh + "  解析 " + sw.ElapsedMilliseconds + "ms");
        if (!mapOk || mw <= 0) { _status = "地图解析失败"; yield break; }
        yield return null;

        // ── 4) 渲染桥：建 D3D 垫片 + 等图库索引 ──
        _status = "初始化渲染桥…";
        RenderBridge.CalibrateLog = s => L("[cal] " + s);
        string err = RenderBridge.Init(SW, SH, PortableInit.RootPath);
        L("RenderBridge.Init err=" + (err ?? "null") + "  Ready=" + RenderBridge.Ready +
          "  图库 " + RenderBridge.LibraryProgress + "/" + RenderBridge.LibraryCount);
        if (err != null) { _status = "渲染桥失败: " + err; yield break; }
        yield return null;

        // ── 5) 挂一个最小用户（镜头跟着他）──
        err = RenderBridge.AttachUser(_map, _cell.x, _cell.y, _dir);
        L("AttachUser err=" + (err ?? "null") + "  (" + _cell.x + "," + _cell.y + ") dir=" + _dir);
        if (err != null) { _status = "挂用户失败: " + err; yield break; }

        // ── 5.5) 建 Unity 纹理（后面每一帧都要往这里贴）──
        // BGRA32：垫片输出就是 BGRA，用这个格式上传时不必再做逐像素 B/R 交换。
        // ★ 宽屏改造：纹理尺寸 = 世界画布（不是界面 1024x768）。
        // 变量名不能叫 ch：本方法前面（语言包统计那段）已有 `foreach (char ch in kv.Value)`，
        // C# 不允许外层作用域再声明同名局部 ⇒ CS0136。改名 texW/texH。
        int texW = Bridge.CanvasWidth, texH = Bridge.CanvasHeight;
        _tex = new Texture2D(texW, texH, TextureFormat.BGRA32, false);
        _tex.filterMode = FilterMode.Point;
        _tex.wrapMode = TextureWrapMode.Clamp;
        _raw = new byte[texW * texH * 4];
        L("画面纹理 " + texW + "x" + texH + "  界面层左边界=" + Bridge.CanvasLeft);

        // ── 5.9) 等内嵌服务端真能连上再发登录包 ──
        // Envir.Start() 是异步的：它先起后台线程，再在**那个线程里**加载
        // Server.MirDB + 480 张地图 + Envir 全部脚本，最后才 TcpListener.Start()。
        // 电脑上是 5 秒，模拟器上会更慢。这期间端口根本没人监听，发登录包必然
        // Connect 失败 —— 所以必须先等，不能让客户端抢跑。
        //
        // 上限 180 秒：低端机 + 冷启动 + IL2CPP 下确实可能超过一分钟。
        // 超时不崩：下面走原来的单机演示分支，并把原因写在屏幕上。
        _status = "等待内嵌服务端就绪…";
        var srvWait = Stopwatch.StartNew();
        while (!EmbeddedServer.Running &&
               string.IsNullOrEmpty(EmbeddedServer.LastError) &&
               srvWait.ElapsedMilliseconds < 180000)
        {
            _status = "等待内嵌服务端就绪… 地图 " + EmbeddedServer.MapCount + " 张  "
                      + srvWait.ElapsedMilliseconds / 1000 + "s";
            yield return null;
        }
        L("内嵌服务端等待结束：" + srvWait.ElapsedMilliseconds + "ms  Running=" + EmbeddedServer.Running
          + "  地图=" + EmbeddedServer.MapCount + "  Port=" + EmbeddedServer.Port
          + (string.IsNullOrEmpty(EmbeddedServer.LastError) ? "" : "  错误=" + EmbeddedServer.LastError));

        // ── 6) 起网络：自动登录 → 直接落到角色选择列表 ──
        // 用户要求：点开 APP 直接进角色列表，不要登录界面、不要选服。
        // NetDriver 把「连接 → 版本号 → 登录（缺账号就建）→ 角色列表（缺角色就建）」
        // 全部自动走完，然后停在原版 SelectScene 上。
        _status = "连接服务器…";
        NetDriver.Log = s => L("[net] " + s);
        NetDriver.SetDeviceId(SystemInfo.deviceUniqueIdentifier);

        // ── 联机模式：单机 / 开服 / 加入别人 ──
        // 用户 2026-11-12 原话：「先是选择，就是单机游玩还是网络游玩……单机游玩的话，
        //   就是自己用自己的服务器，不开放网络接口。」
        // 三档的实际含义：
        //   单机  Envir.LocalOnly = true   只有本机回环能连进来（别人连不上也搜不到）
        //   开服  Envir.LocalOnly = false  局域网任何一台手机都能连
        //   加入  连别人，同时把自己的服务端也关成单机（避免两边互相连成一团）
        // LoadSaved() 返回 true = 上次是「加入」，要把地址指回上次那台机器。
        P2NetMode.Log = L;
        bool joinLastTime = P2NetMode.LoadSaved();
        try { P2NetMode.ApplyToServer(); } catch (System.Exception e) { L("★ 应用联机模式失败 " + e.Message); }

        // 地址：默认本机回环、端口取服务端**实际**监听的那个（不是硬编码 7000），
        // 这样即便 Setup.ini 里改了端口也不会两边错位。
        // 上次是「加入游戏」→ 直接连上次那台，玩家不用每次重新输。
        string connectIp = joinLastTime ? P2NetMode.HostAddress : "127.0.0.1";
        int connectPort = joinLastTime ? P2NetMode.HostPort
                                       : (EmbeddedServer.Port > 0 ? EmbeddedServer.Port : MirPort());
        L("[联机] 本次连接目标 " + connectIp + ":" + connectPort + "  模式=" + P2NetMode.ModeName);
        NetDriver.Configure(connectIp, connectPort, null, null);
        NetDriver.TakeOverScene();
        NetDriver.Start();

        // 退出 → 真正结束进程。
        //
        // 原版所有「退出」都收敛到 Program.Form.Close()（WinForms 关窗，进程随之结束）。
        // 移植层的 Form 是垫片，Close() 以前是**空函数**，于是六处退出逻辑全部失效：
        //   角色选择界面的「退出」、断线错误框的「取消」、连接中对话框的「取消」、
        //   登录界面右上角的叉、退出游戏确认框的「是」、带 close 的消息框「确定」
        // ⇒ 玩家点了没反应，看着像卡死。
        // 现在 Form.Close() 转调 Client.Program.Exit()，而 Exit() 回头调这个钩子。
        // 安卓上 Application.Quit() 只是请求结束，系统会正常走 OnDestroy，不会硬杀。
        System.Action quitApp = () =>
        {
            L("[退出] → 账号库落盘 + Application.Quit()");
            EmbeddedServer.FlushAccounts();
            Application.Quit();
        };
        Mir176Hud.QuitApp = quitApp;
        Client.Program.QuitHandler = quitApp;

        L("NetDriver 启动 目标=" + MirHost() + ":" + MirPort());

        // 等它连上或失败，最多 20 秒（连不上就退回单机演示，不让用户干等）
        var netWait = Stopwatch.StartNew();
        while (NetDriver.Current != NetDriver.Phase.Selecting &&
               NetDriver.Current != NetDriver.Phase.Failed &&
               netWait.ElapsedMilliseconds < 20000)
        {
            NetDriver.Step(_clock.ElapsedMilliseconds);
            yield return null;
        }
        L("网络阶段=" + NetDriver.Current + "  耗时 " + netWait.ElapsedMilliseconds + "ms" +
          (string.IsNullOrEmpty(NetDriver.LastError) ? "" : "  错误=" + NetDriver.LastError));

        _networked = NetDriver.Current == NetDriver.Phase.Selecting ||
                     NetDriver.Current == NetDriver.Phase.InGame;

        if (!_networked)
        {
            // 连不上服务端 → 单机演示模式（地图 + 小人，保证 APP 打开有画面）
            L("★ 没连上服务端，退回单机演示模式");
            _scene = new GameScene();
            _scene.Size = new System.Drawing.Size(SW, SH);
            _scene.InsertControl(0, _map);
            _scene.MapControl = _map;
            NetDriverAutoScene = _scene;
        }
        // 注意：这里的判定**不能**定死。开机那一刻服务端可能还在起，
        // 而 NetDriver 会自己每 4 秒重连；连上之后必须能从单机演示切回联机界面，
        // 否则玩家只看到一张不会动的地图。切换在 Update 里每帧检查（见 TryGoOnline）。

        // 画第一帧，落盘供逐像素比对
        _status = "渲染第一帧…";
        var rsw = Stopwatch.StartNew();
        RenderFrame();
        _drawMs = (int)rsw.ElapsedMilliseconds;
        _renderCount = 1;
        L("首帧渲染 " + _drawMs + "ms  mapTexture=" + (RenderBridge.mapTexture != null ? "有" : "null"));
        DumpFrame("p2_frame.bgra");

        var usw = Stopwatch.StartNew();
        Upload();
        _uploadMs = (int)usw.ElapsedMilliseconds;
        L("纹理上传 " + _uploadMs + "ms  " + Bridge.CanvasWidth + "x" + Bridge.CanvasHeight);

        // 起点必须可走
        if (_map != null && !Bridge.CanWalk(_map, _cell.x, _cell.y)) FindWalkableNear(ref _cell);

        _ready = true;

        // ★ 故意置 0：开机协程跑到这里可能已经隔了几十秒（等服务端、等图库），
        //   期间屏幕比例若变过，画布是拿**旧**的 Screen 尺寸算的。
        //   置 0 让第一次 Update 必定校一次；尺寸本来就一致时 RefitCanvas 返回 null，零副作用。
        _lastSw = 0;
        _lastSh = 0;
        _sizeHooked = true;
        _bootHoldUntil = Time.realtimeSinceStartup + BootHold;
        _status = string.Format("{0} · 画面{1}x{2} 界面{3}x{4} · 图库 {5}/{6} · 首帧 {7}ms · 上传 {8}ms",
            _networked ? "在线" : "单机演示",
            Bridge.CanvasWidth, Bridge.CanvasHeight, SW, SH,
            RenderBridge.LibraryProgress, RenderBridge.LibraryCount,
            _drawMs, _uploadMs);
        L("就绪 " + _status);
    }

    /// <summary>服务端地址：优先读 Mir2Config.ini，没有就用 adb reverse 的 127.0.0.1:7000。</summary>
    static string MirHost() { return NetDriver.Host; }
    static int MirPort() { return NetDriver.Port; }

    /// <summary>
    /// 单机演示 → 联机界面的追赶切换。
    /// 开机时服务端还没起是常态（电脑刚开机、adb reverse 还没建），
    /// NetDriver 会一直重连；一旦它连上并拿到角色列表，这里就把演示场景让开，
    /// 改画原版的 SelectScene/GameScene。用户要的是「点开就在角色列表」，
    /// 不能因为开机早了 10 秒就永远看不到。
    /// </summary>
    void TryGoOnline()
    {
        if (NetDriver.Current != NetDriver.Phase.Selecting &&
            NetDriver.Current != NetDriver.Phase.InGame) return;

        var live = Client.MirControls.MirScene.ActiveScene;
        // ActiveScene 还是那个只转发不画画的壳 ⇒ 说明原版场景还没换上来，再等等
        if (live == null || live is NetRelayScene && ((NetRelayScene)live).Target == null) return;

        _networked = true;
        L("★ 重连成功，切到联机界面 → " + live.GetType().Name);
        _bootHoldUntil = 0f;
    }

    /// <summary>
    /// 画一帧：直接画 ActiveScene。
    /// 登录期的 ActiveScene 是 NetRelayScene —— 它自己的 Draw() 会转发给 Target，
    /// 所以这里不需要特判，一路画下去就是原版场景。
    /// </summary>
    void RenderFrame()
    {
        var scene = Client.MirControls.MirScene.ActiveScene;
        RenderBridge.RenderWithUI(scene ?? NetDriverAutoScene);
    }

    // ────────────────────────────────────────────────────────────
    //  触摸 → 原版控件树
    // ────────────────────────────────────────────────────────────

    // ────────────────────────────────────────────────────────────
    //  按比例居中（letterbox）
    // ────────────────────────────────────────────────────────────

    // ★ 2026-11-11 宽屏改造：画面不再是 4:3，而是「设备什么比例，画布就是什么比例」。
    //   画布尺寸由 Bridge.SetCanvasSize(设备宽, 设备高) 算好（高度恒 768，宽度按比例），
    //   于是宽屏手机（2616x1140）下画布 = 1762x768，和屏幕同比例，铺满整屏**零黑边**。
    //
    //   只有在极端竖屏 / 超宽屏被 SetCanvasSize 夹到 [1024, 2048] 时，
    //   画布比例才会和设备比例有一点出入，这时才真的需要等比缩放进视口里留边。
    //   两种情况用同一段代码处理：算出来的视口永远不变形。
    /// <summary>
    /// 屏幕比例变了（折叠屏开合 / 分屏 / 旋转）→ 重算画布并作废所有派生品。
    ///
    /// 为什么要这一步：Bridge.SetCanvasSize() 只在启动协程里调一次，画布尺寸一确定
    /// 就写死进 MapControl 的视野参数、控件 Size、天气粒子、地面/光照纹理里。
    /// 之后屏幕比例再变，画布还是旧的 ⇒ Viewport() 只能等比缩放 + 留空带。
    ///
    /// 分工：Bridge.RefitCanvas 管 DLL 那侧（Settings / 视野 / 控件 / D3D 垫片），
    ///       这里管 Unity 这侧（上屏纹理 / 原始缓冲 / 黑边底衬 / 摇杆布局缓存）。
    /// </summary>
    void RefitToScreen()
    {
        string msg;
        try { msg = Bridge.RefitCanvas(Screen.width, Screen.height); }
        catch (System.Exception e) { L("★画面重排失败 " + e.GetType().Name + " " + e.Message); return; }

        if (string.IsNullOrEmpty(msg)) return;   // 尺寸没变，或新比例换算回来还是同一个画布
        L("★" + msg);

        // Unity 侧：上屏纹理与原始缓冲必须按新画布重建。
        // 不重建的话 Upload() 里 `w != Bridge.CanvasWidth` 那句守卫会整帧跳过 ⇒ 画面冻住。
        int cw = Bridge.CanvasWidth, ch = Bridge.CanvasHeight;
        if (_tex == null || _tex.width != cw || _tex.height != ch)
        {
            if (_tex != null) Destroy(_tex);
            _tex = new Texture2D(cw, ch, TextureFormat.BGRA32, false);
            _tex.filterMode = FilterMode.Point;
            _tex.wrapMode = TextureWrapMode.Clamp;
        }
        if (_raw == null || _raw.Length != cw * ch * 4) _raw = new byte[cw * ch * 4];

        // 黑边底衬只在画布比例和设备比例对不上时才用得上（极端竖屏/超宽屏），
        // 但它按整屏高度分 96 带、按行取色，屏高变了同样要重来。
        DestroyAmbient();

        L("★重排完成 画布 " + cw + "x" + ch + "  视口 " + Viewport());
    }

    /// <summary>丢掉黑边底衬的两张纹理（尺寸跟屏幕绑死，换比例后必须重建）。</summary>
    void DestroyAmbient()
    {
        if (_ambL != null) { Destroy(_ambL); _ambL = null; }
        if (_ambR != null) { Destroy(_ambR); _ambR = null; }
        _ambLpx = null;
        _ambRpx = null;
        _ambLr = _ambLg = _ambLb = null;
        _ambRr = _ambRg = _ambRb = null;
    }

    static Rect Viewport()
    {
        float sw = Mathf.Max(1, Screen.width);
        float sh = Mathf.Max(1, Screen.height);
        float cw = Mathf.Max(1, Bridge.CanvasWidth);
        float ch = Mathf.Max(1, Bridge.CanvasHeight);

        float vw = sw, vh = sw * ch / cw;              // 先按宽度铺满，算高度
        if (vh > sh) { vh = sh; vw = sh * cw / ch; }   // 高度超了 ⇒ 改按高度铺满
        return new Rect((sw - vw) * 0.5f, (sh - vh) * 0.5f, vw, vh);
    }

    /// <summary>Unity 屏幕坐标 → **画布坐标**（0..CanvasWidth, 0..CanvasHeight，Y 向下）。
    /// ★ 宽屏改造后这里是画布坐标，不是界面坐标 —— InputBridge 会自己减掉 CanvasLeft
    ///   得到界面坐标再写 CMain.MPoint。</summary>
    static void ToLogic(Vector3 screenPos, out int lx, out int ly)
    {
        var vp = Viewport();
        int cw = Bridge.CanvasWidth, ch = Bridge.CanvasHeight;
        // 先把设备坐标减去视口原点，再按视口尺寸缩放到画布尺寸。
        float nx = (screenPos.x - vp.x) / Mathf.Max(1f, vp.width);
        float ny = (screenPos.y - vp.y) / Mathf.Max(1f, vp.height);
        lx = Mathf.RoundToInt(nx * cw);
        ly = Mathf.RoundToInt((1f - ny) * ch);
        lx = Mathf.Clamp(lx, 0, cw - 1);
        ly = Mathf.Clamp(ly, 0, ch - 1);
    }

    /// <summary>
    /// 把触摸转发给原版场景。顺序照抄原版 WinForms 一次点击的次序
    /// （CMain.cs L273 MouseDown → L300 MouseClick → L257 MouseUp）：
    /// 手指落下就把 Move → Down → Click → Up 这一整套走完。
    /// 原版控件（按钮、列表、输入框）全靠 ActiveControl / MouseControl 这两个静态字段定位，
    /// 而它们由 OnMouseMove 里的 IsMouseOver(CMain.MPoint) 决定 —— 所以 MPoint 必须在每次转发前更新。
    /// </summary>
    void HandleSceneTap(Client.MirControls.MirScene scene)
    {
        // 登录期 ActiveScene 是转发壳，事件必须发给壳背后的真场景（SelectScene / GameScene），
        // 只有那个才有控件树和 ActiveControl。
        scene = InputBridge.Resolve(scene);
        if (scene == null) return;

        bool down = false, up = false;
        Vector3 pos = Vector3.zero;

        if (Input.touchCount > 0)
        {
            var tc = Input.GetTouch(0);
            if (tc.phase == TouchPhase.Began) { down = true; pos = tc.position; }
            else if (tc.phase == TouchPhase.Ended || tc.phase == TouchPhase.Canceled) { up = true; pos = tc.position; }
            else if (tc.phase == TouchPhase.Moved || tc.phase == TouchPhase.Stationary)
            {
                ToLogic(tc.position, out int mx, out int my);
                if (mx != _lastTouch.x || my != _lastTouch.y)
                {
                    _lastTouch = new Vector2Int(mx, my);
                    InputBridge.MoveTo(scene, mx, my);
                }
            }
        }

        if (Input.GetMouseButtonDown(0)) { down = true; pos = Input.mousePosition; }
        if (Input.GetMouseButtonUp(0)) { up = true; pos = Input.mousePosition; }

        if (down)
        {
            ToLogic(pos, out int lx, out int ly);
            _mouseDown = true;
            _lastTouch = new Vector2Int(lx, ly);
            string before = scene.GetType().Name;

            // ★ 进游戏后必须「按住」而不是「点一下」（2026-10-29 实测）：
            //   GameScene.CheckInput() 每周期读 MapButtons 决定走不走，
            //   而 MapButtons 由 MouseDown 置位、MouseUp 清位。
            //   ClickAt 把 Down+Up 塞在同一帧 ⇒ CheckInput 永远读到 None ⇒ 按住地面走不动。
            //   场景类（选角/登录）仍用 ClickAt，那里要的正是完整一次点击。
            if (scene is Client.MirScenes.GameScene || scene.GetType().Name == "GameScene")
            {
                _pressScene = scene;
                _pressPos = new Vector2Int(lx, ly);
                InputBridge.PressAt(scene, lx, ly);
                L("按下 (" + lx + "," + ly + ") → " + before);
            }
            else
            {
                // ClickAt 内部是 Move → Down → Click → Up，与 CMain 三个处理器的先后同序
                InputBridge.ClickAt(scene, lx, ly);
                L("点 (" + lx + "," + ly + ") → " + before);
            }
        }
        else if (up)
        {
            _mouseDown = false;
            if (_pressScene != null)
            {
                ToLogic(pos, out int lx, out int ly);
                InputBridge.ReleaseAt(_pressScene, lx, ly);
                L("松开 (" + lx + "," + ly + ")");
                _pressScene = null;
            }
        }
    }

    // 游戏里按住的场景/位置：抬手时才真正 Release。
    Client.MirControls.MirScene _pressScene;
    Vector2Int _pressPos;

    Vector2Int _lastTouch = new Vector2Int(-1, -1);

    /// <summary>状态栏文字：把网络阶段翻译成人话。</summary>
    string NetStatusText(Client.MirControls.MirScene scene)
    {
        string sceneName = scene == null ? "无场景" : scene.GetType().Name;

        switch (NetDriver.Current)
        {
            case NetDriver.Phase.Selecting:
                return "角色选择 · " + NetDriver.AccountID + " · 点角色进游戏 · " + sceneName;
            case NetDriver.Phase.InGame:
                return "游戏中 · " + NetDriver.CharacterName + " · " + sceneName
                       + " · " + NetDriver.SceneObjectText();
            case NetDriver.Phase.Failed:
                return "连接失败：" + NetDriver.LastError;
            case NetDriver.Phase.CreatingAccount:
                return "首次启动，正在建号 " + NetDriver.AccountID;
            case NetDriver.Phase.CreatingChar:
                return "正在建角色 " + NetDriver.CharacterName;
            case NetDriver.Phase.Connecting:
                return "连接中 " + MirHost() + ":" + MirPort();
            case NetDriver.Phase.Logging:
                return "登录中 " + NetDriver.AccountID;
            case NetDriver.Phase.VersionSent:
                return "版本校验中";
            default:
                return "空闲 · " + sceneName;
        }
    }

    // ────────────────────────────────────────────────────────────
    //  地图解包
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 StreamingAssets/Data/Map/maps.pak 里的 577 张 .map 解到 ./Map/。
    ///
    /// 为什么要自己做：真源码的 MapReader 只认磁盘路径（Client/MirObjects/MapCode.cs:132），
    /// 而 Android 的 StreamingAssets 是 APK 里的 jar 内条目，没有真实路径。
    /// 所以必须先把字节取出来落成真文件。且必须**全部**落，
    /// 因为换图时用到哪个文件完全由服务器下发决定（见调用处注释）。
    ///
    /// 幂等：已存在且大小一致的跳过，第二次启动只做 577 次 stat，约几十毫秒。
    /// </summary>
    IEnumerator UnpackMaps()
    {
        string mapDir = Path.Combine(PortableInit.RootPath, "Map");
        string pakUrl = Application.streamingAssetsPath + "/Data/Map/maps.pak";
        var total = Stopwatch.StartNew();

        // ── 取压缩包字节 ──
        byte[] pak = null;
        bool inApk = pakUrl.StartsWith("jar:") || pakUrl.StartsWith("http");
        if (!inApk)
        {
            if (File.Exists(pakUrl)) pak = File.ReadAllBytes(pakUrl);
        }
        else
        {
            using (var req = UnityEngine.Networking.UnityWebRequest.Get(pakUrl))
            {
                req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                yield return req.SendWebRequest();
                if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    pak = req.downloadHandler.data;
                else
                    L("★地图包下载失败: " + req.error);
            }
        }

        if (pak == null || pak.Length < 16)
        {
            L("★地图包缺失或过小 " + pakUrl + " len=" + (pak == null ? -1 : pak.Length));
            yield break;
        }

        if (!Directory.Exists(mapDir)) Directory.CreateDirectory(mapDir);

        // ── 解析并解包 ──
        // 格式见 C:\ai\tmp\run_b\pack_maps.py 头部：
        //   "MIRMAPS1" | int32 count | count × ( u16 nameLen, name, i32 rawLen, i32 zipLen, data )
        int n = 0, written = 0, existed = 0, failed = 0;
        try
        {
            int p = 0;
            var magic = System.Text.Encoding.ASCII.GetString(pak, 0, 8);
            if (magic != "MIRMAPS1") { L("★地图包标识不对: " + magic); yield break; }
            p = 8;
            n = BitConverter.ToInt32(pak, p); p += 4;

            for (int i = 0; i < n; i++)
            {
                int nameLen = BitConverter.ToUInt16(pak, p); p += 2;
                string name = System.Text.Encoding.UTF8.GetString(pak, p, nameLen); p += nameLen;
                int rawLen = BitConverter.ToInt32(pak, p); p += 4;
                int zipLen = BitConverter.ToInt32(pak, p); p += 4;

                string dst = Path.Combine(mapDir, name);

                // 已解过就不重复解（换 map 文件时大小会变，所以按大小比）
                if (File.Exists(dst))
                {
                    var fi = new FileInfo(dst);
                    if (fi.Length == rawLen) { existed++; p += zipLen; continue; }
                }

                try
                {
                    byte[] raw = Inflate(pak, p, zipLen, rawLen);
                    if (raw == null || raw.Length != rawLen)
                    {
                        failed++;
                        L("★解压失败 " + name + " 期望 " + rawLen + " 实得 " +
                          (raw == null ? -1 : raw.Length));
                    }
                    else
                    {
                        File.WriteAllBytes(dst, raw);
                        written++;
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    L("★解压异常 " + name + " : " + e.Message);
                }
                p += zipLen;

                // 每帧最多解 8 张，避免长任务把主线程卡死（首次约 577/8=72 帧）
                if ((i & 7) == 7) yield return null;
            }
        }
        finally
        {
            // 不在这里吞异常：上面的 try 只包住单条记录的解析，
            // 结构性错误（越界等）会冒到外面，由调用方按 mapOk=false 处理。
        }

        L("地图解包：共 " + n + " 张，新解 " + written + "，已存在 " + existed +
          "，失败 " + failed + "，用时 " + total.ElapsedMilliseconds + "ms" +
          "  目录 " + mapDir);
    }

    // ────────────────────────────────────────────────────────────
    //  服务端数据解包（手机自己开服用）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 StreamingAssets/ServerData/ServerData.pak 解到 persistentDataPath。
    ///
    /// 为什么需要：Crystal 服务端全部用相对路径（./Envir、./Configs、./Server.MirDB、
    /// ./Localization），而 Android 上 StreamingAssets 是 APK 里的条目，没有真实路径。
    /// 所以必须先落成真文件——和地图那套一模一样。
    ///
    /// 包内容（788 个文件，原始 638 KB → 压缩后 0.24 MB，打包脚本
    /// C:\ai\tmp\srvpak\pack_server_data.py，回读自检 788/788 全过）：
    ///   Server.MirDB    数据库总入口（地图/物品/怪物/NPC/任务）
    ///   Envir/          NPC 脚本、掉落表、商店 —— 1:1 复刻的内容全在这里
    ///   Configs/        经验表、职业技能、基础属性、刷新倍率、Setup.ini
    ///   Localization/   服务端中文（ServerTextMap）
    ///
    /// 格式：MIRSVR01 | i32 fileCount | i32 indexLen | index | body
    ///   index 每项：u16 nameLen | name(utf8) | i32 rawLen | i32 compLen
    ///   body 逐条 **raw deflate**（无 zlib 头）—— 和 maps.pak 同一种压缩，
    ///   Unity 的 DeflateStream 只认这个格式。
    ///
    /// 幂等：已存在且大小一致的跳过。但服务端会自己改 Server.MirDB（存角色/账号），
    /// 所以**只在文件缺失时补齐**，绝不覆盖 —— 否则玩家每次开 APP 角色就没了。
    /// </summary>
    IEnumerator UnpackServerData()
    {
        string root = PortableInit.RootPath;
        string pakUrl = Application.streamingAssetsPath + "/ServerData/ServerData.pak";
        var total = Stopwatch.StartNew();

        byte[] pak = null;
        bool inApk = pakUrl.StartsWith("jar:") || pakUrl.StartsWith("http");
        if (!inApk)
        {
            if (File.Exists(pakUrl)) pak = File.ReadAllBytes(pakUrl);
        }
        else
        {
            using (var req = UnityEngine.Networking.UnityWebRequest.Get(pakUrl))
            {
                req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                yield return req.SendWebRequest();
                if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    pak = req.downloadHandler.data;
                else
                    L("★服务端数据包下载失败: " + req.error);
            }
        }

        if (pak == null || pak.Length < 16)
        {
            L("★服务端数据包缺失或过小 " + pakUrl + " len=" + (pak == null ? -1 : pak.Length));
            yield break;
        }

        int n = 0, written = 0, existed = 0, failed = 0;
        string srvVerPath = null, srvWantVer = null;
        try
        {
            var magic = System.Text.Encoding.ASCII.GetString(pak, 0, 8);
            if (magic != "MIRSVR01") { L("★服务端包标识不对: " + magic); yield break; }

            n = BitConverter.ToInt32(pak, 8);
            int indexLen = BitConverter.ToInt32(pak, 12);
            int p = 16;

            int n2 = BitConverter.ToInt32(pak, p); p += 4;
            if (n2 != n) { L("★服务端包索引不一致 " + n + " vs " + n2); yield break; }

            // 先把索引全部读出来（名称/原始长度/压缩长度）
            var names = new string[n];
            var rawLens = new int[n];
            var compLens = new int[n];
            for (int i = 0; i < n; i++)
            {
                int nameLen = BitConverter.ToUInt16(pak, p); p += 2;
                names[i] = System.Text.Encoding.UTF8.GetString(pak, p, nameLen); p += nameLen;
                rawLens[i] = BitConverter.ToInt32(pak, p); p += 4;
                compLens[i] = BitConverter.ToInt32(pak, p); p += 4;
            }

            int bodyPos = 16 + indexLen;

            // ★ 内容版本闸门（2026-11-04 加，这是「NPC 脚本还是旧魔改版」的真根因）。
            //
            //   原来的逻辑只有一句「File.Exists 就跳过」，于是：APK 里的 ServerData.pak
            //   就算换成 1.76 正版，设备上**已经存在**的那 790 个文件也永远不会被替换。
            //   实测后果（2026-11-04）：精武馆脚本还是含 LionRoar/狂风斩/双龙斩 等 11 个
            //   非 1.76 技能的旧魔改版，高级道士/高级魔法师是两个 98/107 字节的空壳。
            //   日志写「已存在 790，新解 0」，看起来一切正常。
            //
            //   现在用**内容指纹**做闸门：pak 的字节长度 + 索引里每个文件的 (名字, 原始长度)
            //   算一个 FNV-1a 64 位哈希，落到 .srvpak_version。指纹变了 ⇒ 数据文件全部重解。
            //
            //   ★ 唯独 Server.MirDB / Server.MirADB 例外 —— 它们是**活库**，服务端会往里
            //     写角色和账号，任何时候都不能覆盖，否则玩家角色全没。这两个仍然只补缺。
            ulong pakFp = 1469598103934665603UL;
            {
                foreach (byte b in BitConverter.GetBytes(pak.Length))
                { pakFp ^= b; pakFp *= 1099511628211UL; }
                for (int i = 0; i < n; i++)
                {
                    foreach (char c in names[i])
                    { pakFp ^= (byte)c; pakFp *= 1099511628211UL; pakFp ^= (byte)(c >> 8); pakFp *= 1099511628211UL; }
                    foreach (byte b in BitConverter.GetBytes(rawLens[i]))
                    { pakFp ^= b; pakFp *= 1099511628211UL; }
                }
            }
            string verPath = Path.Combine(root, ".srvpak_version");
            string wantVer = "v2:" + pakFp.ToString("x16");
            bool forceAll = true;
            try
            {
                if (File.Exists(verPath) && File.ReadAllText(verPath).Trim() == wantVer)
                    forceAll = false;
            }
            catch { }
            srvVerPath = verPath; srvWantVer = wantVer;
            if (forceAll) L("服务端数据版本变更 → 全部数据文件重解（Server.MirDB/MirADB 仍只补缺）");

            for (int i = 0; i < n; i++)
            {
                string rel = names[i].Replace('/', Path.DirectorySeparatorChar);
                string dst = Path.Combine(root, rel);

                // 活库永远只补缺，绝不覆盖
                bool liveDb = names[i] == "Server.MirDB" || names[i] == "Server.MirADB";

                if (File.Exists(dst) && (liveDb || !forceAll))
                {
                    // ★ 存在就不动。Server.MirDB / Server.MirADB 是**活库**，
                    //   服务端会往里写角色和账号；覆盖 = 玩家角色全没。
                    //   其余文件由上面的内容指纹决定是否重解（forceAll=true 就往下走）。
                    existed++;
                    bodyPos += compLens[i];
                    continue;
                }

                try
                {
                    string dir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    byte[] raw = Inflate(pak, bodyPos, compLens[i], rawLens[i]);
                    if (raw == null || raw.Length != rawLens[i])
                    {
                        failed++;
                        L("★服务端解压失败 " + names[i] + " 期望 " + rawLens[i] +
                          " 实得 " + (raw == null ? -1 : raw.Length));
                    }
                    else
                    {
                        // ★ 2026-11-09：设备上的文件若是 adb push 推上去的（属主 shell），
                        //   App 用户 u0_a193 改不了它 —— 即使权限位是 rw-rw-rw-（FUSE 认属主）。
                        //   先试删；删不掉但内容长度已经一致，就当「已存在」，不算失败。
                        bool okWrite = false;
                        try
                        {
                            File.WriteAllBytes(dst, raw);
                            okWrite = true;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            try { File.Delete(dst); File.WriteAllBytes(dst, raw); okWrite = true; }
                            catch { }
                        }

                        if (okWrite) written++;
                        else
                        {
                            long have = -1;
                            try { have = new FileInfo(dst).Length; } catch { }
                            if (have == raw.Length) { existed++; }
                            else
                            {
                                failed++;
                                L("★服务端写入被拒 " + names[i] + " 期望 " + raw.Length + " 现有 " + have +
                                  "（文件属主不是本 App，需删掉后重启）");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    L("★服务端解包异常 " + names[i] + " : " + e.GetType().Name + " " + e.Message);
                }
                bodyPos += compLens[i];

                // 每帧最多解 8 个，别把主线程卡死（788 个 ≈ 100 帧）
                if ((i & 7) == 7) yield return null;
            }
        }
        finally { }

        L("服务端数据解包：共 " + n + " 个，新解 " + written + "，已存在 " + existed +
          "，失败 " + failed + "，用时 " + total.ElapsedMilliseconds + "ms");
        if (failed == 0 && srvVerPath != null)
        {
            try { File.WriteAllText(srvVerPath, srvWantVer); }
            catch (Exception e) { L("★服务端数据版本落盘失败 " + e.GetType().Name); }
        }
        L("服务端数据版本 " + srvWantVer);
    }

    /// <summary>
    /// 把 StreamingAssets/SoundData/SoundData.pak 解到 persistentDataPath/Sound/。
    ///
    /// 为什么必须有（2026-11-13 加）：
    ///   以前 Sound/ 那 670 个文件只靠 adb push 推到模拟器上，**完全没进 APK**。
    ///   任何一次重装/换机都会把 Sound/ 清空，游戏立刻全程静音，而日志一切正常
    ///   （SoundList.lst 载入 0 条只是 LoadSoundList 返回 0，没人当回事）。
    ///   现在打包进 APK，首次启动自动铺出来。
    ///
    /// 包内容：669 个 wav（22050Hz 单声道 16bit PCM）+ SoundList.lst
    ///   原始 124.8MB → 转码 54.6MB → raw-deflate 后约 43MB。
    ///   为什么是 16bit PCM 而不是 ADPCM/MP3：Shim/NAudioShim.cs 的 DecodeWav
    ///   只实现 fmtTag 1(PCM)/3(IEEE float)，ADPCM 会解成空数组 ⇒ 静音。
    ///
    /// 格式：MIRSOUND | i32 fileCount | i32 indexLen | index | body
    ///   index 每项：u16 nameLen | name(utf8) | i32 rawLen | i32 compLen
    ///   body 逐条 raw deflate（和 ServerData.pak / maps.pak 同一种压缩）
    ///
    /// 幂等：和 ServerData 一样用**内容指纹**做闸门（包长 + 每个文件的 名字/原始长度），
    /// 指纹变了才全部重解；否则已存在的跳过。Sound/ 是纯只读素材，
    /// 没有「活库」问题，所以不需要 Server.MirDB 那种只补缺的特例。
    /// </summary>
    IEnumerator UnpackSoundData()
    {
        string root = PortableInit.RootPath;
        string pakUrl = Application.streamingAssetsPath + "/SoundData/SoundData.pak";
        var total = Stopwatch.StartNew();

        byte[] pak = null;
        bool inApk = pakUrl.StartsWith("jar:") || pakUrl.StartsWith("http");
        if (!inApk)
        {
            if (File.Exists(pakUrl)) pak = File.ReadAllBytes(pakUrl);
        }
        else
        {
            using (var req = UnityEngine.Networking.UnityWebRequest.Get(pakUrl))
            {
                req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                yield return req.SendWebRequest();
                if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    pak = req.downloadHandler.data;
                else
                    L("★音效数据包下载失败: " + req.error);
            }
        }

        if (pak == null || pak.Length < 16)
        {
            // 不是致命错误：Sound/ 可能已经由 adb push 铺好了，游戏照样有声音。
            L("音效数据包缺失 len=" + (pak == null ? -1 : pak.Length) + "  " + pakUrl);
            yield break;
        }

        int n = 0, written = 0, existed = 0, failed = 0;
        string verPath = Path.Combine(root, ".sndpak_version");
        string wantVer = null;
        try
        {
            string magic = System.Text.Encoding.ASCII.GetString(pak, 0, 8);
            if (magic != "MIRSOUND") { L("★音效包标识不对: " + magic); yield break; }

            n = BitConverter.ToInt32(pak, 8);
            int indexLen = BitConverter.ToInt32(pak, 12);
            int p = 16;

            int n2 = BitConverter.ToInt32(pak, p); p += 4;
            if (n2 != n) { L("★音效包索引不一致 " + n + " vs " + n2); yield break; }

            var names = new string[n];
            var rawLens = new int[n];
            var compLens = new int[n];
            for (int i = 0; i < n; i++)
            {
                int nameLen = BitConverter.ToUInt16(pak, p); p += 2;
                names[i] = System.Text.Encoding.UTF8.GetString(pak, p, nameLen); p += nameLen;
                rawLens[i] = BitConverter.ToInt32(pak, p); p += 4;
                compLens[i] = BitConverter.ToInt32(pak, p); p += 4;
            }

            int bodyPos = 16 + indexLen;

            // 内容指纹（FNV-1a 64）：包长 + 每个 (文件名, 原始长度)
            ulong fp = 1469598103934665603UL;
            foreach (byte b in BitConverter.GetBytes(pak.Length))
            { fp ^= b; fp *= 1099511628211UL; }
            for (int i = 0; i < n; i++)
            {
                foreach (char c in names[i])
                { fp ^= (byte)c; fp *= 1099511628211UL; fp ^= (byte)(c >> 8); fp *= 1099511628211UL; }
                foreach (byte b in BitConverter.GetBytes(rawLens[i]))
                { fp ^= b; fp *= 1099511628211UL; }
            }
            wantVer = "v1:" + fp.ToString("x16");
            bool forceAll = true;
            try
            {
                if (File.Exists(verPath) && File.ReadAllText(verPath).Trim() == wantVer)
                    forceAll = false;
            }
            catch { }
            if (forceAll) L("音效数据版本变更 → 全部音效重解（共 " + n + " 个）");

            int idx = 0;
            for (int i = 0; i < n; i++)
            {
                string rel = names[i].Replace('/', Path.DirectorySeparatorChar);
                string dst = Path.Combine(root, "Sound", rel);

                if (!forceAll && File.Exists(dst) && new FileInfo(dst).Length == rawLens[i])
                {
                    existed++;
                    bodyPos += compLens[i];
                    idx++;
                    if ((idx & 15) == 15) yield return null;
                    continue;
                }

                try
                {
                    string dir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    byte[] raw = Inflate(pak, bodyPos, compLens[i], rawLens[i]);
                    if (raw == null || raw.Length != rawLens[i])
                    {
                        failed++;
                        L("★音效解压失败 " + names[i] + " 期望 " + rawLens[i] +
                          " 实得 " + (raw == null ? -1 : raw.Length));
                    }
                    else
                    {
                        bool okWrite = false;
                        try { File.WriteAllBytes(dst, raw); okWrite = true; }
                        catch (UnauthorizedAccessException)
                        {
                            // 设备上的文件若是 adb push 推的（属主 shell），App 改不了 —— 先删再写
                            try { File.Delete(dst); File.WriteAllBytes(dst, raw); okWrite = true; }
                            catch { }
                        }
                        if (okWrite) written++;
                        else
                        {
                            long have = -1;
                            try { have = new FileInfo(dst).Length; } catch { }
                            if (have == raw.Length) existed++;
                            else
                            {
                                failed++;
                                L("★音效写入被拒 " + names[i] + " 期望 " + raw.Length + " 现有 " + have);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    L("★音效解包异常 " + names[i] + " : " + e.GetType().Name + " " + e.Message);
                }
                bodyPos += compLens[i];

                // 每帧最多解 16 个。43MB/669 个，平均 64KB/个，一帧 16 个约 1MB，
                // 模拟器上大约 100ms —— 再快会明显卡启动动画。
                idx++;
                if ((idx & 15) == 15) yield return null;
            }
        }
        finally { }

        L("音效数据解包：共 " + n + " 个，新解 " + written + "，已存在 " + existed +
          "，失败 " + failed + "，用时 " + total.ElapsedMilliseconds + "ms");
        if (failed == 0 && wantVer != null)
        {
            try { File.WriteAllText(verPath, wantVer); }
            catch (Exception e) { L("★音效数据版本落盘失败 " + e.GetType().Name); }
        }
    }

    /// <summary>把 pak 缓冲里 [offset, offset+len) 的 Deflate 数据解成原始字节。</summary>
    static byte[] Inflate(byte[] buf, int offset, int len, int expected)
    {
        using (var ms = new MemoryStream(buf, offset, len, false))
        using (var ds = new System.IO.Compression.DeflateStream(
                   ms, System.IO.Compression.CompressionMode.Decompress))
        using (var outMs = new MemoryStream(expected > 0 ? expected : 4096))
        {
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }
    }

    // ────────────────────────────────────────────────────────────
    //  素材拷贝
    // ────────────────────────────────────────────────────────────

    IEnumerator CopyAssets()
    {
        string root = PortableInit.RootPath;
        int copied = 0, skipped = 0;

        // ★ 素材版本闸门（2026-11-01 加，这是个真 bug 的修法）。
        //   旧逻辑只有一句「目标 >1024 字节就跳过」，结果：只要设备上装过一次，
        //   之后不管 Assets/StreamingAssets 里的素材换成什么样，**全都不会重新拷**。
        //   实测后果：2026-11-01 重建的 144 张怪物库（调色板红蓝纠正 + 权威偏移 +
        //   224 帧标准布局）打进 APK 了，可设备 persistentDataPath 里还是 10 月份的旧库，
        //   游戏里看到的还是错的怪 —— 而且日志写「已存在跳过 198 个」，看起来一切正常。
        //   现在把版本号写进设备上的 .asset_version，跟包里对不上就强制全量重拷。
        // ★ 2026-11-02 摇杆美术：MakePad 的手绘环 → 离线 3D 渲染的 pad_base/pad_knob
        // ★ 2026-11-03 v19：28 个怪物 Lib 的帧头 X/Y 锚点回填（原为 0，导致怪画在脚下
        //   偏右、且互相叠成一坨看着"特别大"）。素材变了必须让设备重新拷。
        // ★ 2026-11-05 v21：按键换成扁平风 + 内嵌图标（9 个白描图标 + 54 张原版 MagIcon 帧）。
        //   素材变了必须让设备重新拷；瘦包模式下这些文件要走 adb push（见 tools 里的推送脚本）。
        // ★ 2026-11-09 v23：小地图库换成 252 真帧版（15.6 MB）。StreamingAssets 里
        //   一直是 11-08 16:51 的旧 218 帧版（8.3 MB），重打包会把旧小地图带回去。
        // ★ 2026-11-09 v24：中文语言包 Chinese.tsv 补了 3 条寄售行状态
        //   （MarketStatusSold/NoBid/BidMet）。瘦包模式下 Localization 也在 NEEDED 里，
        //   升版本号才会重新拷到设备私有目录。
        const string AssetVersion = "2026-11-09-petbtn-zh-v26";
        string verFile = Path.Combine(root, ".asset_version");
        bool forceCopy = true;
        try
        {
            if (File.Exists(verFile))
                forceCopy = File.ReadAllText(verFile).Trim() != AssetVersion;
        }
        catch { forceCopy = true; }

        // ★ 2026-11-04：**瘦包模式**（素材不进 APK，用 adb 直接推到应用私有目录）。
        //
        //   为什么必须这样：装机实测 —— 862 MB 的包每次 pm install 都会把
        //   system_server 的主线程卡死 60~101 秒，系统看门狗 (Watchdog) 直接
        //   `*** WATCHDOG KILLING SYSTEM PROCESS` 杀掉整台机器，连杀三次。
        //   这个模拟器镜像根本装不下这么大的包，不是内存不够（当时 MemAvailable 3.2 GB）。
        //   日志证据：`Watchdog: Blocked in handler on main thread (main) for 100s`。
        //
        //   所以装机用「瘦包」（只有代码 + maps.pak + ServerData.pak + Localization，
        //   约 157 MB），Data 下的 707 MB 图库由 adb push 直接落到这个目录。
        //
        //   判定：Data/Map/WemadeMir2 这个目录连源带目标都没有 ⇒ 是瘦包。
        //   此时 forceCopy 必须关掉，否则每一轮启动都会尝试去 APK 里读一个
        //   根本不存在的源，日志刷 335 条「★缺源」，而且永远写不上版本号。
        //
        // ★ 2026-11-13 修一个会伤到真用户的判定错误：
        //   旧写法是 Directory.Exists(streamingAssetsPath + "/Data/Map/WemadeMir2")，
        //   但**安卓上 streamingAssetsPath 是 "jar:file:///…/base.apk!/assets"**，
        //   它不是文件系统路径，Directory.Exists 恒为 false
        //   ⇒ 953 MB 的全量包也被判成瘦包（日志实测确实打印了「★ 瘦包模式」）。
        //   后果不是显示问题：forceCopy 被强行关掉后，装了旧版本的手机再装新包，
        //   设备上残留的旧素材不会被覆盖，而版本号照样写成新的
        //   ⇒ 旧素材永久留在设备上，以后改字体/换素材全都刷不进去。
        //   现在：真路径才用 Directory.Exists；jar: 路径改为实际取一个 290 KB 的
        //   探针文件（Smtiles.Lib，核心地图块，全量包必有）来判断包里有没有 Data。
        bool slimBuild;
        string saPath = Application.streamingAssetsPath;
        if (saPath.StartsWith("jar:") || saPath.StartsWith("http"))
        {
            slimBuild = true;
            using (var probe = UnityEngine.Networking.UnityWebRequest.Get(
                       saPath + "/Data/Map/WemadeMir2/Smtiles.Lib"))
            {
                probe.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                yield return probe.SendWebRequest();
                if (probe.result == UnityEngine.Networking.UnityWebRequest.Result.Success
                    && probe.downloadHandler.data != null
                    && probe.downloadHandler.data.Length > 1024)
                    slimBuild = false;
            }
            L("素材来源探测：" + (slimBuild ? "瘦包（APK 内无 Data，靠 adb push）"
                                            : "全量包（APK 内含 " + NEEDED.Length + " 项素材）"));
        }
        else
        {
            slimBuild = !Directory.Exists(Path.Combine(saPath, "Data", "Map", "WemadeMir2"));
        }
        if (slimBuild)
        {
            forceCopy = false;
            L("★ 瘦包模式：Data 图库不打进 APK（adb push 提供）→ 只补缺，不强制重拷");
        }

        if (forceCopy) L("★ 素材版本变更/首次启动 → 强制重拷全部 " + NEEDED.Length + " 项（旧设备素材作废）");
        else L("素材版本一致（" + AssetVersion + "），仅补缺");

        // 启动屏的进度分母（见 DrawBootScreen）。真机首次启动这段要好几分钟，
        // 没有分母就画不出进度条，用户只能对着一屏字猜。
        _bootTotal = NEEDED.Length;
        _bootItems = 0;
        _bootBytes = 0;

        for (int i = 0; i < NEEDED.Length; i++)
        {
            string rel = NEEDED[i];
            _bootItems = i;
            string dst = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            string dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // 目标已存在且不是空壳，就不重复拷（第二次启动秒过）——前提是版本没变
            if (!forceCopy && File.Exists(dst))
            {
                var fi = new FileInfo(dst);
                if (fi.Length > 1024) { skipped++; continue; }
            }

            string url = Application.streamingAssetsPath + "/" + rel;
            bool inApk = url.StartsWith("jar:") || url.StartsWith("http");

            if (!inApk)
            {
                // 编辑器 / 桌面：普通文件，直接拷
                if (File.Exists(url))
                {
                    try
                    {
                        File.Copy(url, dst, true);
                        copied++;
                        L("  拷贝(FILE) " + rel + " " + new FileInfo(dst).Length + " 字节");
                    }
                    catch (Exception e)
                    {
                        L("  ★拷贝失败 " + rel + " -> " + e.GetType().Name + ": " + e.Message);
                    }
                }
                else L("  ★缺源 " + url);
                continue;
            }

            using (var req = UnityEngine.Networking.UnityWebRequest.Get(url))
            {
                req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                yield return req.SendWebRequest();

                if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    L("  ★下载失败 " + rel + ": " + req.error + "  url=" + url);
                    continue;
                }

                byte[] data = req.downloadHandler.data;
                if (data == null || data.Length < 1024) { L("  ★数据太小 " + rel + " " + (data == null ? -1 : data.Length)); continue; }
                // ★ 写盘必须包 try/catch（2026-11-02 事故后加）。
                //   实测症状：之前用 adb push 推上去的 12 个库属主是 root:root，
                //   App（u0_a203）覆盖不了 ⇒ 这里抛 UnauthorizedAccessException ⇒
                //   **整个 Boot 协程当场断掉**，日志停在最后成功的那一项，界面上就是
                //   「启动卡死 5 分钟」，一句错误都看不到。现在单文件失败只跳过它自己，
                //   其余素材照拷，并在日志里写清是哪个文件、什么异常。
                try
                {
                    File.WriteAllBytes(dst, data);
                }
                catch (Exception e)
                {
                    L("  ★写盘失败 " + rel + " -> " + e.GetType().Name + ": " + e.Message
                      + "（目标可能属主不对/只读，adb shell 里删掉再重启即可）");
                    continue;
                }
                copied++;
                _bootBytes += data.Length;
                L("  拷贝(WEB)  " + rel + " " + data.Length + " 字节");
            }
        }

        L("素材就绪：新拷 " + copied + " 个，已存在跳过 " + skipped + " 个，共 " + NEEDED.Length + " 个（版本 " + AssetVersion + "）");
        // 全部拷完才写版本号：中途被杀掉下次还会重来，不会留下「版本对但文件缺」的半成品
        // ★ 2026-11-04 瘦包补丁：瘦包模式下 forceCopy 恒为 false，上面这个条件永远不成立
        //   ⇒ 版本号永远写不上 ⇒ 每次都走一遍全量 File.Exists 检查。虽然只是 stat 调用，
        //   但 NEEDED 有 264 项、跨目录，实测在慢速 emulator 上要 1~2 秒。
        //   瘦包只要「所有文件都在」就算成功，直接落版本号。
        if (copied > 0 || (forceCopy && skipped + copied == NEEDED.Length)
            || (slimBuild && skipped == NEEDED.Length))
        {
            try { File.WriteAllText(verFile, AssetVersion); L("素材版本已落盘 " + AssetVersion); }
            catch (Exception e) { L("★ 写素材版本失败 " + e.Message); }
        }

        // 素材阶段结束，收掉进度条（分母归零 ⇒ DrawBootScreen 不再画它）
        _bootItems = NEEDED.Length;
        _bootTotal = 0;

        // 地图图库体积自检。
        // 这 4 张一旦是截断版，MLibrary 取不到图会静默 return，画面上就是「地图一片黑」，
        // 而日志里一切正常 —— 2026-10-28 排查它花了两轮。这里给出下限值，越界就明说是素材问题，
        // 免得下次又去翻渲染代码。
        var minSize = new string[,] {
            { "Data/Map/WemadeMir2/Tiles.Lib",   "18000000" },
            { "Data/Map/WemadeMir2/Objects.Lib", "40000000" },
            { "Data/Map/WemadeMir2/Objects4.Lib","18000000" },
            { "Data/Map/WemadeMir2/Objects6.Lib","14000000" },
            { "Data/Map/WemadeMir2/Objects2.Lib", "15000000" },
            { "Data/Map/WemadeMir2/Objects3.Lib", "15000000" },
            { "Data/Map/WemadeMir2/Objects5.Lib", "12000000" },
            { "Data/Map/WemadeMir2/Objects7.Lib", "13000000" },
        };
        for (int i = 0; i < minSize.GetLength(0); i++)
        {
            string rel = minSize[i, 0];
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            long have = File.Exists(p) ? new FileInfo(p).Length : 0;
            long need = long.Parse(minSize[i, 1]);
            if (have < need)
                L("★地图图库是残缺版 " + rel + " 只有 " + have + " 字节（需 >= " + need +
                  "）⇒ 进游戏后地图会一片黑。把 Build/Client/Release 下的全尺寸版放进 " +
                  "Assets/StreamingAssets/" + rel);
        }
    }

    // ────────────────────────────────────────────────────────────
    //  像素搬运
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 RenderBridge 画好的 BGRA 缓冲搬进 Unity 纹理。两件必须做的事：
    ///   1) B/R 对调：D3D 的 A8R8G8B8 小端内存里是 B,G,R,A，Color32 字段序是 R,G,B,A。
    ///   2) 竖直翻转：真源码第 0 行 = 屏幕顶部；Unity Texture2D 第 0 行 = 图像底部。
    /// 少做任何一条，画面要么整体发青，要么上下颠倒。
    /// </summary>
    void Upload()
    {
        var t = RenderBridge.mapTexture;
        if (t == null || t.Pixels == null || _tex == null) return;

        byte[] src = t.Pixels;
        int w = t.Width, h = t.Height;
        // ★ 宽屏改造：跟画布比，不是跟界面尺寸比（否则宽屏下每帧都被这一句挡掉 ⇒ 画面冻住）。
        if (w != Bridge.CanvasWidth || h != Bridge.CanvasHeight) return;
        if (_raw == null || _raw.Length != w * h * 4) _raw = new byte[w * h * 4];

        // ★ 纹理格式用 BGRA32，和垫片内存布局完全一致（D3D 的 A8R8G8B8 小端即
        //   B,G,R,A），于是整条 B/R 交换可以不做。
        //
        //   历史：最早是逐像素写 Color32 + SetPixels32（上传 17~29ms），
        //   后来改成逐字节交换 B/R + LoadRawTextureData（上传 63ms —— 更慢，
        //   因为每帧 78 万次随机下标写入）。现在只剩「行序上下翻转」：
        //   垫片原点在左上（D3D），Unity 纹理原点在左下。
        //   每行一次 BlockCopy，768 次 memcpy，实测降到 3~5ms。
        byte[] raw = _raw;
        int rowBytes = w * 4;
        for (int y = 0; y < h; y++)
            System.Buffer.BlockCopy(src, y * rowBytes, raw, (h - 1 - y) * rowBytes, rowBytes);

        _tex.LoadRawTextureData(raw);
        _tex.Apply(false, false);

        UpdateAmbient(src, w, h);
    }

    /// <summary>
    /// 把游戏画面左右内缘的颜色，做成铺满黑边的环境光底衬。每帧一次。
    ///
    /// 做法分三步（都对得起「别为装饰掉帧」这条底线）：
    ///   1) 纵向压成 96 个带，每带取左/右内缘 14 列的平均色 —— 256 次采样/侧；
    ///   2) 每带颜色做指数低通（系数 0.28）：镜头移动时黑边只跟着缓慢变色，
    ///      不滤的话画面一动黑边就频闪，那比纯黑还难看；
    ///   3) 横向按到接缝的距离做幂次衰减（外缘 0.14 → 接缝 0.86，^1.5），
    ///      衰减烘进 alpha，绘制时一次 DrawTexture 就够。
    /// </summary>
    void UpdateAmbient(byte[] src, int w, int h)
    {
        // ★ 2026-11-11 宽屏改造：画布与设备同比例时视口铺满整屏，左右一点黑边都不剩，
        //   这层底衬没有落点。直接跳过，省下每帧 ~2700 次采样 + 两次 12KB 纹理上传。
        //   只有极端竖屏 / 超宽屏被夹到 [1024,2048] 时才真的需要它，那时才跑。
        Rect vp = Viewport();
        bool hasSideGap = vp.x > 1f || Screen.width - vp.xMax > 1f;
        if (!hasSideGap) return;

        EnsureAmbient();
        for (int r = 0; r < AmbH; r++)
        {
            int y0 = r * h / AmbH;
            int y1 = (r + 1) * h / AmbH;
            if (y1 <= y0) y1 = y0 + 1;

            float lr = 0f, lg = 0f, lb = 0f, rr = 0f, rg = 0f, rb = 0f;
            int cnt = 0;
            for (int y = y0; y < y1; y++)
            {
                int rowoff = y * w * 4;
                for (int sk = 0; sk < AmbSampleW; sk++)
                {
                    int xl = 2 + sk, xr = w - 3 - sk;
                    if (xl < 0 || xr >= w) continue;
                    int i = rowoff + xl * 4;      // BGRA：+0=B +1=G +2=R
                    lb += src[i]; lg += src[i + 1]; lr += src[i + 2];
                    i = rowoff + xr * 4;
                    rb += src[i]; rg += src[i + 1]; rr += src[i + 2];
                    cnt++;
                }
            }
            if (cnt == 0) continue;
            float inv = 1f / cnt;
            const float k = 0.28f;               // 时序低通
            _ambLr[r] += (lr * inv - _ambLr[r]) * k;
            _ambLg[r] += (lg * inv - _ambLg[r]) * k;
            _ambLb[r] += (lb * inv - _ambLb[r]) * k;
            _ambRr[r] += (rr * inv - _ambRr[r]) * k;
            _ambRg[r] += (rg * inv - _ambRg[r]) * k;
            _ambRb[r] += (rb * inv - _ambRb[r]) * k;
        }

        // 写到纹理：Unity 纹理第 0 行是**最下面**，逻辑第 0 带在最上面 ⇒ 反序写。
        for (int r = 0; r < AmbH; r++)
        {
            int ty = (AmbH - 1 - r) * AmbW;
            for (int x = 0; x < AmbW; x++)
            {
                float tL = (x + 0.5f) / AmbW;                 // 0=屏左外缘 → 1=接缝
                float tR = 1f - (x + 0.5f) / AmbW;            // 0=屏右外缘 → 1=接缝
                // alpha：外缘 0.06 → 接缝 1.00，幂 0.85（前段抬得快）。
                //   ① 接缝必须是 1.00：采样取的就是游戏最边那 14 列，接缝处按 1.0 画
                //      等于把同一个颜色原样续出去 —— 数值上连续，肉眼看不出接缝在哪。
                //      0.86 会留一道 0.14 的跳变（约 28/255），还是一道看得见的竖线。
                //   ② 幂 0.85（<1）而不是 >1：黑边有 548px 宽，按键列贴在**离屏边 164px**
                //      那一侧（归一化 t≈0.30）。t^1.8 在 t=0.30 只剩 0.12 ⇒ 合到 alpha 只有 0.22，
                //      按键还是浮在近黑上，等于白改。t^0.85 在 t=0.30 是 0.36 ⇒ alpha≈0.40，
                //      按内缘色 (110,95,70) 算约 (44,38,28)，暖的、看得出是画面延续。
                //      （这是按公式推的期望值，装包后必须用截图验证，见 verify_ambient.py。）
                float aL = Mathf.Clamp01(0.06f + 0.94f * Mathf.Pow(tL, 0.85f));
                float aR = Mathf.Clamp01(0.06f + 0.94f * Mathf.Pow(tR, 0.85f));
                _ambLpx[ty + x] = new Color32(Clamp255(_ambLr[r]), Clamp255(_ambLg[r]),
                                              Clamp255(_ambLb[r]), (byte)(aL * 255f));
                _ambRpx[ty + x] = new Color32(Clamp255(_ambRr[r]), Clamp255(_ambRg[r]),
                                              Clamp255(_ambRb[r]), (byte)(aR * 255f));
            }
        }
        _ambL.SetPixels32(_ambLpx);
        _ambL.Apply(false, false);
        _ambR.SetPixels32(_ambRpx);
        _ambR.Apply(false, false);
    }

    /// <summary>float 色 → byte。（Unity 的 Color32 构造无隐式转换，越界会回绕成 255 之外的怪值。）</summary>
    static byte Clamp255(float v)
    {
        int i = Mathf.RoundToInt(v);
        return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i));
    }

    void EnsureAmbient()
    {
        if (_ambL != null) return;
        _ambL = new Texture2D(AmbW, AmbH, TextureFormat.RGBA32, false);
        _ambR = new Texture2D(AmbW, AmbH, TextureFormat.RGBA32, false);
        _ambL.filterMode = FilterMode.Bilinear;
        _ambR.filterMode = FilterMode.Bilinear;
        _ambL.wrapMode = TextureWrapMode.Clamp;
        _ambR.wrapMode = TextureWrapMode.Clamp;
        _ambLpx = new Color32[AmbW * AmbH];
        _ambRpx = new Color32[AmbW * AmbH];
        _ambLr = new float[AmbH]; _ambLg = new float[AmbH]; _ambLb = new float[AmbH];
        _ambRr = new float[AmbH]; _ambRg = new float[AmbH]; _ambRb = new float[AmbH];
    }

    /// <summary>把原始 BGRA 落盘，供 adb pull 后与 PC 基准逐字节比。</summary>
    void DumpFrame(string name)
    {
        var t = RenderBridge.mapTexture;
        if (t == null || t.Pixels == null) { L("★落盘失败：mapTexture 为空"); return; }

        string p = Path.Combine(PortableInit.RootPath, name);
        try
        {
            File.WriteAllBytes(p, t.Pixels);
        }
        catch (Exception e) { L("★落盘异常 " + e.Message); return; }

        int nz = 0;
        byte[] px = t.Pixels;
        for (int i = 0; i < px.Length; i += 4)
            if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) nz++;
        int total = px.Length / 4;
        L("落盘 " + name + "  " + t.Width + "x" + t.Height + "  " + px.Length + " 字节  " +
          "非黑 " + nz + "/" + total + " (" + (nz * 100.0 / total).ToString("F1") + "%)  → " + p);
        _dumped = true;
    }

    // ────────────────────────────────────────────────────────────
    //  行走
    // ────────────────────────────────────────────────────────────

    static int DirFromDelta(int dx, int dy)
    {
        if (dx == 0 && dy < 0) return 0;
        if (dx > 0 && dy < 0) return 1;
        if (dx > 0 && dy == 0) return 2;
        if (dx > 0 && dy > 0) return 3;
        if (dx == 0 && dy > 0) return 4;
        if (dx < 0 && dy > 0) return 5;
        if (dx < 0 && dy == 0) return 6;
        if (dx < 0 && dy < 0) return 7;
        return 4;
    }

    void FindWalkableNear(ref Vector2Int c)
    {
        int mw, mh;
        Bridge.GetSize(_map, out mw, out mh);
        for (int r = 1; r < 60; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = c.x + dx, y = c.y + dy;
                    if (x <= 0 || y <= 0 || x >= mw || y >= mh) continue;
                    if (Bridge.CanWalk(_map, x, y)) { c = new Vector2Int(x, y); return; }
                }
    }

    void RecalcPath()
    {
        _path.Clear();
        _pathIdx = 0;
        _stepT = 0;

        var raw = Bridge.FindPath(_map,
            new System.Drawing.Point(_cell.x, _cell.y),
            new System.Drawing.Point(_target.x, _target.y));
        for (int i = 0; i < raw.Length; i++) _path.Add(new Vector2Int(raw[i].X, raw[i].Y));
        if (_path.Count > 0 && _path[0] == _cell) _path.RemoveAt(0);
        _pathIdx = 0;
    }

    /// <summary>格子增量 → 像素偏移（原版 OffSetMove 口径：走一格从 0 变到 ∓48/∓32）。</summary>
    void GetOffMove(out int omx, out int omy)
    {
        var nxt = _pathIdx < _path.Count ? _path[_pathIdx] : _cell;
        int dx = nxt.x - _cell.x;
        int dy = nxt.y - _cell.y;
        float t = Mathf.Clamp01(_stepT);
        omx = Mathf.RoundToInt(-dx * MapControl.CellWidth * t);
        omy = Mathf.RoundToInt(-dy * MapControl.CellHeight * t);
    }

    /// <summary>重画一帧（真源码 → 纹理）。</summary>
    void RenderNow()
    {
        int omx, omy;
        GetOffMove(out omx, out omy);

        var rsw = Stopwatch.StartNew();
        RenderBridge.SetUserPosition(_cell.x, _cell.y, _dir, omx, omy);
        RenderBridge.SetAction(MirAction.Standing, 0);      // 先回到站定，下面按需覆盖
        if (_pathIdx < _path.Count && _stepT > 0f)
            RenderBridge.SetAction(MirAction.Walking, _walkFrame);
        RenderBridge.RenderWithUI(_scene);
        _drawMs = (int)rsw.ElapsedMilliseconds;
        _renderCount++;

        var usw = Stopwatch.StartNew();
        Upload();
        _uploadMs = (int)usw.ElapsedMilliseconds;
    }

    // ────────────────────────────────────────────────────────────
    //  主循环
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 切后台 / 切回前台。
    ///
    /// 手机端服务端和客户端在同一个进程里：安卓随时可能把整个 APP 回收掉，
    /// 而原版服务端是「每 5 分钟存一次盘」的 PC 服务器节奏。玩家建完角直接
    /// 按 Home 键，回来角色就没了（2026-11-02 实测就是这么丢的）。
    /// 所以每次切后台都立刻落盘一次，退出前再落一次。
    /// </summary>
    void OnApplicationPause(bool paused)
    {
        if (!paused) return;
        L("[生命周期] 切后台 → 账号库落盘");
        EmbeddedServer.FlushAccounts();
    }

    void OnApplicationQuit()
    {
        L("[生命周期] 退出 → 账号库落盘");
        try { P2NetMode.Shutdown(); } catch { }   // 收掉 UDP 广播/发现线程
        EmbeddedServer.FlushAccounts();
    }

    void Update()
    {
        _frames++;
        _fpsT += Time.unscaledDeltaTime;
        if (_fpsT >= 0.5f)
        {
            _fps = _frames / _fpsT;
            _frames = 0;
            _fpsT = 0;
        }

        // ★ 2026-11-04 整帧耗时拆解（定位「各段只花 100ms，FPS 却只有 1.8」）。
        //
        //   现象：网络=0ms 渲染=46~257ms 上传=0ms 绘制=0ms，加起来远小于 550ms/帧。
        //   缺的那几百毫秒要么在 Update 之外（GPU 提交/FB 交换/系统调度），
        //   要么在 Update 内部但我们没计时的段（HandleSceneTap / 状态字符串拼接 /
        //   P2DevCmd.Tick / P2NativeInput.Tick）。
        //   有了这四个数就能一眼分清：
        //     _frameGapMs  = 上一帧结束 → 这一帧开始（Unity 侧/GPU/系统的空档）
        //     _frameBodyMs = Update 从头跑到 return 的时间（我们的活）
        //   两数相加才是一帧。FPS=1.8 ⇒ 一帧 550ms，正常应该 <20ms。
        _frameBodySw = Stopwatch.StartNew();
        _frameGapMs = (int)(_clock.ElapsedMilliseconds - _lastFrameEndMs);
        _lastFrameEndMs = _clock.ElapsedMilliseconds;

        // 三指同时按下任意位置 → 开关调试信息条（默认关，见 OnGUI 的注释）
        int touching = Input.touchCount;
        if (touching >= 3 && !_threeFingerLatch) { _showHud = !_showHud; _threeFingerLatch = true; }
        else if (touching < 3) _threeFingerLatch = false;

        if (!_ready) return;

        // ★ 2026-11-12 运行时换比例（折叠屏开合 / 分屏 / 旋转）。
        //   画布尺寸只在开机时算一次，运行中屏比一变就必须重算，
        //   否则 Viewport() 只能等比缩放并留空带（实测 2048x1536 下上下各留 448/248px）。
        if (_sizeHooked && (Screen.width != _lastSw || Screen.height != _lastSh))
        {
            L("★尺寸变化 Screen " + _lastSw + "x" + _lastSh + " → " + Screen.width + "x" + Screen.height);
            _lastSw = Screen.width;
            _lastSh = Screen.height;
            RefitToScreen();
        }

        // 远程命令行：每 0.5 秒看一次 persistentDataPath/p2_cmd.txt。
        // 手机没有键盘，验收时又要发服务端聊天命令（@LEVEL / @GIVESKILL），
        // 靠这个通道 `adb shell "echo 'level 40' > …/p2_cmd.txt"` 就能驱动，不用重打包。
        P2DevCmd.Log = L;
        P2DevCmd.Tick();

        // 原生输入法：有输入框拿到焦点就弹键盘，把字灌回 DLL 的 TextBox。
        // 创角界面的「名字」和游戏里的聊天框都靠这条（2026-11-02 加）。
        P2NativeInput.Log = L;
        P2NativeInput.Tick();

        // ★ 2026-11-07 修「开机永远停在单机演示」的死锁。
        //
        //   原来这里只有一句 `if (!_networked) TryGoOnline();`，而 TryGoOnline 的
        //   第一行是 `if (Current != Selecting && Current != InGame) return;`
        //   —— 也就是说：**只要还没连上就直接返回**。可负责「把网络推进到连上」
        //   的 NetDriver.Step() 在下面 `if (_networked)` 分支里，_networked 又是
        //   false ⇒ Step 一次都不跑 ⇒ RetryFromFailed 永远没机会执行 ⇒ 状态永远
        //   停在 Failed/Logging ⇒ TryGoOnline 永远返回 ⇒ 死锁。
        //
        //   实测证据（2026-11-07 21:2x p2_boot.log）：
        //     [net] [Logging] 发送登录 mir640544
        //     [srvlog] 用户已登录            ← 服务端其实已经认了
        //     网络阶段=Logging  耗时 21516ms ← 只比 20 秒窗口晚 1.5 秒
        //     ★ 没连上服务端，退回单机演示模式
        //   之后 20 分钟日志里再没有一行 [net]，重连一次都没试过。
        //
        //   修法：单机演示期间也推进网络状态机，让它能自己重连上来。
        //   Step() 内部对 MirrorScene 的 Process 有 null 保护，单机演示时
        //   ActiveScene 是 NetRelayScene（Target=null），跑它是安全的空转。
        if (!_networked)
        {
            NetDriver.Step(_clock.ElapsedMilliseconds);
            TryGoOnline();
        }

        // ── 联网模式：每帧推进网络 + 原版场景，然后把场景画到纹理 ──
        if (_networked)
        {
            long now = _clock.ElapsedMilliseconds;
            var stepSw = Stopwatch.StartNew();
            NetDriver.Step(now);
            _stepMs = (int)stepSw.ElapsedMilliseconds;

            var scene = Client.MirControls.MirScene.ActiveScene;
            bool enteringGame = false;
            if (scene != null && !(scene is NetRelayScene) && scene != _lastDrawnScene)
            {
                _lastDrawnScene = scene;
                L("场景切换 → " + scene.GetType().Name);
                enteringGame = scene is GameScene;
            }

            // ── 虚拟摇杆优先：手指落在摇杆/攻击键热区时，这一帧的触摸归它，
            //    不再转给原版场景（否则摇杆按住的同时还会「点地面走路」，两套输入打架）。
            //    判断只在 GameScene 里做：选角/登录界面的按钮归原版控件走。
            var gameScene = InputBridge.Resolve(scene) as GameScene;
            bool padConsumed = false;

            // ── 联机设置层**排在虚拟摇杆之前** ──
            // 它是模态的：面板一开，这一帧的所有触摸都归它，摇杆和场景都收不到，
            // 否则玩家在设置面板上打字，背后的小人还在跟着走。
            // 面板没开时这一步只负责左上角那颗「设置」小按钮的点击判定。
            P2NetPanel.Log = L;
            P2NetMode.Tick();      // 后台查到的公网地址交给主线程去连（PlayerPrefs 只能在主线程写）
            if (gameScene != null && P2NetPanel.Step())
            {
                padConsumed = true;
                if (_pressScene != null)
                {
                    InputBridge.ReleaseAt(_pressScene, _lastTouch.x, _lastTouch.y);
                    _pressScene = null;
                }
                P2VirtualPad.Reset();
            }
            else if (gameScene != null)
            {
                P2VirtualPad.Log = L;
                padConsumed = P2VirtualPad.Step(gameScene, Viewport());

                // 摇杆接管了触摸，就别留着上一次「按住地面」的按下状态，
                // 否则 MapButtons 会被两边同时写，人会在原地抖。
                if (padConsumed && _pressScene != null)
                {
                    InputBridge.ReleaseAt(_pressScene, _lastTouch.x, _lastTouch.y);
                    _pressScene = null;
                }
            }
            else
            {
                P2VirtualPad.Reset();
            }

            if (!padConsumed) HandleSceneTap(scene);

            var drawSw = Stopwatch.StartNew();
            if ((AbMask & AB_SKIP_SOFTWARE) == 0) RenderFrame();
            _drawMs = (int)drawSw.ElapsedMilliseconds;
            _renderCount++;
            // ★ 2026-11-04：这条路径原来**没有计 uploadMs** —— 它只在 RenderNow()
            //   里赋值，而联网模式下 RenderNow 根本不被调用，于是状态行里的「上传=34ms」
            //   一直是开机首帧的那个陈年数字，真正的每帧上传耗时从来没被量到过。
            //   3MB 的 LoadRawTextureData + Apply 在模拟器的 GLES 翻译层上可能很贵，
            //   必须计进来才能判断 375ms 的缺口在不在这一项。
            var usw = Stopwatch.StartNew();
            if ((AbMask & AB_SKIP_UPLOAD) == 0) Upload();
            _uploadMs = (int)usw.ElapsedMilliseconds;

            // 进游戏后第 3 帧落盘一次：此时 1.76 底栏底板与文字都已画上，
            // 抓到的是 GameScene 真实画面（开机那次 DumpFrame 抓的是角色选择界面，
            // 拿它跟底栏基准比是两回事，2026-10-28 已踩过这个坑）。
            if (enteringGame && !_dumpedGame)
            {
                _gameFrameCount = 0;
                _pendingGameDump = true;
            }
            if (_pendingGameDump && ++_gameFrameCount >= 3)
            {
                _pendingGameDump = false;
                _dumpedGame = true;
                DumpFrame("p2_game.bgra");
            }

            // ★ 进游戏后每隔 _gameDumpMs 记一行状态：只有一次落盘 = 只能拿到第 3 帧，
            //   之后画面怎么变都测不出来（2026-10-28 用两张同一帧的图做比对，结论全废）。
            //   整帧 BGRA 落盘很重（每次几 MB 磁盘 IO），只在 DebugDump 打开时才写。
            if (_dumpedGame)
            {
                long dumpNow = _clock.ElapsedMilliseconds;
                if (dumpNow - _lastGameDumpMs >= _gameDumpMs)
                {
                    _lastGameDumpMs = dumpNow;
                    if (DebugDump) DumpFrame("p2_game2.bgra");
                    L("状态 #" + (++_gameDumpSeq) + "  " + NetDriver.SceneObjectText()
                        + "  | FPS=" + _fps.ToString("F1") + " 网络=" + _stepMs + "ms 渲染=" + _drawMs
                        + "ms 上传=" + _uploadMs + "ms 绘制=" + _guiMs + "ms"
                        // 帧周期 = 上一次 Update 进来到这次进来的间隔（= 真实一帧时长）；
                        // 体内 = 我们这次在 Update 里干活的时间。周期 − 体内 = 不在我们手里的时间。
                        + " ‖帧周期=" + _frameGapMs + "ms 体内=" + _frameBodyMs + "ms"
                        + " A/B=" + AbMask);
                    L("        [阶段] " + (Client.MirScenes.MapControl.LastTiming ?? "（未生成）"));
                    L("        [对象] " + (Client.MirScenes.MapControl.LastObjDetail ?? "（未生成）"));
                    L("        [光照] " + (Client.MirScenes.MapControl.LightTiming ?? "（未执行）"));
                    L("        [渲染桥] " + (Client.MirCore.RenderBridge.BridgeTiming ?? "（未执行）"));
                    L("        " + NetDriver.CombatText());
                    L("        " + NetDriver.ProgressText());
                    L("        " + NetDriver.InputStateText());
                    // ★ 常开（2026-11-04）：DebugDump 编译期是关的 ⇒ 原来这条永远不打，
                    //   「怪物=7 但屏幕上没有」查了三轮都缺运行时数据。改成每 4 次状态
                    //   （约 16 秒）打一行，代价是几十字节日志。
                    if (_gameDumpSeq % 4 == 1)
                        L("怪物位置 " + _gameDumpSeq + ": " + NetDriver.MonsterPosText(6));
                    if (_gameDumpSeq <= 6)
                        L("NPC 位置 " + _gameDumpSeq + ": " + NetDriver.NpcPosText(8));
                }
            }

            _status = NetStatusText(scene);
            // 联网分支到此为止：记下这一帧「我们自己的活」花了多久。
            // 下一帧开头的 _frameGapMs 减掉它，才是 Unity/GPU/系统那边的时间。
            if (_frameBodySw != null) _frameBodyMs = (int)_frameBodySw.ElapsedMilliseconds;
            return;
        }

        // ── 单机回退：原来的点击走路 ──
        if (Input.touchCount > 0)
        {
            var tc = Input.GetTouch(0);
            if (tc.phase == TouchPhase.Ended) HandleTap(tc.position);
        }
        else if (Input.GetMouseButtonUp(0))
        {
            HandleTap(Input.mousePosition);
        }

        // 启动站定期：镜头停在 (200,200)，画面与 PC 基准同一帧
        if (Time.realtimeSinceStartup < _bootHoldUntil) return;

        if (!_autoWalk) return;

        if (_pathIdx < _path.Count)
        {
            _stepT += Time.unscaledDeltaTime / StepTime;
            _walkAnimT += Time.unscaledDeltaTime;
            _walkFrame = (int)(_walkAnimT / WalkFrameTime) % 6;

            var nxt = _path[_pathIdx];
            _dir = DirFromDelta(nxt.x - _cell.x, nxt.y - _cell.y);

            RenderNow();

            while (_stepT >= 1f && _pathIdx < _path.Count)
            {
                _stepT -= 1f;
                _cell = _path[_pathIdx];
                _pathIdx++;
            }
            if (_pathIdx >= _path.Count)
            {
                _stepT = 0;
                _walkAnimT = 0;
                _walkFrame = 0;
                RenderNow();
            }
        }
    }

    void HandleTap(Vector3 screenPos)
    {
        int lx, ly;
        ToLogic(screenPos, out lx, out ly);
        float sx = lx;
        float sy = ly;

        // 反推格子：与真源码 MapControl.MapLocation 完全同口径
        //   MapLocation = (MouseLocation.X / CellWidth - OffSetX, MouseLocation.Y / CellHeight - OffSetY) + User
        // 这样单机演示的点地走路和联机时按原版逻辑走到的格子**是同一格**，
        // 宽屏下 OffSetX 从 10 涨到 18，这条换算自动跟着对。
        // （旧版 X 少减了 OffSetX，点自己脚下会往右偏 10 格，联机时不会。）
        int cx = _cell.x + Mathf.RoundToInt(sx / MapControl.CellWidth) - MapControl.OffSetX;
        int cy = _cell.y + Mathf.RoundToInt(sy / MapControl.CellHeight) - MapControl.OffSetY;

        int mw, mh;
        Bridge.GetSize(_map, out mw, out mh);
        if (cx <= 0 || cy <= 0 || cx >= mw || cy >= mh) return;
        if (!Bridge.CanWalk(_map, cx, cy)) return;

        _target = new Vector2Int(cx, cy);
        RecalcPath();
        _bootHoldUntil = 0f;
        _autoWalk = true;      // 点一下开始走（启动站定期内点也生效）
        _stepT = 0f;
        _walkAnimT = 0f;
        _walkFrame = 0;
    }

    /// <summary>
    /// 调试信息条开关，默认关。
    ///
    /// ★ 为什么必须默认关（2026-10-28 实测）：这两条黑条是**画在游戏画面之上**的，
    ///   顶部 30 设备像素 + 底部 28 设备像素。1764 逻辑宽下 28/1.4844 ≈ 19 逻辑像素，
    ///   正好压住底栏最下面那排（1.76 的经验条在 SH-73、负重条在 SH-40，
    ///   底部装饰边在 y244..250），截图里表现为「底栏最下面多一条灰带」。
    ///   它不属于游戏，是壳子自己的调试 overlay，出正式画面时不能有。
    ///   三指同时按下任意位置切换。
    /// </summary>
    bool _showHud;
    bool _threeFingerLatch;

    long _guiTicks;

    void OnGUI()
    {
        long t0 = Stopwatch.GetTimestamp();
        DrawGui();
        _guiMs = (int)((Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency);
    }

    void DrawGui()
    {
        var vp = Viewport();

        if (_ready && _tex != null)
        {
            GUI.color = Color.white;
            // vp 已经是 4:3 的精确子矩形，所以这里直接铺满 vp 就是等比放大，不会变形。
            if ((AbMask & AB_SKIP_BLIT) == 0)
                GUI.DrawTexture(vp, _tex, ScaleMode.StretchToFill, false);

            // ── 黑边环境光（见 UpdateAmbient 的注释）──
            // 严格只画在 vp 之外：差 1 像素就会在接缝上留下一条深色竖线，
            // 那比纯黑更显眼。AlphaBlend 混合，贴图 alpha 从 0.14 渐变到 0.86。
            // ★ 宽屏改造后通常没有黑边（画布与设备同比例），_ambL 就是 null，整段跳过。
            if (_ambL != null && (AbMask & AB_SKIP_BLIT) == 0)
            {
                if (vp.x > 1f)
                    GUI.DrawTexture(new Rect(0, 0, Mathf.Floor(vp.x), Screen.height), _ambL,
                                    ScaleMode.StretchToFill, true);
                float rx = Mathf.Ceil(vp.xMax);
                if (rx < Screen.width - 1f)
                    GUI.DrawTexture(new Rect(rx, 0, Screen.width - rx, Screen.height),
                                    _ambR, ScaleMode.StretchToFill, true);
            }
        }
        else
        {
            // 还没出第一帧：画启动屏（2026-11-14 改，原来是整屏刷黑）。
            // 真机首次启动要抽 909MB 素材，这段可能持续几分钟 —— 必须让用户看得见进度，
            // 否则和死机的表现完全一样。见 _bootT0 处的注释。
            DrawBootScreen();
        }

        // ── 虚拟摇杆/攻击键：画在游戏画面之上、调试条之下 ──
        // 只在进游戏后画；选角/登录界面不该看到摇杆。
        if (_ready && _networked && _lastDrawnScene is GameScene)
        {
            P2VirtualPad.Draw();
            // 左上角「设置」小键 + 联机面板（面板开着时盖住整个屏幕）
            P2NetPanel.DrawSettingsButton();
            P2NetPanel.Draw();
        }

        if (!_showHud) return;
        GUI.color = new Color(0f, 0f, 0f, 0.72f);
        GUI.DrawTexture(new Rect(vp.x, vp.y, vp.width, 30), Texture2D.whiteTexture);
        GUI.color = Color.white;

        var st = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleLeft };
        st.normal.textColor = _ready ? new Color(0.55f, 1f, 0.55f) : new Color(1f, 0.6f, 0.4f);
        GUI.Label(new Rect(vp.x + 10, vp.y, vp.width - 20, 30), "[P2] " + _status, st);

        if (!_ready) return;

        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(vp.x, vp.yMax - 28, vp.width, 28), Texture2D.whiteTexture);
        GUI.color = Color.white;

        var info = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
        info.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
        GUI.Label(new Rect(vp.x + 10, vp.yMax - 28, vp.width - 20, 28),
            string.Format("({0},{1})→({2},{3}) 剩{4}步  {5:F0}FPS  渲染{6}ms 上传{7}ms 共{8}帧  画面{9}x{10} 界面{11}x{12}",
                _cell.x, _cell.y, _target.x, _target.y,
                Mathf.Max(0, _path.Count - _pathIdx), _fps, _drawMs, _uploadMs, _renderCount,
                Bridge.CanvasWidth, Bridge.CanvasHeight, SW, SH), info);
    }

    // _status 是中文（给三指 HUD 用的，那时 chatfont.ttf 已就绪能画中文）。
    // 但启动屏跑在 chatfont.ttf 抽出来**之前**，只能用 Unity 内置 Arial —— 它没有中文字形，
    // 中文会画成一排方框。所以这里把已知的启动阶段映射成英文；未知阶段退化成
    // "Working..."，不把中文原样丢进内置字体。
    string BootStageEn(string zh)
    {
        if (string.IsNullOrEmpty(zh)) return "Working...";
        if (zh.StartsWith("初始化")) return "Initializing...";
        if (zh.StartsWith("拷贝素材")) return "Unpacking assets...";
        if (zh.StartsWith("解包地图")) return "Unpacking maps...";
        if (zh.StartsWith("解包服务端数据")) return "Unpacking server data...";
        if (zh.StartsWith("解包音效")) return "Unpacking sound...";
        if (zh.StartsWith("启动内嵌服务端")) return "Starting embedded server...";
        if (zh.StartsWith("初始化渲染桥")) return "Initializing renderer...";
        if (zh.StartsWith("等待内嵌服务端就绪")) return "Waiting for server...";
        if (zh.StartsWith("连接服务器")) return "Connecting...";
        if (zh.StartsWith("渲染第一帧")) return "Rendering first frame...";
        // ★ 开头的都是失败态（「★缺 ./Map/0.map」「★字体缺失 …」），原文最关键，保留
        if (zh.StartsWith("★")) return zh;
        if (zh.Contains("失败") || zh.Contains("缺失")) return zh;
        if (_ready) return "Ready";
        return "Working...";
    }

    // ── 启动屏 ──
    // 为什么要有它：_ready 之前原来是纯黑（见 DrawGui 的 else 分支注释）。
    // 真机首次启动要抽 909MB 素材，用户看到的应该是一条会动的进度，而不是黑板。
    //
    // 文字全部 ASCII：Unity 内置 Arial 没有中文字形，画出来只会是一排方框；
    // 而中文只能等 chatfont.ttf 从 APK 抽出来之后才有 —— 那已经是本屏的后半段了。
    void DrawBootScreen()
    {
        float W = Screen.width, H = Screen.height;

        // 背景：深色底 + 顶部一条渐亮的主色带，比纯黑更容易让人相信"活着"
        GUI.color = new Color(0.055f, 0.06f, 0.075f, 1f);
        GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
        GUI.color = new Color(0.16f, 0.42f, 0.78f, 1f);
        GUI.DrawTexture(new Rect(0, 0, W, Mathf.Max(3f, H * 0.006f)), Texture2D.whiteTexture);
        GUI.color = Color.white;

        // 时间：用 Stopwatch 而不是 Time.time —— 后者在首帧之前不一定前进
        double secs = _bootT0 == 0 ? 0.0
                      : (Stopwatch.GetTimestamp() - _bootT0) / (double)Stopwatch.Frequency;

        // 字号跟着屏幕高度走：手机竖屏/横屏、平板、模拟器都能看
        int big = Mathf.Max(16, Mathf.RoundToInt(H * 0.045f));
        int mid = Mathf.Max(12, Mathf.RoundToInt(H * 0.030f));
        int small = Mathf.Max(10, Mathf.RoundToInt(H * 0.024f));
        float cx = W * 0.5f;
        float y = H * 0.30f;

        var sTitle = new GUIStyle(GUI.skin.label)
        { fontSize = big, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        sTitle.normal.textColor = new Color(0.92f, 0.95f, 1f);
        GUI.Label(new Rect(0, y, W, big * 1.6f), _bootTitle, sTitle);
        y += big * 1.9f;

        // 当前步骤
        var sStep = new GUIStyle(GUI.skin.label)
        { fontSize = mid, alignment = TextAnchor.MiddleCenter };
        sStep.normal.textColor = new Color(0.70f, 0.85f, 1f);
        GUI.Label(new Rect(0, y, W, mid * 1.6f), BootStageEn(_status), sStep);
        y += mid * 2.0f;

        // 进度条：总数已知才画（CopyAssets 之外没有分母）
        if (_bootTotal > 0)
        {
            float bw = Mathf.Min(W * 0.72f, H * 1.6f);
            float bh = Mathf.Max(6f, H * 0.014f);
            float bx = cx - bw * 0.5f;
            GUI.color = new Color(1f, 1f, 1f, 0.14f);
            GUI.DrawTexture(new Rect(bx, y, bw, bh), Texture2D.whiteTexture);
            float frac = Mathf.Clamp01(_bootItems / (float)_bootTotal);
            GUI.color = new Color(0.25f, 0.62f, 0.95f, 1f);
            GUI.DrawTexture(new Rect(bx, y, bw * frac, bh), Texture2D.whiteTexture);
            GUI.color = Color.white;
            y += bh + small * 1.2f;

            var sCnt = new GUIStyle(GUI.skin.label)
            { fontSize = small, alignment = TextAnchor.MiddleCenter };
            sCnt.normal.textColor = new Color(0.80f, 0.84f, 0.90f);
            GUI.Label(new Rect(0, y, W, small * 1.5f),
                string.Format("{0} / {1} files   {2:F1} MB", _bootItems, _bootTotal, _bootBytes / 1048576f), sCnt);
            y += small * 1.8f;
        }

        // 秒数：用户判断"还在跑"还是"真卡死"的唯一依据，必须一直在动
        var sTime = new GUIStyle(GUI.skin.label)
        { fontSize = small, alignment = TextAnchor.MiddleCenter };
        sTime.normal.textColor = new Color(0.62f, 0.66f, 0.72f);
        GUI.Label(new Rect(0, y, W, small * 1.5f),
            string.Format("elapsed {0:F0}s    {1}x{2}", secs, W, H), sTime);

        // 首启提示 + 底部保留调试入口的说明（三指同按仍可开 HUD）
        var sHint = new GUIStyle(GUI.skin.label)
        { fontSize = small, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        sHint.normal.textColor = new Color(0.52f, 0.56f, 0.62f);
        GUI.Label(new Rect(W * 0.1f, H - small * 4.2f, W * 0.8f, small * 3.4f),
            "First launch unpacks bundled assets. This can take several minutes. Keep the app open.",
            sHint);
    }
}
