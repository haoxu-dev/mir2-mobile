// 公网中继 —— 让「洛阳开服、北京上海加入」不依赖任何入站端口。
//
// 为什么必须有这一层（2026-11-12 实测，全部有命令输出为证）：
//   · 本机公网 IPv6 = 2408:8221:171c:77d0:d08c:4c2e:ba11:d2d8，地址是真的、能出网；
//     但**入站被上游封了**：itdog.cn 200 个国内监测点对 [IPv6]:7000 全部 100% 超时；
//     英国 chappell 的 IPv6 扫描器对全部 90 个端口回 TCPSTEALTH（而 ICMPv6 有回包）；
//     validator.nu 把 sslip.io 解析到该地址后 connect timed out。
//   · 公网 IPv4 = 182.122.220.2（郑州联通 AS4837），tracert 第二跳就是 ISP 的
//     182.122.223.254；网关 192.168.1.1 无 UPnP、无 NAT-PMP、无 PCP ⇒ 开不了端口。
//   · 本机防火墙已排除：三档 profile 的 DefaultInboundAction 都是 Allow。
//
// 所以走「双方都主动出站」的公共 MQTT：谁都不要开端口，谁都不需要固定 IP。
// 实测性能（<本机>/tmp\mqtt_final.py 与 relay_proto_test.py）：
//   test.mosquitto.org  800KB 连发 丢 0.0%  29 KB/s  单向延迟中位 213ms
//   broker.emqx.io      800KB 连发 丢 87.5%（匿名限流），只当备份
// 协议层实跑（<本机>/tmp\relay_proto_test.py）：
//   [1] 服务端问候 Index=0   首包耗时 463ms
//   [2] ClientVersion 应答   往返 1105ms
//   [3] Login 应答           往返 444ms
//   VERDICT PASS —— MQTT 中继完整承载了 Mir2 协议（问候/版本/登录三层）
//
// 话题约定（code = 6 位公开匹配码）：
//   mir2r/h/<CODE>/up     加入者 → 开服者
//   mir2r/h/<CODE>/down   开服者 → 加入者
// 载荷首字节 = 流号 sid（1..250），其后是原始 TCP 字节。sid 空载荷 = 「建流」。
//
// ★ 一个必须知道的坑：Mir2 服务端按「来源 IP」限流（Settings.IPBlockSeconds=5），
//   同一个 IP 5 秒内只准连一次，而且每个连接都会把自己那个 IP 的封锁时间往后推。
//   中继是在同一台机器上连自己的 127.0.0.1:7000，所有玩家会共用一个来源 IP
//   ⇒ 第二个玩家一定被踢。所以开服端给每个流绑定一个不同的回环源地址 127.0.0.x，
//   服务端就把它们当成不同玩家。已实测：127.0.0.2/.3/.4/.5 连续四次全部拿到首包。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace P2
{
    public static class P2Relay
    {
        public static Action<string> Log = s => { };

        /// <summary>加入端默认在本机开的「假服务端」端口。被占用会自动往上找。</summary>
        public const int FacePort = 7100;

        /// <summary>单条 MQTT 载荷的原始字节上限（不含流号）。</summary>
        const int Chunk = 2048;

        const int KeepAliveSec = 60;

        /// <summary>实测最稳的公共 broker。第一个是主力，后面是备份。</summary>
        static readonly string[] Brokers = { "test.mosquitto.org", "broker.emqx.io" };
        const int BrokerPort = 1883;

        // ── 运行状态（给界面看）──
        public static volatile bool HostRunning;
        public static volatile bool JoinRunning;
        public static volatile string LastError = "";
        public static volatile string BrokerInUse = "";

        public static int JoinPort { get { return _joinPort; } }
        static volatile int _joinPort = FacePort;

        /// <summary>开服端当前接进来几条流。界面用来显示「有人连进来了」。</summary>
        public static int HostStreams { get { return _hostStreams; } }
        static volatile int _hostStreams;

        // ── 加入端看到的「对方在不在线」（开服端每 3 秒发一条心跳）──
        /// <summary>对方在线（最近 8 秒内收到过心跳）。</summary>
        public static volatile bool HostOnline;
        /// <summary>对方的房间名（手机型号）。</summary>
        public static volatile string HostName = "";
        /// <summary>对方服务端上的在线人数。</summary>
        public static volatile int HostOnlineCount;
        static long _hostBeatAt;
        static string _beatName = "";
        static int _beatCount;

        /// <summary>心跳载荷的首字节固定是 0 —— 流号从 1 开始，永远不冲突。</summary>
        const byte BeaconSid = 0;
        const int BeaconIntervalMs = 3000;
        const int BeaconFreshMs = 8000;

        /// <summary>同一流号在这个窗口内的重复握手视为「重发」，不再重建流。</summary>
        const int HandshakeDedupMs = 3000;

        /// <summary>一条流这么久没有任何收发，就当它死了，回收流号。</summary>
        const int StreamIdleMs = 45000;

        static long NowMs() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }

        static readonly object _gate = new object();

        // ── 开服端 ──
        static Thread _hostThread;
        static volatile bool _hostRun;
        static MqttConn _hostMq;
        static int _hostServerPort = 7000;
        static string _hostCode = "";

        // ── 加入端 ──
        static Thread _joinThread;
        static volatile bool _joinRun;
        static MqttConn _joinMq;
        static TcpListener _joinListener;
        static string _faceCode = "";

        // ══════════════════════════════════════════════════════
        //  开服端：把本机服务端的字节搬到 MQTT 上
        // ══════════════════════════════════════════════════════

        public static void StartHost(string code, int serverPort)
        {
            if (string.IsNullOrEmpty(code)) return;
            lock (_gate)
            {
                if (_hostRun && _hostCode == code && _hostServerPort == serverPort) return;
                StopHostLocked();
                _hostRun = true;
                _hostCode = code;
                _hostServerPort = serverPort > 0 ? serverPort : 7000;
                _hostThread = new Thread(HostMain) { IsBackground = true, Name = "P2RelayHost" };
                _hostThread.Start();
            }
            Log("[中继] 开服端启动，匹配码 " + code + " → 本机服务端 127.0.0.1:" + serverPort);
        }

        public static void StopHost()
        {
            lock (_gate) StopHostLocked();
        }

        static void StopHostLocked()
        {
            _hostRun = false;
            var mq = _hostMq;
            _hostMq = null;
            if (mq != null) { try { mq.Close(); } catch { } }
            _hostThread = null;
            _hostCode = "";
            HostRunning = false;
            _hostStreams = 0;
        }

        static void HostMain()
        {
            string code = _hostCode;
            int sport = _hostServerPort;
            string upT = "mir2r/h/" + code + "/up";
            string dnT = "mir2r/h/" + code + "/down";

            MqttConn mq = null;
            try
            {
                mq = ConnectAny("m2h" + Rnd(), out string broker);
                BrokerInUse = broker;
                if (!mq.Subscribe(upT)) throw new IOException("SUBSCRIBE 失败");
                _hostMq = mq;
                HostRunning = true;
                LastError = "";
                Log("[中继] 已连 " + broker + "，开服端就绪，码 " + code);
            }
            catch (Exception e)
            {
                LastError = "中继连不上：" + e.Message;
                Log("[中继] ★ " + LastError);
                if (mq != null) { try { mq.Close(); } catch { } }
                HostRunning = false;
                _hostRun = false;
                return;
            }

            var streams = new Dictionary<int, Socket>();
            var lockObj = new object();

            // 心跳：每 3 秒往 down 话题发一条「我还在、我叫什么、在线几人」。
            // 加入端据此判断「开服端真的在听」，而不是让他对着一个死房间转圈。
            // 顺带让公共 broker 上的这个码保温，不会几小时没人说话就被清干净。
            var beat = new Thread(() =>
            {
                while (_hostRun && mq.Alive)
                {
                    try { mq.Publish(dnT, BeaconPayload(), -1); } catch { return; }
                    Thread.Sleep(BeaconIntervalMs);
                }
            })
            { IsBackground = true, Name = "P2RelayBeacon" };
            beat.Start();

            var lastHs = new Dictionary<int, long>();   // sid → 上次握手时刻（防重复建流）
            var streamsActive = new Dictionary<int, long>();   // sid → 最后一次收发的时刻

            // 流号回收守卫：加入端的「我断了」通知本身就是 QoS0，也会丢。
            // 所以再来一层硬的：任何一条流只要 45 秒内服务端没往它发过东西、
            // 也没从它收到过东西，就判死拆掉。Mir2 服务端只要客户端登着，
            // 心跳/移动包就不会断 45 秒，所以不会误杀真人。
            var reaper = new Thread(() =>
            {
                while (_hostRun && mq.Alive)
                {
                    Thread.Sleep(15000);
                    if (!_hostRun || !mq.Alive) return;
                    long now = NowMs();
                    var dead = new List<Socket>();
                    var deadSid = new List<int>();
                    lock (lockObj)
                    {
                        foreach (var kv in streams)
                        {
                            long last;
                            if (!streamsActive.TryGetValue(kv.Key, out last) || now - last > StreamIdleMs)
                            {
                                dead.Add(kv.Value);
                                deadSid.Add(kv.Key);
                            }
                        }
                        foreach (int d in deadSid) { streams.Remove(d); lastHs.Remove(d); streamsActive.Remove(d); }
                        if (deadSid.Count > 0) _hostStreams = streams.Count;
                    }
                    // 关掉那条 TCP：它的 HostDownPump 会立刻退出（Receive 抛异常），
                    // 服务端随之判定玩家掉线。**不要**往 down 发通知——down 上 sid=0
                    // 是心跳的专用号，加入端会把它当心跳解成乱码。
                    for (int i = 0; i < dead.Count; i++)
                    {
                        Log("[中继] 流 " + deadSid[i] + " 闲置超时，回收流号（客户端没发断开通知）");
                        try { dead[i].Close(); } catch { }
                    }
                }
            })
            { IsBackground = true, Name = "P2RelayReap" };
            reaper.Start();

            try
            {
                while (_hostRun && mq.Alive)
                {
                    string topic; byte[] payload;
                    if (!mq.ReadMessage(out topic, out payload)) break;
                    if (topic != upT || payload == null || payload.Length < 1) continue;

                    int sid = payload[0];

                    // ★ sid 0 在 up 方向保留给「流控制」：载荷 [0][sid] = 那个流断了。
                    //
                    // 为什么必须有它：公共 broker 是 QoS0，没有任何断开通知。加入端
                    // 本机的客户端一走，开服端这边的 TCP 连接还开着（服务端也以为人还在），
                    // 于是 streams 里留一条死流。流号是 1..250 复用的，下一个玩家拿到
                    // 同一个号时，握手会被当成「流已存在」直接吞掉 —— 他永远收不到问候包，
                    // 客户端转 20 秒超时重连，玩家看到的就是「连不上」。
                    // 2026-11-12 安卓实测正是这个症状（同一 App 进程，前两轮能连，之后必失败）。
                    if (sid == 0)
                    {
                        if (payload.Length >= 2)
                        {
                            int dead = payload[1];
                            Socket ds;
                            lock (lockObj)
                            {
                                streams.TryGetValue(dead, out ds);
                                streams.Remove(dead); lastHs.Remove(dead); streamsActive.Remove(dead);
                            }
                            if (ds != null) { try { ds.Close(); } catch { } }
                            _hostStreams = streams.Count;
                            Log("[中继] 流 " + dead + " 关闭（加入端通知）");
                        }
                        continue;
                    }

                    // ── 建流握手：载荷只有一个字节 ──
                    if (payload.Length == 1)
                    {
                        long now = NowMs();
                        Socket old = null;
                        long prev = 0;
                        lock (lockObj)
                        {
                            streams.TryGetValue(sid, out old);
                            lastHs.TryGetValue(sid, out prev);
                        }

                        // 3 秒内同一个号的重复握手 = 加入端在重发（broker 把小包丢了），忽略。
                        // 千万不能重建：多开的每条连接都会占服务端一个 IP 计数（MaxIP=5），
                        // 撞满就把它那个回环地址封 5 秒，反而更糟。
                        if (old != null && now - prev < HandshakeDedupMs) continue;

                        Socket ns = ConnectLocal(sid, sport);
                        if (ns == null) continue;
                        lock (lockObj)
                        {
                            if (old != null) { try { old.Close(); } catch { } }
                            streams[sid] = ns;
                            lastHs[sid] = now;
                            streamsActive[sid] = now;
                        }
                        _hostStreams = streams.Count;
                        Log("[中继] 流 " + sid + " 建立 → 本机服务端");

                        var t = new Thread(() => HostDownPump(mq, dnT, sid, ns, lockObj, streams, streamsActive));
                        t.IsBackground = true;
                        t.Name = "P2RelayDown" + sid;
                        t.Start();
                        continue;
                    }

                    // ── 正常数据 ──
                    Socket s;
                    lock (lockObj) streams.TryGetValue(sid, out s);
                    if (s == null)
                    {
                        Log("[中继] 流 " + sid + " 未建立就收到数据，丢弃 " + (payload.Length - 1) + " 字节");
                        continue;
                    }
                    try
                    {
                        s.Send(payload, 1, payload.Length - 1, SocketFlags.None);
                        lock (lockObj) { if (streamsActive.ContainsKey(sid)) streamsActive[sid] = NowMs(); }
                    }
                    catch (Exception e) { Log("[中继] 流 " + sid + " 上行写入失败：" + e.Message); }
                }
            }
            catch (Exception e)
            {
                LastError = "中继断开：" + e.Message;
                Log("[中继] ★ " + LastError);
            }

            lock (lockObj)
            {
                foreach (var kv in streams) { try { kv.Value.Close(); } catch { } }
                streams.Clear();
            }
            try { mq.Close(); } catch { }
            HostRunning = false;
            _hostStreams = 0;
        }

        /// <summary>
        /// 用不同的回环源地址连本机服务端。
        ///
        /// 为什么要绑源地址：服务端 Settings.IPBlockSeconds=5，同一个来源 IP
        /// 5 秒内只准连一次。所有流都从 127.0.0.1 出去的话，第一个玩家进来后
        /// 后面的人全被踢。绑到 127.0.0.x 之后服务端把它们当成不同玩家。
        /// Linux/Android 的 lo 是 127.0.0.1/8，整个 /8 都能绑；万一某个系统不允许，
        /// 退化成不绑（至少一个玩家能玩），不会因此彻底连不上。
        /// </summary>
        static Socket ConnectLocal(int sid, int sport)
        {
            Socket s = null;
            try
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                bool bound = false;
                try
                {
                    s.Bind(new IPEndPoint(IPAddress.Parse(SourceIpFor(sid)), 0));
                    bound = true;
                }
                catch
                {
                    // 不能绑多个回环地址的系统：退回默认源地址
                }

                var ar = s.BeginConnect(new IPEndPoint(IPAddress.Loopback, sport), null, null);
                if (!ar.AsyncWaitHandle.WaitOne(8000)) throw new IOException("连本机服务端超时");
                s.EndConnect(ar);
                s.NoDelay = true;

                if (!bound) Log("[中继] ★ 流 " + sid + " 无法绑定独立源地址，降级为共用 127.0.0.1");
                return s;
            }
            catch (Exception e)
            {
                Log("[中继] 流 " + sid + " 连服务端失败：" + e.Message);
                try { if (s != null) s.Close(); } catch { }
                return null;
            }
        }

        /// <summary>
        /// 心跳报文：首字节 0（流号从 1 开始，永不冲突），后面是 UTF-8 的
        /// 「房间名|在线人数」。房间名里不会出现竖线（P2NetMode 已把它替换成 "/"）。
        /// </summary>
        static byte[] BeaconPayload()
        {
            string name = "传奇手机版";
            int n = 0;
            try
            {
                if (BeaconInfo != null) BeaconInfo(out name, out n);
            }
            catch { }
            var body = Encoding.UTF8.GetBytes(name + "|" + n);
            var pkt = new byte[body.Length + 1];
            pkt[0] = BeaconSid;
            Buffer.BlockCopy(body, 0, pkt, 1, body.Length);
            return pkt;
        }

        /// <summary>由 P2NetMode 提供「房间名 + 本服在线人数」，避免 P2Relay 依赖游戏层。</summary>
        public delegate void BeaconInfoFn(out string name, out int players);
        public static BeaconInfoFn BeaconInfo;

        static void HostDownPump(MqttConn mq, string dnT, int sid, Socket s,
                                 object lockObj, Dictionary<int, Socket> streams,
                                 Dictionary<int, long> active)
        {
            var buf = new byte[Chunk];
            var pkt = new byte[Chunk + 1];
            while (_hostRun && mq.Alive)
            {
                int n;
                try { n = s.Receive(buf); }
                catch { break; }
                if (n <= 0) break;
                pkt[0] = (byte)sid;
                Buffer.BlockCopy(buf, 0, pkt, 1, n);
                try { mq.Publish(dnT, pkt, n + 1); }
                catch { break; }
                lock (lockObj) { if (active.ContainsKey(sid)) active[sid] = NowMs(); }
            }
            try { s.Close(); } catch { }
            lock (lockObj)
            {
                // ★ 只移除「还是我自己这条」的条目。同一流号被复用时，旧泵线程
                //   退出得晚一点就会把新玩家的 socket 从字典里删掉 —— 那条流随即
                //   变成「收到数据也找不到 socket」，玩家表现为「刚连上就掉」。
                Socket cur;
                if (streams.TryGetValue(sid, out cur) && ReferenceEquals(cur, s))
                {
                    streams.Remove(sid);
                    _hostStreams = streams.Count;
                }
            }
            Log("[中继] 流 " + sid + " 关闭");
        }

        /// <summary>sid 1..250 → 回环源地址 127.0.0.(sid+1)。</summary>
        static string SourceIpFor(int sid)
        {
            int x = (sid % 250) + 2;
            return "127.0.0." + x;
        }

        // ══════════════════════════════════════════════════════
        //  加入端：在本机开一个假服务端，客户端连它
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// 启动加入端。**同步**把 broker 和本机监听端口都就位，失败立刻返回 false，
        /// 调用方（查码线程）就能给玩家一句准确的报错，而不是「一直在转圈」。
        /// </summary>
        public static bool StartJoin(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            lock (_gate)
            {
                if (_joinRun && _faceCode == code) return true;
                StopJoinLocked();

                MqttConn mq = null;
                TcpListener ls = null;
                int port = FacePort;
                try
                {
                    mq = ConnectAny("m2j" + Rnd(), out string broker);
                    BrokerInUse = broker;
                    if (!mq.Subscribe("mir2r/h/" + code + "/down")) throw new IOException("SUBSCRIBE 失败");

                    // 7100 往上找空位，避免和别的进程/上次没退干净的监听撞车
                    for (; port < FacePort + 10; port++)
                    {
                        try
                        {
                            var l = new TcpListener(IPAddress.Loopback, port);
                            l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            l.Start(8);
                            ls = l;
                            break;
                        }
                        catch { }
                    }
                    if (ls == null) throw new IOException("本机端口 7100~7109 都被占用了");
                }
                catch (Exception e)
                {
                    try { if (mq != null) mq.Close(); } catch { }
                    try { if (ls != null) ls.Stop(); } catch { }
                    LastError = e.Message;
                    _joinRun = false; _faceCode = ""; JoinRunning = false;
                    Log("[中继] ★ 加入端启动失败：" + e.Message);
                    return false;
                }

                _joinMq = mq;
                _joinListener = ls;
                _joinPort = port;
                _faceCode = code;
                _joinRun = true;
                JoinRunning = true;
                LastError = "";
                Log("[中继] 已连 " + BrokerInUse + "，加入端就绪，本机连 127.0.0.1:" + port);

                var mqLocal = mq;
                var lsLocal = ls;
                _joinThread = new Thread(() => JoinMain(mqLocal, lsLocal)) { IsBackground = true, Name = "P2RelayJoin" };
                _joinThread.Start();
                return true;
            }
        }

        public static void StopJoin()
        {
            lock (_gate) StopJoinLocked();
        }

        static void StopJoinLocked()
        {
            _joinRun = false;
            var mq = _joinMq;
            _joinMq = null;
            if (mq != null) { try { mq.Close(); } catch { } }
            var ls = _joinListener;
            _joinListener = null;
            if (ls != null) { try { ls.Stop(); } catch { } }
            _joinThread = null;
            _faceCode = "";
            JoinRunning = false;
        }

        public static void StopAll()
        {
            StopHost();
            StopJoin();
        }

        static void JoinMain(MqttConn mq, TcpListener ls)
        {
            string code = _faceCode;
            string upT = "mir2r/h/" + code + "/up";
            string dnT = "mir2r/h/" + code + "/down";

            var streams = new Dictionary<int, Socket>();
            var lockObj = new object();

            // 下行：MQTT → 本地客户端
            var downThread = new Thread(() =>
            {
                while (_joinRun && mq.Alive)
                {
                    string topic; byte[] payload;
                    if (!mq.ReadMessage(out topic, out payload)) break;
                    if (topic != dnT || payload == null || payload.Length < 2) continue;

                    int sid = payload[0];

                    // 流号 0 = 开服端心跳，不往客户端转发，只更新「对方在不在」。
                    if (sid == BeaconSid) { TakeBeacon(payload); continue; }

                    Socket s;
                    lock (lockObj) streams.TryGetValue(sid, out s);
                    if (s == null) continue;
                    try { s.Send(payload, 1, payload.Length - 1, SocketFlags.None); }
                    catch { }
                }
            })
            { IsBackground = true, Name = "P2RelayDownlink" };
            downThread.Start();

            try
            {
                while (_joinRun)
                {
                    Socket c = ls.AcceptSocket();
                    c.NoDelay = true;
                    int sid;
                    lock (lockObj)
                    {
                        sid = AllocSid(streams);
                        streams[sid] = c;
                    }
                    Log("[中继] 客户端接入 → 流 " + sid);

                    // 立刻建流：Mir2 客户端等服务端先说话，自己不会先发字节。
                    //
                    // ★ 发 3 次、间隔 250ms —— 2026-11-12 安卓实测：公共 broker 是
                    //   QoS0，4 条流同时接入时「单字节握手包」的丢失率高得离谱
                    //   （第二轮 4 条流只有 1 条在手机端建成流）。一丢就再也建不成，
                    //   客户端只能干等 20 秒超时重连，玩家看到的就是「连不上」。
                    //   重复发是安全的：开服端对同一个 sid 的第 2、3 次只当「空载荷」
                    //   忽略（payload.Length == 1 时不转发任何字节），不会建出重复流。
                    int mySid = sid;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            if (!_joinRun || !mq.Alive) return;
                            try { mq.Publish(upT, new byte[] { (byte)mySid }, 1); }
                            catch { return; }
                            if (k < 2) Thread.Sleep(250);
                        }
                    });

                    var t = new Thread(() => JoinUpPump(mq, upT, mySid, c, lockObj, streams));
                    t.IsBackground = true;
                    t.Name = "P2RelayUp" + sid;
                    t.Start();
                }
            }
            catch (Exception e)
            {
                if (_joinRun) { LastError = "中继断开：" + e.Message; Log("[中继] ★ " + LastError); }
            }

            try { ls.Stop(); } catch { }
            lock (lockObj)
            {
                foreach (var kv in streams) { try { kv.Value.Close(); } catch { } }
                streams.Clear();
            }
            try { mq.Close(); } catch { }
            JoinRunning = false;
        }

        /// <summary>
        /// 取一个当前没人用的流号。客户端断线重连时 NetDriver 会反复连本机端口，
        /// 如果每次都 +1，很快撞到 250 上限并和还活着的流撞号，所以优先复用空位。
        /// </summary>
        static int AllocSid(Dictionary<int, Socket> streams)
        {
            for (int i = 1; i <= 250; i++)
                if (!streams.ContainsKey(i)) return i;
            return 1;   // 极端情况：全占满，只好复用第一条
        }

        /// <summary>更新「对方在线」状态。载荷 = [0][UTF8 "房间名|人数"]。</summary>
        static void TakeBeacon(byte[] payload)
        {
            try
            {
                string s = Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
                int bar = s.LastIndexOf('|');
                if (bar > 0)
                {
                    _beatName = s.Substring(0, bar);
                    int n;
                    if (int.TryParse(s.Substring(bar + 1), out n)) _beatCount = n;
                }
                else _beatName = s;
                _hostBeatAt = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            }
            catch { }
        }

        /// <summary>界面每帧调一次：把心跳时间折算成「在线/离线」。</summary>
        public static void RefreshOnline()
        {
            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            bool on = _hostBeatAt > 0 && now - _hostBeatAt < BeaconFreshMs;
            HostOnline = on;
            if (on)
            {
                HostName = _beatName;
                HostOnlineCount = _beatCount;
            }
        }

        static void JoinUpPump(MqttConn mq, string upT, int sid, Socket s,
                               object lockObj, Dictionary<int, Socket> streams)
        {
            var buf = new byte[Chunk];
            var pkt = new byte[Chunk + 1];
            while (_joinRun && mq.Alive)
            {
                int n;
                try { n = s.Receive(buf); }
                catch { break; }
                if (n <= 0) break;
                pkt[0] = (byte)sid;
                Buffer.BlockCopy(buf, 0, pkt, 1, n);
                try { mq.Publish(upT, pkt, n + 1); }
                catch { break; }
            }
            try { s.Close(); } catch { }
            lock (lockObj)
            {
                Socket cur;
                if (streams.TryGetValue(sid, out cur) && ReferenceEquals(cur, s)) streams.Remove(sid);
            }

            // ★ 通知开服端「这条流没了」（up 方向 sid=0，载荷 [0][sid]）。
            //   不发的话开服端那条 TCP 永远留着：服务端以为玩家还在，流号也被占住，
            //   下一个玩家撞上同一个号就会握手被吞、收不到问候包 —— 表现为「连不上」。
            try
            {
                if (mq.Alive) mq.Publish(upT, new byte[] { 0, (byte)sid }, 2);
            }
            catch { }
            Log("[中继] 客户端断开 流 " + sid);
        }

        static MqttConn ConnectAny(string cid, out string broker)
        {
            Exception last = null;
            foreach (var h in Brokers)
            {
                try
                {
                    var mq = new MqttConn();
                    mq.Connect(h, BrokerPort, cid, KeepAliveSec);
                    broker = h;
                    return mq;
                }
                catch (Exception e) { last = e; }
            }
            broker = "";
            throw last ?? new IOException("没有可用 broker");
        }

        static string Rnd()
        {
            var r = new Random(Environment.TickCount ^ Guid.NewGuid().GetHashCode());
            return r.Next(100000, 999999).ToString();
        }

        /// <summary>给设置面板显示的一行状态。</summary>
        public static string StatusText()
        {
            if (HostRunning && JoinRunning)
                return "中继已连 " + BrokerInUse + "（开服+加入）";
            if (HostRunning)
                return "中继已连 " + BrokerInUse + "，" + (HostStreams > 0 ? "已接入 " + HostStreams + " 人" : "等朋友输码连入");
            if (JoinRunning)
                return "中继已连 " + BrokerInUse + "，正在等对方响应";
            if (!string.IsNullOrEmpty(LastError)) return "★ " + LastError;
            return "";
        }

        // ══════════════════════════════════════════════════════
        //  最小 MQTT 3.1.1 客户端（只用到 CONNECT/SUBSCRIBE/PUBLISH/PING）
        // ══════════════════════════════════════════════════════

        sealed class MqttConn
        {
            TcpClient _tcp;
            NetworkStream _ns;
            readonly object _wlock = new object();
            volatile bool _alive;
            int _keepalive;

            public bool Alive { get { return _alive; } }

            public void Connect(string host, int port, string cid, int keepaliveSec)
            {
                _keepalive = keepaliveSec;
                _tcp = new TcpClient();
                _tcp.NoDelay = true;
                _tcp.SendTimeout = 120000;
                _tcp.ReceiveTimeout = (keepaliveSec + 20) * 1000;

                var ar = _tcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(10000)) throw new IOException("连接 " + host + " 超时");
                _tcp.EndConnect(ar);

                _ns = _tcp.GetStream();
                _alive = true;

                var vh = new List<byte>();
                vh.AddRange(Str2("MQTT"));
                vh.Add(4);          // protocol level 3.1.1
                vh.Add(0x02);       // clean session
                vh.Add((byte)(keepaliveSec >> 8));
                vh.Add((byte)(keepaliveSec & 0xFF));
                var pl = new List<byte>();
                pl.AddRange(Str2(cid));

                var pkt = new List<byte>();
                pkt.Add(0x10);
                pkt.AddRange(EncLen(vh.Count + pl.Count));
                pkt.AddRange(vh);
                pkt.AddRange(pl);
                var arr = pkt.ToArray();
                RawSend(arr, arr.Length);

                byte hdr; byte[] body;
                if (!ReadPacket(out hdr, out body) || hdr != 0x20 || body == null || body.Length < 2 || body[1] != 0)
                    throw new IOException("CONNACK 被拒（rc="
                        + (body != null && body.Length > 1 ? body[1].ToString() : "?") + "）");

                var t = new Thread(PingLoop) { IsBackground = true, Name = "MqttPing" };
                t.Start();
            }

            void PingLoop()
            {
                int half = Math.Max(5, _keepalive / 2);
                while (_alive)
                {
                    Thread.Sleep(half * 1000);
                    if (!_alive) return;
                    try { RawSend(new byte[] { 0xC0, 0x00 }, 2); }
                    catch { _alive = false; return; }
                }
            }

            public bool Subscribe(string topic)
            {
                var vh = new byte[] { 0x00, 0x01 };
                var pl = new List<byte>();
                pl.AddRange(Str2(topic));
                pl.Add(0);
                var pkt = new List<byte>();
                pkt.Add(0x82);
                pkt.AddRange(EncLen(vh.Length + pl.Count));
                pkt.AddRange(vh);
                pkt.AddRange(pl);
                var arr = pkt.ToArray();
                RawSend(arr, arr.Length);

                byte hdr; byte[] body;
                while (ReadPacket(out hdr, out body))
                {
                    if ((hdr & 0xF0) == 0x90) return true;
                }
                return false;
            }

            /// <summary>发一条 QoS0 PUBLISH。len &lt; 0 表示整个 data.Length。</summary>
            public void Publish(string topic, byte[] data, int len)
            {
                if (len < 0) len = data.Length;
                var pl = new List<byte>(len + topic.Length + 4);
                pl.AddRange(Str2(topic));
                for (int i = 0; i < len; i++) pl.Add(data[i]);
                var pkt = new List<byte>(pl.Count + 5);
                pkt.Add(0x30);
                pkt.AddRange(EncLen(pl.Count));
                pkt.AddRange(pl);
                var arr = pkt.ToArray();
                RawSend(arr, arr.Length);
            }

            void RawSend(byte[] buf, int len)
            {
                lock (_wlock)
                {
                    if (!_alive) throw new IOException("连接已断");
                    _ns.Write(buf, 0, len);
                    _ns.Flush();
                }
            }

            /// <summary>读一个 MQTT 报文。返回 false = 连接断了。</summary>
            public bool ReadPacket(out byte hdr, out byte[] body)
            {
                hdr = 0; body = null;
                try
                {
                    int b0 = _ns.ReadByte();
                    if (b0 < 0) { _alive = false; return false; }
                    hdr = (byte)b0;

                    int mult = 1, val = 0, i = 0, d;
                    do
                    {
                        d = _ns.ReadByte();
                        if (d < 0) { _alive = false; return false; }
                        val += (d & 0x7F) * mult;
                        mult *= 128;
                        if (++i > 4) { _alive = false; return false; }
                    }
                    while ((d & 0x80) != 0);

                    if (val > 4 * 1024 * 1024) { _alive = false; return false; }
                    body = new byte[val];
                    int got = 0;
                    while (got < val)
                    {
                        int n = _ns.Read(body, got, val - got);
                        if (n <= 0) { _alive = false; return false; }
                        got += n;
                    }
                    return true;
                }
                catch
                {
                    _alive = false;
                    return false;
                }
            }

            /// <summary>阻塞读一条 PUBLISH；返回 false = 连接断了。</summary>
            public bool ReadMessage(out string topic, out byte[] payload)
            {
                topic = null; payload = null;
                byte hdr; byte[] body;
                while (_alive)
                {
                    if (!ReadPacket(out hdr, out body)) return false;
                    if ((hdr & 0xF0) != 0x30) continue;      // PINGRESP 等忽略
                    if (body == null || body.Length < 2) continue;
                    int tl = (body[0] << 8) | body[1];
                    if (body.Length < 2 + tl) continue;
                    topic = Encoding.UTF8.GetString(body, 2, tl);
                    payload = new byte[body.Length - 2 - tl];
                    Buffer.BlockCopy(body, 2 + tl, payload, 0, payload.Length);
                    return true;
                }
                return false;
            }

            public void Close()
            {
                _alive = false;
                try { RawSend(new byte[] { 0xE0, 0x00 }, 2); } catch { }
                try { if (_ns != null) _ns.Close(); } catch { }
                try { if (_tcp != null) _tcp.Close(); } catch { }
            }

            static byte[] Str2(string s)
            {
                var b = Encoding.UTF8.GetBytes(s);
                var r = new byte[b.Length + 2];
                r[0] = (byte)(b.Length >> 8);
                r[1] = (byte)(b.Length & 0xFF);
                Buffer.BlockCopy(b, 0, r, 2, b.Length);
                return r;
            }

            static byte[] EncLen(int n)
            {
                var o = new List<byte>(4);
                do
                {
                    int d = n % 128;
                    n /= 128;
                    if (n > 0) o.Add((byte)(d | 0x80));
                    else { o.Add((byte)d); break; }
                }
                while (true);
                return o.ToArray();
            }
        }
    }
}
