// 联机模式（单机游玩 / 开服务器 / 加入游戏）—— 游戏内设置面板的数据与网络层。
//
// 用户原话（2026-11-12）：
//   「先是选择，就是单机游玩还是网络游玩。网络游玩的话，就是相当于是我开服务器，
//     我这个手机开服务器，别人手机……直接输入个匹配码，或者说输入一些什么 IP 还是啥的，
//     反正尽量简洁一点，就能通过这种互联网连接到我这个上面。」
//   「或者说是不需要这个开始界面，你正常进游戏，进完游戏左上角或者右上角有一个
//     比较小的按键，类似于那种设置的按键……点开了之后就有这个选项。」
//
// 所以设计成：
//   · 默认【单机游玩】：进游戏就在玩，跟以前一模一样。
//   · 左上角一颗小设置键，点开才有联机选项，不打断游戏。
//   · 「单机」和「开服务器」用的是**同一条内嵌服务端**，区别只是要不要放外机进来：
//       单机   → Envir.LocalOnly = true   外机 accept 阶段就被断开
//       开服   → Envir.LocalOnly = false  局域网里任何一台手机都能连
//     两者**不需要重连、不需要重启服务端**，切了立刻生效。
//   · 「加入游戏」才需要断线重连到别人的地址。
//
// 连接方式（从简到繁，前两条是给玩家的，第三条是兜底）：
//   1) 自动发现：开服端每 1.5 秒往局域网广播一包 UDP，加入端点「搜索」三秒就能看到
//      房间名和 IP，点一下就进 —— 全程不打字，这是主路径。
//   2) 匹配码：开服端屏幕上大字写着本机 IP（如 192.168.1.5），加入端只输后几位
//      「1.5」或「5」甚至「15」，自动补全成同一网段的完整 IP。
//   3) 完整地址：直接输 192.168.1.5:7000，跨网段/端口映射时用。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Client.MirCore;
using UnityEngine;

namespace P2
{
    public enum NetMode
    {
        Single = 0,   // 单机游玩（默认）：内嵌服务端只认本机
        Host = 1,     // 开服务器：内嵌服务端放行局域网
        Join = 2,     // 加入游戏：客户端连到别人的服务端
    }

    /// <summary>局域网里发现的一个房间。</summary>
    public struct NetRoom
    {
        public string Address;   // 192.168.1.5
        public int Port;         // 7000
        public string Name;      // 房间名（手机型号）
        public int Players;      // 在线人数
        public long SeenAtMs;    // 最后一次收到的时刻
        public string Display { get { return Name + "  " + Address + ":" + Port + "  " + Players + "人在线"; } }
    }

    /// <summary>联机模式的状态与网络动作。全部静态，主线程读写。</summary>
    public static class P2NetMode
    {
        public static Action<string> Log = s => { };

        /// <summary>UDP 广播端口。选一个不会和游戏端口(7000)打架的值。</summary>
        const int DiscoveryPort = 17817;
        const int BroadcastIntervalMs = 1500;
        const int RoomKeepAliveMs = 5000;

        /// <summary>
        /// 公网中继（ntfy.sh）。为什么用它：
        ///   跨城市联机唯一的门槛是「加入端怎么知道开服端此刻的 IP」。
        ///   手机 4G/5G 拿到的是随时会变的公网 IPv6，写死地址不现实。
        ///   所以开服端每隔 1.5 秒把「地址:端口|房间名|人数」发到一个按匹配码命名的
        ///   公共频道，加入端输 6 位码就去这个频道读最新一条 —— 相当于一个免费的电话簿。
        ///   ntfy.sh 不需要注册、不需要 key、支持纯 HTTP GET/POST，实测往返可用。
        /// 只用固定前缀 + 6 位码，避免和别人撞频道名。
        /// </summary>
        /// ★ 2026-11-12 修：单点 ntfy.sh 不够用，改成「一组公共 ntfy 实例」。
        //   实测（本机 python，10:5x）ntfy.sh 对匿名发布直接回
        //     429 {"code":42908,"error":"limit reached: daily message quota reached"}
        //   —— 是**整个 IP 一天**的配额用完了，不是我们发太快。当天再发也不会成功。
        //   而这个配额是全家共用的：任何一次调试把额度打光，玩家当天就再也开不了跨城市房间，
        //   屏幕上还写着「已登记」，属于最难查的那种坏。
        //
        //   所以：发布时按顺序挑**第一个发成功的**实例，查询时**逐个都问**再合并结果。
        //   同一条报文在多个实例各存一份不影响正确性（内容一样，取到哪份都能连）。
        //   已实测可用的三个（2026-11-12 10:59）：
        //     ntfy.sh         POST 200 / GET 命中（配额正常时最快）
        //     ntfy.envs.net   POST 200 / GET 命中（1.0s / 0.3s）
        //     ntfy.mzte.de    POST 200 / GET 命中（2.4s / 0.4s）
        //   注意：这些是**第三方公益实例**，随时可能关停。所以是「轮流试」而不是「绑定某一个」，
        //   任何一个活着就行；未来在手机上发现都挂了，往上加地址即可，逻辑不用动。
        static readonly string[] RelayBases = new string[]
        {
            "https://ntfy.sh/mir2p2cn-",
            "https://ntfy.envs.net/mir2p2cn-",
            "https://ntfy.mzte.de/mir2p2cn-",
        };

        // 8 秒一次。为什么不更快：ntfy.sh 免费档对匿名访问有速率限制
        // （突发 60 次、之后每 5 秒补 1 次），1.5 秒一次会被 429 打回来，
        // 反而登记不上。8 秒 = 每分钟 7.5 次，稳稳在配额内；
        // 加入端查询窗口取 20 秒，保证至少命中一条。
        const int PublishIntervalMs = 8000;

        /// <summary>哪个实例上一次发成功了（0..N-1）。先发它，省一次必然 429 的往返。</summary>
        static int _relayOk = 0;

        /// <summary>最近一次真的登记成功是什么时候（<see cref="NowMs"/> 口径，0=从没成功）。</summary>
        static long _pubOkAtMs = 0;

        public static NetMode Mode { get; private set; } = NetMode.Single;
        public static bool SwitchPending { get; private set; }

        static string _localIp = "";
        static string _hostAddress = "127.0.0.1";   // 当前客户端连的地址
        static int _hostPort = 7000;

        // ── 广播（开服时）──
        static Thread _bcast;
        static volatile bool _bcastRun;
        static UdpClient _bcastUdp;

        // ── 发现（加入时）──
        static Thread _listen;
        static volatile bool _listenRun;
        static UdpClient _listenUdp;
        static readonly List<NetRoom> _rooms = new List<NetRoom>();
        static readonly object _roomLock = new object();

        /// <summary>本机在局域网里的 IPv4（拿不到就返回 "0.0.0.0"）。</summary>
        public static string LocalIp
        {
            get
            {
                if (string.IsNullOrEmpty(_localIp) || _localIp == "0.0.0.0") _localIp = DetectLocalIp();
                return _localIp;
            }
        }

        public static int HostPort { get { return _hostPort; } }
        public static string HostAddress { get { return _hostAddress; } }

        /// <summary>给玩家看的匹配码：本机 IP 的后两段，如 192.168.1.5 → "1.5"。</summary>
        public static string MatchCode
        {
            get
            {
                string ip = LocalIp;
                int a = ip.LastIndexOf('.');
                if (a <= 0) return ip;
                int b = ip.LastIndexOf('.', a - 1);
                if (b <= 0) return ip.Substring(a + 1);
                return ip.Substring(b + 1);
            }
        }

        // ────────────────────────────────────────────────────────
        //  启动时：读上次保存的模式
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// 开机时调一次（在 P2Core 里 NetDriver.Configure 之前）。
        /// 返回 true 表示要连到别人，false 表示连本机。
        /// </summary>
        public static bool LoadSaved()
        {
            int m = PlayerPrefs.GetInt("p2.netmode", (int)NetMode.Single);
            string addr = PlayerPrefs.GetString("p2.netaddr", "");
            _hostPort = PlayerPrefs.GetInt("p2.netport", 7000);

            if (m == (int)NetMode.Join && !string.IsNullOrEmpty(addr))
            {
                Mode = NetMode.Join;
                _hostAddress = addr;
                _hostPort = PlayerPrefs.GetInt("p2.netport", 7000);
                _joinRelayCode = PlayerPrefs.GetString("p2.relaycode", "");
                Log("[联机] 上次是「加入游戏」，恢复连接 " + addr + ":" + _hostPort
                    + (_joinRelayCode.Length > 0 ? "（走中继，码 " + _joinRelayCode + "）" : ""));

                // 上次是走中继的：开机就要把中继先拉起来，再连 7100。
                // 不在这里拉的话，客户端会去连一个没人监听的端口，表现成「一直连不上」。
                if (_joinRelayCode.Length > 0)
                {
                    P2Relay.Log = Log;
                    int face = StartRelayJoin(_joinRelayCode);
                    if (face > 0) { _hostAddress = "127.0.0.1"; _hostPort = face; }
                    else Log("★ 开机恢复中继失败 " + P2Relay.LastError);
                }
                return true;
            }

            Mode = (m == (int)NetMode.Host) ? NetMode.Host : NetMode.Single;
            _hostAddress = "127.0.0.1";
            Log("[联机] 模式=" + Mode + "  本机IP=" + LocalIp + "  匹配码=" + MatchCode);
            return false;
        }

        /// <summary>把当前模式应用到内嵌服务端（只需一行：LocalOnly 开关）。</summary>
        public static void ApplyToServer()
        {
            bool local = Mode != NetMode.Host;
            try
            {
                Server.MirEnvir.Envir.LocalOnly = local;
                Log("[联机] 服务端 LocalOnly=" + local + "（" + (local ? "只允许本机" : "允许局域网") + "）");
            }
            catch (Exception e) { Log("★ 设置 LocalOnly 失败 " + e.Message); }

            if (Mode == NetMode.Host)
            {
                StartBroadcast();
                StartPublish();     // 电话簿：把码登记出去，让别人的「查码」能找到
                StartRelayHost();   // ★ 真正的跨城通路：双方都出站，走公共 MQTT
            }
            else
            {
                StopBroadcast();
                StopPublish();
                StopRelayHost();
            }
        }

        // ────────────────────────────────────────────────────────
        //  公网中继接入（2026-11-12）
        //
        //  为什么必须加这一层：原本跨城市靠「IPv6 直连 + ntfy 电话簿」，
        //  电话簿只能告诉加入端「对方地址是多少」，但**对方根本连不进来** ——
        //  本机 IPv6 入站被上游封死（itdog 200 个国内监测点 100% 超时、
        //  chappell 全端口 TCPSTEALTH），IPv4 那侧网关又没有 UPnP/NAT-PMP/PCP。
        //  中继把「连入」变成「双方都往外连同一个公共 broker」，谁都不要开端口。
        //  实测协议三层全通，首包 463ms、登录往返 444ms（见 P2Relay.cs 头注释）。
        // ────────────────────────────────────────────────────────

        static void StartRelayHost()
        {
            P2Relay.Log = Log;
            P2Relay.BeaconInfo = HostBeaconInfo;
            int port = EmbeddedServer.Port > 0 ? EmbeddedServer.Port : _hostPort;
            _relayHostPort = port;
            P2Relay.StartHost(PublicCode, port);
            Log("[联机] 公网中继（开服端）启动  码=" + PublicCode + "  转发到 127.0.0.1:" + port);
        }

        static void StopRelayHost()
        {
            if (!P2Relay.HostRunning && string.IsNullOrEmpty(P2Relay.StatusText())) return;
            P2Relay.StopHost();
            Log("[联机] 公网中继（开服端）已停");
        }

        static int _relayHostPort = 7000;

        /// <summary>中继心跳要报的「我是谁、几个人在玩」。在后台线程被调用，不能碰 Unity API。</summary>
        static void HostBeaconInfo(out string name, out int players)
        {
            name = SafeDeviceName();
            players = OnlineCount();
        }

        /// <summary>
        /// 加入端：起中继，返回客户端该连的本机端口（0 = 失败）。
        /// 起中继要连 broker + 开监听，所以放在查码的后台线程里调，不卡界面。
        /// </summary>
        public static int StartRelayJoin(string code)
        {
            P2Relay.Log = Log;
            if (!P2Relay.StartJoin(code)) return 0;
            return P2Relay.JoinPort;
        }

        public static void StopRelayJoin()
        {
            if (!P2Relay.JoinRunning) return;
            P2Relay.StopJoin();
            Log("[联机] 公网中继（加入端）已停");
        }

        // ────────────────────────────────────────────────────────
        //  模式切换
        // ────────────────────────────────────────────────────────

        /// <summary>切到单机游玩。不重连，立刻生效。</summary>
        public static bool GoSingle()
        {
            if (Mode == NetMode.Join)
            {
                Log("★ 从「加入游戏」切回单机需要重连，请走 SwitchToSelf()");
                return false;
            }
            Mode = NetMode.Single;
            PlayerPrefs.SetInt("p2.netmode", (int)NetMode.Single);
            PlayerPrefs.SetString("p2.relaycode", "");
            PlayerPrefs.Save();
            _joinRelayCode = "";
            ApplyToServer();
            Log("[联机] → 单机游玩");
            return true;
        }

        /// <summary>切到开服务器。不重连，立刻生效。</summary>
        public static bool GoHost()
        {
            if (Mode == NetMode.Join)
            {
                Log("★ 从「加入游戏」切回开服需要重连，请走 SwitchToSelf()");
                return false;
            }
            Mode = NetMode.Host;
            PlayerPrefs.SetInt("p2.netmode", (int)NetMode.Host);
            PlayerPrefs.SetString("p2.relaycode", "");
            PlayerPrefs.Save();
            _joinRelayCode = "";
            ApplyToServer();
            Log("[联机] → 开服务器  本机IP=" + LocalIp + "  端口=" + _hostPort + "  匹配码=" + MatchCode);
            return true;
        }

        /// <summary>断线重连回本机（切模式时必须走这条）。</summary>
        public static void SwitchToSelf()
        {
            Log("[联机] 重连回本机 127.0.0.1:" + _hostPort);
            _hostAddress = "127.0.0.1";
            PlayerPrefs.SetString("p2.netaddr", "");
            PlayerPrefs.SetInt("p2.netport", _hostPort);

            int m = PlayerPrefs.GetInt("p2.netmode", (int)NetMode.Single);
            Mode = (m == (int)NetMode.Host) ? NetMode.Host : NetMode.Single;
            PlayerPrefs.SetString("p2.relaycode", "");
            PlayerPrefs.Save();
            _joinRelayCode = "";
            StopRelayJoin();

            ApplyToServer();
            Repoint("127.0.0.1", _hostPort);
        }

        /// <summary>连到别人。地址解析成功返回 true。</summary>
        public static bool GoJoin(string typed)
        {
            string ip; int port;
            if (!TryParseAddress(typed, out ip, out port))
            {
                Log("★ 地址看不懂：" + typed);
                return false;
            }

            Mode = NetMode.Join;
            _hostAddress = ip;
            _hostPort = port;
            PlayerPrefs.SetInt("p2.netmode", (int)NetMode.Join);
            PlayerPrefs.SetString("p2.netaddr", ip);
            PlayerPrefs.SetInt("p2.netport", port);
            PlayerPrefs.Save();

            // 连出去的时候自己的服务端不该再放人进来（避免两边互相连成一团）
            try { Server.MirEnvir.Envir.LocalOnly = true; } catch { }
            StopBroadcast();
            StopPublish();

            Log("[联机] → 加入游戏 " + ip + ":" + port);
            Repoint(ip, port);
            return true;
        }

        /// <summary>
        /// 把所有连接收束到这一处。
        ///
        /// ★ 2026-11-12：中继也挂在这里。走中继时目标地址是「本机的假服务端」
        /// 127.0.0.1:7100，而真正对外的链路是 P2Relay 起的那两条 MQTT 通道。
        /// 所以重连前先确认中继还活着 —— 中继断了但地址没变的话，
        /// 客户端会一直连上 7100 却什么都收不到（7100 上没人听就会直接拒绝，
        /// 表现成「每 4 秒重连一次，永远连不上」）。
        /// </summary>
        static void Repoint(string ip, int port)
        {
            SwitchPending = true;
            try
            {
                // 存档必须落盘：连着别人时本机服务端的账号库不会再被存盘循环碰到了
                EmbeddedServer.FlushAccounts();
            }
            catch { }

            EnsureRelayForJoin();

            try { Client.MirCore.NetDriver.Repoint(ip, port); }
            catch (Exception e) { Log("★ 重连失败 " + e.Message); }

            SwitchPending = false;
        }

        /// <summary>
        /// 走中继的加入端，重连前保证中继在跑。
        /// 开机恢复上次的「加入游戏」也走这里 —— 否则开机会连到 7100 而中继根本没起。
        /// </summary>
        static void EnsureRelayForJoin()
        {
            if (string.IsNullOrEmpty(_joinRelayCode)) return;
            if (P2Relay.JoinRunning) return;

            Log("[联机] 中继不在跑，重新拉起（码 " + _joinRelayCode + "）");
            P2Relay.Log = Log;
            int face = StartRelayJoin(_joinRelayCode);
            if (face <= 0)
            {
                Log("★ 中继重连失败 " + P2Relay.LastError);
                return;
            }
            _hostAddress = "127.0.0.1";
            _hostPort = face;
        }

        /// <summary>
        /// 把玩家输入的串解析成 IP:端口。支持：
        ///   192.168.1.5          → 192.168.1.5:7000
        ///   192.168.1.5:7001     → 原样
        ///   1.5                  → 本机前三段 + 1.5      （最常见：同一个路由器）
        ///   5                    → 本机前三段 + 5
        ///   15                   → 本机前三段 + 15
        /// 端口省略时用本机当前端口（默认 7000）。
        /// </summary>
        public static bool TryParseAddress(string typed, out string ip, out int port)
        {
            ip = null; port = _hostPort > 0 ? _hostPort : 7000;
            if (string.IsNullOrEmpty(typed)) return false;
            string s = typed.Trim().Replace("：", ":").Replace("。", ".").Replace(" ", "");

            // ★ 2026-11-12：IPv6 字面量必须单独判，不能按「第一个冒号」切端口。
            //   2408:8221:171c:77d0:d08c:4c2e:ba11:d2d8 里有 8 个冒号，
            //   原来切出来的「端口」是 8221:171c:...（解析失败）⇒ 玩家手打公网地址必炸。
            //   规则：冒号 ≥2 个就按 IPv6 处理；带端口的写法用 [地址]:端口。
            int colonCount = 0;
            foreach (char ch in s) if (ch == ':') colonCount++;

            if (colonCount >= 2 || (colonCount == 1 && s.StartsWith("[")))
            {
                string v6 = s;
                if (v6.StartsWith("["))
                {
                    int rb = v6.IndexOf(']');
                    if (rb < 0) return false;
                    string after = v6.Substring(rb + 1);
                    v6 = v6.Substring(1, rb - 1);
                    if (after.Length > 0)
                    {
                        if (after[0] != ':') return false;
                        int p6;
                        if (!int.TryParse(after.Substring(1), out p6) || p6 <= 0 || p6 > 65535) return false;
                        port = p6;
                    }
                }
                System.Net.IPAddress a6;
                if (!System.Net.IPAddress.TryParse(v6, out a6)) return false;
                if (a6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
                ip = v6;
                return true;
            }

            // 带端口
            int c = s.IndexOf(':');
            if (c > 0)
            {
                int p;
                if (!int.TryParse(s.Substring(c + 1), out p) || p <= 0 || p > 65535) return false;
                port = p;
                s = s.Substring(0, c);
            }
            if (s.Length == 0) return false;

            int dots = 0;
            foreach (char ch in s) { if (ch == '.') dots++; else if (ch < '0' || ch > '9') return false; }

            if (dots == 3) { ip = s; return true; }

            // 补全本机网段
            string mine = LocalIp;
            string[] q = mine.Split('.');
            if (q.Length != 4) return false;

            if (dots == 0) { ip = q[0] + "." + q[1] + "." + q[2] + "." + s; return true; }
            if (dots == 1) { ip = q[0] + "." + q[1] + "." + s; return true; }
            if (dots == 2) { ip = q[0] + "." + s; return true; }
            return false;
        }

        // ────────────────────────────────────────────────────────
        //  UDP 广播 / 发现
        // ────────────────────────────────────────────────────────

        static void StartBroadcast()
        {
            if (_bcastRun) return;
            _bcastRun = true;
            _bcast = new Thread(BroadcastLoop) { IsBackground = true, Name = "P2NetBcast" };
            _bcast.Start();
            Log("[联机] 已开始广播（" + DiscoveryPort + "）");
        }

        static void StopBroadcast()
        {
            _bcastRun = false;
            try { if (_bcastUdp != null) _bcastUdp.Close(); } catch { }
            _bcastUdp = null;
            _bcast = null;
        }

        static void BroadcastLoop()
        {
            string name = SafeDeviceName();
            while (_bcastRun)
            {
                try
                {
                    if (_bcastUdp == null) _bcastUdp = new UdpClient();
                    _bcastUdp.EnableBroadcast = true;
                    string msg = "MIRSRV|" + _hostPort + "|" + name + "|" + OnlineCount();
                    byte[] b = Encoding.UTF8.GetBytes(msg);
                    var ep = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
                    _bcastUdp.Send(b, b.Length, ep);
                }
                catch (Exception) { try { _bcastUdp.Close(); } catch { } _bcastUdp = null; }
                Thread.Sleep(BroadcastIntervalMs);
            }
        }

        /// <summary>开始搜索房间（加入游戏面板打开时调）。</summary>
        public static void StartDiscovery()
        {
            if (_listenRun) return;
            _listenRun = true;
            lock (_roomLock) _rooms.Clear();
            _listen = new Thread(DiscoveryLoop) { IsBackground = true, Name = "P2NetDiscover" };
            _listen.Start();
            Log("[联机] 开始搜索局域网房间…");
        }

        public static void StopDiscovery()
        {
            _listenRun = false;
            try { if (_listenUdp != null) _listenUdp.Close(); } catch { }
            _listenUdp = null;
            _listen = null;
        }

        static void DiscoveryLoop()
        {
            while (_listenRun)
            {
                try
                {
                    if (_listenUdp == null)
                    {
                        _listenUdp = new UdpClient();
                        _listenUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        _listenUdp.ExclusiveAddressUse = false;
                        _listenUdp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                    }

                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] b = _listenUdp.Receive(ref from);
                    if (b == null || b.Length == 0) continue;
                    string msg = Encoding.UTF8.GetString(b);
                    string[] f = msg.Split('|');
                    if (f.Length < 4 || f[0] != "MIRSRV") continue;

                    int port; int players;
                    if (!int.TryParse(f[1], out port)) continue;
                    int.TryParse(f[3], out players);

                    string addr = from.Address.ToString();
                    if (addr == LocalIp) continue;      // 自己发的，忽略

                    var r = new NetRoom { Address = addr, Port = port, Name = f[2], Players = players, SeenAtMs = NowMs() };
                    lock (_roomLock)
                    {
                        bool found = false;
                        for (int i = 0; i < _rooms.Count; i++)
                        {
                            if (_rooms[i].Address == addr && _rooms[i].Port == port)
                            { _rooms[i] = r; found = true; break; }
                        }
                        if (!found)
                        {
                            _rooms.Add(r);
                            Log("[联机] 发现房间 " + r.Display);
                        }
                    }
                }
                catch (Exception) { try { if (_listenUdp != null) _listenUdp.Close(); } catch { } _listenUdp = null; Thread.Sleep(300); }
            }
        }

        /// <summary>当前可加入的房间（已剔除 5 秒内没再广播的）。</summary>
        public static List<NetRoom> Rooms()
        {
            long now = NowMs();
            var list = new List<NetRoom>();
            lock (_roomLock)
            {
                for (int i = _rooms.Count - 1; i >= 0; i--)
                {
                    if (now - _rooms[i].SeenAtMs > RoomKeepAliveMs) _rooms.RemoveAt(i);
                    else list.Add(_rooms[i]);
                }
            }
            return list;
        }

        // ────────────────────────────────────────────────────────
        //  公网通道（跨城市联机）
        // ────────────────────────────────────────────────────────
        //
        // 现实约束（2026-11-12 实测）：
        //   · 手机走 4G/5G 基本拿不到公网 IPv4（运营商 CG-NAT），端口映射无从谈起；
        //   · 但国内三大运营商的移动网络**普遍下发公网 IPv6**，家宽也大多有；
        //   · 所以「跨城市」这条路走 IPv6 直连：开服端监听 IPv6Any:7000，
        //     加入端直接连这个 IPv6 地址，中间不经任何服务器。
        //   · 唯一的问题变成：加入端怎么知道对方的 IPv6？→ 用 ntfy.sh 当免费电话簿。
        //
        // 玩家能感知到的只有「6 位匹配码」：开服端显示码，加入端输码。
        // 底下的 IPv6 地址、端口、房间名都在码后面自动交换。

        static string _publicCode = "";
        static string _publicIp = "";
        static long _publicIpAtMs;
        static Thread _pubThread;
        static volatile bool _pubRun;

        // 加入端查询状态（0 空闲 1 查询中 2 找到 3 失败）
        static volatile int _lookupState;
        static string _lookupMsg = "";
        static string _lookupIp = "";
        static int _lookupPort;

        /// <summary>本机的 6 位公网匹配码。第一次用时生成并存盘，之后固定。</summary>
        public static string PublicCode
        {
            get
            {
                if (!string.IsNullOrEmpty(_publicCode)) return _publicCode;
                _publicCode = PlayerPrefs.GetString("p2.pubcode", "");
                if (string.IsNullOrEmpty(_publicCode) || _publicCode.Length != 6)
                {
                    _publicCode = MakeCode();
                    PlayerPrefs.SetString("p2.pubcode", _publicCode);
                    PlayerPrefs.Save();
                }
                return _publicCode;
            }
        }

        /// <summary>给玩家换一个新码（旧码立刻作废，别人就搜不到你了）。</summary>
        public static void NewPublicCode()
        {
            _publicCode = MakeCode();
            PlayerPrefs.SetString("p2.pubcode", _publicCode);
            PlayerPrefs.Save();
            if (_pubRun) { StopPublish(); StartPublish(); }
            Log("[联机] 新匹配码 " + _publicCode);
        }

        /// <summary>
        /// 6 位码。字母表用 Crockford Base32（去掉 I L O U，其余全留）。
        ///
        /// ★ 2026-11-12 修：原来是「只留 ABCDEFGHJKMNPQRTUVWXY346789」（没有 0/1/2/5），
        ///   而下面的 NormalizeCode 把玩家输入的 O→0、I/L→1。两个规则互相打架：
        ///   生成的码里根本不存在 0 和 1，所以任何一次 O/I/L 的误输入都被改写成
        ///   一个「永远不存在的码」，玩家看到的就是「明明输对了却说查不到」。
        ///   换成 Crockford 之后 O→0、I/L→1 真正落在合法字符上，误输入能自愈。
        ///   另外这个字母表是全集的超集，玩家已经拿到的旧码继续有效。
        /// </summary>
        static string MakeCode()
        {
            const string alpha = "0123456789ABCDEFGHJKMNPQRSTUVWXY";
            var r = new System.Random(Environment.TickCount ^ (int)DateTime.UtcNow.Ticks);
            var sb = new StringBuilder(6);
            for (int i = 0; i < 6; i++) sb.Append(alpha[r.Next(alpha.Length)]);
            return sb.ToString();
        }

        /// <summary>
        /// 本机的公网 IPv6（拿不到返回空串）。
        /// 为什么不用外网服务查：手机刚开机/切网时外网服务常超时，
        /// 而本地枚举网卡是毫秒级且不依赖网络。这里只认全局单播（2xxx/3xxx），
        /// 排除 fe80 链路本地、fec0 站点本地、::1 环回 —— 那些跨城市连不了。
        /// </summary>
        public static string PublicIp
        {
            get
            {
                // 缓存 20 秒：切 WiFi/切 4G 后地址会变，不能永久缓存
                if (!string.IsNullOrEmpty(_publicIp) && NowMs() - _publicIpAtMs < 20000) return _publicIp;
                _publicIp = DetectPublicIpv6();
                _publicIpAtMs = NowMs();
                return _publicIp;
            }
        }

        /// <summary>一个地址是不是「能跨城市连」的公网 IPv6（全局单播 2000::/3）。</summary>
        static bool IsGlobalV6(IPAddress a)
        {
            if (a == null || a.AddressFamily != AddressFamily.InterNetworkV6) return false;
            try
            {
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return false;
            }
            catch { return false; }
            byte[] b = a.GetAddressBytes();
            if (b == null || b.Length != 16) return false;
            return (b[0] & 0xE0) == 0x20;
        }

        static string CleanV6(IPAddress a)
        {
            string s = a.ToString();
            int pc = s.IndexOf('%');       // fe80::1%wlan0 这种带 zone id 的
            if (pc > 0) s = s.Substring(0, pc);
            return s;
        }

        static string DetectPublicIpv6()
        {
            // 主路径：Dns.GetHostEntry 在 Android/Mono 上会返回本机所有网卡地址，
            // 比自己遍历 NetworkInterface 稳（后者在 IL2CPP 上常抛 NotImplemented）。
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                if (host != null && host.AddressList != null)
                {
                    for (int i = 0; i < host.AddressList.Length; i++)
                        if (IsGlobalV6(host.AddressList[i])) return CleanV6(host.AddressList[i]);
                }
            }
            catch { }

            // 兜底：遍历网卡。只碰 UnicastAddresses，不用 GetIPv6Properties/SuffixOrigin
            //（这两个在部分 Unity 后端里缺失或抛异常）。
            try
            {
                var ifs = NetworkInterface.GetAllNetworkInterfaces();
                if (ifs != null)
                {
                    for (int i = 0; i < ifs.Length; i++)
                    {
                        var ni = ifs[i];
                        if (ni == null) continue;
                        try
                        {
                            if (ni.OperationalStatus != OperationalStatus.Up) continue;
                            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        }
                        catch { continue; }

                        UnicastIPAddressInformationCollection ua;
                        try { ua = ni.GetIPProperties().UnicastAddresses; }
                        catch { continue; }
                        if (ua == null) continue;

                        for (int j = 0; j < ua.Count; j++)
                        {
                            var a = ua[j];
                            if (a == null) continue;
                            if (IsGlobalV6(a.Address)) return CleanV6(a.Address);
                        }
                    }
                }
            }
            catch (Exception e) { Log("[联机] 取 IPv6 失败：" + e.Message); }
            return "";
        }

        // ── 开服端：往电话簿写自己 ──

        public static void StartPublish()
        {
            if (_pubRun) return;
            _pubRun = true;
            _pubThread = new Thread(PublishLoop) { IsBackground = true, Name = "P2NetPublish" };
            _pubThread.Start();
            Log("[联机] 公网电话簿已登记  码=" + PublicCode);
        }

        public static void StopPublish()
        {
            _pubRun = false;
            _pubThread = null;
        }

        public static bool Publishing { get { return _pubRun; } }

        /// <summary>
        /// 给面板显示的一行公网状态。
        ///
        /// ★ 2026-11-12 改口径：以前这里写的是「公网地址 xxxx，朋友在全国任何地方都能连」——
        ///   这句话是**假的**。地址是真的，但入站被运营商封了（itdog 200 个国内监测点
        ///   对 [IPv6]:7000 100% 超时、chappell 全端口 TCPSTEALTH），谁连都连不进来。
        ///   现在唯一能跨城市的路是公共 MQTT 中继，所以这行字改成如实说中继的状态。
        /// </summary>
        public static string PublicStatusText()
        {
            if (P2Relay.HostRunning)
            {
                int n = P2Relay.HostStreams;
                return "中继已连（" + P2Relay.BrokerInUse + "）"
                     + (n > 0 ? "，已接入 " + n + " 人" : "，等朋友输码连进来")
                     + "  跨城市可用";
            }

            if (!string.IsNullOrEmpty(P2Relay.LastError))
                return "★ 中继没连上：" + P2Relay.LastError + "（检查手机能不能上网）";

            string ip = PublicIp;
            if (string.IsNullOrEmpty(ip)) return "正在连公网中继…（本机没有公网 IPv6，直连方案不适用）";
            return "正在连公网中继…（本机公网地址 " + ip + " 入站被封，只能走中继）";
        }

        /// <summary>
        /// 每隔 8 秒把「我是谁、在哪」写进电话簿。
        ///
        /// 为什么没有公网 IPv6 也要写：只有公网地址才登记的话，纯局域网玩家
        /// 输码永远查不到东西（这个查询通道就成了摆设）；而写成局域网 IP，
        /// 同一个 WiFi 的朋友输码反而能进 —— 一条代码覆盖两种场景。
        /// 报文里两个地址都带：第 3 段是主地址（优先公网 IPv6），第 8 段是局域网 IP。
        /// </summary>
        static void PublishLoop()
        {
            while (_pubRun)
            {
                try
                {
                    string pub = PublicIp;
                    string lan = LocalIp;
                    string primary = !string.IsNullOrEmpty(pub) ? pub : lan;
                    if (!string.IsNullOrEmpty(primary) && primary != "0.0.0.0")
                    {
                        string payload = "MIR2|" + PublicCode + "|" + primary + "|" + _hostPort + "|"
                                       + SafeDeviceName() + "|" + OnlineCount() + "|" + NowMs()
                                       + "|" + lan;
                        PublishOne(payload);
                    }
                }
                catch (Exception) { }
                Thread.Sleep(PublishIntervalMs);
            }
        }

        /// <summary>
        /// 把一条报文发到电话簿：先试上次成功的实例，失败就按顺序换下一个，
        /// 只要**有一个**发成功就收工（多个实例同时收也是好事，等于多份备份）。
        /// 返回值只用于更新 <see cref="_relayOk"/>，不对外暴露。
        /// </summary>
        static void PublishOne(string payload)
        {
            int n = RelayBases.Length;
            string code = PublicCode;
            for (int k = 0; k < n; k++)
            {
                int i = (_relayOk + k) % n;
                if (Post(RelayBases[i] + code, payload, 6000))
                {
                    _relayOk = i;
                    _pubOkAtMs = NowMs();
                    return;
                }
            }
        }

        /// <summary>发一条 HTTP POST；只有 2xx 才算成功（429 配额用完必须当失败往下换）。</summary>
        static bool Post(string url, string body, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "text/plain; charset=utf-8";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.KeepAlive = false;
                byte[] b = Encoding.UTF8.GetBytes(body);
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    bool ok = (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
                    try { resp.Close(); } catch { }
                    return ok;
                }
            }
            catch (Exception) { }
            return false;
        }

        // ── 加入端：从电话簿查地址 ──

        public static bool LookupBusy { get { return _lookupState == 1; } }
        public static string LookupMessage { get { return _lookupMsg; } }
        public static int LookupState { get { return _lookupState; } }

        /// <summary>
        /// 按 6 位码查对方的公网地址（后台线程，不卡界面）。
        /// 查到之后自动发起连接。返回 false 表示码本身不合法。
        /// </summary>
        public static bool StartLookup(string code)
        {
            code = NormalizeCode(code);
            if (code == null) { _lookupMsg = "码不对：应该是 6 位字母数字"; return false; }
            if (_lookupState == 1) return false;

            _lookupState = 1;
            _lookupMsg = "正在通过互联网查找 " + code + "…";
            _lookupIp = "";
            _lookupPort = _hostPort;

            string c = code;
            var t = new Thread(() => LookupWorker(c)) { IsBackground = true, Name = "P2NetLookup" };
            t.Start();
            Log("[联机] 查码 " + code);
            return true;
        }

        /// <summary>把一个输入串规整成 6 位码；不像码就返回 null。</summary>
        public static string NormalizeCode(string typed)
        {
            if (string.IsNullOrEmpty(typed)) return null;
            var sb = new StringBuilder(6);
            foreach (char ch in typed)
            {
                char c = ch;
                if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
                if (c == 'O') c = '0';
                if (c == 'I' || c == 'L') c = '1';
                if (c < '0' || (c > '9' && c < 'A') || c > 'Z') continue;
                sb.Append(c);
            }
            string s = sb.ToString();
            if (s.Length != 6) return null;
            return s;
        }

        /// <summary>
        /// 后台线程只负责 HTTP 查询；查到之后**不能**在后台线程改 Unity 状态
        /// （PlayerPrefs / Repoint 都必须在主线程），所以置一个待办标志，
        /// 由 <see cref="Tick"/> 在主线程执行真正的连接。
        /// </summary>
        static volatile bool _pendingJoin;
        static string _pendingIp;
        static int _pendingPort;
        static string _pendingRoom;
        static string _pendingRelayCode;

        /// <summary>主线程每帧调一次：把后台查到的结果应用到连接上。</summary>
        public static void Tick()
        {
            P2Relay.RefreshOnline();      // 心跳 → 「对方在线/离线」，只是读时间戳，不碰 Unity
            if (!_pendingJoin) return;
            _pendingJoin = false;

            string ip = _pendingIp, room = _pendingRoom, rc = _pendingRelayCode;
            int port = _pendingPort;
            if (string.IsNullOrEmpty(ip)) return;

            _lookupMsg = "找到房间 " + room + "（" + ip + "），正在连接…";
            Log("[联机] 码解析成功 → " + ip + ":" + port);
            GoJoinRaw(ip, port, rc);
        }

        /// <summary>
        /// 按码加入。**不再去连对方的 IP** —— 对方根本连不进来（入站被运营商封死，
        /// 取证见 P2Relay.cs 头注释）。现在的做法是：
        ///   1) 双方各自出站连到同一个公共 MQTT broker，按码分话题；
        ///   2) 本机起一个假服务端（127.0.0.1:7100），游戏客户端连它；
        ///   3) 「假服务端」把字节搬上 broker，对方那边再搬给他的服务端。
        /// 所以这里要等的是「对方的心跳」，不是「对方的 IP」。
        /// </summary>
        static void LookupWorker(string code)
        {
            _lookupMsg = "正在连公网中继…";
            int face = StartRelayJoin(code);
            if (face <= 0)
            {
                _lookupState = 3;
                _lookupMsg = "连不上公网中继（" + P2Relay.LastError + "）。检查手机能不能上网。";
                Log("[联机] ★ 中继启动失败 " + P2Relay.LastError);
                return;
            }
            _lookupMsg = "已连上中继，正在等 " + code + " 回应…";

            // 开服端每 3 秒发一条心跳。等 15 秒还没等到，就是对方没开服 / 码打错了。
            for (int i = 0; i < 30; i++)
            {
                P2Relay.RefreshOnline();
                if (P2Relay.HostOnline)
                {
                    _pendingIp = "127.0.0.1";
                    _pendingPort = face;
                    _pendingRoom = string.IsNullOrEmpty(P2Relay.HostName) ? "对方" : P2Relay.HostName;
                    _pendingRelayCode = code;
                    _lookupState = 2;
                    _pendingJoin = true;
                    Log("[联机] 码 " + code + " 找到：对方「" + _pendingRoom + "」在线，"
                        + P2Relay.HostOnlineCount + " 人，经中继 127.0.0.1:" + face);
                    return;
                }
                Thread.Sleep(500);
            }

            P2Relay.StopJoin();
            _lookupState = 3;
            _lookupMsg = "对方没回应：确认他的手机开着「开服务器」，并且他那页显示「中继已连上」。";
            Log("[联机] 码 " + code + " 没等到心跳");
        }

        /// <summary>
        /// 从「公网地址 / 局域网地址」里挑一个这台机器真能连上的。
        /// 判据是简单握手：对 7000 端口发起 TCP 连接，2 秒内连上就算通。
        /// 为什么值得多花这两秒：玩家输了码，最差的结果是「好像连上了，其实连不上」；
        /// 先握手能把这种情况变成「换一个地址再试」。
        /// </summary>
        static string PickReachable(string primary, string fallback)
        {
            if (!string.IsNullOrEmpty(primary) && Probe(primary, _lookupPort)) return primary;
            if (!string.IsNullOrEmpty(fallback) && fallback != "0.0.0.0"
                && Probe(fallback, _lookupPort)) return fallback;
            // 两个都不通：还是把主地址交出去，让正式连接去报错（总比什么都不做强）
            return string.IsNullOrEmpty(primary) ? fallback : primary;
        }

        static bool Probe(string host, int port)
        {
            if (string.IsNullOrEmpty(host) || port <= 0) return false;
            try
            {
                bool v6 = host.IndexOf(':') >= 0;
                using (var c = new TcpClient(v6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork))
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(2000)) return false;
                    c.EndConnect(ar);
                    return c.Connected;
                }
            }
            catch { return false; }
        }

        static string Get(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>
        /// 从 ntfy 的 NDJSON 里取最后一条 message。
        /// 不引 JSON 库：返回格式是固定的 {"id":..,"message":"<我们的内容>",..}，
        /// 我们的内容里没有引号，直接找 `"message":"` 到下一个引号即可。
        /// </summary>
        static string ExtractLastMessage(string ndjson)
        {
            if (string.IsNullOrEmpty(ndjson)) return null;
            const string key = "\"message\":\"";
            string last = null;
            int i = 0;
            while (true)
            {
                int a = ndjson.IndexOf(key, i, StringComparison.Ordinal);
                if (a < 0) break;
                a += key.Length;
                int b = ndjson.IndexOf('"', a);
                if (b < 0) break;
                string v = ndjson.Substring(a, b - a);
                if (v.StartsWith("MIR2|", StringComparison.Ordinal)) last = v;
                i = b + 1;
            }
            return last;
        }

        /// <summary>查到地址后直连（和数据面板的「加入游戏」走同一套）。</summary>
        static void GoJoinRaw(string ip, int port)
        {
            GoJoinRaw(ip, port, null);
        }

        /// <summary>同上，但记住「这个连接是靠哪个码走中继的」，断线后能自动重连。</summary>
        static void GoJoinRaw(string ip, int port, string relayCode)
        {
            Mode = NetMode.Join;
            _hostAddress = ip;
            _hostPort = port;
            _joinRelayCode = relayCode;
            PlayerPrefs.SetInt("p2.netmode", (int)NetMode.Join);
            PlayerPrefs.SetString("p2.netaddr", ip);
            PlayerPrefs.SetInt("p2.netport", port);
            PlayerPrefs.SetString("p2.relaycode", relayCode == null ? "" : relayCode);
            PlayerPrefs.Save();

            try { Server.MirEnvir.Envir.LocalOnly = true; } catch { }
            StopBroadcast();
            StopPublish();
            StopRelayHost();
            Repoint(ip, port);
        }

        /// <summary>这次「加入游戏」是不是走中继（空 = 直连局域网/IP）。</summary>
        static string _joinRelayCode = "";

        /// <summary>加入端看到的状态：对方在不在线、几个人。</summary>
        public static bool PeerOnline { get { return P2Relay.HostOnline; } }
        public static string PeerName { get { return P2Relay.HostName; } }
        public static int PeerPlayers { get { return P2Relay.HostOnlineCount; } }

        // ────────────────────────────────────────────────────────
        //  状态查询
        // ────────────────────────────────────────────────────────

        /// <summary>自己服务端的在线真人（不含本机那一条）。</summary>
        public static int OnlineCount()
        {
            try
            {
                var cs = Server.MirEnvir.Envir.Main.Connections;
                if (cs == null) return 0;
                int n = 0;
                lock (cs)
                {
                    for (int i = 0; i < cs.Count; i++)
                        if (cs[i] != null && cs[i].Connected) n++;
                }
                n -= 1;                       // 减掉本机那一条
                return n < 0 ? 0 : n;         // 不用 Mathf：这个函数会被后台广播线程调用
            }
            catch { return 0; }
        }

        public static string ModeName
        {
            get
            {
                switch (Mode)
                {
                    case NetMode.Host: return "开服务器";
                    case NetMode.Join: return "已加入 " + _hostAddress + ":" + _hostPort;
                    default: return "单机游玩";
                }
            }
        }

        /// <summary>给人看的一行状态（设置面板/调试用）。</summary>
        public static string StatusText()
        {
            if (Mode == NetMode.Join) return "已加入 " + _hostAddress + ":" + _hostPort;
            if (Mode == NetMode.Host) return "开服中 " + LocalIp + ":" + _hostPort + "  在线 " + OnlineCount() + " 人";
            return "单机游玩";
        }

        // ────────────────────────────────────────────────────────
        //  工具
        // ────────────────────────────────────────────────────────

        // 只用 .NET 的时钟：这个函数会被后台的广播/发现线程调用，
        // 而 Time.realtimeSinceStartup 只能在主线程取（Unity API 的硬限制）。
        static readonly DateTime _epoch = DateTime.UtcNow;
        static long NowMs() { return (long)(DateTime.UtcNow - _epoch).TotalMilliseconds; }

        static string SafeDeviceName()
        {
            try
            {
                string n = SystemInfo.deviceModel;
                if (string.IsNullOrEmpty(n)) n = "传奇手机版";
                if (n.Length > 18) n = n.Substring(0, 18);
                return n.Replace("|", "/");
            }
            catch { return "传奇手机版"; }
        }

        static string DetectLocalIp()
        {
            // 主路径：对一个外网地址 connect（UDP，不真发包），让系统选出口网卡。
            // 比遍历 NetworkInterface 稳 —— 后者在 Android 上会列出 rmnet/wlan 一堆，
            // 挑哪个全靠运气，挑错就是"IP 显示了但别人连不上"。
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 53);
                    var ep = s.LocalEndPoint as IPEndPoint;
                    if (ep != null && ep.Address != null)
                    {
                        string ip = ep.Address.ToString();
                        if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0") return ip;
                    }
                }
            }
            catch { }

            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var a in host.AddressList)
                {
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = a.ToString();
                    if (ip.StartsWith("127.")) continue;
                    return ip;
                }
            }
            catch { }

            return "0.0.0.0";
        }

        /// <summary>APP 退出/切后台时收尾。</summary>
        public static void Shutdown()
        {
            StopBroadcast();
            StopDiscovery();
            StopPublish();
        }
    }
}
