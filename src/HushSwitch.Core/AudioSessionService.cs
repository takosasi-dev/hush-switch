// Core Audio を呼ぶ唯一の場所。有効な再生デバイスすべてのセッションを列挙し、
// 指定されたセッションのミュートを読み書きする。書き換えるのはミュートだけ(INV-3)。
// スレッドの面倒は見ない。呼ぶ側が1本のスレッドから直列に呼ぶこと(D-11)。
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace HushSwitch;

/// <summary>列挙した1セッション。Exe は小文字で、解決できなければ null。</summary>
public sealed record SessionInfo(
    string DeviceId, string DeviceName, string InstanceId,
    uint Pid, string? Exe, string State, bool Muted, bool IsSystemSounds)
{
    /// <summary>セッションの同一性(§9.4 の規則 3)。</summary>
    public (string, string) Key => (DeviceId, InstanceId);
}

/// <summary>ミュートの書き込み結果。Sessions は書いたあとに読み直した全セッション(INV-8)、Failed は書けなかったもの。</summary>
public sealed record MuteResult(IReadOnlyList<SessionInfo> Sessions, IReadOnlyList<SessionInfo> Failed);

/// <summary>MuteController から見える Core Audio。偽の実装を差し込んで規則を試験するために切り出してある(AC-19)。</summary>
public interface IAudioSessions
{
    IReadOnlyList<SessionInfo> ListSessions();

    /// <summary>target が真を返したセッションにミュートを書き、書いたあとの状態を読み直して返す。</summary>
    MuteResult SetMute(Func<SessionInfo, bool> target, bool mute);
}

public sealed class AudioSessionService : IAudioSessions
{
    /// <summary>直前の呼び出しで読めなかったデバイス(「デバイス名: 理由」)。</summary>
    public List<string> DeviceErrors { get; } = [];

    public IReadOnlyList<SessionInfo> ListSessions() => Walk(null, false).Sessions;

    public MuteResult SetMute(Func<SessionInfo, bool> target, bool mute) => Walk(target, mute);

    private MuteResult Walk(Func<SessionInfo, bool>? target, bool mute)
    {
        DeviceErrors.Clear();
        var names = ProcessNames.Snapshot();
        var list = new List<SessionInfo>();
        var failed = new List<SessionInfo>();

        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device)
            {
                string deviceName = "(名前を取得できず)";
                try
                {
                    deviceName = device.FriendlyName;
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        uint pid = session.GetProcessID;
                        var volume = session.SimpleAudioVolume;
                        var info = new SessionInfo(
                            device.ID, deviceName, session.GetSessionInstanceIdentifier,
                            pid, names.GetValueOrDefault(pid),
                            session.State.ToString().Replace("AudioSessionState", ""),
                            volume.Mute, session.IsSystemSoundsSession);

                        // システム音と、exe 名を解決できなかったセッションには、何を渡されても書かない(FR-1)
                        if (target != null && !info.IsSystemSounds && info.Exe != null && target(info))
                        {
                            bool written = true;
                            try { volume.Mute = mute; }
                            catch (Exception) { written = false; } // アクセス拒否等。ほかのセッションは続ける(§10)
                            info = info with { Muted = volume.Mute };
                            if (!written) failed.Add(info);
                        }
                        list.Add(info);
                    }
                }
                catch (COMException e)
                {
                    // 途中で抜かれた等。このデバイスのセッションは無かったものとして扱う(§10)
                    DeviceErrors.Add($"{deviceName}: 0x{e.HResult:X8}");
                }
            }
        }
        return new MuteResult(list, failed);
    }
}
