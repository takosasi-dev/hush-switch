// 自動テスト(製品には含めない)。偽の Core Audio を差し込んで §9.4 の状態規則を確かめ(AC-19)、
// 設定ファイルの読み書きと対象の追加・削除の検証を確かめる。失敗した件数を終了コードで返す。
using System.Text;
using System.Text.Json;
using HushSwitch;

Console.OutputEncoding = Encoding.UTF8;
int failures = 0;
const string A = "a.exe", B = "b.exe";

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "NG  ")}{what}");
    if (!ok) failures++;
}

Display Show(Snapshot snapshot, string exe) => snapshot.Targets.Single(t => t.Exe == exe).Display;

// ---- §9.4 表示状態(5 つ)と、Intent の更新 1・2 ----
{
    var audio = new FakeAudio();
    var controller = new MuteController(audio);
    audio.Add("a1", A, muted: true);
    audio.Add("a2", A, muted: true, device: "dev2");
    Check(Show(controller.Sync([A]), A) == Display.Muted, "表示: 全部ミュート → ミュート中");
    audio.Mute("a2", false);
    Check(Show(controller.Sync([A]), A) == Display.Partial, "表示: 一部だけミュート → 一部ミュート");
    Check(audio.Written.Count == 0, "補足: Windows 側で個別に解除されたセッションは再ミュートしない");
    audio.Sessions.Clear();
    Check(Show(controller.Sync([A]), A) == Display.Reserved, "規則 1: 最初の列挙で全部ミュートなら Intent は Muted(セッションが消えると ミュート予約中)");

    audio.Add("a3", A, muted: true);
    controller.Sync([A]);
    audio.Mute("a3", false);
    Check(Show(controller.Sync([A]), A) == Display.Unmuted, "表示: ミュート 0 件 → ミュートなし");
    audio.Sessions.Clear();
    Check(Show(controller.Sync([A]), A) == Display.Silent, "規則 2: Windows 側で全部解除されたら Intent は Unmuted(セッションが消えると 音を出していません)");
    audio.Add("a4", A, muted: false);
    Check(Show(controller.Sync([A]), A) == Display.Unmuted && audio.Written.Count == 0, "規則 2 のあと、新しいセッションは自動ではミュートしない(AC-8)");
}
{
    var audio = new FakeAudio();
    var controller = new MuteController(audio);
    audio.Add("a1", A, muted: true);
    audio.Add("a2", A, muted: false);
    controller.Sync([A]);
    audio.Sessions.Clear();
    Check(Show(controller.Sync([A]), A) == Display.Silent, "規則 1: 最初の列挙で一部ミュートなら Intent は Unmuted");
}

// ---- トグル ----
{
    var audio = new FakeAudio();
    var controller = new MuteController(audio);
    audio.Add("a1", A, muted: false);
    audio.Add("a2", A, muted: false, device: "dev2");
    audio.Add("b1", B, muted: false);
    audio.Add("sys", null, muted: false, system: true);
    audio.Add("gone", null, muted: false);
    controller.Sync([A]);

    Check(Show(controller.Toggle([A], A), A) == Display.Muted, "トグル: ミュートなし → 全デバイスの全セッションをミュート → ミュート中");
    Check(audio.Written.Order().SequenceEqual(["a1", "a2"]), "INV-10: 書いたのは対象のセッションだけ(登録外・システム音・解決できないセッションには書かない)");
    Check(Show(controller.Toggle([A], A), A) == Display.Unmuted, "トグル: ミュート中 → 解除 → ミュートなし");
    controller.Toggle([A], A);
    audio.Mute("a1", false);
    controller.Sync([A]);
    Check(Show(controller.Toggle([A], A), A) == Display.Muted, "トグル: 一部ミュート → ミュート → ミュート中");

    audio.Written.Clear();
    controller.Toggle([A], B);
    Check(audio.Written.Count == 0 && !audio.IsMuted("b1"), "INV-10: 登録していない exe のトグルは何も書かない");
    controller.Toggle([A], A);
    controller.Toggle([A], A);
    Check(audio.IsMuted("a1") && audio.IsMuted("a2"), "連打: 2 回押すと元の状態に戻る(偶奇で決まる)");
}

// ---- ミュート予約と、Intent の更新 3・4 ----
{
    var audio = new FakeAudio();
    var controller = new MuteController(audio);
    Check(Show(controller.Sync([A, B]), A) == Display.Silent, "表示: セッション 0 件で Intent が Unmuted → 音を出していません");
    Check(Show(controller.Toggle([A, B], A), A) == Display.Reserved, "トグル: 音を出していません → ミュート予約中");
    audio.Add("a1", A, muted: false);
    audio.Add("b1", B, muted: false);
    var after = controller.Sync([A, B]);
    Check(Show(after, A) == Display.Muted && audio.IsMuted("a1"), "規則 3: Intent が Muted の対象に現れた新しいセッションをミュートする");
    Check(Show(after, B) == Display.Unmuted && audio.Written.SequenceEqual(["a1"]), "規則 4: Intent が Unmuted の対象の新しいセッションには書かない");

    audio.Mute("a1", false);
    audio.Add("a2", A, muted: false);
    audio.Written.Clear();
    Check(Show(controller.Sync([A, B]), A) == Display.Partial && audio.Written.SequenceEqual(["a2"]),
        "規則 3: 新しいセッションだけをミュートし、前からあって解除されたセッションは再ミュートしない");

    audio.Mute("b1", true);
    controller.Sync([A, B]);
    audio.Sessions.RemoveAll(s => s.Exe == B);
    Check(Show(controller.Sync([A, B]), B) == Display.Silent, "規則 4: Windows 側でミュートされても Intent は変えない");

    audio.Sessions.Clear();
    controller.Sync([A, B]);
    Check(Show(controller.Toggle([A, B], A), A) == Display.Silent, "トグル: ミュート予約中 → 音を出していません");
    audio.Add("a3", A, muted: false);
    Check(Show(controller.Sync([A, B]), A) == Display.Unmuted, "予約を解除したあとの新しいセッションはミュートしない");
}

// ---- 対象の追加と削除で Intent を持ち越さない ----
{
    var audio = new FakeAudio();
    var controller = new MuteController(audio);
    controller.Sync([A]);
    audio.Add("b1", B, muted: true);
    controller.Sync([A, B]);
    audio.Sessions.Clear();
    Check(Show(controller.Sync([A, B]), B) == Display.Reserved, "あとから追加した対象は、追加後の最初の列挙で Intent を決める");
    controller.Sync([A]);
    Check(Show(controller.Sync([A, B]), B) == Display.Silent, "削除した対象の Intent は残さない");
}

// ---- INV-8: 表示は書き込みの結果ではなく、読み直した状態から作る ----
{
    var audio = new FakeAudio();
    var controller = new MuteController(audio);
    audio.Add("a1", A, muted: false);
    audio.Add("a2", A, muted: false);
    controller.Sync([A]);
    audio.Ignore.Add("a1");
    Check(Show(controller.Toggle([A], A), A) == Display.Partial, "INV-8: 書けたと返ってきても、読み直して変わっていなければ ミュート中 にしない");

    audio.Ignore.Clear();
    controller.Toggle([A], A);
    controller.Toggle([A], A);
    Check(!audio.IsMuted("a1") && !audio.IsMuted("a2"), "(準備)全部解除に戻す");
    audio.Refuse.Add("a2");
    var state = controller.Toggle([A], A).Targets.Single();
    Check(state.Display == Display.Partial && state.WriteFailed, "書き込みを断られたセッションがあれば 一部ミュート のまま、行に知らせる(§10)");
    audio.Refuse.Clear();
    state = controller.Toggle([A], A).Targets.Single();
    Check(state.Display == Display.Muted && !state.WriteFailed, "次のトグルが通れば、知らせを消す");
}

// ---- まとめトグル(FR-7)と削除の可否(FR-4)----
{
    var audio = new FakeAudio();
    var controller = new MuteController(audio);
    audio.Add("a1", A, muted: true);
    audio.Add("x1", "x.exe", muted: false);
    controller.Sync([A, B]);
    var all = controller.ToggleAll([A, B]);
    Check(all.Targets.All(t => t.IsOn) && Show(all, B) == Display.Reserved && !audio.IsMuted("x1"),
        "まとめ: ミュート中でも予約中でもない対象が 1 つでもあれば、全対象をミュートする(登録外には書かない)");
    all = controller.ToggleAll([A, B]);
    Check(Show(all, A) == Display.Unmuted && Show(all, B) == Display.Silent, "まとめ: 全対象がミュート中か予約中なら、全対象を解除する");

    Check(new[] { Display.Muted, Display.Partial, Display.Reserved }.All(d => !new TargetState(A, d, false).CanRemove)
        && new[] { Display.Unmuted, Display.Silent }.All(d => new TargetState(A, d, false).CanRemove),
        "削除: ミュート中・一部ミュート・ミュート予約中は削除できない");
    Check(all.Apps.SequenceEqual([A, "x.exe"]), "追加ダイアログの一覧: 今セッションを持つ exe 名");
}

// ---- カテゴリーごとのまとめトグル ----
{
    const string C = "c.exe";
    var audio = new FakeAudio();
    var controller = new MuteController(audio);
    audio.Add("a1", A, muted: false);
    audio.Add("c1", C, muted: false);
    audio.Add("x1", "x.exe", muted: false);
    controller.Sync([A, B, C]);
    controller.Toggle([A, B, C], B);   // B はミュート予約中(カテゴリーの外)
    var some = controller.ToggleSome([A, B, C], [A]);
    Check(Show(some, A) == Display.Muted && !audio.IsMuted("c1") && !audio.IsMuted("x1"),
        "カテゴリー: 中の対象だけをミュートし、外の対象と登録外には書かない(INV-10)");
    Check(Show(some, B) == Display.Reserved, "カテゴリー: 外の対象の予約(意図)は残る");
    audio.Add("b1", B, muted: false);
    Check(Show(controller.Sync([A, B, C]), B) == Display.Muted, "カテゴリー: 外の対象の予約は生きていて、鳴り始めたらミュートする");
    some = controller.ToggleSome([A, B, C], [A]);
    Check(Show(some, A) == Display.Unmuted && Show(some, B) == Display.Muted, "カテゴリー: 中が全部ミュート中なら、中だけを解除する");
}

// ---- 終了時の確認が要る状態と、終了時の解除(FR-13, FR-17)----
{
    Check(new[] { Display.Muted, Display.Partial, Display.Reserved }.All(d => new TargetState(A, d, false).HasMute)
        && new[] { Display.Unmuted, Display.Silent }.All(d => !new TargetState(A, d, false).HasMute),
        "ミュートあり: ミュート中・一部ミュート・ミュート予約中だけ(終了時の確認と、トレイの絵)");

    var audio = new FakeAudio();
    var controller = new MuteController(audio);
    audio.Add("a1", A, muted: true);
    audio.Add("a2", A, muted: false);
    audio.Add("c1", "c.exe", muted: true);
    controller.Sync([A, B]);
    controller.Toggle([A, B], B);   // B はミュート予約中
    var after = controller.UnmuteAll([A, B]);
    Check(Show(after, A) == Display.Unmuted && Show(after, B) == Display.Silent && audio.IsMuted("c1"),
        "終了時の解除: 全対象を解除し、予約も取り消す。登録していないアプリには書かない(INV-10)");
    audio.Add("b1", B, muted: false);
    Check(Show(controller.Sync([A, B]), B) == Display.Unmuted, "終了時の解除のあと、新しいセッションはミュートしない");
}

// ---- 設定ファイル(FR-2, FR-3, FR-11)----
string dir = Path.Combine(Path.GetTempPath(), "hushswitch-tests-" + Guid.NewGuid().ToString("N"));
string path = Path.Combine(dir, "settings.json");
try
{
    var store = new TargetStore(path);
    Check(store.Targets.Count == 0 && store.Notices.Count == 0, "設定: ファイルが無ければ既定値で、知らせは出さない");
    Check(store.Add("Discord.EXE") == null && store.Targets.SequenceEqual(["discord.exe"]), "追加: 小文字にして登録する");
    Check(store.Add("DISCORD.exe") == "すでに登録されています", "追加: 大文字小文字違いの重複を拒否する");
    Check(store.Add(new string('a', 125) + ".exe") == "ファイル名が長すぎます", "追加: 128 文字を超える名前を拒否する");
    Check(store.Add("readme.txt") != null && store.Add("a:b.exe") != null, "追加: exe ファイル名でないものを拒否する");
    for (int i = 1; i < TargetStore.MaxTargets; i++) store.Add($"app{i:00}.exe");
    Check(store.Targets.Count == 20 && store.Add("one-more.exe") == "登録できるのは 20 件までです", "追加: 21 件目を拒否する");
    store.Remove("app01.exe");
    Check(store.Targets.Count == 19 && !store.SaveFailed, "削除: 対象から外して保存する");

    using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
    {
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name);
        Check(keys.SequenceEqual(["schema", "targets", "close_to_tray", "always_on_top", "on_exit", "theme", "start_in_tray", "hotkey", "window",
            "design", "accent", "text_size", "opacity", "show_icons", "show_status", "show_summary", "show_remove", "categories", "category_of"]),
            "INV-9: 設定ファイルにあるのは §9.2 のキーと画面の設定だけ(ミュートの状態は書かない)");
    }
    Check(new TargetStore(path).Targets.SequenceEqual(store.Targets), "設定: 再起動後も対象が同じ順で残る");

    // カテゴリー
    Check(store.Categories.Select(c => c.Name).SequenceEqual(TargetStore.DefaultCategories), "カテゴリー: 初めは既定の 4 つ");
    Check(store.AddCategory("その他") != null && store.AddCategory(" ") != null && store.AddCategory("ゲーム") != null
            && store.AddCategory(new string('あ', 21)) != null, "カテゴリー: 「その他」・空・重複・長すぎる名前を拒否する");
    Check(store.AddCategory(" 配信 ") == null && store.Categories[^1].Name == "配信", "カテゴリー: 前後の空白を除いて足す");
    store.Assign("discord.exe", "配信");
    store.Assign("app02.exe", "ゲーム");
    store.Assign("app03.exe", "無いカテゴリー");
    Check(store.CategoryOf("discord.exe") == "配信" && store.CategoryOf("app03.exe") == null, "カテゴリー: 振り分ける(無いカテゴリーには入れない)");
    Check(store.RenameCategory("配信", "通話2") == null && store.CategoryOf("discord.exe") == "通話2", "カテゴリー: 名前を変えると振り分けも付いていく");
    Check(store.RenameCategory("通話2", "ゲーム") != null, "カテゴリー: ほかと同じ名前には変えられない");
    store.Categories.First(c => c.Name == "ゲーム").Hotkey = "Ctrl+Alt+G";
    store.Hotkey = "Ctrl+Alt+M";
    Check(store.HotkeyUsedBy("ctrl+alt+g", "通話2") == "ゲーム" && store.HotkeyUsedBy("Ctrl+Alt+G", "ゲーム") == null
            && store.HotkeyUsedBy("Ctrl+Alt+M", "ゲーム") != null && store.HotkeyUsedBy("Ctrl+Alt+M", "") == null,
        "カテゴリー: ホットキーの重なりを見つける(自分は除く)");
    store.Save();
    var reloaded = new TargetStore(path);
    Check(reloaded.CategoryOf("discord.exe") == "通話2" && reloaded.Categories.First(c => c.Name == "ゲーム").Hotkey == "Ctrl+Alt+G",
        "カテゴリー: 再起動後も振り分けとホットキーが残る");
    Check(File.ReadAllText(path).Contains("\"ゲーム\""), "カテゴリー: 名前は読める形で保存する");
    store.RemoveCategory("通話2");
    store.Remove("app02.exe");
    reloaded = new TargetStore(path);
    Check(reloaded.CategoryOf("discord.exe") == null && reloaded.CategoryOf("app02.exe") == null && reloaded.Categories.All(c => c.Name != "通話2"),
        "カテゴリー: 消したカテゴリーのアプリは「その他」に戻り、外したアプリの振り分けも消える");
    File.WriteAllText(path, """{"schema":1,"targets":["a.exe"],"categories":[],"category_of":{"a.exe":"ゲーム","b.exe":"ゲーム"}}""");
    Check(new TargetStore(path).Categories.Count == 0 && new TargetStore(path).CategoryOf("a.exe") == null,
        "カテゴリー: 空の配列なら空のまま(既定に戻さない)。無いカテゴリーを指す振り分けは捨てる");
    File.WriteAllText(path, """{"schema":1,"targets":["a.exe"],"categories":[{"name":"ゲーム","collapsed":true},{"name":"その他"},{"name":"ゲーム"},5],"category_of":{"a.exe":"ゲーム","b.exe":"ゲーム"}}""");
    reloaded = new TargetStore(path);
    Check(reloaded.Categories.Count == 1 && reloaded.Categories[0].Collapsed && reloaded.CategoryOf("a.exe") == "ゲーム" && reloaded.CategoryOf("b.exe") == null,
        "カテゴリー: 使えない要素は読み飛ばし、登録していないアプリの振り分けは捨てる");
    store = new TargetStore(path);

    File.WriteAllText(path, """{"schema":1,"targets":["ok.exe","Upper.exe","no-ext","a/b.exe",5,"ok.exe"],"close_to_tray":false,"always_on_top":true,"on_exit":"leave","unknown":1}""");
    store = new TargetStore(path);
    Check(store.Targets.SequenceEqual(["ok.exe"]) && store.Notices.Count == 1, "設定: 検証に通らない要素は読み飛ばして知らせる");
    store.Add("b.exe");
    store = new TargetStore(path);
    Check(!store.CloseToTray && store.AlwaysOnTop && store.OnExit == "leave" && store.Targets.Count == 2, "設定: 保存してもほかの設定を失わない");

    File.WriteAllText(path, """{"schema":1,"targets":[],"on_exit":"later"}""");
    Check(new TargetStore(path).OnExit == "ask", "設定: on_exit が知らない値なら ask に戻す");

    store = new TargetStore(path);
    store.AlwaysOnTop = true;
    store.OnExit = "unmute";
    store.Theme = "dark";
    store.StartInTray = true;
    store.Hotkey = "Ctrl+Alt+M";
    store.Window = [10, 20, 400, 500];
    (store.Design, store.Accent, store.TextSize, store.Opacity, store.ShowRemove) = ("tiles", "pink", "xlarge", 60, false);
    store.Save();
    store = new TargetStore(path);
    Check(store.AlwaysOnTop && store.OnExit == "unmute" && store.CloseToTray, "設定: 手前に表示と on_exit の変更が保存される(FR-13, FR-18)");
    Check(store.Theme == "dark" && store.StartInTray && store.Hotkey == "Ctrl+Alt+M" && store.Window!.SequenceEqual([10, 20, 400, 500.0]),
        "設定: 配色・起動時の格納・ホットキー・ウィンドウの位置が保存される");
    Check(store.Design == "tiles" && store.Accent == "pink" && store.TextSize == "xlarge" && store.Opacity == 60
            && !store.ShowRemove && store.ShowIcons && store.ShowStatus && store.ShowSummary,
        "設定: デザイン・アクセント・文字の大きさ・透明度・表示の切り替えが保存される");

    File.WriteAllText(path, """{"schema":1,"targets":[],"theme":"pink","start_in_tray":"yes","hotkey":5,"window":[1,2,-3,4],"design":"neon","accent":"#123456","text_size":3,"opacity":10,"show_icons":"no"}""");
    store = new TargetStore(path);
    Check(store.Theme == "system" && !store.StartInTray && store.Hotkey == "" && store.Window == null,
        "設定: 画面の設定が知らない値なら既定値に戻す");
    Check(store.Design == "standard" && store.Accent == "system" && store.TextSize == "medium" && store.Opacity == 100 && store.ShowIcons,
        "設定: 見た目の設定が知らない値なら既定値に戻す(透明度は 30% 未満を認めない)");

    Check(ExePaths.DevicePath(@"{0.0.0.00000000}.{9af1e3c8}|\Device\HarddiskVolume3\Apps\Some App\app.exe%b{00000000-0000-0000-0000-000000000000}|1%b8488")
            == @"\Device\HarddiskVolume3\Apps\Some App\app.exe"
        && ExePaths.DevicePath("{0.0.0.00000000}.{4f1ec70d}|#%b{A9EF3FD9-4240-455E-A4D5-F2B3301887B2}|1%b#") == null
        && ExePaths.DevicePath("") == null,
        "アイコン: セッションの識別子から exe の場所を取り出す(システム音には無い)");
    Check(ExePaths.FromSession(@"x|\Device\Nowhere9\a.exe%b{0}|1%b1") == null, "アイコン: ドライブが分からない場所は null");

    foreach (string broken in new[] { """{"schema":1,"targets":["a.e""", """{"schema":2,"targets":["a.exe"]}""" })
    {
        foreach (string old in Directory.GetFiles(dir, "settings.json.bad-*")) File.Delete(old);
        File.WriteAllText(path, broken);
        store = new TargetStore(path);
        Check(store.Targets.Count == 0 && store.Notices.Count == 1 && store.Notices[0].Contains("settings.json.bad-")
            && !File.Exists(path) && Directory.GetFiles(dir, "settings.json.bad-*").Length == 1,
            $"設定: {(broken.Contains("2") ? "schema が 1 でない" : "途中で切れた")}ファイルは退避して、既定値で起動する(AC-13)");
    }

    string blocked = Path.Combine(dir, "file-not-folder");
    File.WriteAllText(blocked, "");
    store = new TargetStore(Path.Combine(blocked, "settings.json"));
    Check(store.Add("a.exe") == null && store.SaveFailed && store.Targets.Count == 1, "設定: 書けなくても、メモリ上の設定で動作を続ける");
}
finally
{
    Directory.Delete(dir, recursive: true);
}

// ---- ログ(FR-15, §9.3, AC-18)----
string logDir = Path.Combine(Path.GetTempPath(), "hushswitch-tests-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(logDir);
    string Old(int days)
    {
        string file = Path.Combine(logDir, $"hushswitch-200001{days:00}-01.jsonl");
        File.WriteAllText(file, "{}\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-days));
        return file;
    }
    string old15 = Old(15), old13 = Old(13);
    var log = new EventLog(logDir);
    Check(!File.Exists(old15) && File.Exists(old13), "ログ: 更新から 15 日前のファイルは起動時に消し、13 日前のファイルは残す");
    File.Delete(old13);

    List<JsonElement> Lines() => Directory.GetFiles(logDir).Order().SelectMany(File.ReadAllLines)
        .Select(line => JsonDocument.Parse(line).RootElement).ToList();
    string? Text(JsonElement line, string key) => line.GetProperty(key).GetString();

    var audio = new FakeAudio();
    var controller = new MuteController(audio, log);
    audio.Add("a1", A, muted: false);
    audio.Add("a2", A, muted: false, device: "dev2");
    audio.Add("b1", B, muted: false);
    controller.Sync([A, B]);
    Check(Lines().Count == 0, "ログ: 変化の無い同期は何も書かない");

    controller.Toggle([A, B], A);
    var line = Lines().Single();
    Check(line.EnumerateObject().Select(p => p.Name).SequenceEqual(["ts", "event", "source", "exe", "action", "sessions", "latency_ms", "result", "reason"]),
        "ログ: 1 行が §9.3 のキーを持つ JSON");
    Check(DateTimeOffset.TryParse(Text(line, "ts"), out _) && Text(line, "event") == "toggle" && Text(line, "source") == "ui"
        && Text(line, "exe") == A && Text(line, "action") == "mute" && line.GetProperty("sessions").GetInt32() == 2
        && line.GetProperty("latency_ms").GetInt64() is >= 0 and < 1000 && Text(line, "result") == "ok" && Text(line, "reason") == null,
        "ログ: 行のトグルは toggle / ui / mute / セッション数 / ok");

    audio.Refuse.Add("a1");
    controller.Toggle([A, B], A);
    line = Lines()[^1];
    Check(Text(line, "action") == "unmute" && Text(line, "result") == "partial" && Text(line, "reason") == "set_mute 0x80070005",
        "ログ: 一部のセッションに書けなければ partial と理由");
    audio.Refuse.Add("a2");
    controller.Toggle([A, B], A);
    Check(Text(Lines()[^1], "result") == "failed", "ログ: 全部のセッションに書けなければ failed");
    audio.Refuse.Clear();

    audio.DeviceErrors.Add("device 0x88890004");
    controller.ToggleAll([A, B]);
    var group = Lines().TakeLast(2).ToList();
    Check(group.Select(l => Text(l, "exe")).SequenceEqual([A, B]) && group.All(l => Text(l, "source") == "group"
        && Text(l, "result") == "partial" && Text(l, "reason") == "device 0x88890004"),
        "ログ: まとめトグルは対象ごとに 1 行(group)。途中でデバイスが抜かれたら partial と理由(§10)");
    audio.DeviceErrors.Clear();

    // ここまでで A・B とも Intent は Muted。新しいセッションを、通知の経路と同期の経路で見つける
    int before = Lines().Count;
    audio.Add("a3", A, muted: false);
    controller.Sync([A, B], System.Diagnostics.Stopwatch.GetTimestamp());
    line = Lines()[^1];
    Check(Lines().Count == before + 1 && Text(line, "event") == "session_created" && Text(line, "source") == "notify"
        && Text(line, "exe") == A && line.GetProperty("sessions").GetInt32() == 1 && Text(line, "result") == "ok" && audio.IsMuted("a3"),
        "ログ: 通知で見つけた新しいセッションのミュートは session_created / notify");
    audio.Add("b2", B, muted: true);
    audio.Written.Clear();
    controller.Sync([A, B]);
    line = Lines()[^1];
    Check(Text(line, "source") == "sync" && Text(line, "exe") == B && Text(line, "reason") == "already_muted" && audio.Written.Count == 0,
        "ログ: Windows がミュートを戻していた新しいセッションには書かず、already_muted と残す");

    audio.Mute("a3", false);
    audio.Mute("b1", false);
    audio.Mute("b2", false);
    controller.Sync([A, B]);
    var external = Lines().TakeLast(2).ToList();
    Check(external.All(l => Text(l, "event") == "external_change" && Text(l, "action") == "unmute")
        && external[0].GetProperty("sessions").GetInt32() == 1 && external[1].GetProperty("sessions").GetInt32() == 2,
        "ログ: Windows 側での変更は external_change(FR-9)");

    controller.ToggleAll([A, B], source: "tray");
    Check(Lines().TakeLast(2).All(l => Text(l, "event") == "toggle" && Text(l, "source") == "tray" && Text(l, "action") == "mute"),
        "ログ: トレイのまとめトグルは toggle / tray");
    controller.UnmuteAll([A, B]);
    Check(Lines().TakeLast(2).All(l => Text(l, "event") == "toggle" && Text(l, "source") == "exit" && Text(l, "action") == "unmute")
        && audio.Sessions.All(s => !s.Muted),
        "ログ: 終了時の解除は toggle / exit / unmute");
    log.Write("exit", "tray", action: "unmute");
    Check(Text(Lines()[^1], "event") == "exit" && Lines()[^1].EnumerateObject().Count() == 9, "ログ: 終了の行も §9.3 の 9 キー");

    string today = Path.Combine(logDir, $"hushswitch-{DateTime.Now:yyyyMMdd}-");
    File.WriteAllBytes(today + "01.jsonl", new byte[EventLog.MaxBytes + 1]);
    log.Write("error", reason: "test");
    Check(File.ReadAllText(today + "02.jsonl").Contains("\"reason\":\"test\""), "ログ: 5 MB を超えたら新しいファイルに切り替える");
}
finally
{
    Directory.Delete(logDir, recursive: true);
}

// ---- 専用スレッドの失敗の数えかた(§10: 続けて 10 回失敗したらエラー表示)----
{
    var counts = new List<int>();
    Exception? last = null;
    int calls = 0;
    using var done = new ManualResetEventSlim();
    using var thread = new AudioThread(
        () =>
        {
            calls++;
            if (calls is <= 3 or 5) throw new AudioUnavailableException("test");
            if (calls == 6) done.Set();
        },
        (e, count) => { last = e; counts.Add(count); }, intervalMs: 5);
    Check(done.Wait(5000) && counts.SequenceEqual([1, 2, 3, 1]) && last is AudioUnavailableException,
        "専用スレッド: 例外になっても次の周期で再開し、続けて失敗した回数を知らせる(成功で 0 に戻る)");
}

Console.WriteLine(failures == 0 ? "# すべて通過" : $"# 失敗 {failures} 件");
return failures;

/// <summary>偽の Core Audio。セッションの出入りと、書き込みの失敗をテストから操る。</summary>
sealed class FakeAudio : IAudioSessions
{
    public List<SessionInfo> Sessions { get; } = [];
    public HashSet<string> Refuse { get; } = [];   // 書き込みを断る(失敗として返す)セッション
    public HashSet<string> Ignore { get; } = [];   // 書けたと返すが、実際には変わらないセッション
    public List<string> Written { get; } = [];     // 書き込みを試みたセッション

    public void Add(string id, string? exe, bool muted, string device = "dev1", bool system = false) =>
        Sessions.Add(new SessionInfo(device, device, id, 1, exe, "Active", muted, system));

    public void Mute(string id, bool muted)
    {
        int i = Sessions.FindIndex(s => s.InstanceId == id);
        Sessions[i] = Sessions[i] with { Muted = muted };
    }

    public bool IsMuted(string id) => Sessions.Single(s => s.InstanceId == id).Muted;

    public IReadOnlyList<SessionInfo> ListSessions() => [.. Sessions];

    public List<string> DeviceErrors { get; } = []; // 書き込みの途中で読めなくなったデバイス

    public MuteResult SetMute(Func<SessionInfo, bool> target, bool mute)
    {
        var failed = new List<WriteFailure>();
        for (int i = 0; i < Sessions.Count; i++)
        {
            var session = Sessions[i];
            if (!target(session)) continue;
            Written.Add(session.InstanceId);
            if (Refuse.Contains(session.InstanceId)) failed.Add(new WriteFailure(session, unchecked((int)0x80070005)));
            else if (!Ignore.Contains(session.InstanceId)) Sessions[i] = session with { Muted = mute };
        }
        return new MuteResult([.. Sessions], failed, [.. DeviceErrors]);
    }
}
