// 設定の画面の動き。変えた値をその場で settings.json に保存し、メインウィンドウへ知らせて反映させる。
// 自動起動だけは設定ファイルでなく、Windows の起動項目そのものを見て書く(Autostart)。
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HushSwitch;

/// <summary>アクセントの色の選択肢 1 つ。Key は settings.json に書く値。</summary>
public sealed record AccentChoice(string Key, string Name, Brush Swatch);

public partial class SettingsWindow : Window
{
    private readonly TargetStore store;
    private readonly GlobalHotKey hotkey;
    private readonly Action applied;
    private readonly Action registerAll;
    private readonly bool loading = true;
    private const int TestHotkey = 9999;   // 登録できるかを試すときの番号

    /// <param name="applied">保存のたびに呼ぶ(メインウィンドウが見た目・見出しを合わせる)。</param>
    /// <param name="registerAll">ホットキーを全部登録し直す(メインウィンドウが持っている)。</param>
    public SettingsWindow(TargetStore store, GlobalHotKey hotkey, Action applied, Action registerAll)
    {
        this.store = store;
        this.hotkey = hotkey;
        this.applied = applied;
        this.registerAll = registerAll;
        InitializeComponent();
        Select(DesignBox, store.Design);
        Select(ThemeBox, store.Theme);
        AccentBox.ItemsSource = AccentChoices();
        AccentBox.SelectedValue = store.Accent;
        Select(TextSizeBox, store.TextSize);
        OpacitySlider.Value = store.Opacity;
        IconsBox.IsChecked = store.ShowIcons;
        StatusBox.IsChecked = store.ShowStatus;
        SummaryBox.IsChecked = store.ShowSummary;
        RemoveBox.IsChecked = store.ShowRemove;
        ShowDependents();
        Select(CloseBox, store.CloseToTray ? "tray" : "exit");
        Select(ExitBox, store.OnExit);
        TopBox.IsChecked = store.AlwaysOnTop;
        StartInTrayBox.IsChecked = store.StartInTray;
        AutostartBox.IsChecked = Autostart.Enabled;
        HotkeyBox.Text = store.Hotkey.Length > 0 ? store.Hotkey : "なし";
        ShowCategories();
        VersionText.Text = $"HushSwitch {GetType().Assembly.GetName().Version?.ToString(3)}";
        loading = false;
    }

    /// <summary>アクセントの色の選択肢(並びは TargetStore.Accents と同じ)。</summary>
    public static List<AccentChoice> AccentChoices()
    {
        string[] names = ["Windows の色", "青", "青緑", "緑", "紫", "ピンク", "オレンジ", "赤"];
        return TargetStore.Accents.Select((key, i) => new AccentChoice(key, names[i],
            Accent.Base(key) is { } color ? new SolidColorBrush(color) : SystemColors.AccentColorBrush)).ToList();
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == tag);

    private static string Chosen(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;   // 部品を作っている途中(スライダーは最小値を入れた時点で呼ばれる)
        store.Design = Chosen(DesignBox);
        store.Theme = Chosen(ThemeBox);
        store.Accent = (string)AccentBox.SelectedValue;
        store.TextSize = Chosen(TextSizeBox);
        store.Opacity = (int)OpacitySlider.Value;
        store.ShowIcons = IconsBox.IsChecked == true;
        store.ShowStatus = StatusBox.IsChecked == true;
        store.ShowSummary = SummaryBox.IsChecked == true;
        store.ShowRemove = RemoveBox.IsChecked == true;
        ShowDependents();
        store.AlwaysOnTop = TopBox.IsChecked == true;
        store.StartInTray = StartInTrayBox.IsChecked == true;
        store.CloseToTray = Chosen(CloseBox) == "tray";
        store.OnExit = Chosen(ExitBox);
        store.Save();
        applied();
    }

    // ほかの値によって変わる表示
    private void ShowDependents()
    {
        OpacityText.Text = $"{(int)OpacitySlider.Value}%";
        bool gaming = Chosen(DesignBox) == "gaming";
        ThemeBox.IsEnabled = !gaming;
        ThemeLabel.Text = gaming ? "配色(ゲーミングはダーク固定)" : "配色";
    }

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        bool ok = Autostart.Set(AutostartBox.IsChecked == true);
        AutostartBox.IsChecked = Autostart.Enabled;
        AutostartNote.Text = "自動起動の設定を変えられませんでした";
        AutostartNote.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    // 欄を選んでいるあいだはホットキーを全部外しておく(同じキーを押し直しても、ミュートが切り替わらないように)
    private void OnHotkeyFocus(object sender, RoutedEventArgs e) => hotkey.Clear();

    private void OnHotkeyBlur(object sender, RoutedEventArgs e) => registerAll();

    // ホットキーの欄でキーが押された。まとめトグルの欄(HotkeyBox)か、カテゴリーの行の欄
    private void OnHotkeyKey(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var category = box == HotkeyBox ? null : (Category)box.DataContext;
        var note = category == null ? HotkeyNote : CategoryNote;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Tab(欄から出る)と Esc(閉じる)は、そのまま通す
        if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Tab or Key.Escape) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Delete or Key.Back)
        {
            SaveHotkey(box, category, "");
            note.Text = "ホットキーをなしにしました";
            return;
        }

        if (GlobalHotKey.Format(key, Keyboard.Modifiers) is not { } text)
        {
            note.Text = "そのキーは使えません。Ctrl か Alt を含む組み合わせか、F1〜F24 にしてください";
            return;
        }
        // 欄を選んでいるあいだは全部外してあるので、自分の設定どうしの重なりは先に調べる
        if (store.HotkeyUsedBy(text, category?.Name ?? "") is { } owner)
        {
            note.Text = $"{text} は「{owner}」で使っています";
            return;
        }
        // ほかのアプリが使っていないかは、試しに登録して確かめ、すぐ外す(欄を出たときに全部登録し直す)
        bool ok = hotkey.Set(TestHotkey, text);
        hotkey.Set(TestHotkey, "");
        if (!ok)
        {
            note.Text = $"{text} は登録できません。ほかのアプリが使っています";
            return;
        }
        SaveHotkey(box, category, text);
        note.Text = $"{text} に決めました";
    }

    private void OnHotkeyClear(object sender, RoutedEventArgs e)
    {
        SaveHotkey(HotkeyBox, null, "");
        registerAll();
        HotkeyNote.Text = "ホットキーをなしにしました";
    }

    private void SaveHotkey(TextBox box, Category? category, string text)
    {
        if (category == null) store.Hotkey = text;
        else category.Hotkey = text;
        store.Save();
        box.Text = text.Length > 0 || category != null ? text : "なし";
        applied();
    }

    // ---- カテゴリー ----

    private void ShowCategories()
    {
        CategoryList.ItemsSource = store.Categories.ToList();
        NewCategoryBox.IsEnabled = AddCategoryButton.IsEnabled = store.Categories.Count < TargetStore.MaxCategories;
    }

    private void OnCategoryAdd(object sender, RoutedEventArgs e)
    {
        if (store.AddCategory(NewCategoryBox.Text) is { } error)
        {
            CategoryNote.Text = error;
            return;
        }
        CategoryNote.Text = $"「{store.Categories[^1].Name}」を足しました。一覧の行を右クリックして、アプリを入れてください";
        NewCategoryBox.Text = "";
        ShowCategories();
        registerAll();   // ホットキーの番号は並び順なので、足したら登録し直す
        applied();
    }

    // 名前の欄から出たときに確定する。だめなら元の名前に戻す
    private void OnCategoryRename(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        var category = (Category)box.DataContext;
        if (store.RenameCategory(category.Name, box.Text) is { } error)
        {
            CategoryNote.Text = error;
            box.Text = category.Name;
            return;
        }
        applied();
    }

    private void OnCategoryRemove(object sender, RoutedEventArgs e)
    {
        var category = (Category)((FrameworkElement)sender).DataContext;
        store.RemoveCategory(category.Name);
        CategoryNote.Text = $"「{category.Name}」を消しました。入っていたアプリは「{TargetStore.OtherCategory}」に戻ります";
        ShowCategories();
        registerAll();
        applied();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenFolder(@"%APPDATA%\HushSwitch");

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(@"%LOCALAPPDATA%\HushSwitch\logs");

    private static void OpenFolder(string folder)
    {
        try
        {
            string path = Environment.ExpandEnvironmentVariables(folder);
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }
}
