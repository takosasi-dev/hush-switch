// メインウィンドウの動き。ボタンの押下を Core Audio 専用スレッドへ渡し、
// 読み直した状態(Snapshot)を受け取って一覧に映す。UI スレッドからは Core Audio を呼ばない(INV-7)。
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

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

    public MainWindow()
    {
        InitializeComponent();
        List.ItemsSource = rows;
        ShowNotice(string.Join("\n", store.Notices));
        var service = new AudioSessionService(log);
        controller = new MuteController(service, log);
        // 新しいセッションの通知(D-9)。同期ループを待たずに、専用スレッドで列挙し直す
        service.SessionCreated += () =>
        {
            long at = Stopwatch.GetTimestamp();
            audio?.Post(() => Publish(controller.Sync(store.Targets, at)));
        };
        audio = new AudioThread(() => Publish(controller.Sync(store.Targets)), OnAudioFailed);
        Closed += (_, _) => audio.Dispose();
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
            row.Update(state);
        }
        RenderFrame();
        addDialog?.Update(snapshot.Apps);
    }

    // 一覧の外側(空の表示・まとめトグル・エラー帯)を今の状態に合わせる
    private void RenderFrame()
    {
        bool loaded = latest != null;
        Placeholder.Text = loaded ? "アプリを追加してください" : "読み込み中…";
        Placeholder.Visibility = !loaded || store.Targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.IsEnabled = connected;
        AllButton.IsEnabled = connected && rows.Count > 0;
        AllButton.Content = rows.Count > 0 && rows.All(r => r.State.IsOn) ? "すべて解除" : "すべてミュート";
        AddButton.IsEnabled = loaded;
        var errors = new List<string>();
        if (!connected) errors.Add("オーディオサービスに接続できません。再試行しています");
        if (store.SaveFailed) errors.Add("設定を保存できません");
        ErrorText.Text = string.Join("\n", errors);
        ErrorBand.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnToggleAll(object sender, RoutedEventArgs e)
    {
        ShowNotice("");
        long at = Stopwatch.GetTimestamp();
        audio.Post(() => Publish(controller.ToggleAll(store.Targets, at)));
    }

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
        addDialog = new AddDialog(store, latest!.Apps) { Owner = this };
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
    public string ButtonText => State.IsOn ? "解除" : "ミュート";

    public string Status => State.Display switch
    {
        Display.Muted => "ミュート中",
        Display.Partial => "一部ミュート",
        Display.Unmuted => "ミュートなし",
        Display.Reserved => "ミュート予約中",
        _ => "音を出していません",
    };

    public void Update(TargetState state)
    {
        if (state == State) return;
        State = state;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
