// 追加ダイアログの動き。一覧は表示中も同期のたびに更新され、登録済みの exe は選べない。
// 登録するのはファイル名だけで、選んだファイルのパスは持たない(INV-6)。
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace HushSwitch;

/// <summary>一覧の1件。登録済みなら CanAdd は false(灰色で選択不可)。</summary>
public sealed record AppItem(string Exe, bool CanAdd, ImageSource? Icon)
{
    // 読み上げソフトに渡す名前になる
    public override string ToString() => Exe;
}

public partial class AddDialog : Window
{
    private readonly TargetStore store;
    private readonly AppIcons icons;

    public AddDialog(TargetStore store, Snapshot snapshot, AppIcons icons)
    {
        this.store = store;
        this.icons = icons;
        InitializeComponent();
        Update(snapshot);
    }

    /// <summary>同期のたびにメインウィンドウから呼ばれる。中身が変わったときだけ並べ直し、選択は保つ。</summary>
    public void Update(Snapshot snapshot)
    {
        var items = snapshot.Apps.Where(TargetStore.IsValidName)
            .Select(exe => new AppItem(exe, !store.Targets.Contains(exe), icons.Get(exe, snapshot.Ids.GetValueOrDefault(exe)))).ToList();
        if (Apps.ItemsSource is List<AppItem> current && current.SequenceEqual(items)) return;
        string? selected = (Apps.SelectedItem as AppItem)?.Exe;
        Apps.ItemsSource = items;
        Apps.SelectedItem = items.FirstOrDefault(item => item.Exe == selected && item.CanAdd);
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => AddButton.IsEnabled = Apps.SelectedItem != null;

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (Apps.SelectedItem is AppItem item) TryAdd(item.Exe);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "exe ファイル (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) TryAdd(Path.GetFileName(dialog.FileName));
    }

    private void TryAdd(string fileName)
    {
        string? error = store.Add(fileName);
        if (error == null)
        {
            DialogResult = true;
            return;
        }
        Message.Text = error;
        Message.Visibility = Visibility.Visible;
    }
}
