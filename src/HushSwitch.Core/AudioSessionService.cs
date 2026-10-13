// Core Audio を呼ぶ唯一の場所。有効な再生デバイスすべてのセッションを列挙し、
// 指定されたセッションのミュートを読み書きする。書き換えるのはミュートだけ(INV-3)。
// スレッドの面倒は見ない。呼ぶ側が1本のスレッドから直列に呼ぶこと(D-11)。
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

/// <summary>ミュートを書けなかったセッションと、そのときのエラーコード。</summary>
public sealed record WriteFailure(SessionInfo Session, int HResult);

/// <summary>
/// ミュートの書き込み結果。Sessions は書いたあとに読み直した全セッション(INV-8)、Failed は書けなかったもの、
/// DeviceErrors は読めなかったデバイスのエラーコード(ログ用。デバイス名は入れない)。
/// </summary>
public sealed record MuteResult(
    IReadOnlyList<SessionInfo> Sessions, IReadOnlyList<WriteFailure> Failed, IReadOnlyList<string> DeviceErrors);

/// <summary>Core Audio に接続できない(オーディオサービスの停止等。FR-14)。</summary>
public sealed class AudioUnavailableException(string reason) : Exception(reason);

/// <summary>MuteController から見える Core Audio。偽の実装を差し込んで規則を試験するために切り出してある(AC-19)。</summary>
public interface IAudioSessions
{
    IReadOnlyList<SessionInfo> ListSessions();

    /// <summary>target が真を返したセッションにミュートを書き、書いたあとの状態を読み直して返す。</summary>
    MuteResult SetMute(Func<SessionInfo, bool> target, bool mute);
}

public sealed class AudioSessionService(EventLog? log = null) : IAudioSessions
{
    private const int DeviceInvalidated = unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED

    // セッション作成通知を受けるために持ち続けるデバイス(ID → デバイス)。列挙は毎回、取り直したデバイスで行う
    private readonly Dictionary<string, MMDevice> watched = [];
    private HashSet<string>? known;   // 前回の有効なデバイス

    /// <summary>直前の呼び出しで読めなかったデバイス(「デバイス名: 理由」)。</summary>
    public List<string> DeviceErrors { get; } = [];

    /// <summary>どれかのデバイスに新しいセッションができた(D-9)。Core Audio の通知スレッドから呼ばれる。</summary>
    public event Action? SessionCreated;

    public IReadOnlyList<SessionInfo> ListSessions() => Walk(null, false).Sessions;

    public MuteResult SetMute(Func<SessionInfo, bool> target, bool mute) => Walk(target, mute);

    private MuteResult Walk(Func<SessionInfo, bool>? target, bool mute)
    {
        DeviceErrors.Clear();
        var names = ProcessNames.Snapshot();
        var list = new List<SessionInfo>();
        var failed = new List<WriteFailure>();
        var codes = new List<string>();
        var active = new HashSet<string>();
        int deviceCount = 0;
        bool lost = false;   // 抜かれた以外の理由で読めないデバイスがあった

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                deviceCount++;
                bool keep = false;
                string deviceName = "(名前を取得できず)";
                try
                {
                    string id = device.ID;
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
                            id, deviceName, session.GetSessionInstanceIdentifier,
                            pid, names.GetValueOrDefault(pid),
                            session.State.ToString().Replace("AudioSessionState", ""),
                            volume.Mute, session.IsSystemSoundsSession);

                        // システム音と、exe 名を解決できなかったセッションには、何を渡されても書かない(FR-1)
                        if (target != null && !info.IsSystemSounds && info.Exe != null && target(info))
                        {
                            int error = 0;
                            try { volume.Mute = mute; }
                            catch (Exception e) { error = e.HResult; } // アクセス拒否等。ほかのセッションは続ける(§10)
                            info = info with { Muted = volume.Mute };
                            if (error != 0) failed.Add(new WriteFailure(info, error));
                        }
                        list.Add(info);
                    }

                    active.Add(id);
                    if (!watched.ContainsKey(id))
                    {
                        // 通知の登録は上の RefreshSessions が済ませている。このデバイスは捨てずに持ち続ける
                        manager.OnSessionCreated += (_, _) => SessionCreated?.Invoke();
                        watched[id] = device;
                        keep = true;
                    }
                }
                catch (Exception e)
                {
                    // 途中で抜かれた等。このデバイスのセッションは無かったものとして扱う(§10)
                    DeviceErrors.Add($"{deviceName}: 0x{e.HResult:X8}");
                    codes.Add($"device 0x{e.HResult:X8}");
                    if (e.HResult != DeviceInvalidated) lost = true;
                }
                finally
                {
                    if (!keep) device.Dispose();
                }
            }
        }
        catch (Exception e)
        {
            Unwatch(_ => true);
            throw new AudioUnavailableException($"{e.GetType().Name} 0x{e.HResult:X8}");
        }

        // いなくなったデバイスの購読をやめる。読めないデバイスがあったときは、
        // 持っている購読も切れているかもしれないので全部捨てて、次の列挙で作り直す
        Unwatch(id => codes.Count > 0 || !active.Contains(id));
        if (deviceCount > 0 && codes.Count == deviceCount && lost)
            throw new AudioUnavailableException(codes[0]);

        if (known != null && !known.SetEquals(active))
            log?.Write("device_changed", "sync", reason: $"devices {known.Count} -> {active.Count}");
        known = active;
        return new MuteResult(list, failed, codes);
    }

    private void Unwatch(Func<string, bool> which)
    {
        foreach (string id in watched.Keys.Where(which).ToList())
        {
            try { watched[id].Dispose(); }
            catch (Exception) { } // もう無いデバイス。捨てるだけなので無視する
            watched.Remove(id);
        }
    }
}
