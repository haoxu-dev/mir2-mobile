// 游戏内「联机设置」面板 —— 左上角一颗小按钮，点开才有单机/开服/加入三选一。
//
// 用户原话（2026-11-12）：
//   「不需要这个开始界面，你正常进游戏，进完游戏左上角或者右上角有一个比较小的按键，
//     类似于那种设置的按键，可以设置游戏上的一些东西。点开了之后就有这个选项，
//     就是比如说别人想连的话，该怎么连？就是越简单越好。」
//
// 所以：不进游戏不打扰、不弹开始界面、不拦启动流程。玩家想联机才点它。
//
// 视觉约束（都是这个 Tuanjie 构建的实测结论，见 P2VirtualPad 顶部那段长注释）：
//   IMGUI 的 A 通道整条链被丢掉，GUI.color 的 alpha 不起作用，
//   每次 DrawTexture 都是**不透明地刷一整块矩形**。
//   ⇒ 这一层只用不透明纯色贴图 + 文字，不做半透明叠层，不叠两张图。
using System.Collections.Generic;
using Client.MirCore;
using UnityEngine;

namespace P2
{
    /// <summary>联机设置面板：绘制 + 触摸 + 文本输入，全部在这一个类里。</summary>
    public static class P2NetPanel
    {
        public static System.Action<string> Log = s => { };

        public static bool IsOpen { get; private set; }

        // ── 设置小按钮（左上角）──
        // 位置避开 NPC 对话选项（对话一开就让位，见 DrawSettings / HitSettings）
        const float BtnX = 40f, BtnY = 40f, BtnW = 116f, BtnH = 116f;

        // ── 样式 ──
        static GUIStyle _title, _body, _btn, _btnOn, _small;
        static Font _font;
        static bool _fontTried;

        // ── 输入（加入游戏要打字）──
        static TouchScreenKeyboard _kb;
        static string _typed = "";
        static bool _kbWasOpen;

        // ── 手指锁（adb input tap 只报 Ended，Began/Ended 都要判）──
        static int _finger = -2;
        // 面板自己的页签：0=单机 1=开服 2=加入。
        // ★ 不能拿 P2NetMode.Mode 当页签用 —— 「加入游戏」点开时其实还没连出去，
        //   真正的连接要等玩家填完地址按「连接」。两件事必须分开存。
        static int _tab;
        static float _flashT;

        static readonly Dictionary<int, Texture2D> _solid = new Dictionary<int, Texture2D>();

        // ══════════════════════════════════════════════════════
        //  开关
        // ══════════════════════════════════════════════════════

        public static void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            _tab = P2NetMode.Mode == NetMode.Join ? 2 : (P2NetMode.Mode == NetMode.Host ? 1 : 0);
            _typed = P2NetMode.Mode == NetMode.Join ? P2NetMode.HostAddress : "";
            Log("[联机] 打开设置面板  当前=" + P2NetMode.StatusText());
            // 进面板就开始搜房间：玩家点「加入游戏」时列表已经在了，不用干等。
            P2NetMode.StartDiscovery();
        }

        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            // ★ 2026-11-12 修「设置键只有第一次能点开，之后怎么点都没反应」：
            //   _finger 是「这根手指已经被处理过」的锁，只有 Reset() 会清它。
            //   点面板外空白关闭时走的是 Close() —— 锁留在 0 没清。Android 复用手指数，
            //   下一次点击的 fingerId 还是 0 ⇒ `if (_finger == t.fingerId) continue;`
            //   永远跳过 ⇒ 玩家点一百次也开不出面板。
            //   设备日志铁证：整份 p2_boot.log 只有一次「打开设置面板」，紧接着一次
            //   「关闭设置面板」，之后再没有任何一条 —— 不是日志丢了，是根本没进来。
            _finger = -2;
            P2NetMode.StopDiscovery();
            CloseKeyboard();
            Log("[联机] 关闭设置面板");
        }

        public static void Toggle() { if (IsOpen) Close(); else Open(); }

        // ══════════════════════════════════════════════════════
        //  输入法
        // ══════════════════════════════════════════════════════

        static void OpenKeyboard()
        {
            if (!TouchScreenKeyboard.isSupported)
            {
                Log("[联机] 本机不支持系统键盘，只能输纯数字以外的字符会丢（GUI.TextField 兜底）");
                return;
            }
            try
            {
                TouchScreenKeyboard.hideInput = true;
                _kb = TouchScreenKeyboard.Open(_typed ?? "", TouchScreenKeyboardType.Default,
                                               false, false, false, false, "", 32);
                _kbWasOpen = _kb != null;
                Log("[联机] 弹出键盘 " + (_kb != null ? "ok" : "null"));
            }
            catch (System.Exception e) { Log("[联机] 键盘异常 " + e.Message); }
        }

        static void CloseKeyboard()
        {
            if (_kb != null) { try { _kb.active = false; } catch { } }
            _kb = null;
            _kbWasOpen = false;
        }

        static void PumpKeyboard()
        {
            if (_kb == null) return;
            try
            {
                var st = _kb.status;
                bool ended = st == TouchScreenKeyboard.Status.Done
                          || st == TouchScreenKeyboard.Status.Canceled
                          || st == TouchScreenKeyboard.Status.LostFocus;
                // 顺序必须是「先判状态、再决定要不要灌」：
                // BACK 收键盘那一帧 text 会先被清空，倒过来就把输入框清了（P2NativeInput 踩过）
                if (!ended)
                {
                    string t = _kb.text;
                    if (t != null) _typed = t;
                }
                if (ended) CloseKeyboard();
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════
        //  布局（绘制与点选共用这一份，绝不各算一遍）
        // ══════════════════════════════════════════════════════

        struct Hit
        {
            public int id;
            public Rect r;
        }

        static readonly List<Hit> _hits = new List<Hit>();
        static Rect _panel, _inputBox;
        static float _modeBtnBase;
        static float _roomTop;      // 房间列表第一行的 y（Layout 与 Draw 共用，避免对不上）
        static Rect _hitNB;         // 「换一个码」按钮（仅开服页）

        const int ID_CLOSE = 1;
        const int ID_MODE_SINGLE = 2;
        const int ID_MODE_HOST = 3;
        const int ID_MODE_JOIN = 4;
        const int ID_INPUT = 5;
        const int ID_CONNECT = 6;
        const int ID_SEARCH = 7;
        const int ID_NEWCODE = 8;
        // ── 按键外观页（2026-11-26 新增，对应 Issue #1「按钮超大无法修改」）──
        const int ID_LOOK = 9;              // 第四个页签
        const int ID_SCALE_DOWN = 20;
        const int ID_SCALE_UP = 21;
        const int ID_ALPHA_DOWN = 22;
        const int ID_ALPHA_UP = 23;
        const int ID_LOOK_RESET = 24;
        const int ID_ROOM_BASE = 100;

        // 页签数：0 单机 / 1 开服 / 2 加入 / 3 外观
        const int TabCount = 4;
        // 外观页控件矩形（Layout 算好，Draw 直接用，避免"看得见点不着"）
        static Rect[] _lookMinus = new Rect[2], _lookPlus = new Rect[2], _lookVal = new Rect[2];
        static Rect _lookReset;
        static Rect _lookPrev;   // 真实按键预览框（空矩形 = 放不下，不画）
        static float _lookTop;   // 两行控件从这里起（上面留一行说明文字的高度）

        static void Layout()
        {
            _hits.Clear();
            float W = Screen.width, H = Screen.height;
            float pw = Mathf.Min(W * 0.80f, 1500f);
            float ph = Mathf.Min(H * 0.84f, 860f);
            _panel = new Rect((W - pw) * 0.5f, (H - ph) * 0.5f, pw, ph);

            float pad = pw * 0.035f;
            float y = _panel.y + pad;

            // 标题栏：右上角关闭
            float cs = ph * 0.075f;
            _hits.Add(new Hit { id = ID_CLOSE, r = new Rect(_panel.xMax - pad - cs, _panel.y + pad * 0.55f, cs, cs) });

            y += ph * 0.115f;

            // 页签按钮横排（2026-11-26 从 3 个改成 4 个）
            float gap = pw * 0.018f;
            float bw = (pw - pad * 2 - gap * (TabCount - 1)) / TabCount;
            float bh = ph * 0.115f;
            _modeBtnBase = y;
            _hits.Add(new Hit { id = ID_MODE_SINGLE, r = new Rect(_panel.x + pad, y, bw, bh) });
            _hits.Add(new Hit { id = ID_MODE_HOST, r = new Rect(_panel.x + pad + (bw + gap), y, bw, bh) });
            _hits.Add(new Hit { id = ID_MODE_JOIN, r = new Rect(_panel.x + pad + (bw + gap) * 2f, y, bw, bh) });
            _hits.Add(new Hit { id = ID_LOOK, r = new Rect(_panel.x + pad + (bw + gap) * 3f, y, bw, bh) });

            y += bh + ph * 0.045f;

            // 内容区
            if (_tab == 1)
            {
                // 换码按钮：和 Draw 用同一个矩形，避免"看得见点不着"
                _hitNB = new Rect(_panel.x + pad, _panel.yMax - pad - ph * 0.10f, pw * 0.26f, ph * 0.10f);
                _hits.Add(new Hit { id = ID_NEWCODE, r = _hitNB });
            }
            else if (_tab == 2)
            {
                float ih = ph * 0.095f;
                _inputBox = new Rect(_panel.x + pad, y, pw - pad * 2 - bw * 0.5f, ih);
                _hits.Add(new Hit { id = ID_INPUT, r = _inputBox });
                _hits.Add(new Hit { id = ID_CONNECT, r = new Rect(_inputBox.xMax + gap, y, bw * 0.5f - gap, ih) });

                // 房间列表的起点由这里算好，Draw 直接用 _roomTop ——
                // 两边各算一遍必然有一天对不上，出现"看到了但点不中"。
                _roomTop = _inputBox.yMax + ph * 0.020f + BodyFontSize() * 2f + 40f;
                var rooms = P2NetMode.Rooms();
                int n = Mathf.Min(rooms.Count, 4);
                float rh = ph * 0.088f;
                for (int i = 0; i < n; i++)
                {
                    _hits.Add(new Hit
                    {
                        id = ID_ROOM_BASE + i,
                        r = new Rect(_panel.x + pad, _roomTop + (rh + ph * 0.014f) * i, pw - pad * 2, rh)
                    });
                }
            }
            else if (_tab == 3)
            {
                // 每行：标签（左）/ [−] [当前值] [+]（右）
                float rowH = ph * 0.122f;
                float stepW = ph * 0.100f;                 // 加减按钮是方键
                float labW = pw * 0.20f;                   // 标签列收窄：值框要放得下「标准（本机最高到这）」
                float valW = pw - pad * 2 - labW - stepW * 2 - gap * 2;
                if (valW < pw * 0.30f) valW = pw * 0.30f;

                // 说明文字占一行：按 BodyFontSize 的实际字号推，不写死
                _lookTop = y + BodyFontSize() + 26f;

                float rowStep = rowH + ph * 0.026f;
                for (int i = 0; i < 2; i++)
                {
                    float ry = _lookTop + rowStep * i;
                    float rx = _panel.x + pad + labW;
                    _lookMinus[i] = new Rect(rx, ry, stepW, rowH);
                    _lookVal[i] = new Rect(rx + stepW + gap, ry, valW, rowH);
                    _lookPlus[i] = new Rect(rx + stepW + gap + valW + gap, ry, stepW, rowH);
                }
                _hits.Add(new Hit { id = ID_SCALE_DOWN, r = _lookMinus[0] });
                _hits.Add(new Hit { id = ID_SCALE_UP, r = _lookPlus[0] });
                _hits.Add(new Hit { id = ID_ALPHA_DOWN, r = _lookMinus[1] });
                _hits.Add(new Hit { id = ID_ALPHA_UP, r = _lookPlus[1] });

                // 「恢复默认」和预览框并排：左边一颗按钮，右边剩下的全给预览。
                // 竖着排的话矮屏上预览会被挤没，而预览才是这一页最该看见的东西。
                float btnH = ph * 0.100f;
                float btnW = pw * 0.26f;
                float by = _lookTop + rowStep * 2f + ph * 0.014f;
                _lookReset = new Rect(_panel.x + pad, by, btnW, btnH);
                _hits.Add(new Hit { id = ID_LOOK_RESET, r = _lookReset });

                float px0 = _lookReset.xMax + gap;
                float pyMax = _panel.yMax - pad;
                float availH = pyMax - by;
                float availW = _panel.xMax - pad - px0;
                // ★ 2026-11-26：框的宽高比跟着**内容**走，不能把整条横带都给它。
                //   原来「宽度 = 剩下的全部」⇒ 978 宽只有 163 高，里面三颗键挤在中间
                //   一小块、两边各留一大片空 —— 看着像框没画完，按键也没显大。
                //   现在先按内容比例定框，剩下多出来的宽度让它居中留白。
                float aspect = P2VirtualPad.LookPreviewAspect;
                if (aspect < 0.5f) aspect = 2.7f;      // 防御：表被改成奇怪值时退回实测比例
                float boxH = availH;
                float boxW = boxH * aspect;
                if (boxW > availW) { boxW = availW; boxH = boxW / aspect; }
                // 放得下才画（矮屏宁可不画，也不留一条压扁的缝）
                // ★ 2026-11-26：门槛从 pw*0.28 降到 pw*0.20 —— 16:9 矮屏算出来正好
                //   382px 宽，卡在旧门槛外，而那恰恰是「按钮占屏比例最大、最需要这个
                //   设置」的机型。现在它能画出来（k≈0.51，标准键半径 39px），看得清。
                if (boxW > pw * 0.20f && boxH > ph * 0.10f)
                    _lookPrev = new Rect(px0 + (availW - boxW) * 0.5f, by, boxW, boxH);
                else
                    _lookPrev = new Rect(0, 0, 0, 0);
            }
        }

        static int BodyFontSize()
        {
            return Mathf.RoundToInt(Mathf.Clamp(_panel.height * 0.040f, 16f, 32f));
        }

        static Rect ModeBtnRect(int index)
        {
            float pw = _panel.width;
            float pad = pw * 0.035f;
            float gap = pw * 0.018f;
            float bw = (pw - pad * 2 - gap * (TabCount - 1)) / TabCount;
            float bh = _panel.height * 0.115f;
            return new Rect(_panel.x + pad + (bw + gap) * index, _modeBtnBase, bw, bh);
        }

        // ══════════════════════════════════════════════════════
        //  触摸（面板打开时吃掉全部触摸，游戏不会在背后乱走）
        // ══════════════════════════════════════════════════════

        /// <summary>NPC 对话框开着的时候，左上角被原版对话选项占了，设置键让位。</summary>
        static bool DialogBlocked
        {
            get
            {
                try { return Bridge.NpcDialogVisible(); } catch { return false; }
            }
        }

        /// <summary>返回 true 表示这一帧的触摸已被面板处理，外壳不要再给摇杆/场景。</summary>
        public static bool Step()
        {
            PumpKeyboard();

            if (!IsOpen)
            {
                // ★ 2026-11-18 开会定案（三刀·A2）：画的时候让位了，点的时候也必须让位。
                //   NPC 对话第一个选项在 (140,70) r=62，整块压在 (40,40)-(156,156) 这个
                //   设置键占位里；而对话框一开设置键是**隐形**的（DrawSettingsButton 里
                //   `if (DialogBlocked) return;`）。原来只判画不判点 ⇒ 点「买」「卖」「修理」
                //   会直接弹出联机设置面板，对话被打断，玩家看到的是「点了选项跳出设置页」。
                if (DialogBlocked) return false;
                for (int i = 0; i < Input.touchCount; i++)
                {
                    var t = Input.GetTouch(i);
                    Vector2 p = ToGui(t.position);
                    if (t.phase != TouchPhase.Began && t.phase != TouchPhase.Ended) continue;
                    // 四个边界都要判：原来只判右/下界，屏幕最左上 (0,0)-(40,40) 也算按钮
                    if (p.x < BtnX || p.y < BtnY || p.x > BtnX + BtnW || p.y > BtnY + BtnH) continue;
                    if (_finger == t.fingerId) continue;
                    _finger = t.fingerId;
                    Open();
                    _flashT = 0.12f;
                    return true;
                }
                return false;
            }

            Layout();
            if (_flashT > 0f) _flashT -= Time.unscaledDeltaTime;

            // ★ 2026-11-12 修「点了没反应」：_finger 是「这一根手指已经被我们处理过」的锁。
            //   原来的复位只有两处 —— Close() 和帧尾的 `if (Input.touchCount == 0)`。
            //   手机帧率低（这台模拟器只有 0.3 帧/秒）时，「抬手」那一瞬间可能正好落在
            //   两帧之间，于是 touchCount==0 永远观察不到 → _finger 卡在 0 不放。
            //   Android 复用手指数（第一根手指永远是 0），之后每一次点击的 fingerId
            //   都等于它，全部被 `continue` 跳过 —— 玩家看到的就是「按了没反应」。
            //
            //   复位条件改成「这根手指已经从触摸列表里消失」，不再依赖「整屏没手指」：
            //   下面允许在 Began 和 Ended 两个阶段都触发，所以不能一看到 Ended 就解锁
            //   （那样同一根手指会在下一帧被当成新点击，一次点击触发两遍）。
            if (_finger >= 0)
            {
                bool stillDown = false;
                for (int i = 0; i < Input.touchCount; i++)
                    if (Input.GetTouch(i).fingerId == _finger) { stillDown = true; break; }
                if (!stillDown) _finger = -2;
            }

            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase != TouchPhase.Began && t.phase != TouchPhase.Ended) continue;
                Vector2 p = ToGui(t.position);
                if (_finger == t.fingerId) continue;

                for (int h = 0; h < _hits.Count; h++)
                {
                    if (!_hits[h].r.Contains(p)) continue;
                    _finger = t.fingerId;
                    _flashT = 0.12f;
                    DoHit(_hits[h].id);
                    return true;
                }

                // 面板外：点空白关掉（手游习惯）
                if (t.phase == TouchPhase.Began && !_panel.Contains(p))
                {
                    _finger = t.fingerId;
                    Close();
                    return true;
                }
                // 面板内的空白：吃掉，不落到游戏
            }

            if (Input.touchCount == 0) _finger = -2;
            return true;   // 面板开着就吃掉全部输入
        }

        static void DoHit(int id)
        {
            switch (id)
            {
                case ID_CLOSE:
                    Close();
                    return;

                case ID_MODE_SINGLE:
                    _tab = 0;
                    if (P2NetMode.Mode == NetMode.Join) { P2NetMode.GoSingle(); P2NetMode.SwitchToSelf(); }
                    else P2NetMode.GoSingle();
                    Log("[联机] 点「单机游玩」→ " + P2NetMode.StatusText());
                    return;

                case ID_MODE_HOST:
                    _tab = 1;
                    if (P2NetMode.Mode == NetMode.Join) { P2NetMode.GoHost(); P2NetMode.SwitchToSelf(); }
                    else P2NetMode.GoHost();
                    Log("[联机] 点「开服务器」→ " + P2NetMode.StatusText());
                    return;

                case ID_MODE_JOIN:
                    // 只切页面，不立刻断开：玩家还没填地址，别把现有游戏踢掉
                    _tab = 2;
                    P2NetMode.StartDiscovery();
                    Log("[联机] 点「加入游戏」→ 已开始搜索");
                    return;

                case ID_INPUT:
                    OpenKeyboard();
                    return;

                case ID_CONNECT:
                    {
                        string s = _typed;
                        if (string.IsNullOrEmpty(s) || s.Trim().Length == 0)
                        {
                            Log("[联机] 还没输地址");
                            return;
                        }
                        CloseKeyboard();

                        // 先看是不是 6 位公网匹配码（纯字母数字，没有点和冒号）。
                        // 是 → 走互联网电话簿查地址；不是 → 当 IP/端口直接连。
                        string code = P2NetMode.NormalizeCode(s);
                        if (code != null)
                        {
                            Log("[联机] 当作公网匹配码 " + code);
                            P2NetMode.StartLookup(code);
                            return;
                        }

                        if (P2NetMode.GoJoin(s)) Close();
                    }
                    return;

                case ID_SEARCH:
                    P2NetMode.StartDiscovery();
                    return;

                case ID_NEWCODE:
                    P2NetMode.NewPublicCode();
                    Log("[联机] 换成新码 " + P2NetMode.PublicCode);
                    return;

                // ── 按键外观页（2026-11-26）──
                case ID_LOOK:
                    _tab = 3;
                    return;

                case ID_SCALE_DOWN:
                    P2VirtualPad.StepScale(-1);
                    return;

                case ID_SCALE_UP:
                    P2VirtualPad.StepScale(1);
                    return;

                case ID_ALPHA_DOWN:
                    P2VirtualPad.StepAlpha(-1);
                    return;

                case ID_ALPHA_UP:
                    P2VirtualPad.StepAlpha(1);
                    return;

                case ID_LOOK_RESET:
                    P2VirtualPad.ResetLook();
                    Log("[外观] 已恢复默认");
                    return;
            }

            if (id >= ID_ROOM_BASE)
            {
                var rooms = P2NetMode.Rooms();
                int k = id - ID_ROOM_BASE;
                if (k >= 0 && k < rooms.Count)
                {
                    var r = rooms[k];
                    Log("[联机] 点房间 " + r.Display);
                    if (P2NetMode.GoJoin(r.Address + ":" + r.Port)) Close();
                }
            }
        }

        static Vector2 ToGui(Vector3 screenPos)
        {
            // Unity 触摸坐标左下原点、Y 向上；GUI 左上原点、Y 向下。
            return new Vector2(screenPos.x, Screen.height - screenPos.y);
        }

        // ══════════════════════════════════════════════════════
        //  绘制
        // ══════════════════════════════════════════════════════

        static void EnsureStyle()
        {
            if (!_fontTried)
            {
                _fontTried = true;
                _font = Resources.Load<Font>("chatfont");
                if (_font == null)
                {
                    string[] cands = { "Noto Sans CJK SC", "Noto Sans SC", "Droid Sans Fallback", "Arial Unicode MS" };
                    try { _font = Font.CreateDynamicFontFromOSFont(cands, 32); } catch { _font = null; }
                }
            }

            _title = NewStyle(TextAnchor.MiddleLeft);
            _body = NewStyle(TextAnchor.MiddleLeft);
            _small = NewStyle(TextAnchor.MiddleCenter);
            _btn = NewStyle(TextAnchor.MiddleCenter);
            _btnOn = NewStyle(TextAnchor.MiddleCenter);
        }

        static GUIStyle NewStyle(TextAnchor a)
        {
            var s = new GUIStyle(GUI.skin.label);
            if (_font != null) s.font = _font;
            s.fontStyle = FontStyle.Normal;   // SimHei 没有粗体面，合成加粗会糊
            s.alignment = a;
            s.clipping = TextClipping.Overflow;
            s.wordWrap = false;
            s.padding = new RectOffset(0, 0, 0, 0);
            s.margin = new RectOffset(0, 0, 0, 0);
            return s;
        }

        // 面板配色：和 1.76 的暖褐石纹同一族，不再用冷灰（冷灰撞暖褐是"丑"的根因）
        static readonly Color32 C_BG = new Color32(30, 24, 18, 255);
        static readonly Color32 C_BG2 = new Color32(48, 38, 28, 255);
        static readonly Color32 C_BTN = new Color32(70, 56, 40, 255);
        static readonly Color32 C_BTN_ON = new Color32(120, 94, 52, 255);
        static readonly Color32 C_LINE = new Color32(150, 118, 66, 255);
        static readonly Color32 C_TXT = new Color32(236, 222, 190, 255);
        static readonly Color32 C_GOLD = new Color32(255, 214, 120, 255);
        static readonly Color32 C_DIM = new Color32(168, 152, 126, 255);

        static Texture2D Solid(Color32 c)
        {
            int key = (c.r << 24) | (c.g << 16) | (c.b << 8) | 1;
            Texture2D t;
            if (_solid.TryGetValue(key, out t) && t != null) return t;
            t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.SetPixels32(new Color32[] { c, c, c, c });
            t.Apply(false, false);
            _solid[key] = t;
            return t;
        }

        static void Fill(Rect r, Color32 c)
        {
            GUI.color = Color.white;
            GUI.DrawTexture(r, Solid(c), ScaleMode.StretchToFill, false);
        }

        static void Frame(Rect r, Color32 c, float w)
        {
            Fill(new Rect(r.x, r.y, r.width, w), c);
            Fill(new Rect(r.x, r.yMax - w, r.width, w), c);
            Fill(new Rect(r.x, r.y, w, r.height), c);
            Fill(new Rect(r.xMax - w, r.y, w, r.height), c);
        }

        static void Text(Rect r, string s, GUIStyle st, Color32 c)
        {
            st.normal.textColor = new Color32(0, 0, 0, 255);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s, st);
            st.normal.textColor = c;
            GUI.Label(r, s, st);
        }

        public static void Draw()
        {
            if (!IsOpen) return;
            EnsureStyle();
            Layout();

            float W = Screen.width, H = Screen.height;

            // 压暗全屏底（不透明纯色，A 通道被丢也无所谓）：让面板"浮起来"
            Fill(new Rect(0, 0, W, H), C_BG);

            var p = _panel;
            Fill(p, C_BG2);
            Frame(p, C_LINE, 4f);

            float pad = p.width * 0.035f;
            _title.fontSize = Mathf.RoundToInt(Mathf.Clamp(p.height * 0.058f, 20f, 44f));
            Text(new Rect(p.x + pad, p.y + pad * 0.6f, p.width - pad * 2 - p.height * 0.09f, p.height * 0.09f),
                 "设置", _title, C_GOLD);

            // 关闭
            var cr = new Rect(p.xMax - pad - p.height * 0.075f, p.y + pad * 0.55f, p.height * 0.075f, p.height * 0.075f);
            Fill(cr, C_BTN);
            Frame(cr, C_LINE, 2f);
            _btn.fontSize = Mathf.RoundToInt(cr.height * 0.66f);
            Text(cr, "×", _btn, C_TXT);

            // 版本号（左上角标题右边，避开「设置」两个字的实际宽度）
            // ★ 2026-11-26：「游戏内「设置」页可见版本号」这句对外承诺写在
            //   CONTRIBUTING.md 和 Issue 模板里，但运行时代码以前从没读过它 ——
            //   报 bug 的人照着找会发现根本没有。这里把它兑现。
            //   Application.version = PlayerSettings.bundleVersion，
            //   由 P2Build.cs:111 打成 "1.76." + yyyyMMdd，正好是反馈要的格式。
            //   取不到就整块不画，绝不显示一个孤零零的 "v"。
            if (!string.IsNullOrEmpty(Application.version))
            {
                float gapT = p.width * 0.018f;
                float vw = p.width * 0.34f;
                var oldAlign = _small.alignment;
                _small.fontSize = Mathf.RoundToInt(Mathf.Clamp(p.height * 0.036f, 14f, 30f));
                _small.alignment = TextAnchor.MiddleRight;
                Text(new Rect(cr.x - gapT - vw, p.y + pad * 0.6f, vw, p.height * 0.09f),
                     "v" + Application.version, _small, C_DIM);
                _small.alignment = oldAlign;
            }

            // 四个页签
            _btn.fontSize = Mathf.RoundToInt(Mathf.Clamp(p.height * 0.048f, 18f, 38f));
            DrawModeBtn(0, "单机游玩", _tab == 0);
            DrawModeBtn(1, "开服务器", _tab == 1);
            DrawModeBtn(2, "加入游戏", _tab == 2);
            DrawModeBtn(3, "按键外观", _tab == 3);

            float y = _modeBtnBase + p.height * 0.115f + p.height * 0.045f;
            _body.fontSize = BodyFontSize();   // 和 Layout 算命中框用的是同一个值，必须一致
            _small.fontSize = Mathf.RoundToInt(Mathf.Clamp(p.height * 0.036f, 14f, 30f));

            if (_tab == 0)
            {
                Text(new Rect(p.x + pad, y, p.width - pad * 2, _body.fontSize + 14f),
                     "现在只有你自己玩。外机连不进来，别人搜也搜不到。", _body, C_TXT);
                y += _body.fontSize + 26f;
                Text(new Rect(p.x + pad, y, p.width - pad * 2, _body.fontSize + 14f),
                     "想和朋友一起玩，点上面的「开服务器」。", _body, C_DIM);
            }
            else if (_tab == 1)
            {
                string ip = P2NetMode.LocalIp;
                string code = P2NetMode.MatchCode;
                string pub = P2NetMode.PublicCode;

                Text(new Rect(p.x + pad, y, p.width - pad * 2, _body.fontSize + 14f),
                     "你已经是服务器了。把这个码发给朋友，他在「加入游戏」里输进去就行：", _body, C_TXT);
                y += _body.fontSize + 26f;

                // 6 位匹配码：这是主路径，做成整页最大的一行
                _title.fontSize = Mathf.RoundToInt(Mathf.Clamp(p.height * 0.098f, 28f, 76f));
                Text(new Rect(p.x + pad, y, p.width - pad * 2, _title.fontSize + 24f),
                     "匹配码    " + pub, _title, C_GOLD);
                y += _title.fontSize + 44f;

                _body.fontSize = Mathf.RoundToInt(Mathf.Clamp(p.height * 0.040f, 16f, 32f));
                string pubLine = P2NetMode.PublicStatusText();
                // 绿色 = 中继真的连上了，这时候朋友才连得进来；
                // 橙色 = 还没连上/连不上。只看「有没有公网地址」是旧的假判据。
                bool relayOk = P2NetRelayOk();
                Text(new Rect(p.x + pad, y, p.width - pad * 2, _body.fontSize + 14f),
                     pubLine, _body, relayOk ? C_DIM : new Color32(255, 140, 110, 255));
                y += _body.fontSize + 26f;

                Text(new Rect(p.x + pad, y, p.width - pad * 2, _body.fontSize + 14f),
                     "同一个 WiFi 的朋友：不用输码，下面会自动出现你的房间，点一下就能进。", _body, C_DIM);
                y += _body.fontSize + 22f;

                Text(new Rect(p.x + pad, y, p.width - pad * 2, _body.fontSize + 14f),
                     "局域网地址 " + ip + "   局域网码 " + code + "   端口 " + P2NetMode.HostPort
                     + "   在线 " + P2NetMode.OnlineCount() + " 人", _body, C_DIM);
                y += _body.fontSize + 24f;

                if (ip == "0.0.0.0" || ip.StartsWith("127."))
                {
                    Text(new Rect(p.x + pad, y, p.width - pad * 2, _body.fontSize + 14f),
                         "★ 没拿到局域网 IP：先连上 WiFi 再开服（不影响跨城市的码）。", _body, new Color32(255, 140, 110, 255));
                    y += _body.fontSize + 22f;
                }

                // 换码：码被太多人知道、或者想清场时用（矩形由 Layout 算好，这里只画）
                var nb = _hitNB;
                Fill(nb, C_BTN);
                Frame(nb, C_LINE, 2f);
                Text(nb, "换一个码", _btn, C_TXT);
            }
            else if (_tab == 2)
            {
                // ── 加入游戏 ──
                Text(new Rect(p.x + pad, y - 6f, p.width - pad * 2, _body.fontSize + 14f),
                     "输对方的 6 位匹配码（如 7K3QMX），不同城市也能连。同一个 WiFi 也可以直接输 IP，如 1.5", _body, C_DIM);

                var ib = _inputBox;
                Fill(ib, new Color32(16, 12, 9, 255));
                Frame(ib, C_GOLD, 3f);
                _btn.fontSize = Mathf.RoundToInt(ib.height * 0.55f);
                string shown = string.IsNullOrEmpty(_typed) ? "点这里输入…" : _typed;
                Text(new Rect(ib.x + 18f, ib.y, ib.width - 36f, ib.height), shown, _btn,
                     string.IsNullOrEmpty(_typed) ? C_DIM : C_GOLD);

                // 已经连上时，把对方的真实状态摆在输入框下面：
                // 「对面在不在线」比「连了吗」更能说明问题 —— 掉线时玩家一眼知道该催谁。
                if (P2NetMode.Mode == NetMode.Join)
                {
                    bool on = P2NetMode.PeerOnline;
                    string who = string.IsNullOrEmpty(P2NetMode.PeerName) ? "对方" : P2NetMode.PeerName;
                    Text(new Rect(p.x + pad, ib.yMax + p.height * 0.004f, p.width - pad * 2, _body.fontSize + 10f),
                         on ? "已连上 " + who + "，他那边 " + P2NetMode.PeerPlayers + " 人在玩"
                            : "★ 和 " + who + " 断了，正在等他恢复…",
                         _small, on ? C_DIM : new Color32(255, 140, 110, 255));
                }

                var cb = new Rect(ib.xMax + p.width * 0.018f, ib.y,
                                  ModeBtnRect(0).width * 0.5f - p.width * 0.018f, ib.height);
                Fill(cb, C_BTN_ON);
                Frame(cb, C_GOLD, 3f);
                Text(cb, "连接", _btn, C_GOLD);

                // 查码进行中／失败的结果，直接显示在按钮下面
                int ls = P2NetMode.LookupState;
                float ry = ib.yMax + p.height * 0.020f;
                if (ls == 1 || ls == 3)
                {
                    Text(new Rect(p.x + pad, ry, p.width - pad * 2, _body.fontSize + 16f),
                         P2NetMode.LookupMessage, _body,
                         ls == 1 ? C_GOLD : new Color32(255, 140, 110, 255));
                }
                else if (ls == 2)
                {
                    Text(new Rect(p.x + pad, ry, p.width - pad * 2, _body.fontSize + 16f),
                         "连上了。点右上角 × 关掉这个面板就能玩。", _body, C_GOLD);
                }

                // 房间列表（局域网自动发现）
                var rooms = P2NetMode.Rooms();
                float ly = _roomTop - _body.fontSize - 18f;   // 标题行紧贴列表上方
                float rh = p.height * 0.088f;
                Text(new Rect(p.x + pad, ly, p.width - pad * 2, _body.fontSize + 10f),
                     rooms.Count == 0
                        ? "同一个 WiFi 的房间会自动出现在下面（不同城市就用上面的码）"
                        : "同一个 WiFi 里找到的房间：", _small, C_DIM);

                if (rooms.Count > 0)
                {
                    int n = Mathf.Min(rooms.Count, 4);
                    for (int i = 0; i < n; i++)
                    {
                        var r = new Rect(p.x + pad, _roomTop + (rh + p.height * 0.014f) * i, p.width - pad * 2, rh);
                        Fill(r, C_BTN);
                        Frame(r, C_LINE, 2f);
                        Text(new Rect(r.x + 20f, r.y, r.width - 40f, r.height), rooms[i].Display, _btn, C_TXT);
                    }
                }
            }
            else
            {
                // ── 按键外观（2026-11-26 新增，对应 Issue #1：按键超大无法修改）──
                // 所有矩形由 Layout 算好（_lookMinus/_lookVal/_lookPlus），这里只画，杜绝"看得见点不着"。
                Text(new Rect(p.x + pad, y, p.width - pad * 2, _lookTop - y),
                     "手机屏幕大小不一。按键嫌大就调小一档，改完立刻生效，也会记住。", _body, C_DIM);

                DrawLookRow(0, "按键大小", P2VirtualPad.ScaleLabel, P2VirtualPad.ScaleAtMin, P2VirtualPad.ScaleAtMax);
                DrawLookRow(1, "按键浓淡", P2VirtualPad.AlphaLabel, P2VirtualPad.AlphaAtMin, P2VirtualPad.AlphaAtMax);

                var rb = _lookReset;
                Fill(rb, C_BTN);
                Frame(rb, C_LINE, 2f);
                Text(rb, "恢复默认", _btn, C_TXT);

                // 真实按键预览：和游戏里同一个 DrawFunc，改档位这里当场变
                if (_lookPrev.width > 0f)
                {
                    Fill(_lookPrev, new Color32(14, 11, 8, 255));   // 深底才能看不出按键的描边压暗
                    Frame(_lookPrev, C_LINE, 2f);
                    P2VirtualPad.DrawLookPreview(_lookPrev);
                }
            }
        }

        /// <summary>外观页的一行：左标签，右 [−] [值] [+]。矩形全部来自 Layout。</summary>
        static void DrawLookRow(int i, string label, string val, bool atMin, bool atMax)
        {
            var p = _panel;
            float pad = p.width * 0.035f;
            float gap = p.width * 0.018f;
            float lh = _lookVal[i].height;

            // ★ 字号必须在每次 Text 之前设：DrawLookStep 结尾会把 _btn.fontSize 还原成
            //   页签那档，先设一次会被它冲掉，值框里的字就比标签小一截。
            int rowFs = Mathf.RoundToInt(Mathf.Clamp(lh * 0.46f, 16f, 40f));

            // 标签宽度**从来**= 从内边距到 [−] 键左边（由 Layout 的 labW 决定），
            // 这里再手写一个比例迟早对不上，直接由 _lookMinus 反推。
            _btn.fontSize = rowFs;
            float labW = _lookMinus[i].x - (p.x + pad) - gap;
            Text(new Rect(p.x + pad, _lookVal[i].y, labW, lh), label, _btn, C_TXT);

            DrawLookStep(_lookMinus[i], "−", !atMin);
            DrawLookStep(_lookPlus[i], "+", !atMax);

            var vr = _lookVal[i];
            Fill(vr, new Color32(16, 12, 9, 255));
            Frame(vr, C_GOLD, 3f);
            _btn.fontSize = rowFs;
            Text(vr, val, _btn, C_GOLD);
            _btn.fontSize = Mathf.RoundToInt(Mathf.Clamp(p.height * 0.048f, 18f, 38f));
        }

        static void DrawLookStep(Rect r, string glyph, bool enabled)
        {
            Fill(r, enabled ? C_BTN_ON : new Color32(40, 32, 24, 255));
            Frame(r, enabled ? C_GOLD : new Color32(90, 74, 52, 255), enabled ? 3f : 2f);
            _btn.fontSize = Mathf.RoundToInt(r.height * 0.62f);
            Text(r, glyph, _btn, enabled ? C_GOLD : new Color32(110, 96, 74, 255));
            _btn.fontSize = Mathf.RoundToInt(Mathf.Clamp(_panel.height * 0.048f, 18f, 38f));
        }

        /// <summary>中继是不是真的连上了（面板配色用，不要靠猜）。</summary>
        static bool P2NetRelayOk()
        {
            return P2Relay.HostRunning || P2Relay.JoinRunning;
        }

        static void DrawModeBtn(int i, string label, bool on)
        {
            var r = ModeBtnRect(i);
            Fill(r, on ? C_BTN_ON : C_BTN);
            Frame(r, on ? C_GOLD : C_LINE, on ? 4f : 2f);
            Text(r, label, _btn, on ? C_GOLD : C_TXT);
        }

        /// <summary>设置小按钮（画在虚拟按键之上）。</summary>
        public static void DrawSettingsButton()
        {
            if (IsOpen) return;
            if (DialogBlocked) return;   // NPC 对话选项占了左上角，让位

            EnsureStyle();
            var r = new Rect(BtnX, BtnY, BtnW, BtnH);
            Fill(r, new Color32(44, 34, 24, 255));
            Frame(r, C_LINE, 3f);
            _btn.fontSize = Mathf.RoundToInt(BtnH * 0.34f);
            Text(r, "设置", _btn, C_GOLD);
        }

        public static void Reset()
        {
            Close();
            _finger = -2;
            _typed = "";
        }
    }
}
