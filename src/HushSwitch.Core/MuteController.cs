// 対象ごとのミュート意図(Intent)を持ち、§9.4 の状態規則(表示・トグル・Intent の更新)を当てはめる。
// 表示は毎回、読み直したセッションの状態から作る(INV-8)。登録された対象のセッションにしか書かない(INV-10)。
// スレッドの面倒は見ない。呼ぶ側が1本のスレッドから直列に呼ぶこと(D-11)。
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
}

/// <summary>画面に出す1回ぶんの状態。Apps は今セッションを持つ exe 名(追加ダイアログ用)。</summary>
public sealed record Snapshot(IReadOnlyList<TargetState> Targets, IReadOnlyList<string> Apps);

public sealed class MuteController(IAudioSessions audio)
{
    private readonly Dictionary<string, bool> intents = [];   // true = Muted。無い対象は、まだ最初の列挙をしていない
    private readonly HashSet<string> writeFailed = [];
    private HashSet<(string, string)> seen = [];               // 前回の列挙にあったセッション
    private IReadOnlyList<SessionInfo> sessions = [];

    /// <summary>同期ループの1回ぶん。列挙して規則を当てはめる。</summary>
    public Snapshot Sync(IReadOnlyList<string> targets) => Apply(targets, audio.ListSessions());

    public Snapshot Toggle(IReadOnlyList<string> targets, string exe) =>
        Set(targets, [exe], !State(exe).IsOn);

    /// <summary>まとめトグル(FR-7)。全対象がミュート中かミュート予約中なら解除し、そうでなければミュートする。</summary>
    public Snapshot ToggleAll(IReadOnlyList<string> targets) =>
        Set(targets, targets, !targets.All(exe => State(exe).IsOn));

    private Snapshot Set(IReadOnlyList<string> targets, IReadOnlyList<string> exes, bool mute)
    {
        var chosen = exes.Where(targets.Contains).ToHashSet();
        foreach (var exe in chosen)
        {
            intents[exe] = mute;
            writeFailed.Remove(exe);
        }
        var result = audio.SetMute(s => chosen.Any(exe => Matches(s, exe)), mute);
        NoteFailures(result);
        return Apply(targets, result.Sessions);
    }

    private Snapshot Apply(IReadOnlyList<string> targets, IReadOnlyList<SessionInfo> list)
    {
        foreach (var gone in intents.Keys.Where(exe => !targets.Contains(exe)).ToList())
        {
            intents.Remove(gone);
            writeFailed.Remove(gone);
        }

        // 規則 3: Intent が Muted の対象に、前回の列挙に無かったセッションが現れたら、そのセッションだけをミュートする
        var fresh = list
            .Where(s => !s.Muted && !seen.Contains(s.Key)
                && targets.Any(exe => Matches(s, exe) && intents.GetValueOrDefault(exe)))
            .Select(s => s.Key).ToHashSet();
        // 下の書き込みの途中で現れたセッションは「見た」ことにしない(次の同期で規則 3 にかける)
        seen = list.Select(s => s.Key).ToHashSet();
        if (fresh.Count > 0)
        {
            var result = audio.SetMute(s => fresh.Contains(s.Key), true);
            NoteFailures(result);
            list = result.Sessions;
        }

        foreach (var exe in targets)
        {
            var own = list.Where(s => Matches(s, exe)).ToList();
            if (!intents.TryGetValue(exe, out bool muted))
                intents[exe] = own.Count > 0 && own.All(s => s.Muted);   // 規則 1(あとから追加した対象は、追加後の最初の列挙)
            else if (muted && own.Count > 0 && !own.Any(s => s.Muted))
                intents[exe] = false;                                    // 規則 2: Windows 側で全部解除された
        }

        sessions = list;
        var apps = list.Where(s => !s.IsSystemSounds && s.Exe != null).Select(s => s.Exe!).Distinct().Order().ToList();
        return new Snapshot(targets.Select(State).ToList(), apps);
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
        foreach (var session in result.Failed)
            if (session.Exe != null) writeFailed.Add(session.Exe);
    }
}
