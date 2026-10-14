// メインウィンドウの動き。ボタンの押下を Core Audio 専用スレッドへ渡し、
// 読み直した状態(Snapshot)を受け取って一覧に映す。UI スレッドからは Core Audio を呼ばない(INV-7)。
// × でのトレイへの格納(FR-16)、常に手前に表示(FR-18)、終了時の確認(FR-13)、設定の反映もここで扱う。
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace HushSwitch;

public partial class MainWindow : Window
{
    private readonly TargetStore store = new(Environment.ExpandEnvironmentVariables(@"%APPDATA%\HushSwitch\settings.json"));
    private readonly EventLog log = new(Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\HushSwitch\logs"));
    private readonly MuteController controller;
    private readonly AudioThread audio;
    private readonly ObservableCollection<TargetRow> rows = [];
    private Snapshot? latest;      // null のあいだは読み込み中
    private AddDialog? addDialog;
    private bool connected = true; // Core Audio に接続できているか(FR-14)
    private readonly TrayIcon tray;
    private readonly AppIcons icons = new(Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\HushSwitch\icons"));
    private readonly GlobalHotKey hotkey;
    private readonly ListCollectionView view;
    private bool otherCollapsed;   // 「その他」を畳んでいるか(保存しない)
    private const int AllHotkey = 1, CategoryHotkeys = 100;   // ホットキーの番号。カテゴリーは 100 + 並び順
    private bool exitRequested;    // 終了の確認中か、終了の処理中
    private bool exiting;          // 本当に閉じる(× でのトレイへの格納をしない)

    /// <summary>ウィンドウを出さずに、トレイに格納した状態で始める設定か。</summary>
    public bool StartInTray => store.StartInTray;

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        List.ItemsSource = rows;
        // カテゴリーの順、その中は登録順。見出しを出すかどうかは Regroup が決める
        view = (ListCollectionView)CollectionViewSource.GetDefaultView(rows);
        view.SortDescriptions.Add(new SortDescription(nameof(TargetRow.CategoryOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(TargetRow.Order), ListSortDirection.Ascending));
        Look.Apply(this, List, Root, store);
        Topmost = store.AlwaysOnTop;
        SourceInitialized += (_, _) => ApplyOpacity();
        hotkey = new GlobalHotKey(OnHotkey);
        foreach (string key in RegisterHotkeys())
            store.Notices.Add($"ホットキー({key})を登録できませんでした。設定で別のキーを選んでください");
        ShowNotice(string.Join("\n", store.Notices));
        tray = new TrayIcon(BringToFront, () => ToggleAll("tray"), name => ToggleCategory(name, "tray"), OpenSettings, () => RequestExit("tray"));
        var service = new AudioSessionService(log);
        controller = new MuteController(service, log);
        // 新しいセッションの通知(D-9)。同期ループを待たずに、専用スレッドで列挙し直す
        service.SessionCreated += () =>
        {
            long at = Stopwatch.GetTimestamp();
            audio?.Post(() => Publish(controller.Sync(store.Targets, at)));
        };
        audio = new AudioThread(() => Publish(controller.Sync(store.Targets)), OnAudioFailed);
        Closed += (_, _) =>
        {
            audio.Dispose();
            tray.Dispose();
            hotkey.Dispose();
        };
    }

    // × はトレイへの格納(FR-16)。close_to_tray が false なら、× を終了として扱う
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        SavePlacement();
        if (exiting) return;
        e.Cancel = true;
        if (store.CloseToTray) Hide();
        // 閉じる処理の途中では、確認を出すことも、もう一度閉じることもできないので、抜けてから終了の流れに入る
        else Dispatcher.InvokeAsync(() => RequestExit("ui"));
    }

    // 前に閉じたときの位置と大きさに戻す。その場所がもう画面に無ければ(モニターを外した等)、何もしない
    private void RestorePlacement()
    {
        if (store.Window is not [var left, var top, var width, var height]) return;
        double screenLeft = SystemParameters.VirtualScreenLeft, screenTop = SystemParameters.VirtualScreenTop;
        if (left + width < screenLeft + 80 || left > screenLeft + SystemParameters.VirtualScreenWidth - 80
            || top < screenTop || top > screenTop + SystemParameters.VirtualScreenHeight - 80) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (left, top, Math.Max(width, MinWidth), Math.Max(height, MinHeight));
    }

    private void SavePlacement()
    {
        var box = RestoreBounds;   // 最大化・最小化していても、元の位置と大きさが入っている
        if (box.IsEmpty) return;   // 一度も表示していない
        double[] now = [box.Left, box.Top, box.Width, box.Height];
        if (store.Window != null && store.Window.SequenceEqual(now)) return;
        store.Window = now;
        store.Save();
    }

    // 終了(FR-13)。ミュートのある対象が残っていれば、on_exit に従う
    private void RequestExit(string source)
    {
        if (exitRequested) return;
        exitRequested = true;
        string choice = "leave";
        if (rows.Any(r => r.State.HasMute))
        {
            choice = store.OnExit;
            if (choice == "ask")
            {
                var dialog = new ExitDialog();
                if (IsVisible) dialog.Owner = this;
                else
                {
                    // トレイに格納中は親にできるウィンドウが無い
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    dialog.ShowInTaskbar = true;
                    dialog.Topmost = true;
                }
                if (dialog.ShowDialog() != true)
                {
                    exitRequested = false;
                    return;
                }
                choice = dialog.Unmute ? "unmute" : "leave";
                if (dialog.Remember)
                {
                    store.OnExit = choice;
                    store.Save();
                }
            }
        }

        void Finish(string? action)
        {
            log.Write("exit", source, action: action);
            exiting = true;
            Close();
        }
        if (choice != "unmute")
        {
            Finish(null);
            return;
        }
        // 解除は Core Audio 専用スレッドで行い、済んでから閉じる。解除に失敗しても終了はする
        audio.Post(() =>
        {
            try { controller.UnmuteAll(store.Targets); }
            finally { Dispatcher.InvokeAsync(() => Finish("unmute")); }
        });
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    // Core Audio 専用スレッドから呼ばれる
    private void Publish(Snapshot snapshot) => Dispatcher.InvokeAsync(() => Render(snapshot));

    // Core Audio 専用スレッドから呼ばれる。接続できないと分かったとき、または続けて 10 回失敗したときにエラー帯を出す(§10)
    private void OnAudioFailed(Exception e, int count)
    {
        bool unavailable = e is AudioUnavailableException;
        if (count == 1 || count == 10)
            log.Write("error", reason: unavailable ? $"audio_unavailable {e.Message}" : $"{e.GetType().Name} 0x{e.HResult:X8}");
        if (unavailable || count >= 10)
            Dispatcher.InvokeAsync(() =>
            {
                connected = false;
                RenderFrame();
            });
    }

    private void Render(Snapshot snapshot)
    {
        latest = snapshot;
        connected = true;
        var targets = store.Targets;
        for (int i = rows.Count - 1; i >= 0; i--)
            if (!targets.Contains(rows[i].Exe)) rows.RemoveAt(i);
        foreach (var state in snapshot.Targets.Where(s => targets.Contains(s.Exe)))
        {
            var row = rows.FirstOrDefault(r => r.Exe == state.Exe);
            if (row == null) rows.Add(row = new TargetRow(state));
            row.Update(state, icons.Get(state.Exe, snapshot.Ids.GetValueOrDefault(state.Exe)));
        }
        RenderFrame();
        addDialog?.Update(snapshot);
    }

    // 一覧の外側(空の表示・まとめトグル・エラー帯)を今の状態に合わせる
    private void RenderFrame()
    {
        if (exiting) return;   // 閉じたあとに届いた結果で、片づけたトレイアイコンに触らない
        bool loaded = latest != null;
        Placeholder.Text = loaded ? "アプリを追加してください" : "読み込み中…";
        Placeholder.Visibility = !loaded || store.Targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.IsEnabled = connected;
        AllButton.IsEnabled = connected && rows.Count > 0;
        AllButton.Content = rows.Count > 0 && rows.All(r => r.State.IsOn) ? "すべて解除" : "すべてミュート";
        AddButton.IsEnabled = loaded;
        TopCheck.IsChecked = Topmost;
        int muted = rows.Count(r => r.State.HasMute);
        Summary.Text = rows.Count == 0 ? "" : muted == 0 ? $"{rows.Count} 件・ミュートなし" : $"{rows.Count} 件中 {muted} 件にミュートあり";
        var errors = new List<string>();
        if (!connected) errors.Add("オーディオサービスに接続できません。再試行しています");
        if (store.SaveFailed) errors.Add("設定を保存できません");
        ErrorText.Text = string.Join("\n", errors);
        ErrorBand.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Regroup();
        tray.Update(rows.Any(r => r.State.HasMute), (string)AllButton.Content, AllButton.IsEnabled);
        // トレイには、見出しを出しているときだけ、アプリの入っているカテゴリーを並べる
        tray.UpdateCategories(view.GroupDescriptions.Count == 0 ? []
            : rows.OrderBy(r => r.CategoryOrder).GroupBy(r => r.Category)
                .Select(g => (g.Key, $"{g.Key}: {g.First().CategoryButtonText}")).ToList(), AllButton.IsEnabled);
    }

    private void ShowNotice(string text)
    {
        Notice.Text = text;
        Notice.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        string exe = ((TargetRow)((FrameworkElement)sender).DataContext).Exe;
        ShowNotice("");
        long at = Stopwatch.GetTimestamp();
        audio.Post(() => Publish(controller.Toggle(store.Targets, exe, at)));
    }

    private void OnToggleAll(object sender, RoutedEventArgs e) => ToggleAll("group");

    // まとめトグル。ウィンドウのボタン(group)、トレイのメニュー(tray)、ホットキー(hotkey)から呼ぶ
    private void ToggleAll(string source)
    {
        ShowNotice("");
        long at = Stopwatch.GetTimestamp();
        audio.Post(() => Publish(controller.ToggleAll(store.Targets, at, source)));
    }

    private void OnTopClick(object sender, RoutedEventArgs e)
    {
        Topmost = TopCheck.IsChecked == true;
        store.AlwaysOnTop = Topmost;
        store.Save();
        RenderFrame();
    }

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();

    // 設定の画面を開く。ウィンドウの歯車と、トレイのメニューから呼ぶ
    private void OpenSettings()
    {
        // 追加・設定の画面が開いているか、終了の確認中なら、重ねて開かない
        if (OwnedWindows.Count > 0 || exitRequested) return;
        BringToFront();
        ShowNotice("");
        new SettingsWindow(store, hotkey, ApplySettings, () => RegisterHotkeys()) { Owner = this }.ShowDialog();
        // ホットキーの欄を選んだまま閉じたときのために、登録し直す
        if (RegisterHotkeys() is { Count: > 0 } failed)
            ShowNotice($"ホットキー({string.Join("、", failed)})を登録できませんでした。設定で別のキーを選んでください");
        ApplySettings();
    }

    /// <summary>まとめトグルとカテゴリーのホットキーを全部登録し直す。登録できなかったキーを返す。</summary>
    private List<string> RegisterHotkeys()
    {
        hotkey.Clear();
        var failed = new List<string>();
        if (!hotkey.Set(AllHotkey, store.Hotkey)) failed.Add(store.Hotkey);
        for (int i = 0; i < store.Categories.Count; i++)
            if (!hotkey.Set(CategoryHotkeys + i, store.Categories[i].Hotkey)) failed.Add(store.Categories[i].Hotkey);
        return failed;
    }

    // ボタンが押せない状態(未接続・対象なし)では、ホットキーも何もしない
    private void OnHotkey(int id)
    {
        if (id == AllHotkey)
        {
            if (AllButton.IsEnabled) ToggleAll("hotkey");
        }
        else if (store.Categories.ElementAtOrDefault(id - CategoryHotkeys) is { } category) ToggleCategory(category.Name, "hotkey");
    }

    // カテゴリーをまとめて切り替える(見出しのボタン・トレイ・ホットキー)。「その他」はどこにも入れていないアプリ
    private void ToggleCategory(string name, string source)
    {
        var exes = store.Targets.Where(exe => CategoryName(exe) == name).ToList();
        if (!connected || latest == null || exes.Count == 0) return;
        ShowNotice("");
        long at = Stopwatch.GetTimestamp();
        audio.Post(() => Publish(controller.ToggleSome(store.Targets, exes, at, source)));
    }

    private string CategoryName(string exe) => store.CategoryOf(exe) ?? TargetStore.OtherCategory;

    private void OnCategoryToggle(object sender, RoutedEventArgs e) =>
        ToggleCategory((string)((CollectionViewGroup)((FrameworkElement)sender).DataContext).Name, "category");

    private void OnCategoryCollapse(object sender, RoutedEventArgs e)
    {
        string name = (string)((CollectionViewGroup)((FrameworkElement)sender).DataContext).Name;
        if (store.Categories.FirstOrDefault(c => c.Name == name) is { } category)
        {
            category.Collapsed = !category.Collapsed;
            store.Save();
        }
        else otherCollapsed = !otherCollapsed;
        RenderFrame();
    }

    // 行の右クリック(とメニューキー)。開くたびに、削除とカテゴリーの選択肢を組む
    private void OnRowMenu(object sender, ContextMenuEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TargetRow row)
        {
            e.Handled = true;   // 行の外(見出し等)ではメニューを出さない
            return;
        }
        var menu = List.ContextMenu;
        menu.Items.Clear();
        var move = new MenuItem { Header = "カテゴリー" };
        foreach (string name in store.Categories.Select(c => c.Name).Append(TargetStore.OtherCategory))
        {
            string? category = name == TargetStore.OtherCategory ? null : name;
            var item = new MenuItem { Header = name, IsChecked = store.CategoryOf(row.Exe) == category };
            item.Click += (_, _) =>
            {
                store.Assign(row.Exe, category);
                RenderFrame();
            };
            move.Items.Add(item);
        }
        if (store.Categories.Count == 0) move.Items.Add(new MenuItem { Header = "(設定でカテゴリーを作れます)", IsEnabled = false });
        menu.Items.Add(move);
        var remove = new MenuItem { Header = "一覧から削除", DataContext = row };
        remove.Click += OnRemove;
        menu.Items.Add(remove);
    }

    // 行のカテゴリーと並び順を今の設定に合わせ、変わっていれば見出しを作り直す。
    // 見出しは、振り分けたアプリが 1 つでもあるときだけ出す(無ければ今までどおりの平らな一覧)
    private void Regroup()
    {
        bool changed = false;
        var targets = store.Targets;
        foreach (var row in rows)
        {
            string name = CategoryName(row.Exe);
            int index = store.Categories.FindIndex(c => c.Name == name);
            var members = rows.Where(r => CategoryName(r.Exe) == name).ToList();
            bool collapsed = index >= 0 ? store.Categories[index].Collapsed : otherCollapsed;
            changed |= row.SetGroup(name, index >= 0 ? index : int.MaxValue, targets.ToList().IndexOf(row.Exe), !collapsed,
                members.All(r => r.State.IsOn) ? "すべて解除" : "すべてミュート");
        }
        bool grouped = targets.Any(exe => store.CategoryOf(exe) != null);
        if (grouped != view.GroupDescriptions.Count > 0)
        {
            view.GroupDescriptions.Clear();
            if (grouped) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TargetRow.Category)));
        }
        else if (changed) view.Refresh();   // 並べ替えと見出しは、変わったときにだけ作り直す
    }

    // 設定の画面で値が変わるたびに呼ばれる
    private void ApplySettings()
    {
        Look.Apply(this, List, Root, store);
        Topmost = store.AlwaysOnTop;
        ApplyOpacity();
        RenderFrame();
    }

    // ウィンドウの不透明度。自分のウィンドウを Windows の「半透明にできるウィンドウ」にして、全体の濃さを決める
    // (WPF の AllowsTransparency は枠とタイトルバーを失うので使わない)。設定やダイアログには当てない
    private void ApplyOpacity()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;   // まだ一度も表示していない。表示するときに当てる
        int style = GetWindowLong(handle, ExStyle);
        if (store.Opacity >= 100)
        {
            SetWindowLong(handle, ExStyle, style & ~Layered);
            return;
        }
        SetWindowLong(handle, ExStyle, style | Layered);
        SetLayeredWindowAttributes(handle, 0, (byte)(store.Opacity * 255 / 100), LwaAlpha);
    }

    private const int ExStyle = -20;          // GWL_EXSTYLE
    private const int Layered = 0x00080000;   // WS_EX_LAYERED
    private const uint LwaAlpha = 0x2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var row = (TargetRow)((FrameworkElement)sender).DataContext;
        if (!row.State.CanRemove)
        {
            ShowNotice("ミュート中のアプリは削除できません。先に解除してください");
            return;
        }
        ShowNotice("");
        store.Remove(row.Exe);
        rows.Remove(row);
        RenderFrame();
        audio.Post(() => Publish(controller.Sync(store.Targets)));
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        ShowNotice("");
        addDialog = new AddDialog(store, latest!, icons) { Owner = this };
        bool added = addDialog.ShowDialog() == true;
        addDialog = null;
        RenderFrame();
        if (added) audio.Post(() => Publish(controller.Sync(store.Targets)));
    }
}

/// <summary>一覧の1行。状態が変わったときだけ画面へ知らせる。</summary>
public sealed class TargetRow(TargetState state) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public TargetState State { get; private set; } = state;
    public string Exe => State.Exe;
    public bool WriteFailed => State.WriteFailed;
    public bool HasMute => State.HasMute;
    public string ButtonText => State.IsOn ? "解除" : "ミュート";
    public string ButtonGlyph => State.IsOn ? "" : "";   // 押したあとの姿(鳴っているスピーカー / 消したスピーカー)
    public ImageSource? Icon { get; private set; }

    // カテゴリー(見出しの名前と並び順)。MainWindow.Regroup が入れる
    public string Category { get; private set; } = TargetStore.OtherCategory;
    public int CategoryOrder { get; private set; }
    public int Order { get; private set; }
    /// <summary>カテゴリーが畳まれていなければ true(畳んでも行は消さず、隠すだけ)。</summary>
    public bool Shown { get; private set; } = true;
    /// <summary>見出しのまとめトグルの文言(同じカテゴリーの行はみな同じ値)。</summary>
    public string CategoryButtonText { get; private set; } = "すべてミュート";

    /// <returns>カテゴリーか並び順が変わったか(見出しの作り直しが要るか)。</returns>
    public bool SetGroup(string category, int categoryOrder, int order, bool shown, string buttonText)
    {
        bool moved = category != Category || categoryOrder != CategoryOrder || order != Order;
        if (!moved && shown == Shown && buttonText == CategoryButtonText) return false;
        (Category, CategoryOrder, Order, Shown, CategoryButtonText) = (category, categoryOrder, order, shown, buttonText);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        return moved;
    }

    // 状態の印の色。明るい配色でも暗い配色でも見分けがつく中間の濃さにしてある
    public Brush StatusBrush => State.Display switch
    {
        Display.Muted => Red,
        Display.Partial => Orange,
        Display.Unmuted => Green,
        Display.Reserved => Blue,
        _ => Gray,
    };

    private static readonly Brush Red = Solid(0xE5, 0x48, 0x4D), Orange = Solid(0xF5, 0xA5, 0x24),
        Green = Solid(0x30, 0xA4, 0x6C), Blue = Solid(0x4C, 0x8D, 0xF6), Gray = Solid(0x8B, 0x8D, 0x98);

    private static Brush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public string Status => State.Display switch
    {
        Display.Muted => "ミュート中",
        Display.Partial => "一部ミュート",
        Display.Unmuted => "ミュートなし",
        Display.Reserved => "ミュート予約中",
        _ => "音を出していません",
    };

    public void Update(TargetState state, ImageSource? icon)
    {
        if (state == State && icon == Icon) return;
        State = state;
        Icon = icon;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
