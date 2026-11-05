using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using CampusPortalAutoLogin.Animations;
using CampusPortalAutoLogin.Core.Logging;
using CampusPortalAutoLogin.Core.Models;

namespace CampusPortalAutoLogin.Views;

public partial class LogWindow : Window
{
    private readonly LogService log;
    private readonly ObservableCollection<LogEntry> entries = new();
    private bool closed, loading, reloadRequested;
    private bool closeAnimated;
    public LogWindow(LogService log)
    {
        this.log = log; InitializeComponent(); WindowStartupLocation = WindowStartupLocation.CenterScreen; Entries.ItemsSource = entries;
        WindowFrame.Attach(this, WindowBorder);
        MotionService.AttachAmbientMotion(this, Lights);
        MotionService.AttachCursorAura(this, CursorLayer, CursorAura);
        CollectionViewSource.GetDefaultView(entries).Filter = FilterEntry;
        Loaded += async (_, _) => { MotionService.Enter(Surface); MotionService.BlurIn(Lights); MotionService.AttachTextGradientFlow(HeroTitle); MotionService.WireHover(Surface); await ReloadAsync(); };
        log.EntryAdded += AddEntry;
        log.Cleared += LogsCleared;
        Closing += async (_, args) =>
        {
            if (!closeAnimated && MotionService.Enabled && (Application.Current as App)?.IsExiting != true)
            { args.Cancel = true; closeAnimated = true; await MotionService.ExitAsync(Surface); Close(); }
        };
        Closed += (_, _) => { closed = true; log.EntryAdded -= AddEntry; log.Cleared -= LogsCleared; };
    }
    private bool FilterEntry(object value) => value is LogEntry e && (Filter.SelectedIndex == 0 || (int)e.Level == Filter.SelectedIndex - 1);
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (Entries?.ItemsSource is not null) CollectionViewSource.GetDefaultView(entries).Refresh(); }
    private void AddEntry(LogEntry entry)
    {
        LogsCleared();
    }
    private void LogsCleared()
    {
        if (!closed) Dispatcher.BeginInvoke(async () => { if (!closed) await ReloadAsync(); });
    }
    private async Task ReloadAsync()
    {
        if (closed) return;
        if (loading) { reloadRequested = true; return; }
        loading = true;
        do
        {
            reloadRequested = false;
            var task = Task.Run(log.ReadRecent);
            if (await Task.WhenAny(task, Task.Delay(150)) != task && !closed)
            {
                LoadingText.Visibility = Visibility.Visible;
                if (MotionService.Enabled) LoadingText.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(750)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            try
            {
                var result = await task;
                if (closed || reloadRequested) continue;
                // 普通追加保留已有对象和选择；仅真正清空或文件被替换时重置集合。
                var first = result.Count == 0 ? -1 : entries.IndexOf(result[0]);
                if (first >= 0) for (var i = 0; i < first; i++) entries.RemoveAt(0);
                if (entries.Count > result.Count || !entries.SequenceEqual(result.Take(entries.Count))) entries.Clear();
                for (var i = entries.Count; i < result.Count; i++) entries.Add(result[i]);
                CountText.Text = $"{entries.Count} 条本地记录";
                if (AutoScroll.IsChecked == true && entries.Count > 0) Entries.ScrollIntoView(entries[^1]);
            }
            catch (Exception e) { if (!closed) CountText.Text = "读取失败：" + e.GetType().Name; }
            finally { LoadingText.BeginAnimation(OpacityProperty, null); LoadingText.Visibility = Visibility.Collapsed; }
        } while (reloadRequested && !closed);
        loading = false;
    }
    private async void Refresh(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void Copy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, Entries.SelectedItems.Cast<LogEntry>().Select(e => e.ToString()))); }
        catch { CountText.Text = "剪贴板暂时不可用，请重试。"; }
    }
    private void OpenDirectory(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(log.DirectoryPath); Process.Start(new ProcessStartInfo(log.DirectoryPath) { UseShellExecute = true }); }
        catch { CountText.Text = "无法打开日志目录。"; }
    }
    private async void Clear(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "删除本地日志文件？配置和密码会保留。", "清空日志", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { log.Clear(); await ReloadAsync(); } catch { CountText.Text = "清空失败，日志文件可能被其它程序占用。"; }
    }
}
