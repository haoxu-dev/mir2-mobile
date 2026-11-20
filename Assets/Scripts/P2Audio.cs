// P2Audio —— 把真源码的音效接到 Unity 的音频设备上。
//
// 背景（2026-11-04）：
//   Crystal 的 SoundManager 是照着原版 NAudio 写的 —— 它把一堆 ISampleProvider
//   混成一个流交给 WaveOutEvent。安卓上 WaveOutEvent 是垫片（ClientPortable/Shim/
//   NAudioShim.cs），垫片里有一个静态的 HostAudio 总线，负责真的把采样算出来。
//   但算出来之后还得有人把它交给设备 —— 就是这个脚本干的活。
//
// 做法：
//   AudioClip.Create(name, lengthSamples, channels, frequency, stream=true,
//                    PCMReaderCallback) —— 让 Unity 每帧（音频线程）来要数据，
//   我们在回调里调 NAudio.Wave.HostAudio.Fill()。
//   这样等价于原版的 WaveOutEvent，而且不需要给每一路声音各开一个 AudioSource。
//
// 为什么必须补 AudioListener：
//   Assets/Scenes/P2.unity 里一个 AudioListener 都没有（grep 实测 0 命中），
//   整个 Unity 场景是「零音频对象」的。没有 Listener 时 AudioSource 照样能播，
//   但设备上什么都听不到。所以这里在 Awake 里自己造一个并挂到相机/自己身上。
using System;
using NAudio.Wave;
using UnityEngine;

public class P2Audio : MonoBehaviour
{
    public static P2Audio Instance;

    /// <summary>日志走 P2Core.L，和别的模块统一。</summary>
    public static Action<string> Log;

    const int SampleRate = NAudio.Wave.HostAudio.SampleRate;
    const int Channels = NAudio.Wave.HostAudio.Channels;

    /// <summary>
    /// 缓冲区长度 = 1 秒。
    /// 用流式 AudioClip 时这个值只决定「第一次要多少数据」，真正的数据由回调按需给。
    /// 给 1 秒是为了让 Unity 侧有足够余量：模拟器上音频线程偶尔会被卡住 200~300ms，
    /// 缓冲太短就变成「一顿一顿的爆音」。
    /// </summary>
    const int BufferSamples = SampleRate; // 每声道 1 秒

    AudioSource _source;
    AudioClip _clip;
    float[] _buf;
    int _chunk;

    long _lastSamples;
    long _lastNonZero;
    float _lastPeak;
    float _statT;
    float _idleT;          // 连续安静了多少秒（安静时 30 秒才打一次心跳）
    int _cbCount;

    public static string LastError;

    public static P2Audio Ensure(GameObject host)
    {
        if (Instance != null) return Instance;
        if (host == null) return null;
        var c = host.AddComponent<P2Audio>();
        return c;
    }

    void Awake()
    {
        Instance = this;
        try { Setup(); }
        catch (Exception e)
        {
            LastError = e.GetType().Name + ": " + e.Message;
            Log?.Invoke("★音频初始化失败 " + LastError);
        }
    }

    void Setup()
    {
        // 0) AudioListener —— 场景里没有，必须自己补
        var listeners = UnityEngine.Object.FindObjectsOfType<AudioListener>();
        if (listeners == null || listeners.Length == 0)
        {
            var lg = new GameObject("P2AudioListener");
            lg.transform.SetParent(transform, false);
            lg.AddComponent<AudioListener>();
            Log?.Invoke("[audio] 场景里没有 AudioListener，已补一个");
        }
        else
        {
            Log?.Invoke("[audio] 已有 AudioListener × " + listeners.Length);
        }

        // 1) 采样缓冲。PCMReaderCallback 每帧要的长度不固定，按最大值预分配。
        //    Unity 的流式 clip 每次大约要 1024 帧（= 每声道 1024 个 float，立体声 2048）。
        _chunk = 2048;
        _buf = new float[_chunk * 2];

        // 2) 流式 AudioClip：数据由 PCMReaderCallback 提供，不占内存。
        _clip = AudioClip.Create("P2Mix", BufferSamples, Channels, SampleRate, true, OnAudioRead, OnAudioSetPosition);
        if (_clip == null)
        {
            LastError = "AudioClip.Create 返回 null";
            Log?.Invoke("★音频初始化失败 " + LastError);
            return;
        }

        // 3) AudioSource。2D（spatialBlend=0）—— 传奇的音效不随镜头移动做 3D 定位，
        //    原版就是直接混到输出上的。
        var srcGo = new GameObject("P2AudioSource");
        srcGo.transform.SetParent(transform, false);
        _source = srcGo.AddComponent<AudioSource>();
        _source.clip = _clip;
        _source.loop = true;
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;
        _source.volume = 1f;
        _source.mute = false;
        _source.bypassEffects = true;
        _source.bypassListenerEffects = true;
        _source.priority = 0;   // 最高优先级，别被系统掐掉
        _source.Play();

        Log?.Invoke("[audio] 就绪 " + SampleRate + "Hz×" + Channels
                    + "  Play=" + _source.isPlaying
                    + "  clip=" + (_clip != null ? _clip.samples.ToString() : "null"));
    }

    /// <summary>
    /// Unity 音频线程回调。interleaved = 交错立体声：L R L R …
    /// 绝对不能在这里抛异常（会让整条音频管线静音），所以全包 try。
    /// </summary>
    void OnAudioRead(float[] data)
    {
        try
        {
            if (data == null) return;
            NAudio.Wave.HostAudio.Fill(data, data.Length);
            _cbCount++;
        }
        catch (Exception)
        {
        }
    }

    void OnAudioSetPosition(int newPosition)
    {
        // 流式 clip 不做定位，空实现即可
    }

    /// <summary>状态字符串，给 P2DevCmd 的 `audio` 命令用。</summary>
    public static string Status()
    {
        if (Instance == null) return "P2Audio 未创建" + (LastError != null ? "  " + LastError : "");
        var p = Instance;
        return string.Format(
            "clip={0} play={1} 回调={2} 送出采样={3} 有波形块={4} 峰值={5:F3} 活动源={6}",
            p._clip != null, p._source != null && p._source.isPlaying, p._cbCount,
            System.Threading.Interlocked.Read(ref NAudio.Wave.HostAudio.SamplesOut),
            System.Threading.Interlocked.Read(ref NAudio.Wave.HostAudio.NonZeroBlocks),
            NAudio.Wave.HostAudio.LastPeak,
            NAudio.Wave.HostAudio.InputCount);
    }

    /// <summary>
    /// 往日志里写音频状态（有声音时每秒一行；安静时 30 秒一行心跳）。
    /// 验收靠它：`回调` 在涨 = 音频线程在跑；`有波形块` 在涨 = 真有声音出来；
    /// `活动源` > 0 = 游戏逻辑确实往 mixer 里塞了声音。
    /// 没有这三条，就只能靠耳朵，而模拟器上根本听不见。
    /// </summary>
    void Update()
    {
        _statT += Time.unscaledDeltaTime;
        if (_statT < 1f) return;
        _statT = 0f;

        if (Log == null) return;

        long s = System.Threading.Interlocked.Read(ref NAudio.Wave.HostAudio.SamplesOut);
        long nz = System.Threading.Interlocked.Read(ref NAudio.Wave.HostAudio.NonZeroBlocks);
        float peak = NAudio.Wave.HostAudio.LastPeak;
        int live = NAudio.Wave.HostAudio.InputCount;

        long dS = s - _lastSamples, dNz = nz - _lastNonZero;
        _lastSamples = s;
        _lastNonZero = nz;

        // ★ 2026-11-13：真正的「有动静才打」。
        //   改之前这里无条件每秒写一行，实测把 p2_boot.log 整个淹掉 ——
        //   `tail -25` 全是 [audio]，查刷怪/NPC/任务日志只能先 grep -v 过滤。
        //   现在三条同时满足才写：这秒真出了波形块 / 有音源在放 / 距上次汇报超过 30 秒。
        //   也就是说安静时 30 秒才一行心跳，一有声音立刻逐秒可见。
        bool loud = dNz > 0 || live > 0;
        _idleT += 1f;
        if (!loud && _idleT < 30f) return;
        _idleT = 0f;

        Log("[audio] 回调=" + _cbCount + " +采样=" + dS + " +波形块=" + dNz
            + " 峰值=" + peak.ToString("F3") + " 活动源=" + live
            + " 播放=" + (_source != null && _source.isPlaying));
    }
}
