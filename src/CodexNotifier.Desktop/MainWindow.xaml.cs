using CodexNotifier.Core;
using Microsoft.Win32;
namespace CodexNotifier.Desktop;

public partial class MainWindow : Window
{
    readonly App app;
    bool refreshing, busy;
    SourceProfile? Selected => SourcesBox.SelectedItem as SourceProfile;
    public MainWindow(App app)
    {
        this.app = app; InitializeComponent();
        Closing += OnPanelClosing;
        RefreshSources(); Refresh();
    }
    void OnPanelClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (app.Exiting) return;
        e.Cancel = true;
        var prompt = new ClosePrompt { Owner = this };
        if (prompt.ShowDialog() != true) return;
        // Wait until this Closing event has returned before closing the same window again.
        if (prompt.ExitRequested) Dispatcher.BeginInvoke(new Action(app.ExitApp));
        else
        {
            Hide();
            app.Log("程序已最小化到系统托盘；点击托盘图标或右键悬浮球可打开主面板。");
        }
    }
    public void Refresh()
    {
        if (EnabledToggle == null) return;
        refreshing = true;
        EnabledToggle.IsChecked = app.Settings.Enabled; AudioToggle.IsChecked = app.Settings.AudioEnabled;
        OrbToggle.IsChecked = app.Settings.OrbVisible; GlowToggle.IsChecked = app.Settings.GlowEnabled; FlashToggle.IsChecked = app.Settings.ScreenFlash;
        AudioPathBox.Text = string.IsNullOrEmpty(app.Settings.AudioPath) ? "默认 · Audio/over.wav" : app.Settings.AudioPath;
        VolumeSlider.Value = app.Settings.Volume; SizeSlider.Value = app.Settings.OrbSize;
        StateLabel.Text = !app.Settings.Enabled ? "Ⅱ 已暂停" : app.Tracker.Active > 0 ? $"◌ 工作中 · {app.Tracker.Active}" : "● 空闲";
        refreshing = false;
    }
    void RefreshSources(string? id = null)
    {
        id ??= Selected?.Id; SourcesBox.ItemsSource = null; SourcesBox.ItemsSource = app.Settings.Sources;
        SourcesBox.SelectedItem = app.Settings.Sources.FirstOrDefault(s => s.Id == id) ?? app.Settings.Sources.FirstOrDefault();
        ShowSource();
    }
    void ShowSource()
    {
        if (Selected is not { } s) { SourceStatus.Text = "尚未选择目录。可检测或手动添加。"; return; }
        HomeBox.Text = s.Home; DistroBox.Text = s.Distro;
        try
        {
            var record = JsonStore.Read<IntegrationRecord>(Integration.RecordPath(s));
            if (record != null) ForwardToggle.IsChecked = record.ForwardPrevious;
            SourceStatus.Text = (s.Enabled ? "监测开启 · " : "监测停用 · ") + Integration.Status(s);
        }
        catch (Exception e) { SourceStatus.Text = "无法读取配置：" + e.Message; }
    }
    void SourceChanged(object sender, SelectionChangedEventArgs e) { if (HomeBox != null) ShowSource(); }
    void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (refreshing) return;
        app.Settings.Enabled = EnabledToggle.IsChecked == true; app.Settings.AudioEnabled = AudioToggle.IsChecked == true;
        app.Settings.OrbVisible = OrbToggle.IsChecked == true; app.Settings.GlowEnabled = GlowToggle.IsChecked == true; app.Settings.ScreenFlash = FlashToggle.IsChecked == true;
        app.ApplySettings();
    }
    void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (refreshing || VolumeSlider == null || SizeSlider == null || app.Orb == null) return;
        app.Settings.Volume = VolumeSlider.Value; app.Settings.OrbSize = SizeSlider.Value; app.ApplySettings();
    }
    void ChooseAudio(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AudioLibrary.DirectoryPath);
            var dialog = new OpenFileDialog
            {
                Title = "选择提示音 · Audio 音频库", InitialDirectory = AudioLibrary.DirectoryPath,
                Filter = "音频文件 (*.wav;*.mp3)|*.wav;*.mp3", CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            app.Settings.AudioPath = AudioLibrary.Import(dialog.FileName); app.ApplySettings();
        }
        catch (Exception ex) { app.Log("音频选择失败：" + ex.Message); }
    }

    void PreviewAudio(object sender, RoutedEventArgs e) => app.PlayAudio();
    void ResetAudio(object sender, RoutedEventArgs e) { app.Settings.AudioPath = ""; app.ApplySettings(); }
    void ResetPosition(object sender, RoutedEventArgs e) { app.Settings.OrbLeft = SystemParameters.WorkArea.Left + 70; app.Settings.OrbTop = SystemParameters.WorkArea.Top + 150; app.ApplySettings(); }
    async void Demo(object sender, RoutedEventArgs e) => await app.Demonstrate();
    void Exit(object sender, RoutedEventArgs e) => app.ExitApp();
    async void Detect(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            var sources = SourceDiscovery.Initial();
            app.Log("正在检测当前用户与已运行的 WSL 发行版…");
            try { sources.AddRange(await SourceDiscovery.DetectWsl()); } catch { app.Log("WSL 检测不可用，Windows 目录仍可使用；也可手动添加。"); }
            foreach (var s in sources) if (app.Settings.Sources.All(x => x.Id != s.Id)) app.Settings.Sources.Add(s);
            app.SaveSettings(); RefreshSources(); app.RestartMonitor();
        });
    }
    void AddSource(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = SourceDiscovery.FromPath(HomeBox.Text, DistroBox.Text);
            if (!File.Exists(Path.Combine(s.Home, "config.toml"))) throw new InvalidOperationException("目录中没有 config.toml，请选择实际 Codex 配置目录。");
            var existing = app.Settings.Sources.FirstOrDefault(x => x.Id == s.Id);
            if (existing != null)
            {
                var record = JsonStore.Read<IntegrationRecord>(Integration.RecordPath(existing));
                if (record is { Status: "installed" } && existing.Distro != s.Distro) throw new InvalidOperationException("请先清除旧接入，再更改执行环境。");
                s.Id = existing.Id; app.Settings.Sources.Remove(existing);
            }
            app.Settings.Sources.Add(s); app.SaveSettings(); RefreshSources(s.Id); app.RestartMonitor();
        }
        catch (Exception ex) { app.Log(ex.Message); }
    }
    void ForgetSource(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        try
        {
            var record = JsonStore.Read<IntegrationRecord>(Integration.RecordPath(s));
            if (record != null && record.Status != "removed") throw new InvalidOperationException("请先清除该目录的接入配置，再移出列表。");
            app.Settings.Sources.Remove(s); app.SaveSettings(); RefreshSources(); app.RestartMonitor();
        }
        catch (Exception ex) { app.Log(ex.Message); }
    }
    string StageRelay() => RelayAssets.Stage(app.DataDirectory);
    async void Deploy(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        await Guard(async () =>
        {
            _ = NotifyConfig.Read(File.ReadAllText(Path.Combine(s.Home, "config.toml")));
            if (s.IsWsl) await SourceDiscovery.Run("wsl.exe", ["-d", s.Distro, "--exec", "python3", "--version"]);
            var previous = NotifyConfig.Read(File.ReadAllText(Path.Combine(s.Home, "config.toml")));
            string description = $"目标：{s.Display}\n\n仅替换根级 notify，并保存原回调与精确恢复记录。\n执行方式：{(s.IsWsl ? "python3 → Windows 转发器" : "Windows 转发器")}\n原回调：{(previous.Length == 0 ? "无" : previous[0])}\n继续原回调：{(ForwardToggle.IsChecked == true ? "是（可能同时响两次）" : "否（移除时恢复）")}\n\n配置会备份到该目录的 codex-notifier 子目录。不会改模型、权限或认证配置。\n部署后请重启 Codex 扩展使回调生效。";
            if (MessageBox.Show(this, description, "确认部署接入", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            string relay = StageRelay();
            var record = Integration.Deploy(s, relay, ForwardToggle.IsChecked == true);
            s.Id = record.SourceId; s.Enabled = true; app.SaveSettings(); app.RestartMonitor(); ShowSource();
            app.Log("接入已部署。请重启 Codex 扩展；当前只读监测可继续工作。");
        });
    }
    async void Remove(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        await Guard(() =>
        {
            if (MessageBox.Show(this, "恢复部署前的 notify，保留其他配置，并停止此目录的监测。\n备份和恢复记录会保留，便于核查。", "清除接入配置", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return Task.CompletedTask;
            if (File.Exists(Integration.RecordPath(s))) Integration.Remove(s);
            s.Enabled = false; app.SaveSettings(); app.RestartMonitor(); ShowSource(); app.Log("本目录接入已清除并停用监测。请重启 Codex 扩展，让原回调重新生效。");
            return Task.CompletedTask;
        });
    }
    async void TestBridge(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            string relay = StageRelay();
            if (Selected is { IsWsl: true } s)
            {
                string linux = "/mnt/" + char.ToLowerInvariant(relay[0]) + relay[2..].Replace('\\', '/');
                await SourceDiscovery.Run("wsl.exe", ["-d", s.Distro, "--exec", linux, "--ping"]);
            }
            else await SourceDiscovery.Run(relay, ["--ping"]);
        });
    }
    async Task Guard(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        try { await action(); }
        catch (Exception ex) { app.Log(ex.Message); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; }
    }
}
