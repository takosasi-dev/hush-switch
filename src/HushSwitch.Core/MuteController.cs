// 対象ごとのミュート意図(Intent)を持ち、§9.4 の状態規則(表示・トグル・Intent の更新)を当てはめる。
// 表示は毎回、読み直したセッションの状態から作る(INV-8)。登録された対象のセッションにしか書かない(INV-10)。
// スレッドの面倒は見ない。呼ぶ側が1本のスレッドから直列に呼ぶこと(D-11)。
using System.Diagnostics;

namespace HushSwitch;

/// <summary>§9.4 の表示状態。順に ミュート中 / 一部ミュート / ミュートなし / ミュート予約中 / 音を出していません。</summary>
public enum Display { Muted, Partial, Unmuted, Reserved, Silent }

/// <summary>対象1件の表示。WriteFailed は「一部のセッションを操作できませんでした」を出すか。</summary>
public sealed record TargetState(string Exe, Display Display, bool WriteFailed)
{
    /// <summary>ボタンを押すと解除になる状態か(§9.4 のトグル)。</summary>
    public bool IsOn => Display is Display.Muted or Display.Reserved;

    /// <summary>削除してよい状態か(FR-4)。</summary>
    public bool CanRemove => Display is Display.Unmuted or Display.Silent;

    /// <summary>ミュート中・一部ミュート・ミュート予約中のどれかか(終了時の確認 FR-13 と、トレイの絵 FR-17)。</summary>
    public bool HasMute => Display is Display.Muted or Display.Partial or Display.Reserved;
}

/// <summary>
/// 画面に出す1回ぶんの状態。Apps は今セッションを持つ exe 名(追加ダイアログ用)、
/// Ids は exe 名 → そのアプリのセッションの識別子(アイコンを読むために、exe の場所を取り出す)。
/// </summary>
public sealed record Snapshot(
    IReadOnlyList<TargetState> Targets, IReadOnlyList<string> Apps, IReadOnlyDictionary<string, string> Ids);

public sealed class MuteController(IAudioSessions audio, EventLog? log = null)
{
    private readonly Dictionary<string, bool> intents = [];   // true = Muted。無い対象は、まだ最初の列挙をしていない
    private readonly HashSet<string> writeFailed = [];
    private HashSet<(string, string)> seen = [];               // 前回の列挙にあったセッション
    private IReadOnlyList<SessionInfo> sessions = [];

    /// <summary>
    /// 列挙して規則を当てはめる。同期ループからは引数なしで、セッション作成通知からは
    /// 通知を受けた時刻(Stopwatch.GetTimestamp)を渡して呼ぶ。
    /// </summary>
    public Snapshot Sync(IReadOnlyList<string> targets, long? notifiedAt = null)
    {
        long origin = notifiedAt ?? Stopwatch.GetTimestamp();
        var list = audio.ListSessions();
        LogExternalChanges(targets, list);
        return Apply(targets, list, notifiedAt != null ? "notify" : "sync", origin);
    }

    /// <param name="pressedAt">ボタンが押された時刻(Stopwatch.GetTimestamp)。ログの latency_ms の起点。</param>
    public Snapshot Toggle(IReadOnlyList<string> targets, string exe, long? pressedAt = null) =>
        Set(targets, [exe], !State(exe).IsOn, "ui", pressedAt);

    /// <summary>まとめトグル(FR-7)。全対象がミュート中かミュート予約中なら解除し、そうでなければミュートする。</summary>
    /// <param name="source">ログの source。ウィンドウのボタンは group、トレイのメニューは tray、ホットキーは hotkey。</param>
    public Snapshot ToggleAll(IReadOnlyList<string> targets, long? pressedAt = null, string source = "group") =>
        Set(targets, targets, !targets.All(exe => State(exe).IsOn), source, pressedAt);

    /// <summary>
    /// 一部の対象(カテゴリー)をまとめて切り替える。規則は FR-7 と同じで、exes が全部ミュート中か予約中なら解除する。
    /// targets には全対象を渡す(渡さなかった対象の意図は Apply で消えるため)。exes の外には書かない(INV-10)。
    /// </summary>
    public Snapshot ToggleSome(IReadOnlyList<string> targets, IReadOnlyList<string> exes, long? pressedAt = null, string source = "category") =>
        Set(targets, exes, !exes.All(exe => State(exe).IsOn), source, pressedAt);

    /// <summary>終了時に全対象を解除する(FR-13 の unmute)。</summary>
    public Snapshot UnmuteAll(IReadOnlyList<string> targets) => Set(targets, targets, false, "exit", null);

    private Snapshot Set(IReadOnlyList<string> targets, IReadOnlyList<string> exes, bool mute, string source, long? pressedAt)
    {
        long origin = pressedAt ?? Stopwatch.GetTimestamp();
        var chosen = exes.Where(targets.Contains).ToList();
        foreach (var exe in chosen)
        {
            intents[exe] = mute;
            writeFailed.Remove(exe);
        }
        var result = audio.SetMute(s => chosen.Any(exe => Matches(s, exe)), mute);
        NoteFailures(result);
        foreach (var exe in chosen)
            Log("toggle", source, exe, mute, result, result.Sessions.Where(s => Matches(s, exe)).ToList(), origin);
        // いま書いたセッションは「見た」ことにする(下の規則 3 でもう一度書かない)
        seen.UnionWith(result.Sessions.Where(s => chosen.Any(exe => Matches(s, exe))).Select(s => s.Key));
        return Apply(targets, result.Sessions, "sync", origin);
    }

    private Snapshot Apply(IReadOnlyList<string> targets, IReadOnlyList<SessionInfo> list, string source, long origin)
    {
        foreach (var gone in intents.Keys.Where(exe => !targets.Contains(exe)).ToList())
        {
            intents.Remove(gone);
            writeFailed.Remove(gone);
        }

        // 規則 3: Intent が Muted の対象に、前回の列挙に無かったセッションが現れたら、そのセッションだけをミュートする
        var fresh = list
            .Where(s => !seen.Contains(s.Key) && targets.Any(exe => Matches(s, exe) && intents.GetValueOrDefault(exe)))
            .ToList();
        // Windows が保存済みのミュートを戻していて、もうミュートになっているものには書かない
        var unmuted = fresh.Where(s => !s.Muted).Select(s => s.Key).ToHashSet();
        // 下の書き込みの途中で現れたセッションは「見た」ことにしない(次の同期で規則 3 にかける)
        seen = list.Select(s => s.Key).ToHashSet();
        var result = new MuteResult(list, [], []);
        if (unmuted.Count > 0)
        {
            result = audio.SetMute(s => unmuted.Contains(s.Key), true);
            NoteFailures(result);
            list = result.Sessions;
        }
        foreach (var exe in fresh.Select(s => s.Exe!).Distinct())
            Log("session_created", source, exe, true, result, fresh.Where(s => s.Exe == exe).ToList(), origin,
                fresh.Any(s => s.Exe == exe && !s.Muted) ? null : "already_muted");

        foreach (var exe in targets)
        {
            var own = list.Where(s => Matches(s, exe)).ToList();
            if (!intents.TryGetValue(exe, out bool muted))
                intents[exe] = own.Count > 0 && own.All(s => s.Muted);   // 規則 1(あとから追加した対象は、追加後の最初の列挙)
            else if (muted && own.Count > 0 && !own.Any(s => s.Muted))
                intents[exe] = false;                                    // 規則 2: Windows 側で全部解除された
        }

        sessions = list;
        var named = list.Where(s => !s.IsSystemSounds && s.Exe != null).ToList();
        var apps = named.Select(s => s.Exe!).Distinct().Order().ToList();
        var ids = named.GroupBy(s => s.Exe!).ToDictionary(g => g.Key, g => g.First().InstanceId);
        return new Snapshot(targets.Select(State).ToList(), apps, ids);
    }

    private TargetState State(string exe)
    {
        var own = sessions.Where(s => Matches(s, exe)).ToList();
        int muted = own.Count(s => s.Muted);
        var display =
            own.Count == 0 ? (intents.GetValueOrDefault(exe) ? Display.Reserved : Display.Silent)
            : muted == own.Count ? Display.Muted
            : muted > 0 ? Display.Partial
            : Display.Unmuted;
        return new TargetState(exe, display, writeFailed.Contains(exe));
    }

    // システム音と、exe 名を解決できなかったセッションは対象外(FR-1)
    private static bool Matches(SessionInfo session, string exe) => !session.IsSystemSounds && session.Exe == exe;

    private void NoteFailures(MuteResult result)
    {
        foreach (var failure in result.Failed)
            if (failure.Session.Exe != null) writeFailed.Add(failure.Session.Exe);
    }

    // 対象 1 件ぶんの操作をログに書く。own は、その操作が相手にしたセッション
    private void Log(string @event, string source, string exe, bool mute, MuteResult result,
        IReadOnlyList<SessionInfo> own, long origin, string? note = null)
    {
        if (log == null) return;
        var errors = result.Failed.Where(f => own.Any(s => s.Key == f.Session.Key))
            .Select(f => $"set_mute 0x{f.HResult:X8}").ToList();
        string outcome = errors.Count > 0 && errors.Count == own.Count ? "failed"
            : errors.Count > 0 || result.DeviceErrors.Count > 0 ? "partial"
            : "ok";
        var reasons = errors.Concat(result.DeviceErrors).Distinct().ToList();
        log.Write(@event, source, exe, mute ? "mute" : "unmute", own.Count,
            (long)Stopwatch.GetElapsedTime(origin).TotalMilliseconds, outcome,
            reasons.Count > 0 ? string.Join("; ", reasons) : note);
    }

    // Windows 側(音量ミキサー等)で変えられたミュートをログに残す(FR-9)。前回もあったセッションだけを比べる
    private void LogExternalChanges(IReadOnlyList<string> targets, IReadOnlyList<SessionInfo> list)
    {
        if (log == null) return;
        foreach (var exe in targets)
            foreach (bool muted in new[] { true, false })
            {
                int count = list.Count(s => Matches(s, exe) && s.Muted == muted
                    && sessions.Any(old => old.Key == s.Key && old.Muted != muted));
                if (count > 0) log.Write("external_change", "sync", exe, muted ? "mute" : "unmute", count, result: "ok");
            }
    }
}
