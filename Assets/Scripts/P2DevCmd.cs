// P2DevCmd —— 手机上的「远程命令行」。
//
// 为什么要这个东西（2026-11-02）：
//   手机没有键盘，外壳也没做聊天输入框。可服务端的验收命令全是聊天命令：
//     @LEVEL 40            提等级（PlayerObject.cs:2392，TestServer=True 放行普通号）
//     @GIVESKILL HalfMoon 3 直接授技能
//   不打通这条，1 级小号永远学不了 7 级技能，「精武堂传功」整条链路没法验。
//
// 做法：每 0.5 秒看一次 persistentDataPath/p2_cmd.txt，一行一条命令，执行完清空。
//   调试时 `adb shell "echo 'level 40' > .../p2_cmd.txt"` 即可，**不用重打包**。
//
// 命令表（都是给人/agent 用的短名，真正发出去的是原版聊天命令）：
//   level 40           → @LEVEL 40
//   giveskill 技能名 3 → @GIVESKILL <Spell> <level>
//   chat 任意文字      → 原样发
//   npc <链接文字>     → 点 NPC 对话框上的那个链接（要先 talk 过）
//   talk               → 找最近 NPC 走过去喊话
//   skill              → 开关技能页（等同右下角「技能」键）
//   cast 4             → 按 F4 出招（GameScene.UseSpell）。被动技能原版直接 return，没反应是正常的
//   skills             → 列已学技能（键位|中文名|英文枚举）
//   report             → 把已学技能 / 技能栏 / 对话框按钮全打进日志
using System;
using System.IO;
using System.Text;      // StringBuilder（charinfo 命令）
using Client;           // CMain.Time（手机端的服务定位器垫片）
using Client.MirCore;
using Client.MirObjects;    // MapObject.User / CurrentLocation
using Client.MirScenes;     // GameScene
using Client.MirNetwork;    // Network.Enqueue（revive 命令）
using C = ClientPackets;
using UnityEngine;

public static class P2DevCmd
{
    public static Action<string> Log;
    static string _path;
    static float _next;
    static readonly System.Collections.Generic.List<string> _queue =
        new System.Collections.Generic.List<string>();

    public static void Init(string root)
    {
        try { _path = Path.Combine(root, "p2_cmd.txt"); } catch { _path = null; }
        EnsureFile();
    }

    // ★ App 必须自己建命令文件并放开 0666（2026-11-02 事故后加）。
    //   原因：`adb shell "echo 'level 40' > .../p2_cmd.txt"` 建出来的文件属主是
    //   `shell ext_data_rw`，App 用户是 `u0_a203` —— **读不了**。界面上表现为
    //   「命令明明写进去了但永远没反应」，日志里一句错都没有，白查半小时。
    //   反过来，App 建的文件属主是 u0_a203、权限 0666，adb（shell）照样能写，
    //   所以「App 抢先建 + 放开 666」是唯一两边都能用的做法。
    static bool _ensured;

    static void EnsureFile()
    {
        if (_ensured || _path == null) return;
        try
        {
            if (!File.Exists(_path)) File.WriteAllText(_path, "");
            Chmod666(_path);
            _ensured = true;
        }
        catch
        {
            // 拿不到（例如文件是 root 属主）就下一帧再试，别让整个 Tick 挂掉
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    static extern int chmod(string pathname, int mode);

    static void Chmod666(string p)
    {
        try { chmod(p, 0x1B6 /* 0666 八进制 = 438 十进制 */); } catch { }
    }
#else
    static void Chmod666(string p) { }
#endif

    /// <summary>每帧调。自己节流，调用方不用管。</summary>
    public static void Tick()
    {
        if (_path == null) return;
        EnsureFile();
        // ★ 走路必须每帧推，不能进 0.5 秒节流（2026-11-02 修）。
        //   MirCoreBridge 的长距离寻路是**分段**喂给 AutoPath 的（每段 10 格），
        //   走完一段要有人调 WalkRemaining() 才会续下一段。原来 DoTalk 只调一次
        //   WalkTo 就撒手，实测 `talknpc 传送小姐` 走几格就停，永远到不了贴身。
        StepWalk();
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.5f;

        try
        {
            if (!File.Exists(_path)) return;
            string text = File.ReadAllText(_path);
            File.WriteAllText(_path, "");   // 先清空，避免命令执行失败时死循环重放
            if (string.IsNullOrEmpty(text)) return;

            _queue.Clear();
            foreach (var raw in text.Split('\n'))
            {
                string line = (raw ?? "").Trim();
                if (line.Length > 0) _queue.Add(line);
            }
            // ★ 每条命令单独 try/catch（2026-11-02 修）。
            //   原来整个循环共用外层一个 catch，一条命令抛异常（实测 `walk 700 612` 越界）
            //   后面的命令就全被跳过，还得等 0.5 秒重新读文件，调试时非常费时间。
            for (int i = 0; i < _queue.Count; i++)
            {
                try { Exec(_queue[i]); }
                catch (Exception ex)
                {
                    if (Log != null) Log("[CMD] 执行失败 " + _queue[i] + " -> " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            if (Log != null) Log("[CMD] 读命令失败 " + ex.Message);
        }
    }

    static void Exec(string line)
    {
        string[] parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1].Trim() : "";

        if (Log != null) Log("[CMD] > " + line);

        switch (cmd)
        {
            case "level":
                Chat("@LEVEL " + (arg.Length == 0 ? "40" : arg));
                break;

            case "giveskill":
                // 服务端解析：@GIVESKILL <Spell> <level>（PlayerObject.cs:3295）
                Chat("@GIVESKILL " + (arg.Length == 0 ? "Fencing 3" : arg));
                break;

            case "chat":
                Chat(arg);
                break;

            case "talk":
                DoTalk(null);
                break;

            case "talknpc":
                // talk 只找「最近的那个」，出生点旁边站着 4 个 NPC，
                // 想专门验精武馆（脚本最长、有 GIVESKILL）就必须能点名。
                DoTalk(arg);
                break;

            case "npc":
                ClickNpcLink(arg);
                break;

            case "skill":
            {
                bool on = Bridge.ToggleSkillPage();
                if (Log != null) Log("[CMD] 技能页=" + (on ? "开" : "关"));
                break;
            }

            case "cast":
            {
                // cast 4 —— 等价于按 F4（GameScene.UseSpell(4)）。
                // ★ 验收别用 1（基本剑术）和 2（攻杀剑术）：这两个是被动，
                //   原版 UseSpell 第一串 case 直接 return，屏幕上永远没反应。
                //   要看得见的效果挑 3=刺杀剑术 / 4=半月弯刀（会弹聊天「使用…」）。
                int key = 0;
                if (!int.TryParse(arg, out key) || key < 1 || key > 24)
                {
                    if (Log != null) Log("[CMD] 用法: cast <1..16>（1..8=Bar1，9..16=Bar2）");
                    break;
                }
                var u = GameScene.User;
                if (u == null) { if (Log != null) Log("[CMD] 还没进游戏"); break; }
                int hpBefore = u.HP, mpBefore = u.MP;
                string chatBefore = Bridge.ChatTail(1);
                bool ok = Bridge.UseSpell(key);
                // 开关技能（半月/刺杀/双龙）不会掉蓝、不出伤害数字，唯一证据是
                // ChatDialog 里那行「使用半月弯刀。」+ UserObject.HalfMoon 翻成 true。
                string chatAfter = Bridge.ChatTail(1);
                if (Log != null)
                {
                    Log("[CMD] 放技能 键位=" + key + " 投递=" + ok
                        + " 蓝 " + mpBefore + "→" + u.MP + " HP " + hpBefore + "→" + u.HP
                        + " 开关=[" + Bridge.SpellToggles() + "]");
                    if (chatAfter != chatBefore) Log("[CMD] 新聊天: " + chatAfter);
                }
                break;
            }

            case "skills":
            {
                // 列出已学技能（键位 + 中文名），给验收用
                if (Log != null)
                {
                    Log("[CMD] 技能清单: " + (string.IsNullOrEmpty(Bridge.SkillSlots()) ? "（无）" : Bridge.SkillSlots()));
                    Log("[CMD] 原始: " + (string.IsNullOrEmpty(Bridge.MagicList()) ? "（无）" : Bridge.MagicList()));
                }
                break;
            }

            case "vcast":
            {
                // vcast 1 —— 等价于**手指点屏幕上第 1 个出招键**（走外壳 DoCast）。
                // ★ 和 cast 的区别（2026-11-15 加）：cast 直接调 Bridge.UseSpell，
                //   绕过了外壳那一层；vcast 走 DoCast，能验到「远程技能目标在射程外
                //   → 自己走过去 → 进射程再放」这条这条只有外壳才有的逻辑。
                int vi = 0;
                if (!int.TryParse(arg, out vi) || vi < 1 || vi > 8)
                {
                    if (Log != null) Log("[CMD] 用法: vcast <1..8>（屏幕上这一排的第几格）");
                    break;
                }
                var vu = GameScene.User;
                if (vu == null) { if (Log != null) Log("[CMD] 还没进游戏"); break; }
                // 点技能键 = 玩家接管移动，命令行遗留的 walk/talknpc 必须让位
                // （否则 A* 被覆盖后 StepWalk 会照旧目标重新寻路，把角色拽回去）
                CancelCmdWalk();
                if (Log != null)
                {
                    var vt = MapObject.TargetObject;
                    Log("[CMD] vcast → 第 " + vi + " 格｜键位=" + (P2VirtualPad.CastKeyForSlot(vi))
                        + " 目标=" + (vt == null ? "无" : vt.Name + "@(" + vt.CurrentLocation.X + "," + vt.CurrentLocation.Y + ")")
                        + " 距离=" + (vt == null ? "?" : Client.MirCore.Bridge.MaxDistanceTo(vt.CurrentLocation.X, vt.CurrentLocation.Y).ToString()));
                }
                P2VirtualPad.CastBySlot(vi);
                break;
            }

            case "range":
            {
                // range 1 —— 查某个键位的技能射程（0=近战，9=远程默认）
                int rk = 0;
                if (!int.TryParse(arg, out rk) || rk < 1 || rk > 16)
                {
                    if (Log != null) Log("[CMD] 用法: range <1..16>");
                    break;
                }
                if (Log != null)
                    Log("[CMD] 键位 " + rk + " 射程=" + Bridge.SpellRange(rk)
                        + " 远程=" + Bridge.SpellIsRanged(rk));
                break;
            }

            case "walk":
            {
                // walk 649 602 —— 直接走到地图坐标（原版右键寻路，不模拟按键）
                int wx = 0, wy = 0;
                var sp = arg.Split(' ');
                if (sp.Length >= 2) { int.TryParse(sp[0], out wx); int.TryParse(sp[1], out wy); }
                int steps = Bridge.WalkTo(wx, wy);
                if (Log != null) Log("[CMD] 走向 (" + wx + "," + wy + ") 路径=" + steps + " 步");
                // ★ 长距离必须交给 StepWalk 每帧续段，否则走 10 格就停（2026-11-02 修）
                StartWalk(wx, wy, 0, "", false);
                break;
            }

            case "where":
            {
                var u2 = MapObject.User;
                if (Log != null) Log("[CMD] 当前坐标=" + (u2 != null ? (u2.CurrentLocation.X + "," + u2.CurrentLocation.Y) : "?"));
                break;
            }

            case "belt":
            {
                // belt       —— 列腰带 6 格
                // belt fill  —— 把背包里的药一键摆进腰带（原版拖拽的手机版）
                // belt 1     —— 用第 1 格（= 按 D1）
                string names = Bridge.BeltNames();
                if (arg == "fill")
                {
                    string diag = "";
                    int n = Bridge.AutoFillBelt(out diag);
                    if (Log != null) Log("[CMD] 腰带装药 " + n + " 件 ｜ " + diag + " ｜ 现在=[" + Bridge.BeltNames() + "]");
                }
                else if (arg.Length > 0 && char.IsDigit(arg[0]))
                {
                    int slot = arg[0] - '1';
                    bool ok = Bridge.UseBeltSlot(slot);
                    if (Log != null) Log("[CMD] 用腰带第 " + (slot + 1) + " 格 ok=" + ok + " ｜ 腰带=[" + Bridge.BeltNames() + "]");
                }
                else
                {
                    if (Log != null) Log("[CMD] 腰带=[" + (string.IsNullOrEmpty(names) ? "（空）" : names.Replace(";", " | ")) + "]");
                }
                break;
            }

            case "shop":
            {
                // shop               —— 列货架
                if (Log != null)
                {
                    string g = Bridge.ShopGoods();
                    Log("[CMD] 货架: " + (string.IsNullOrEmpty(g) ? "（商店没开着，先 talknpc 后 npc 买）" : g));
                }
                break;
            }

            case "buy":
            {
                // buy 金创药 5   —— 买 5 瓶（原名/部分名都行）
                string nm = arg; int n = 1;
                var sp = arg.Split(' ');
                if (sp.Length >= 2) { nm = sp[0]; int.TryParse(sp[1], out n); }
                string diag = "";
                int got = Bridge.BuyByName(nm, n, out diag);
                if (Log != null) Log("[CMD] 买货 " + got + " 件 ｜ " + diag);
                break;
            }

            case "sell":
            {
                string diag = "";
                int ok = Bridge.SellByName(arg, out diag);
                if (Log != null) Log("[CMD] 卖货 ok=" + ok + " ｜ " + diag);
                break;
            }

            case "consign":
            {
                // consign 肉 5000 —— 寄售挂单（原版 C.ConsignItem → 服务端 PlayerObject.ConsignItem）
                string nm = arg ?? "";
                uint price = 5000;
                var sp = nm.Split(' ');
                if (sp.Length >= 2) { nm = sp[0]; uint.TryParse(sp[1], out price); }
                string diag = "";
                int ok = Bridge.ConsignByName(nm, price, out diag);
                if (Log != null) Log("[CMD] 寄售挂单 ok=" + ok + " ｜ " + diag);
                break;
            }

            case "consignprobe":
            {
                if (Log != null) Log("[CMD] 【寄售行】" + Bridge.ConsignProbe());
                break;
            }

            case "quests":
            {
                // quests —— 任务链路探针（2026-11-11 加）。
                //   症状：任务日志面板只显示「任务: 1/20」和一行空的组名，NPC 上没有任务。
                //   NPCIndex 由服务端 NPCScript.ParseQuests() 在建图时回填，失败只会静默
                //   return，所以必须把「服务端认的任务」和「客户端收到的任务」并排打出来。
                if (Log != null)
                {
                    string t = Bridge.QuestProbeText();
                    foreach (var ln in t.Split('\n'))
                        Log("[CMD] 【任务】" + ln);
                }
                break;
            }

            case "tap":
            {
                // tap <x> <y> —— 按**逻辑坐标**点一下游戏场景（真点击链，不是旁路调 Click）。
                //
                // ★ 为什么加（2026-11-11）：`npc 我的寄售` 只能点 NPC 对话框上的文字链接，
                //   寄售窗里的「取消寄售」是图按钮、没有文字，只能按坐标点。
                //   而 adb input tap 在本机不可靠（帧率低 + 0.5 秒轮询），
                //   所以由设备端同步完成：报命中控件 + 投递一次完整点击。
                var xy = (arg ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                int tx, ty;
                if (xy.Length < 2 || !int.TryParse(xy[0], out tx) || !int.TryParse(xy[1], out ty))
                {
                    if (Log != null) Log("[CMD] 用法: tap <x> <y>");
                    break;
                }
                if (Log != null) Log("[CMD] 点击 (" + tx + "," + ty + ") → " + Bridge.TapScene(tx, ty));
                break;
            }

            case "report":
                Report();
                break;

            case "selui":
            {
                // selui —— 导出**当前场景**控件树（选角界面也能用）。
                // `ui`/`uiall` 写死 GameScene，在角色选择界面一律回「还没进游戏」，
                // 于是「创角面板的职业按钮点不中」在手机上没法取证。
                if (Log != null) Log("[CMD] 【场景树】" + Bridge.SceneTree());
                break;
            }

            case "probe":
            {
                // probe —— 只报四个面板自己的坐标（不受场景树 60 条截断影响）
                if (Log != null) Log("[CMD] 【面板探针】" + Bridge.PanelProbe());
                break;
            }

            case "npcprobe":
            {
                // npcprobe —— 当前地图所有 NPC 的名字/坐标/图号/是否画得出
                if (Log != null) Log("[CMD] 【NPC探针】" + Bridge.NpcProbe());
                break;
            }

            case "monlist":
            {
                // monlist [关键词] [每批条数] [起始条] —— 服务端怪物全表
                //   monlist              全表，每批 80 条，把 656 种分 9 行打完
                //   monlist 龙 50        只看含「龙」的
                // ★ logcat 单条日志约 2000 字符就截断，所以这里**主动分批**，
                //   一次命令发多行日志，而不是让调用方手动翻页。
                if (Log == null) break;
                string kw = "";
                int lim = 80, start = 0;
                var a2 = (arg ?? "").Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (a2.Length >= 1 && a2[0] != "-") kw = a2[0];
                if (a2.Length >= 2) int.TryParse(a2[1], out lim);
                if (a2.Length >= 3) int.TryParse(a2[2], out start);
                if (lim <= 0) lim = 80;

                // 分页直到打空：每条最多 80 种，最多 12 页（960 种封顶）
                for (int page = 0; page < 12; page++)
                {
                    string one = Bridge.ServerMonsterNames(kw, lim, start);
                    Log("[CMD] 【怪物全表】" + one);
                    if (one.Contains("（无更多）") || !one.Contains("下批 start=")) break;
                    // 从输出里抠出下一次的 start
                    int p = one.LastIndexOf("下批 start=");
                    if (p < 0) break;
                    string tail = one.Substring(p + "下批 start=".Length).TrimEnd(')', ' ');
                    if (!int.TryParse(tail, out start)) break;
                    System.Threading.Thread.Sleep(60);   // 让 logcat 跟上
                }
                break;
            }

            case "spawnmap":
            {
                // spawnmap [地图片段] [起始] [条数] —— 每张图的刷怪点/怪种/样例怪名
                if (Log == null) break;
                string kw = "";
                int st = 0, cnt = 25;
                var a3 = (arg ?? "").Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (a3.Length >= 1) kw = a3[0];
                if (a3.Length >= 2) int.TryParse(a3[1], out st);
                if (a3.Length >= 3) int.TryParse(a3[2], out cnt);
                Log("[CMD] 【刷怪地图】" + Bridge.SpawnByMap(kw, st, cnt));
                break;
            }

            case "goto":
            {
                // goto <地图文件名> <X> <Y> —— 把自己传到指定地图坐标。
                // goto 3 / goto 3 340 335 / goto B341 17 22
                //
                // ★ 为什么加（2026-11-11）：补种的 39 个 NPC 散在 8 张图上，
                //   靠走路过去一张图要几十秒还可能被怪打断；验收「每张图的
                //   NPC 到底在不在」必须能自己瞬移，否则只能验盟重省一张。
                string g = (arg ?? "").Trim();
                if (Log != null) Log("[CMD] 【传送】" + Bridge.GotoMap(g));
                break;
            }

            case "minimap":
            {
                // minimap         —— 报当前档位
                // minimap 1       —— 直接切到局域档
                // minimap 2       —— 直接切到全图档
                // minimap 0       —— 关
                // minimap next    —— 按底栏按钮那样循环（0→1→关）
                // minimap body    —— 1.76 点地图本体那样在 1⇄2 之间切
                //
                // ★ 为什么加（2026-11-10）：用户点名「小地图的位置都不对，而且分
                //   局域地图和全地图，分好几种」。1.76 的三档是 PlayScn.pas:882-1016
                //   的 DrawMiniMap，验收必须能自己把三档都走一遍、读回真实几何，
                //   不能靠 adb input keyevent（本机实测不进 Unity Input Manager）。
                string a = (arg ?? "").Trim();
                if (a.Length == 0)
                {
                    if (Log != null) Log("[CMD] 【小地图】" + Bridge.MiniMapProbe());
                    break;
                }
                int lv = -1;
                if (a == "next") lv = Bridge.ToggleMiniMap();
                else if (a == "body") lv = Bridge.CycleMiniMapBody();
                else
                {
                    int want;
                    if (!int.TryParse(a, out want)) { if (Log != null) Log("[CMD] 用法: minimap [0|1|2|next|body]"); break; }
                    lv = Bridge.SetMiniMapLevel(want);
                }
                if (Log != null) Log("[CMD] 【小地图】" + Bridge.MiniMapProbe() + " ｜ 切换后档=" + lv);
                break;
            }

            case "bigmap":
            {
                bool on = Bridge.ToggleBigMap();
                if (Log != null) Log("[CMD] 【大地图】可见=" + (on ? 1 : 0));
                break;
            }

            case "barscan":
            {
                // barscan —— 底栏按钮自检：程序自己点一遍、读面板开关、再点回来。
                //
                // ★ 为什么加（2026-11-08）：用户要求「能点到的地方都得点得开」，
                //   而 adb input tap 在本机不可靠（FPS 0.2~0.8，一次点击 1.2~4.8 秒
                //   才处理，命令文件还要 0.5 秒轮询），测试脚本读到的经常是**上一条**
                //   命令的结果，已经因此判出过好几次假故障。
                //   这条由设备端同步跑完，一次调用给出 11 个按钮的确定结论。
                if (Log != null) Log("[CMD] 【底栏自检】" + Bridge.BarScan());
                break;
            }

            case "rank":
            {
                // rank —— 排行榜三边体检（服务端两套榜 / 客户端缓存 / 行控件可见数）
                if (Log != null) Log("[CMD] 【排行榜】" + Bridge.RankSnapshot());
                break;
            }

            case "ppanel":
            {
                // ppanel <名字> [页号] —— 直接把指定面板摆到指定页，供截图验收。
                // 例：ppanel guild 3 → 行会窗「职位」页
                string[] ap = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string nm = ap.Length > 0 ? ap[0] : "guild";
                int pg = -1;
                if (ap.Length > 1) int.TryParse(ap[1], out pg);
                if (Log != null) Log("[CMD] 【面板定位】" + Bridge.ShowPanel(nm, pg));
                break;
            }

            case "clickaudit":
            {
                // clickaudit —— 运行时找「看得见、点得着，但 Click 没人订阅」的按钮。
                //
                // ★ 为什么加（2026-11-08）：静态扫源码判不准 —— 数组下标写法
                //   （ClassButton[i].Click +=）匹配不到、滚动条只绑 OnMoving 也误报。
                //   运行时读 Click 事件的委托链是唯一确定的答案：null 就是点了没反应。
                if (Log != null) Log("[CMD] 【点击体检】" + Bridge.ClickAuditAll());
                break;
            }

            case "panels":
            {
                // panels —— 一次读出全部面板的开关状态（1=开 0=关 -=不存在）。
                //
                // ★ 为什么加（2026-11-08）：验收「点一下必须开、再点一下必须关」时，
                //   截图比像素会被地图滚动/怪物走动污染（实测同一动作 38%~42% 差别，
                //   全是背景在动），判不出来。这条直接读 Visible，一点歧义都没有。
                if (Log != null) Log("[CMD] 【面板开关】" + Bridge.AllPanels());
                break;
            }

            case "ui":
            {
                // ui —— 控件树体检：列出「可见但画不出图」的控件。
                //
                // ★ 为什么需要它（2026-11-04）：
                //   本机素材是 1.76 版（Prguse.Lib 只有 1770 帧），Crystal 源码按
                //   完整版写成，大量控件引用 1900~2200 号帧。越界时 MLibrary.Draw
                //   静默 return —— 不报错、不崩溃、不留日志，画面上就是少一块。
                //   静态扫源码只能给候选（有的被 Mir176Hud 覆盖、有的被父级隐藏），
                //   只有跑起来遍历真控件树才能定论。
                if (Log != null) Log("[CMD] 【UI体检】" + Bridge.UiAudit());
                break;
            }

            case "uiall":
            {
                // uiall —— 把每个面板逐个打开、体检、再关回去。
                //
                // ★ ui 只能扫「当前可见」的控件树，而玩家一进游戏，菜单/背包/
                //   角色/大地图/行会/交易这些面板**默认全隐藏**，扫不到。
                //   静态审计报的 157 处越界绝大多数正好躺在这些面板里 ——
                //   只有真把它们打开一遍，才知道哪个面板一打开就是一片空白。
                // ★ 2026-11-09：58 个面板一次全塞进一行会被 logcat 截断
                //   （实测尾部 ItemRenting 系列整段丢失，只剩 "It"）。
                //   按 " ; " 切成多段，每段单独一行输出。
                {
                    string all = Bridge.UiAuditAll();
                    if (Log != null)
                    {
                        int sep = all.IndexOf(" ‖ ", StringComparison.Ordinal);
                        if (sep < 0) { Log("[CMD] 【UI全检】" + all); break; }
                        Log("[CMD] 【UI全检】" + all.Substring(0, sep + 3));
                        string[] segs = all.Substring(sep + 3).Split(new[] { " ; " }, StringSplitOptions.None);
                        var chunk = new System.Text.StringBuilder("[CMD] 【UI全检】");
                        for (int i = 0; i < segs.Length; i++)
                        {
                            if (chunk.Length + segs[i].Length > 700)
                            {
                                Log(chunk.ToString());
                                chunk.Length = 0;
                                chunk.Append("[CMD] 【UI全检】");
                            }
                            chunk.Append(segs[i]);
                            if (i < segs.Length - 1) chunk.Append(" ; ");
                        }
                        if (chunk.Length > 0) Log(chunk.ToString());
                    }
                }
                break;
            }

            case "uitext":
            {
                // uitext —— 列出屏幕上**真显示着**的英文字。
                //
                // ★ 为什么加（2026-11-09）：用户说界面还有很多英文。语言包本身
                //   （Chinese.json 1090 / Chinese.srv.tsv 771）已经全译完，所以
                //   英文只可能是代码里写死的字面量。grep 源码只能给候选，有的
                //   根本不上屏；只有遍历运行中的控件树、把 Text/Hint 里含
                //   A-Za-z 的可见条目全列出来，才是玩家眼圈的真答案。
                //   空结果（★英文=0）= 当前这个画面确实没有英文。
                if (Log != null) Log("[CMD] 【UI英文】" + Bridge.UiEnglish());
                break;
            }

            case "uitextall":
            {
                // uitextall —— 逐个打开 20 个面板查英文，查完关回去。
                //
                // ★ 为什么加（2026-11-09）：uitext 只看得见当前画面上已显示的字，
                //   而行会、寄售、交易、宠物这些面板默认隐藏，扫不到；下拉框
                //   的选项更是要展开才上屏。这条会把每个面板打开扫一遍（含
                //   MirDropDownBox.Items 里没展开的选项），再恢复原状。
                if (Log != null) Log("[CMD] 【UI英文全检】" + Bridge.UiEnglishAll());
                break;
            }

            // ★ 2026-11-03 补：把「只看代码、没在真机上跑过」的几个系统做成可验收命令。
            //   用户问「整个游戏对照源码排查一遍，是不是有很多错」，
            //   光是读代码只能猜，必须能在真机上按一下看到结果才算数。
            case "inv":
            {
                // inv        —— 开/关背包，并列出背包每一格
                // inv list   —— 只列，不开关
                if (arg != "list")
                {
                    bool open = Bridge.ToggleInventory();
                    if (Log != null) Log("[CMD] 背包可见=" + open);
                }
                if (Log != null)
                {
                    Log("[CMD] 背包格: " + Bridge.InventoryIds());
                    Log("[CMD] 身上: " + Bridge.EquippedNames());
                }
                break;
            }

            case "char":
            {
                // char —— 开/关角色面板（属性、装备栏）
                bool open = Bridge.ToggleCharacter();
                if (Log != null)
                {
                    Log("[CMD] 角色面板可见=" + open);
                    var u = GameScene.User;
                    if (u != null)
                        Log("[CMD] 属性 等级=" + u.Level + " 职业=" + u.Class +
                            " HP=" + u.HP + "/" + (u.Stats != null ? u.Stats[Stat.HP] : 0) +
                            " MP=" + u.MP + "/" + (u.Stats != null ? u.Stats[Stat.MP] : 0) +
                            " 经验=" + u.Experience + "/" + u.MaxExperience);
                }
                break;
            }

            case "bag":
            {
                // bag all —— 把背包里能穿的自动穿上（验装备栏位判定对不对）
                string diag = "";
                int n = Bridge.EquipAllFromInventory(out diag);
                if (Log != null) Log("[CMD] 自动穿戴 " + n + " 件 ｜ " + diag +
                    " ｜ 现在身上=" + Bridge.EquippedNames());
                break;
            }

            case "eqp":
            {
                // eqp —— 装备穿脱自检：手上拿着哪一格 / 装备面板每格有什么 / 服务端已穿什么
                if (Log != null) Log("[CMD] " + Bridge.EquipProbe());
                break;
            }

            case "use":
            {
                // use 治愈术 —— 使用背包里的物品（技能书/药/卷轴），走原版 UseItem 同一条路。
                string diag = "";
                int n = Bridge.UseFromInventory(arg, out diag);
                if (Log != null) Log("[CMD] 使用 " + n + " 件 ｜ " + diag);
                break;
            }

            case "potion":
            {
                // potion —— 一键喝药（HP 低时用金创药）
                string s = Bridge.AutoPotion();
                if (Log != null) Log("[CMD] 喝药: " + s);
                break;
            }

            case "vitals":
            {
                int hpPct, mpPct, hp, maxHp;
                Bridge.GetVitals(out hpPct, out mpPct, out hp, out maxHp);
                if (Log != null) Log("[CMD] 血=" + hp + "/" + maxHp + "(" + hpPct + "%) 蓝=" + mpPct + "%");
                break;
            }

            case "revive":
            {
                // revive —— 发真实的 C.TownRevive 包，走服务端 TownRevive 回城复活。
                //
                // ★ 为什么加（2026-11-12）：角色死后服务端 PlayerObject.CallNPC 第一行就是
                //   `if (Dead) return;`（PlayerObject.cs:8088），之后**任何 NPC 对话都静默无效** ——
                //   验收 NPC 买卖/炼药时踩到这个，会误判成「NPC 脚本坏了」。
                //   客户端原本只有死亡弹窗 YesButton 才发这个包，而弹窗靠 ShowReviveMessage
                //   + User.CurrentAction == MirAction.Dead 触发，FPS 1~3 时经常来不及。
                //   @REVIVE 走不通：那是 GM 命令，Account.AdminAccount 为 false 时 IsGM=false 直接 return。
                var u = GameScene.User;
                if (u == null) { if (Log != null) Log("[CMD] 还没进游戏"); break; }
                if (!u.Dead) { if (Log != null) Log("[CMD] 没死，不用复活"); break; }

                // ★ Client.MirNetwork.Network 是 internal（Network.cs:9 `static class Network`），
                //   工程里直接写 Client.MirNetwork.Network 报 CS0122「protection level」。
                //   只能反射。Enqueue 本身是 public static（Network.cs:306）。
                try
                {
                    var asm = typeof(GameScene).Assembly;
                    var netType = asm.GetType("Client.MirNetwork.Network");
                    if (netType == null) { if (Log != null) Log("[CMD] ★ 找不到 Client.MirNetwork.Network"); break; }
                    var mi = netType.GetMethod("Enqueue",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (mi == null) { if (Log != null) Log("[CMD] ★ 找不到 Network.Enqueue"); break; }
                    mi.Invoke(null, new object[] { new C.TownRevive() });
                    if (Log != null) Log("[CMD] 已发 TownRevive 复活请求");
                }
                catch (System.Exception ex)
                {
                    if (Log != null) Log("[CMD] ★ 复活失败 " + ex.GetType().Name + " " + ex.Message);
                }
                break;
            }

            case "close":
            {
                // close —— 关掉 NPC 对话
                Bridge.CloseNpcDialog();
                if (Log != null) Log("[CMD] 已关 NPC 对话");
                break;
            }

            case "closeall":
            {
                // closeall —— 一次关掉全部可开面板，让基线回到「干净出厂态」。
                //
                // ★ 为什么加（2026-11-08）：验收「点一下必须开、再点一下必须关」时，
                //   最怕上一轮的残留面板留在屏幕上。以前每个测试脚本开头只发 close
                //   （那只关 NPC 对话），小地图/好友/行会却一直开着，于是「基线」
                //   本身就不干净 —— 实测打开好友时像素变化 4%，关闭后仍有 4% 残留，
                //   差点被误判成「关不掉」。
                //   这条把 GameScene 里所有能 Show 的面板统一 Hide，等价于原版的
                //   KeybindOptions.Closeall 分支（GameScene.cs:688-728）。
                if (Log != null) Log("[CMD] 【关全部】" + Bridge.CloseAllPanels());
                break;
            }

            case "walkto":
            {
                // walkto 330 270 —— 自动寻路走过去
                var sp = arg.Split(' ');
                int wx, wy;
                if (sp.Length >= 2 && int.TryParse(sp[0], out wx) && int.TryParse(sp[1], out wy))
                {
                    int steps = Bridge.WalkTo(wx, wy);
                    if (Log != null) Log("[CMD] 走向 (" + wx + "," + wy + ") 路径=" + steps + " 步");
                }
                else if (Log != null) Log("[CMD] 用法: walkto <x> <y>");
                break;
            }

            case "pickup":
            {
                bool ok = Bridge.PickUp();
                if (Log != null) Log("[CMD] 捡物 ok=" + ok + " 脚下=" + Bridge.ItemUnderFeet());
                break;
            }

            case "enter":
            {
                // enter —— 从角色选择界面进游戏（2026-11-04 加）。
                //   为什么需要：NetDriver.AutoEnter 默认 false（用户要的行为：点开 APP 先看到
                //   角色列表，自己点进去），于是每次重启 App 都停在选角界面。
                //   手机上没键盘、没鼠标，agent 验收时只能靠 adb input tap 猜坐标点按钮 ——
                //   这条命令直接把「点角色」这一步变成确定性的。
                if (Log != null)
                {
                    var cur = NetDriver.Current;
                    if (cur == NetDriver.Phase.InGame)
                        Log("[CMD] 已经在游戏里了 phase=" + cur);
                    else
                    {
                        NetDriver.EnterGame(CMain.Time);
                        Log("[CMD] 触发进游戏 phase(before)=" + cur + " 角色=" + NetDriver.CharacterName);
                    }
                }
                break;
            }

            case "load":
            {
                // load —— 服务端负载快照（2026-11-04 加）。
                //   进程线程取证显示 EmbeddedMirServ utime=63s，是渲染之外第二座山。
                //   这里量出「480 张图里到底几张有人在、多少刷怪点、多少只怪」，
                //   用来判断「只处理玩家所在图」这条裁剪能省多少。
                if (Log != null) Log("[CMD] 【负载】" + Bridge.ServerLoadSnapshot());
                break;
            }

            case "throttle":
            {
                // throttle on|off —— 运行时空图降频开关（2026-11-04 加）。
                //   这项裁剪的收益必须在同一会话里 A/B 才可信（跨次启动的刷怪相位
                //   和 GC 状态都不一样）；改代码重打包一次 5 分钟，运行时切换几十秒。
                if (Log == null) break;
                string m = (arg ?? "").Trim().ToLowerInvariant();
                if (m != "on" && m != "off" && m != "1" && m != "0")
                {
                    Log("[CMD] throttle 用法：throttle on|off");
                    break;
                }
                bool on = (m == "on" || m == "1");
                Log("[CMD] 【降频】" + Bridge.SetMapThrottle(on));
                break;
            }

            case "items":
            {
                // items [关键词] [数量] —— 倒服务端已加载的物品名（2026-11-09 加）。
                //   用途：写 Envir/Recipe/*.txt 配方时必须有逐字正确的物品名；
                //   物品定义在 Server.MirDB 里是二进制，猜名字写进去会被
                //   Envir.GetItemInfo 返回 null 静默跳过，面板看起来「没配方」。
                if (Log == null) break;
                string kw = null;
                int lim = 120;
                if (!string.IsNullOrEmpty(arg))
                {
                    var iargs = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (iargs.Length > 0) kw = iargs[0];
                    if (iargs.Length > 1) int.TryParse(iargs[1], out lim);
                }
                Log("[CMD] 【物品】" + Bridge.ServerItemNames(kw, lim));
                break;
            }

            case "drops":
            {
                // drops [怪名] [数量] —— 查「杀怪不掉东西/经验不动」的根因（2026-11-09 加）。
                //   一次打全：工作目录 / DropPath 实际解析 / 怪物库里 Drops.Count 的分布 /
                //   指定怪的定义行 / 场上活的怪的 EXPOwner+Master / 玩家 CanGainExp+地图开关。
                if (Log == null) break;
                string dkw = null;
                int dlim = 6;
                if (!string.IsNullOrEmpty(arg))
                {
                    var dargs = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (dargs.Length > 0) dkw = dargs[0];
                    if (dargs.Length > 1) int.TryParse(dargs[1], out dlim);
                }
                Log("[CMD] 【掉落】" + Bridge.MonsterDropText(dkw, dlim));
                break;
            }

            case "mem":
            {
                // mem —— 内存去向（2026-11-04 加）。
                //   症状：托管堆 1.8GB、进程 RSS 2.2GB，但 CPU 只跑 0.67 核就只出 2~3 FPS。
                //   这说明瓶颈是「内存压力 → 换页 / gen2 GC 停顿」，不是算力。
                //   只看一个 GC.GetTotalMemory() 总数定不了位，必须按大块拆开量：
                //     服务端 480 张地图的格数组 / 矿洞对象 / 客户端图库纹理。
                if (Log != null)
                {
                    // 全部走 Bridge：DXManager 是 internal class（无 public 修饰符），
                    // Unity 侧 Assembly-CSharp 直接访问会 CS0122 —— 上一版就是这么挂的。
                    Log("[CMD] 【内存】" + Bridge.MemSnapshot());
                }
                break;
            }

            case "audio":
            {
                // audio —— 音频链路自检（2026-11-04 加）。
                //   没有这条只能靠耳朵，而模拟器上根本听不见。
                //   要看的三个数：
                //     回调    在涨 = Unity 音频线程真的在要数据
                //     送出采样 在涨 = 我们的混音器真的在算
                //     活动源  > 0 = 游戏逻辑真的 PlaySound 了
                if (Log != null) Log("[CMD] 【音频】" + P2Audio.Status());
                break;
            }

            case "playsound":
            {
                // playsound <索引> —— 直接播一个音效索引（绕过游戏逻辑，验链路用）。
                //   10104 = SoundList.ButtonB（测试里唯一有文件的按钮音）。
                if (Log == null) break;
                int idx;
                if (!int.TryParse((arg ?? "").Trim(), out idx)) idx = 10104;
                try
                {
                    Client.MirSounds.SoundManager.PlaySound(idx);
                    Log("[CMD] 【音频】直接播放索引 " + idx
                        + "  → 活动源=" + NAudio.Wave.HostAudio.InputCount);
                }
                catch (Exception e)
                {
                    Log("[CMD] ★播放失败 " + idx + " " + e.GetType().Name + ": " + e.Message);
                }
                break;
            }

            case "soundlst":
            {
                // soundlst —— 打印 SoundList.lst 载入情况 + 任意一个索引对应哪个文件。
                //   「有声音吗」的第一问永远是「lst 载入了吗、文件在不在」。
                if (Log == null) break;
                var idxs = Client.MirSounds.SoundList.Indexes;
                Log("[CMD] 【音频】" + Client.MirCore.Bridge.AudioDiag());
                if (idxs != null)
                {
                    int shown = 0;
                    foreach (var kv in idxs)
                    {
                        string p = System.IO.Path.Combine(Client.MirCore.Bridge.GetSoundPath(), kv.Value);
                        bool ok = System.IO.File.Exists(p);
                        Log("[CMD]   " + kv.Key + " -> " + kv.Value + (ok ? "  ✔" : "  ✘缺"));
                        if (++shown >= 8) break;
                    }
                }
                break;
            }

            case "ab":
            {
                // ab <模式> —— 帧耗时 A/B 开关（2026-11-04 加）。
                //
                //   为什么需要：状态行显示「帧周期=446ms 体内=71ms」，即在 Update 之外
                //   每帧有约 375ms 不知去向。这个缺口只有几种可能，靠远程开关逐个排除
                //   比反复改代码重打包（每次 ~4 分钟）快得多：
                //     ab full   全开（默认，正常游戏）
                //     ab noown  跳过我们自己的 MapControl 纹理重建（只画黑底）
                //     ab noui   跳过原版控件树 Draw（只画地图）
                //     ab nopost 跳过每帧 Upload()（纹理不上传 GPU）
                //     ab none   全关（每帧只做网络 Step，什么都不画）
                if (Log == null) break;
                string mode = (arg ?? "").Trim().ToLowerInvariant();
                if (mode == "full") P2Core.AbMask = 0;
                else if (mode == "nosw") P2Core.AbMask = P2Core.AB_SKIP_SOFTWARE;
                else if (mode == "noup") P2Core.AbMask = P2Core.AB_SKIP_UPLOAD;
                else if (mode == "noblit") P2Core.AbMask = P2Core.AB_SKIP_BLIT;
                else if (mode == "none") P2Core.AbMask = P2Core.AB_SKIP_SOFTWARE | P2Core.AB_SKIP_UPLOAD | P2Core.AB_SKIP_BLIT;
                else { Log("[CMD] ab 用法：ab full|nosw|noup|noblit|none"); break; }
                Log("[CMD] 【A/B】AbMask=" + P2Core.AbMask + " 模式=" + (mode == "" ? "(空)" : mode));
                break;
            }

            case "mons":
            {
                // mons [n] —— 列画面内怪物的完整绘制状态（诊断「怪看不见」用）
                if (Log == null) break;
                int n = 6;
                int.TryParse(arg, out n);
                if (n <= 0) n = 6;
                Log("[CMD] 【怪物】" + Client.MirCore.NetDriver.MonsterPosText(n));
                break;
            }

            case "itempos":
            {
                // itempos [n] —— 地面物品的绘制状态（图库/帧号/尺寸/屏幕坐标）。
                //   用 drops 只能证明「服务端掉出来了、客户端收到了」，
                //   证明不了「画出来了」；这条补的就是最后一环。
                if (Log == null) break;
                int n = 8;
                int.TryParse(arg, out n);
                if (n <= 0) n = 8;
                Log("[CMD] 【地面物品】" + Client.MirCore.NetDriver.ItemPosText(n));
                break;
            }

            case "mon":
            {
                // mon <名字片段> —— 只查一个名字，一行短输出（名/图号/库文件名/帧/尺寸/主人ID）。
                // 比 mons 强的地方：不会被 logcat 单行长度截断。
                if (Log == null) break;
                if (arg.Length == 0) { Log("[CMD] mon 用法：mon 骷髅"); break; }
                Log("[CMD] 【查怪】" + Client.MirCore.NetDriver.MonsterByNameText(arg));
                break;
            }

            case "lock":
            {
                // lock [名字片段] —— 锁定攻击目标（等同玩家按攻击键）。
                //
                // ★ 为什么加（2026-11-13）：验任务「击杀计数」时必须真打死怪。
                //   一直以为点屏幕上的怪就能打，实测**点不到**：原版 OnMouseClick
                //   左键只处理 NPC，怪物要靠 MapControl 的 MouseOver 命中 ——
                //   而手机上那一步要求预先 OnMouseMove 把 MouseObject 刷出来，
                //   触摸链路只投一次合成 Move，怪物又一直在走，所以 MouseObject 恒空，
                //   发Attack 永远是 0。玩家真机上是按**攻击键**打的（P2VirtualPad
                //   的 ToggleAttackLock：锁最近活怪 → 原版 CheckInput 自动贴身 Attack1）。
                //   这条命令复刻同一条路，只是能把目标指到指定名字上。
                if (Log == null) break;
                var user0 = MapObject.User;
                if (user0 == null) { Log("[CMD] 还没进游戏"); break; }
                MapObject best = null;
                int bestD = int.MaxValue;
                var list = Client.MirScenes.MapControl.ObjectsList;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var ob = list[i];
                        if (ob == null || ob == user0 || ob.Dead) continue;
                        if (ob.Race != ObjectType.Monster) continue;
                        if (arg.Length > 0 && (ob.Name == null || !ob.Name.Contains(arg))) continue;
                        int dist = Functions.MaxDistance(user0.CurrentLocation, ob.CurrentLocation);
                        if (dist < bestD) { bestD = dist; best = ob; }
                    }
                if (best == null) { Log("[CMD] 【锁定】附近没有" + (arg.Length > 0 ? "「" + arg + "」" : "怪")); break; }
                MapObject.TargetObjectID = best.ObjectID;
                Log("[CMD] 【锁定】" + best.Name + " 距离=" + bestD
                    + " 位置=(" + best.CurrentLocation.X + "," + best.CurrentLocation.Y + ")");
                break;
            }

            case "next":
            case "cycle":
            {
                // next —— 循环切换目标（等同玩家按「换怪」键）。
                //
                // 走的是 P2VirtualPad.CycleTarget()，和手指按键**完全相同**的一条路，
                // 所以这条命令能把按钮逻辑一起验掉，不是另写一份挑怪代码。
                // 连按 N 次就靠调用方连发 N 行 next。
                if (Log == null) break;
                if (MapObject.User == null) { Log("[CMD] 还没进游戏"); break; }
                bool ok = P2VirtualPad.CycleTarget();
                var tgt = MapObject.TargetObject;
                int d = tgt == null || MapObject.User == null ? -1
                        : Functions.MaxDistance(MapObject.User.CurrentLocation, tgt.CurrentLocation);
                Log("[CMD] 【换目标】" + (ok && tgt != null
                        ? tgt.Name + " 距离=" + d + " id=" + tgt.ObjectID
                          + " 位置=(" + tgt.CurrentLocation.X + "," + tgt.CurrentLocation.Y + ")"
                        : "附近没有可打的怪（已清空锁定）"));
                break;
            }

            case "trade":
            {
                // trade [名字] —— 现场伪造一个「XXX 想和你交易」的弹窗，用于验收交易邀请链路。
                // ★ 2026-11-04 加：不加这条就只能等第二个玩家真的来点你，
                //   而单机内嵌服根本没别人 —— 那样 IsTradeInvite 分支永远跑不到。
                //   走反射调 GameScene.TradeRequest(S.TradeRequest)：它构造的框和
                //   服务端真发来的完全一样（同一个 MirMessageBox + YesNo + 两个 Click），
                //   所以验的是**真链路**，不是另写一份假弹窗。
                if (Log == null) break;
                var gs = GameScene.Scene;
                if (gs == null) { Log("[CMD] 还没进游戏"); break; }
                string who = string.IsNullOrEmpty(arg) ? "测试员" : arg;
                try
                {
                    var mi = gs.GetType().GetMethod("TradeRequest",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (mi == null) { Log("[CMD] ★ 找不到 TradeRequest 方法"); break; }
                    mi.Invoke(gs, new object[] { new ServerPackets.TradeRequest { Name = who } });
                    Log("[CMD] 【交易】已伪造邀请，来自「" + who + "」→ 应弹 YesNo 且**不被自动按否**");
                }
                catch (System.Exception ex)
                {
                    Log("[CMD] ★ 伪造交易邀请失败 " + ex.GetType().Name + " " + ex.Message);
                }
                break;
            }

            case "self":
            {
                // self —— 一次性把「我这个角色」的全部状态打出来，验收用
                var u = GameScene.User;
                if (u == null) { if (Log != null) Log("[CMD] 还没进游戏"); break; }
                if (Log != null)
                {
                    Log("[CMD] 【角色】" + u.Name + " Lv" + u.Level + " " + u.Class + " " + u.Gender +
                        " 位置=" + u.CurrentLocation + " 方向=" + u.Direction +
                        " HP=" + u.HP + "/" + (u.Stats != null ? u.Stats[Stat.HP] : 0) +
                        " MP=" + u.MP + "/" + (u.Stats != null ? u.Stats[Stat.MP] : 0) +
                        " 经验=" + u.Experience + "/" + u.MaxExperience);
                    Log("[CMD] 【身上】" + Bridge.EquippedNames());
                    Log("[CMD] 【背包】" + Bridge.InventoryIds());
                    Log("[CMD] 【技能】" + Bridge.SkillSlots());
                    Log("[CMD] 【腰带】" + (Bridge.BeltNames() ?? ""));
                    Log("[CMD] 【场景】" + (GameScene.Scene != null
                        ? "NPC对话=" + (GameScene.Scene.NPCDialog != null && GameScene.Scene.NPCDialog.Visible) +
                          " 包裹=" + (GameScene.Scene.InventoryDialog != null && GameScene.Scene.InventoryDialog.Visible) +
                          " 角色=" + (GameScene.Scene.CharacterDialog != null && GameScene.Scene.CharacterDialog.Visible) +
                          " 技能页=" + (GameScene.Scene.SkillBarDialogs != null && GameScene.Scene.SkillBarDialogs.Count > 0 && GameScene.Scene.SkillBarDialogs[0].Visible)
                        : "无场景"));
                }
                break;
            }

            case "newchar":
            {
                // newchar <名字> <warrior|wizard|taoist> [male|female]
                //   —— 在选角界面确定性地建一个角色（2026-11-04 加）。
                //
                // ★ 为什么需要：手机上建角必须「点按钮 → 弹键盘 → 输名字 → 按完成」，
                //   而 BACK 收键盘会连带把名字框清空、按钮又变回灰的（实测），
                //   靠 adb tap 猜坐标验收极其脆弱。这条命令直接把请求发出去，
                //   用来验证「服务端建角 + 客户端角色列表刷新」这条链路本身。
                if (Log != null)
                {
                    var cur = NetDriver.Current;
                    if (cur == NetDriver.Phase.InGame)
                    {
                        Log("[CMD] 已经在游戏里了，先退回选角界面再建角");
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        Log("[CMD] 用法: newchar <名字> <warrior|wizard|taoist> [male|female]");
                        break;
                    }
                    var a = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string name = a[0];

                    // ★ 2026-11-08 修：原来只认英文 warrior/wizard/taoist + male/female，
                    //   敲中文（「道士」「男」）会静默落到默认值 Warrior/Male ——
                    //   日志实测「建角请求 名字=测试道 职业=Warrior 性别=Male」。
                    //   这里 (1) 补中文别名；(2) **不依赖顺序**，逐个 token 自己认，
                    //   所以「newchar 测试道 男 道士」和「newchar 测试道 道士 男」都对；
                    //   (3) 没认出来的 token 写进日志，不再静默吞掉。
                    MirClass mc = MirClass.Warrior;
                    MirGender mg = MirGender.Male;
                    bool clsKnown = false, sexKnown = false;
                    for (int i = 1; i < a.Length; i++)
                    {
                        switch (a[i].ToLowerInvariant())
                        {
                            case "wizard": case "法师": case "魔法": case "法":
                                if (clsKnown) continue; mc = MirClass.Wizard; clsKnown = true; continue;
                            case "taoist": case "道士": case "道": case "tao":
                                if (clsKnown) continue; mc = MirClass.Taoist; clsKnown = true; continue;
                            case "warrior": case "战士": case "战": case "war":
                                if (clsKnown) continue; mc = MirClass.Warrior; clsKnown = true; continue;
                            case "male": case "男": case "男号": case "m":
                                if (sexKnown) continue; mg = MirGender.Male; sexKnown = true; continue;
                            case "female": case "女": case "女号": case "f":
                                if (sexKnown) continue; mg = MirGender.Female; sexKnown = true; continue;
                            default:
                                Log("[CMD] newchar 参数「" + a[i] + "」不认识，已忽略（可用：warrior/战士  wizard/法师  taoist/道士  male/男  female/女）");
                                continue;
                        }
                    }

                    Log("[CMD] 建角请求 名字=" + name + " 职业=" + mc + (clsKnown ? "" : "(没给，默认)") +
                        " 性别=" + mg + (sexKnown ? "" : "(没给，默认)") + " phase=" + cur);
                    NetDriver.CreateCharacterNow(name, mc, mg, CMain.Time);
                }
                break;
            }

            case "delchar":
            {
                // delchar <名字> —— 删掉一个角色（2026-11-04 加）。
                // 用途：1.76 每个账号最多 2 个角色（Globals.MaxCharacterCount），
                // 角色位占满后建角会被服务端回 Result=4。验收建角流程前先用这条腾位。
                if (Log == null) break;
                if (string.IsNullOrWhiteSpace(arg)) { Log("[CMD] 用法: delchar <名字>"); break; }
                NetDriver.DeleteCharacterNow(arg.Trim(), CMain.Time);
                break;
            }

            case "charinfo":
            {
                // charinfo —— 列出当前账号全部角色（选角界面验收用）
                if (Log != null)
                {
                    var cs = NetDriver.Characters;
                    if (cs == null || cs.Count == 0) { Log("[CMD] 【角色列表】空"); break; }
                    var sb = new StringBuilder();
                    sb.Append("【角色列表】").Append(cs.Count).Append(" 个：");
                    for (int i = 0; i < cs.Count; i++)
                    {
                        var c = cs[i];
                        sb.Append(i == 0 ? " " : " | ")
                          .Append(c.Name).Append('(').Append(c.Class).Append(' ')
                          .Append(c.Gender).Append(" Lv").Append(c.Level).Append(')');
                    }
                    Log("[CMD] " + sb);
                }
                break;
            }

            default:
                if (Log != null) Log("[CMD] 不认识的命令: " + cmd);
                break;
        }
    }

    static void Chat(string msg)
    {
        bool ok = Bridge.SendChat(msg);
        if (Log != null) Log("[CMD] 发送聊天「" + msg + "」 ok=" + ok);
    }

    static void DoTalk(string want)
    {
        var user = MapObject.User;
        if (user == null) { if (Log != null) Log("[CMD] 还没进游戏"); return; }

        int x, y; uint id; string name;
        bool found = want != null && want.Length > 0
            ? Bridge.FindNpcByName(want, 30, out x, out y, out id, out name)
            : Bridge.FindNearestNpc(20, out x, out y, out id, out name);
        if (!found)
        {
            if (Log != null) Log("[CMD] 视野内没有 NPC" + (want != null && want.Length > 0 ? "「" + want + "」" : ""));
            return;
        }
        int d = Functions.MaxDistance(user.CurrentLocation, new System.Drawing.Point(x, y));
        if (d <= 1)
        {
            Bridge.CallNpc(id, "[@Main]");
            if (Log != null) Log("[CMD] 直接喊话 " + name);
            return;
        }
        int steps = Bridge.WalkTo(x, y);
        if (Log != null) Log("[CMD] 走向 " + name + " 距离=" + d + " 路径=" + steps + " 步");

        // 交给 StepWalk 每帧推进（见 Tick 里的说明）
        StartWalk(x, y, id, name, true);
    }

    /// <summary>
    /// 掐掉命令行发起的那条走路任务（walk / talknpc）。
    ///
    /// ★ 2026-11-15 实机定案：不掐会把角色往反方向拽。
    ///   现象：先 `walk 320 270` 记下了一条 196 步的长路，接着点技能键让外壳
    ///   （P2VirtualPad.TickPendingCast）自己寻路去打怪 —— WalkTo 覆盖了路线，
    ///   人确实朝怪走了几格；但一旦那条 A* 走完/被取消，StepWalk 里
    ///   `if (Bridge.WalkRemaining() > 0) return;` 掉到下面的每 0.5 秒重算，
    ///   **又照 _npcX/_npcY = (320,270) 重新寻过去**，实机日志
    ///   `寻路=剩196步 AutoPath=True 终点=(320,270)` 就是这么来的：
    ///   玩家的技能意图和命令行的旧路线互相踩。
    /// </summary>
    public static void CancelCmdWalk()
    {
        if (!_npcWalking) return;
        _npcWalking = false;
        try { Bridge.StopWalk(); } catch { }
        if (Log != null) Log("[CMD] 命令行走路被接管，取消 → " + _npcName + "(" + _npcX + "," + _npcY + ")");
    }

    /// <summary>接管一条走路任务，交给 StepWalk 每帧推进。talkAtEnd=true 表示到底后要喊 NPC。</summary>
    static void StartWalk(int x, int y, uint id, string name, bool talkAtEnd)
    {
        _npcX = x; _npcY = y; _npcId = id; _npcName = name;
        _npcLastX = -1; _npcLastY = -1;             // 重置「原地不动」判定
        _npcDeadline = Time.unscaledTime + 8f;
        _npcRepathT = 0f;
        _talkAtEnd = talkAtEnd;
        _npcWalking = true;
    }

    // ── talknpc / walk 走路状态 ──
    static bool _npcWalking;
    static bool _talkAtEnd;
    static int _npcX, _npcY;
    static uint _npcId;
    static string _npcName;
    static float _npcDeadline;
    static int _npcLastX = -1, _npcLastY = -1;

    /// <summary>
    /// 每帧推进 talknpc 的走路：续段 + 到了就喊话。
    /// 和 P2VirtualPad.TryWalkToNpc 是同一套逻辑（分段续命 + 贴身重新问 ID），
    /// 但独立维护状态，两边互不干扰。
    /// </summary>
    static void StepWalk()
    {
        if (!_npcWalking) return;
        var user = MapObject.User;
        if (user == null) { _npcWalking = false; return; }

        var here = user.CurrentLocation;
        if (here.X != _npcLastX || here.Y != _npcLastY)
        {
            _npcLastX = here.X; _npcLastY = here.Y;
            _npcDeadline = Time.unscaledTime + 8f;      // 还在挪 → 续命
        }
        else if (Time.unscaledTime > _npcDeadline)
        {
            Bridge.StopWalk();
            _npcWalking = false;
            if (Log != null) Log("[CMD] 走不过去，放弃 " + _npcName + " 停在(" + here.X + "," + here.Y + ")");
            return;
        }

        int d = Functions.MaxDistance(here, new System.Drawing.Point(_npcX, _npcY));
        // ★ 到 2 格就算到（2026-11-02 实测修）。
        //   原来写 d<=1 要求贴死相邻格，实测在比奇省 (598,651)→目标(600,650) 卡在
        //   距离=2、路径=1 步，角色在 (598,650)/(598,651) 两格之间**来回横跳 40 秒**：
        //   WalkTo 对「NPC 占着的那一格」不可走，会改用附近的空位，每次选的空位不一样，
        //   于是永远贴身不了。服务端 PlayerObject.cs:8060 只要求 InRange(…, DataRange=16)
        //   + NPC 可见，根本不要求相邻 —— 2 格完全合法。
        if (d <= 2)
        {
            Bridge.StopWalk();
            _npcWalking = false;
            if (!_talkAtEnd)
            {
                if (Log != null) Log("[CMD] 已到达 (" + here.X + "," + here.Y + ") 距目标=" + d);
                return;
            }
            uint id = _npcId;
            if (id == 0)
            {
                int nx, ny; uint nid; string nname;
                if (Bridge.FindNearestNpc(4, out nx, out ny, out nid, out nname)) id = nid;
            }
            if (id == 0)
            {
                if (Log != null) Log("[CMD] 到了 " + _npcName + " 跟前但视野里没对象，未发包");
                return;
            }
            Bridge.CallNpc(id, "[@Main]");
            if (Log != null) Log("[CMD] 走到贴身，已发 CallNPC " + _npcName + " id=" + id);
            return;
        }

        if (Bridge.WalkRemaining() > 0) return;          // 还有路 → 继续

        // 路走完/断了但没贴身 → 每 0.5 秒重算一次
        if (Time.unscaledTime >= _npcRepathT)
        {
            _npcRepathT = Time.unscaledTime + 0.5f;
            int re = Bridge.WalkTo(_npcX, _npcY);
            if (re > 0) { if (Log != null) Log("[CMD] 重新寻路 " + _npcName + " 距离=" + d + " 路径=" + re + " 步"); return; }
            // ★ 重寻路返回 0 = 目标格连贴边都找不到落脚点（在水里/悬崖边/被围死）。
            //   此时再等也是空转，直接收工（原来是等 8 秒「原地不动」超时才结束）。
            Bridge.StopWalk();
            _npcWalking = false;
            if (Log != null) Log("[CMD] 无路可走，停在(" + here.X + "," + here.Y + ") 距目标=" + d
                + " 目标=(" + _npcX + "," + _npcY + ")");
        }
    }
    static float _npcRepathT;

    static void ClickNpcLink(string want)
    {
        var texts = Bridge.NpcButtonTexts();
        if (texts == null || texts.Length == 0)
        {
            if (Log != null) Log("[CMD] 对话框没有链接按钮");
            return;
        }
        // Bridge.ClickNpcButton 是**按文字**点的（精确优先），没有按序号的重载。
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] == null) continue;
            if (want.Length == 0 || texts[i].IndexOf(want, StringComparison.Ordinal) >= 0)
            {
                int bx, by;
                bool ok = Bridge.ClickNpcButton(texts[i], out bx, out by);
                if (Log != null) Log("[CMD] 点链接 [" + i + "]「" + texts[i] + "」 ok=" + ok);
                return;
            }
        }
        if (Log != null) Log("[CMD] 没找到链接「" + want + "」，现有: " + string.Join(" / ", texts));
    }

    static void Report()
    {
        if (Log == null) return;
        // GameScene.User 与 GameScene.Gold 都是 static（GameScene.cs:10439 / :249）
        var u = GameScene.User;
        Log("[CMD] 报告 等级=" + (u != null ? u.Level.ToString() : "?")
            + " 金币=" + GameScene.Gold
            + " 坐标=" + (u != null ? (u.CurrentLocation.X + "," + u.CurrentLocation.Y) : "?"));
        Log("[CMD] 已学技能: " + (string.IsNullOrEmpty(Bridge.MagicList()) ? "（无）" : Bridge.MagicList()));
        Log("[CMD] 技能栏 Bar1[" + Bridge.SkillBarIcons(0) + "] Bar2[" + Bridge.SkillBarIcons(1) + "]");
        var texts = Bridge.NpcButtonTexts();
        Log("[CMD] NPC 对话框可见=" + Bridge.NpcDialogVisible()
            + " 按钮=" + (texts == null ? 0 : texts.Length)
            + (texts == null || texts.Length == 0 ? "" : " 内容=" + string.Join(" / ", texts)));
        Log("[CMD] 开关技能: " + Bridge.SpellToggles());
        Log("[CMD] 腰带: " + (string.IsNullOrEmpty(Bridge.BeltNames()) ? "（空）" : Bridge.BeltNames().Replace(";", " | ")));
        Log("[CMD] 最近聊天: " + Bridge.ChatTail(5));
    }
}
