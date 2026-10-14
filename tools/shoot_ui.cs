// 画面の見た目を、ウィンドウを出さずに PNG へ描く(開発用。製品には含めない)。
// XAML を読み、見本のデータを入れ、本体と同じ Look.Apply で見た目の設定を当てて描く。
// メインウィンドウはデザインごと・ライトとダークで、ほかの画面はライトとダークで描く。本体は起動しないので、キー入力も音も触らない。
// 使い方: dotnet run tools/shoot_ui.cs -- <出力フォルダ>    /    dotnet run tools/shoot_ui.cs -- keys <語>(テーマの資源の名前を探す)
#:project ../src/HushSwitch/HushSwitch.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property UseWindowsForms=true
#:property NoWarn=WPF0001
#:property PublishAot=false
#:property PublishTrimmed=false
#:property ImplicitUsings=disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HushSwitch;

string source = Path.Combine(Path.GetDirectoryName(Here())!, "..", "src", "HushSwitch");
var thread = new Thread(() => Run(args));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

// x:Class と、イベントの関数名(On〜)を外すと、コードビハインド無しで読める
object Load(string file) => XamlReader.Parse(
    Regex.Replace(File.ReadAllText(Path.Combine(source, file)), @"\s(x:Class|\w+)=""(HushSwitch\.\w+|On[A-Z]\w*)""", ""));

void Run(string[] args)
{
    var app = (Application)Load("App.xaml");
    if (args is ["keys", var word])
    {
        app.ThemeMode = ThemeMode.Dark;
        foreach (var key in Keys(app.Resources).Where(k => k.Contains(word, StringComparison.OrdinalIgnoreCase)).Distinct().Order())
            Console.WriteLine(key);
        return;
    }

    string output = args.Length > 0 ? args[0] : ".";
    Directory.CreateDirectory(output);
    Brush Dot(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    // 見本のアイコン(色の四角に 1 文字)。公開する画像に実在のアプリの名前やアイコンを出さない
    ImageSource Badge(string color, string letter)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(Dot(color), null, new Rect(0, 0, 48, 48), 10, 10);
            var text = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 28, Brushes.White, 1.0);
            dc.DrawText(text, new Point((48 - text.Width) / 2, (48 - text.Height) / 2));
        }
        var bitmap = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
    var browser = Badge("#2B7CD3", "B");
    var voice = Badge("#6E56CF", "V");
    var game = Badge("#E5484D", "G");
    // 見本の行は本物の TargetRow(表示の規則は本体と同じ)。カテゴリーは (名前, 並び順)
    (string Exe, Display Display, bool Failed, ImageSource? Icon, string Category, int Order)[] samples =
    [
        ("browser.exe", Display.Muted, false, browser, "ブラウザ", 1),
        ("voicechat.exe", Display.Unmuted, false, voice, "通話", 2),
        ("game.exe", Display.Partial, true, game, "ゲーム", 0),
        ("music.exe", Display.Reserved, false, null, "音楽", 3),
        ("a-very-long-application-name-that-does-not-fit.exe", Display.Silent, false, null, TargetStore.OtherCategory, int.MaxValue),
        ("launcher.exe", Display.Muted, false, Badge("#30A46C", "L"), "ゲーム", 0),
    ];
    // grouped: カテゴリーの見出しを出す(MainWindow.Regroup と同じ並べ方)。collapsed: 畳むカテゴリー
    System.Collections.ObjectModel.ObservableCollection<TargetRow> Rows(bool grouped, string? collapsed)
    {
        var list = new System.Collections.ObjectModel.ObservableCollection<TargetRow>();
        for (int i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            if (!grouped && sample.Exe == "launcher.exe") continue;
            var row = new TargetRow(new TargetState(sample.Exe, sample.Display, sample.Failed));
            row.Update(row.State, sample.Icon);
            var members = samples.Where(x => x.Category == sample.Category && (grouped || x.Exe != "launcher.exe"));
            bool allOn = members.All(x => x.Display is Display.Muted or Display.Reserved);
            row.SetGroup(grouped ? sample.Category : TargetStore.OtherCategory, grouped ? sample.Order : 0, i,
                sample.Category != collapsed, allOn ? "すべて解除" : "すべてミュート");
            list.Add(row);
        }
        var view = (System.Windows.Data.ListCollectionView)System.Windows.Data.CollectionViewSource.GetDefaultView(list);
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(TargetRow.CategoryOrder), System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(TargetRow.Order), System.ComponentModel.ListSortDirection.Ascending));
        if (grouped) view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(TargetRow.Category)));
        return list;
    }
    object[] apps =
    [
        new { Exe = "browser.exe", CanAdd = false, Icon = browser },
        new { Exe = "voicechat.exe", CanAdd = true, Icon = voice },
        new { Exe = "unknown.exe", CanAdd = true, Icon = (ImageSource?)null },
    ];

    // 描く組み合わせ: (ファイル, 名前, 設定を変える関数)
    var shots = new List<(string File, string Name, Action<TargetStore> Set)>();
    foreach (string theme in new[] { "light", "dark" })
    {
        foreach (string design in TargetStore.Designs.Where(d => d != "gaming" || theme == "dark"))
            shots.Add(("MainWindow.xaml", $"{design}-{theme}", s => (s.Theme, s.Design) = (theme, design)));
        foreach (string file in new[] { "SettingsWindow.xaml", "AddDialog.xaml", "ExitDialog.xaml" })
            shots.Add((file, theme, s => s.Theme = theme));
    }
    shots.Add(("MainWindow.xaml", "accent-pink-dark", s => (s.Theme, s.Accent) = ("dark", "pink")));
    shots.Add(("MainWindow.xaml", "accent-green-light", s => (s.Theme, s.Accent) = ("light", "green")));
    shots.Add(("MainWindow.xaml", "xlarge-hidden-light", s => (s.Theme, s.TextSize, s.ShowIcons, s.ShowRemove, s.ShowSummary) = ("light", "xlarge", false, false, false)));
    shots.Add(("MainWindow.xaml", "category-standard-light", s => s.Theme = "light"));
    shots.Add(("SettingsWindow.xaml", "tall-dark", s =>
    {
        s.Theme = "dark";
        s.Categories[0].Hotkey = "Ctrl+Alt+G";
    }));
    shots.Add(("MainWindow.xaml", "category-collapsed-dark", s => s.Theme = "dark"));
    shots.Add(("MainWindow.xaml", "category-tiles-dark", s => (s.Theme, s.Design) = ("dark", "tiles")));
    shots.Add(("MainWindow.xaml", "category-gaming-dark", s => (s.Theme, s.Design) = ("dark", "gaming")));
    shots.Add(("MainWindow.xaml", "category-compact-light", s => (s.Theme, s.Design) = ("light", "compact")));
    shots.Add(("SettingsWindow.xaml", "accent-orange-light", s => (s.Theme, s.Accent, s.Opacity) = ("light", "orange", 85)));
    // 特大は、大きめ・標準と組み合わせ、既定の幅と最小の幅で描く(名前の列が残るか)
    foreach (string design in new[] { "standard", "large" })
        foreach (string width in new[] { "", "-narrow" })
            shots.Add(("MainWindow.xaml", $"xlarge-{design}{width}-light", s => (s.Theme, s.Design, s.TextSize) = ("light", design, "xlarge")));

    string settings = Path.Combine(Path.GetTempPath(), "hushswitch-shoot-" + Guid.NewGuid().ToString("N") + ".json");
    foreach (var (file, name, set) in shots)
    {
        var store = new TargetStore(settings);   // ファイルは作らない(Save を呼ばない)
        set(store);
        var window = (Window)Load(file);
        // 見本の状態(本体ではコードビハインドが入れる)
        if (window.FindName("List") is ItemsControl list)
        {
            list.ItemsSource = Rows(name.StartsWith("category"), name.Contains("collapsed") ? "ブラウザ" : null);
            Look.Apply(window, list, (FrameworkElement)window.FindName("Root"), store);
            if (name.Contains("narrow")) window.Width = window.MinWidth;   // 本体でも、最小の幅より狭くはならない
        }
        else
        {
            App.ApplyTheme(store.Theme);
            Accent.Apply(store.Accent);
        }
        if (window.FindName("Apps") is ItemsControl box) box.ItemsSource = apps;
        if (window.FindName("Placeholder") is UIElement placeholder) placeholder.Visibility = Visibility.Collapsed;
        if (window.FindName("Summary") is TextBlock summary) summary.Text = "5 件中 3 件にミュートあり";
        if (window.FindName("AllButton") is Button all) all.IsEnabled = true;
        if (window.FindName("AddButton") is Button add) add.IsEnabled = true;
        if (window.FindName("TopCheck") is System.Windows.Controls.Primitives.ToggleButton pin) pin.IsChecked = name.StartsWith("accent");
        // 設定の画面の欄は、本体と同じく store の値から入れる
        if (window.FindName("AccentBox") is ComboBox accentBox)
        {
            accentBox.ItemsSource = SettingsWindow.AccentChoices();
            accentBox.SelectedValue = store.Accent;
        }
        foreach (var (boxName, value) in new[] { ("DesignBox", store.Design), ("ThemeBox", store.Theme), ("TextSizeBox", store.TextSize),
                     ("CloseBox", store.CloseToTray ? "tray" : "exit"), ("ExitBox", store.OnExit) })
            if (window.FindName(boxName) is ComboBox choice)
                choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == value);
        foreach (var (boxName, value) in new[] { ("IconsBox", store.ShowIcons), ("StatusBox", store.ShowStatus),
                     ("SummaryBox", store.ShowSummary), ("RemoveBox", store.ShowRemove), ("TopBox", store.AlwaysOnTop) })
            if (window.FindName(boxName) is CheckBox check) check.IsChecked = value;
        if (window.FindName("OpacitySlider") is Slider slider) slider.Value = store.Opacity;
        if (window.FindName("OpacityText") is TextBlock opacity) opacity.Text = $"{store.Opacity}%";
        // エラー帯はダークの標準にだけ出す(ライトの標準は README の画像に使う)
        if (name == "standard-dark" && window.FindName("ErrorBand") is UIElement band && window.FindName("ErrorText") is TextBlock error)
        {
            band.Visibility = Visibility.Visible;
            error.Text = "オーディオサービスに接続できません。再試行しています";
        }
        if (window.FindName("HotkeyBox") is TextBox hotkey) hotkey.Text = "Ctrl+Alt+M";
        if (window.FindName("CategoryList") is ItemsControl categories) categories.ItemsSource = store.Categories;
        if (name.Contains("tall")) window.Height = 1500;   // 設定の画面を、スクロールせずに全部描く
        if (window.FindName("VersionText") is TextBlock version) version.Text = "HushSwitch 0.3.0";

        // ウィンドウは出せないので、中身だけを、ウィンドウの地の色と文字色の上に描く
        bool dark = app.ThemeMode == ThemeMode.Dark;
        var style = (Style)app.FindResource(typeof(Window));
        object? Setter(DependencyProperty property) =>
            style.Setters.OfType<Setter>().LastOrDefault(s => s.Property == property)?.Value is DynamicResourceExtension d
                ? app.TryFindResource(d.ResourceKey) : null;
        var background = store.Design == "gaming" && file == "MainWindow.xaml" ? (Brush)window.Resources["GamingBackground"] : Setter(Control.BackgroundProperty) as Brush;
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new ContentControl
        {
            Content = content, Width = window.Width - 16, Height = window.Height - 40,
            Background = background,
            Foreground = Setter(Control.ForegroundProperty) as Brush ?? (dark ? Brushes.White : Brushes.Black),
            Resources = window.Resources,   // 行のテンプレートや表示の切り替えは、ウィンドウの資源にある
        };
        var back = new Border { Child = host, Background = background ?? (dark ? Dot("#202020") : Dot("#F3F3F3")) };
        back.Measure(new Size(host.Width, host.Height));
        back.Arrange(new Rect(0, 0, host.Width, host.Height));
        back.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)host.Width * 2, (int)host.Height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(back);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string png = Path.Combine(output, $"{Path.GetFileNameWithoutExtension(file)}-{name}.png");
        using (var stream = File.Create(png)) encoder.Save(stream);
        Console.WriteLine(png);
    }
}

IEnumerable<string> Keys(ResourceDictionary dictionary) =>
    dictionary.Keys.OfType<string>().Concat(dictionary.MergedDictionaries.SelectMany(Keys));
