// P2NativeInput —— 把安卓系统输入法接到 DLL 里的 TextBox 上。
//
// 为什么必须有这一层（2026-11-02）：
//   Crystal 的输入框是 WinForms 的 TextBox（由 ClientPortable/Shim/TextBoxShim.cs 顶替），
//   它只负责存字、不管怎么进来。Windows 上由操作系统把键盘敲的字塞进去，
//   安卓上没有这个"操作系统",于是创角界面「请先输入名字」永远输不进去、
//   「确定」永远是灰的 —— 手机上根本选不了职业。
//
// 链路：
//   Bridge.KeyboardWanted()   → 有输入框拿到焦点？→ TouchScreenKeyboard.Open
//   Bridge.SetKeyboardText()  ← 每帧把 kb.text 灌回去 → 触发原版 TextChanged
//   Bridge.KeyboardSubmit()   → 回车（创角界面的「确定」）
//   Bridge.KeyboardBlur()     → 收焦点 / 收键盘
//
// 为什么不直接让 Unity 引用 System.Windows.Forms：
//   那是 MirClientCore.dll 内部的垫片类型（namespace 撞名），Unity 侧引用会打架。
//   所以统一走 Client.MirCore.Bridge 这一道门面。
//
// 与 P0HChat 的关系：那套是旧外壳（P0 系列）自己的聊天输入，已经被 P2Core 取代，
//   本文件是唯一在跑的输入通道。两边都写会抢键盘，别把 P0 那套再挂回来。
using Client.MirCore;
using UnityEngine;

public static class P2NativeInput
{
    public static System.Action<string> Log;

    static TouchScreenKeyboard _kb;
    static bool _wasWanted;
    static string _lastText = null;
    static bool _openFailedLogged;

    /// <summary>每帧调。自己判断该弹还是该收。</summary>
    public static void Tick()
    {
        bool want = false;
        try { want = Bridge.KeyboardWanted(); } catch { want = false; }

        if (want != _wasWanted)
        {
            if (want) Open();
            else Close();
            _wasWanted = want;
        }

        if (!want || _kb == null) return;

        // 安卓的 TouchScreenKeyboard 是「整串文字」语义，不是逐键事件。
        // 每帧比对**内容**变化再灌，避免每帧都重建控件纹理（那会明显掉帧）。
        //
        // ★ 2026-11-02 修：原来只比长度（`t.Length != _lastLen`）。
        //   安卓输入法里「删一个字再打一个字」「选中替换」「拼音候选换词」都会
        //   产生同长度但内容不同的串，那些改动一个都灌不进去，输入框里就永远停在旧字上。
        //   长度只是内容相等的一个必要条件，不能当判据用。
        try
        {
            // ★ 2026-11-04 修（第二处，这才是真根因）：
            //
            //   安卓上玩家按 BACK 收键盘时，TouchScreenKeyboard 会在**同一帧**里
            //   先把 text 清成 ""，再置 status=Canceled。原来的顺序是先读 text 灌字、
            //   再判 status —— 于是这一帧先执行 `Bridge.SetKeyboardText("")`，
            //   把名字框清空了，后面 Canceled 分支再"补灌"也救不回来（此刻 text 已经是空串）。
            //
            //   真机日志顺序就是证据：
            //     [输入] 灌字 8 字：DaoShi01    ← 正常打字
            //     [输入] 灌字 0 字：             ← ★ BACK 那一帧，text 已空，直接清框
            //   （"补灌" 那条日志一次都没出现过 —— 因为进到 Canceled 分支时 fin 已经是空串。）
            //
            //   所以必须**先判状态、再决定要不要灌**：键盘已结束（Done/Canceled/LostFocus）
            //   时一律不拿 text 去覆盖输入框，只在 Done 时提交、其余情况直接收键盘。
            var st = _kb.status;
            bool ended = st == TouchScreenKeyboard.Status.Done
                      || st == TouchScreenKeyboard.Status.Canceled
                      || st == TouchScreenKeyboard.Status.LostFocus;

            string t = _kb.text ?? "";
            if (!ended && t != _lastText)
            {
                _lastText = t;
                Bridge.SetKeyboardText(t);
                Say("[输入] 灌字 " + t.Length + " 字：" + t);
            }

            // 玩家按键盘右下角「完成/回车」= 原版按 Enter。
            // 创角界面的 TextBox_KeyPress 只认 Enter，靠这一下触发「确定」。
            if (st == TouchScreenKeyboard.Status.Done)
            {
                if (t.Length > 0) Bridge.SetKeyboardText(t);
                Bridge.KeyboardSubmit();
                Close();
                _wasWanted = Bridge.KeyboardWanted();
            }
            else if (st == TouchScreenKeyboard.Status.Canceled
                  || st == TouchScreenKeyboard.Status.LostFocus)
            {
                // 按 BACK / 系统收起键盘 = 「我要点屏幕上的按钮了」，不是「取消输入」。
                // 此时 text 已被安卓清空，绝不能拿它覆盖输入框；
                // 之前灌进去的字原样留在 TextBox 里，玩家接着点「确定」即可。
                Say("[输入] 键盘收起，框内保留 " + ((_lastText ?? "").Length) + " 字");
                Close();
                _wasWanted = Bridge.KeyboardWanted();
            }
        }
        catch (System.Exception ex)
        {
            Say("[输入] 读键盘失败 " + ex.GetType().Name + ": " + ex.Message);
            Close();
        }
    }

    static void Open()
    {
        try
        {
            if (!TouchScreenKeyboard.isSupported)
            {
                if (!_openFailedLogged)
                {
                    _openFailedLogged = true;
                    Say("[输入] 本平台不支持 TouchScreenKeyboard，输入框无法输入");
                }
                return;
            }
            // hideInput=true：消掉键盘上方那块不透明 EditText 浮层（P0HChat 已验证过的做法）
            try { TouchScreenKeyboard.hideInput = true; } catch { }

            string init = "";
            int max = 0;
            try { init = Bridge.KeyboardText() ?? ""; } catch { }
            try { max = Bridge.KeyboardMaxLength(); } catch { }
            if (max <= 0) max = 32;

            _kb = TouchScreenKeyboard.Open(init, TouchScreenKeyboardType.Default,
                                           false, false, false, false, "", max);
            _lastText = init;
            if (_kb == null)
            {
                Say("[输入] 系统拒绝弹键盘（Open 返回 null）");
                return;
            }
            Say("[输入] 弹键盘 初始=" + (init.Length == 0 ? "（空）" : init) + " 上限=" + max);
        }
        catch (System.Exception ex)
        {
            Say("[输入] 弹键盘失败 " + ex.GetType().Name + ": " + ex.Message);
            _kb = null;
        }
    }

    static void Close()
    {
        try { if (_kb != null && _kb.active) _kb.active = false; } catch { }
        _kb = null;
        _lastText = null;
    }

    static void Say(string s) { if (Log != null) Log(s); }
}
