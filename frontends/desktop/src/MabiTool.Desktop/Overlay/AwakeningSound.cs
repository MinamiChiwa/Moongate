using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 徽章觉醒提示音：打开了音效、且当前版面有未关闭的觉醒监控浮窗或打开了觉醒显示的 DPS 元件（<see cref="AwakeningBoard.Watched"/>）时播放；
/// 每次触发只调用一次，两种元件同时存在也只响一次。内置提示音在运行时用代码合成（两个上行的衰减正弦音），按音量缩放样本后以
/// winmm <c>PlaySound</c> 从内存播放；用户选的本机 wav 或 mp3 用 MCI（mpegvideo 设备）播放并设音量，文件不存在或打不开时改用内置提示音。
/// 只调用 Windows 自带接口，不写临时文件，不下载任何东西。
/// </summary>
public static class AwakeningSound
{
    private const int SampleRate = 44100;
    // PlaySound 异步从内存播放时要求缓冲区在播放期间保持有效且地址不变：WAV 拷进非托管内存，
    // 下一次播放前先停止当前声音再释放上一块。
    private static IntPtr playing;
    private static bool mciOpen;
    private const string Alias = "moongate_awakening";

    /// <summary>测试替身：收到要播放的声音（文件或空、音量）时调用，代替实际播放。</summary>
    internal static Action<string?, double>? Player { get; set; }

    /// <summary>徽章觉醒触发：按当前版面与提示设置决定是否播放。可在任一线程调用，播放在界面线程上进行。</summary>
    public static void Notify(OverlayLayoutStore store)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Notify(store));
            return;
        }
        var alert = store.Awakening.Badge;
        if (!alert.Sound || !AwakeningBoard.Watched(store)) return;
        var file = alert.SoundFile is { } path && File.Exists(path) ? path : null;
        if (Player is { } player) { player(file, alert.Volume); return; }
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (file is null || !PlayFile(file, alert.Volume)) PlayTone(alert.Volume);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>内置提示音的 WAV（16 位单声道 44.1 kHz）：两个上行的衰减正弦音，音量 0–1 直接缩放样本。</summary>
    public static byte[] Tone(double volume)
    {
        const double first = 880.0, second = 1318.5;
        const double noteSeconds = 0.16, gapSeconds = 0.02;
        var noteSamples = (int)(SampleRate * noteSeconds);
        var gapSamples = (int)(SampleRate * gapSeconds);
        var total = noteSamples * 2 + gapSamples;
        var amplitude = Math.Clamp(volume, 0, 1) * 0.6 * short.MaxValue;
        var samples = new short[total];
        void Note(int offset, double frequency)
        {
            for (var i = 0; i < noteSamples; i++)
            {
                var t = i / (double)SampleRate;
                // 5 ms 起音，之后按指数衰减，避免爆音。
                var envelope = Math.Min(1, t / 0.005) * Math.Exp(-t * 14);
                samples[offset + i] = (short)Math.Round(amplitude * envelope * Math.Sin(2 * Math.PI * frequency * t));
            }
        }
        Note(0, first);
        Note(noteSamples + gapSamples, second);
        var data = new byte[total * 2];
        Buffer.BlockCopy(samples, 0, data, 0, data.Length);
        using var stream = new MemoryStream(44 + data.Length);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + data.Length); writer.Write("WAVE"u8);
            writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(SampleRate); writer.Write(SampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(data.Length); writer.Write(data);
        }
        return stream.ToArray();
    }

    private static void PlayTone(double volume)
    {
        if (volume <= 0) return;
        var wave = Tone(volume);
        Stop();
        playing = Marshal.AllocHGlobal(wave.Length);
        Marshal.Copy(wave, 0, playing, wave.Length);
        PlaySound(playing, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
    }

    /// <summary>停止正在播放的内置提示音并释放它的缓冲区。</summary>
    private static void Stop()
    {
        if (playing == IntPtr.Zero) return;
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        Marshal.FreeHGlobal(playing);
        playing = IntPtr.Zero;
    }

    private static bool PlayFile(string path, double volume)
    {
        if (mciOpen) { Mci($"close {Alias}"); mciOpen = false; }
        if (path.Contains('"')) return false;
        if (Mci($"open \"{path}\" type mpegvideo alias {Alias}") != 0) return false;
        mciOpen = true;
        Mci($"setaudio {Alias} volume to {(int)Math.Round(Math.Clamp(volume, 0, 1) * 1000)}");
        return Mci($"play {Alias} from 0") == 0;
    }

    private static int Mci(string command) => mciSendStringW(command, IntPtr.Zero, 0, IntPtr.Zero);

    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = false)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(string command, IntPtr buffer, int bufferSize, IntPtr callback);
}
