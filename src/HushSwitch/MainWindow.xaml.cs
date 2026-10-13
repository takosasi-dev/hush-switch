// メインウィンドウの動き。ボタンの押下を Core Audio 専用スレッドへ渡し、
// 読み直した状態(Snapshot)を受け取って一覧に映す。UI スレッドからは Core Audio を呼ばない(INV-7)。
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace HushSwitch;

public partial class MainWindow : Window
{
    private readonly TargetStore store = new(Environment.ExpandEnvironmentVariables(@"%APPDATA%\HushSwitch\settings.json"));
    private readonly MuteController controller = new(new AudioSessionService());
    private readonly AudioThread audio;
    private readonly ObservableCollection<TargetRow> rows = [];
    private Snapshot? latest;      // null のあいだは読み込み中
    private AddDialog? addDialog;

    public MainWindow()
    {
        InitializeComponent();
        List.ItemsSource = rows;
        ShowNotice(string.Join("\n", store.Notices));
        audio = new AudioThread(() => Publish(controller.Sync(store.Targets)));
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

    private void Render(Snapshot snapshot)
    {
        latest = snapshot;
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
        bool empty = store.Targets.Count == 0;
        Placeholder.Text = "アプリを追加してください";
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AllButton.IsEnabled = rows.Count > 0;
        AllButton.Content = rows.Count > 0 && rows.All(r => r.State.IsOn) ? "すべて解除" : "すべてミュート";
        AddButton.IsEnabled = true;
        ErrorText.Text = "設定を保存できません";
        ErrorBand.Visibility = store.SaveFailed ? Visibility.Visible : Visibility.Collapsed;
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
        audio.Post(() => Publish(controller.Toggle(store.Targets, exe)));
    }

    private void OnToggleAll(object sender, RoutedEventArgs e)
    {
        ShowNotice("");
        audio.Post(() => Publish(controller.ToggleAll(store.Targets)));
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
