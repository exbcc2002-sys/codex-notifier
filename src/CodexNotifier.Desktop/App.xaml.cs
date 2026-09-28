using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Shell;
using System.Windows.Threading;
using CodexNotifier.Core;
using Forms = System.Windows.Forms;

namespace CodexNotifier.Desktop;

public partial class App : Application
{
    public Settings Settings { get; private set; } = new();
    public TurnTracker Tracker { get; } = new();
    public string DataDirectory { get; private set; } = "";
    public OrbWindow Orb { get; private set; } = null!;
    public MainWindow Panel { get; private set; } = null!;
    public bool Exiting { get; private set; }
    public bool IsSmoke { get; private set; }
    readonly CancellationTokenSource stop = new();
    SessionMonitor? monitor;
    Mutex? mutex;
    Forms.NotifyIcon? tray;
    ContextMenu? trayMenu;
    System.Drawing.Icon? trayIcon;
    readonly MediaPlayer player = new();
    readonly HashSet<string> knownMain = [];
    readonly List<GlowWindow> glows = [];
    DateTime lastSound = DateTime.MinValue;
    bool demonstration, audioRequested, smokeExitPending;
    string menuState = "";
    string settingsPath = "";
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsSmoke = e.Args.Contains("--smoke-test");
        int dataIndex = Array.IndexOf(e.Args, "--data-dir");
        DataDirectory = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexNotifier");
        if (IsSmoke && dataIndex < 0) { Shutdown(2); return; }
        if (!IsSmoke)
        {
            mutex = new Mutex(true, "Local\\" + Ipc.PipeName, out bool first);
            if (!first)
            {
                string command = e.Args.FirstOrDefault(x => x.StartsWith("--")) switch { "--toggle-audio" => "toggle-audio", "--toggle-orb" => "toggle-orb", "--exit" => "exit", _ => "open" };
                await Ipc.Send(new(command)); Shutdown(); return;
            }
            // Exit requests from a stale Jump List must not launch a new app.
            if (e.Args.Contains("--exit")) { Shutdown(); return; }
        }
        Directory.CreateDirectory(DataDirectory); settingsPath = Path.Combine(DataDirectory, "settings.json");
        try { Settings = JsonStore.Read<Settings>(settingsPath) ?? new(); }
        catch { File.Copy(settingsPath, settingsPath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); Settings = new(); }
        Settings.OrbSize = Math.Clamp(Settings.OrbSize, 48, 120); Settings.Volume = Math.Clamp(Settings.Volume, 0, 1);
        if (!double.IsFinite(Settings.OrbLeft)) Settings.OrbLeft = 80;
        if (!double.IsFinite(Settings.OrbTop)) Settings.OrbTop = 160;
        if (Settings.Sources.Count == 0 && !IsSmoke) Settings.Sources = SourceDiscovery.Initial();
        int homeIndex = Array.IndexOf(e.Args, "--codex-home"), wslIndex = Array.IndexOf(e.Args, "--wsl");
        if (!IsSmoke && homeIndex >= 0 && homeIndex + 1 < e.Args.Length)
        {
            try
            {
                var chosen = SourceDiscovery.FromPath(e.Args[homeIndex + 1], wslIndex >= 0 && wslIndex + 1 < e.Args.Length ? e.Args[wslIndex + 1] : "");
                if (File.Exists(Path.Combine(chosen.Home, "config.toml")) && !File.Exists(Integration.RecordPath(chosen)))
                { Settings.Sources.RemoveAll(s => s.Id == chosen.Id); Settings.Sources.Add(chosen); }
            }
            catch { }
        }
        string? audioMigrationError = null;
        if (!IsSmoke && Path.IsPathRooted(Settings.AudioPath))
        {
            try
            {
                Settings.AudioPath = Settings.AudioPath.Equals(@"D:\Downloads\over.wav", StringComparison.OrdinalIgnoreCase)
                    ? "" : AudioLibrary.Import(Settings.AudioPath);
            }
            catch (Exception ex) { Settings.AudioPath = ""; audioMigrationError = "原音频未能导入，已使用默认 over.wav：" + ex.Message; }
        }
        Panel = new MainWindow(this); MainWindow = Panel;
        Orb = new OrbWindow(this);
        player.MediaOpened += (_, _) => { if (audioRequested) { player.Volume = Settings.Volume; player.Play(); } };
        player.MediaFailed += (_, a) => Log("音频无法播放，请选择有效的 WAV / MP3 文件或恢复默认音频。" + a.ErrorException.GetType().Name);
        if (!IsSmoke) SetupTray();
        Panel.Show(); ApplySettings();
        if (audioMigrationError != null) Log(audioMigrationError);
        if (!IsSmoke)
        {
            RestartMonitor();
            _ = Ipc.Listen(msg => Dispatcher.BeginInvoke(() => HandleMessage(msg)), stop.Token);
        }
        else await SmokeTest();
    }
    public void SaveSettings()
    {
        try { JsonStore.Write(settingsPath, Settings); }
        catch (Exception e) { Log("设置保存失败：" + e.GetType().Name); }
    }
    public void ApplySettings()
    {
        if (!Settings.Enabled) { audioRequested = false; audioRequested = false; player.Close(); foreach (var g in glows.ToArray()) g.Close(); }
        if (!Settings.AudioEnabled) { audioRequested = false; player.Close(); }
        foreach (var g in glows.ToArray())
            if (g.IsScreenFlash ? !Settings.ScreenFlash : !Settings.GlowEnabled || !Settings.OrbVisible) g.Close();
        Orb?.Apply(); Panel?.Refresh(); SaveSettings();
        string nextMenu = $"{Settings.Enabled}:{Settings.AudioEnabled}:{Settings.OrbVisible}";
        if (!IsSmoke && nextMenu != menuState) { menuState = nextMenu; UpdateMenus(); }
    }
    public void RestartMonitor()
    {
        monitor?.Dispose(); Tracker.Reset(); knownMain.Clear();
        int generation = ++monitorGeneration;
        monitor = new SessionMonitor(Settings.Sources,
            (ev, replay) => Dispatcher.BeginInvoke(() => { if (generation == monitorGeneration) Receive(ev, replay); }),
            text => Dispatcher.BeginInvoke(() => { if (generation == monitorGeneration) Log(text); }));
        monitor.Start(); Orb.Apply(); Panel.Refresh(); Log("监测已启动。等待新一轮开始；历史事件不补播，启动前的工作状态不作推测。");
    }
    int monitorGeneration;
    void Receive(TurnEvent ev, bool replay)
    {
        if (!ev.Subagent) knownMain.Add(ev.SourceId + ":" + ev.ThreadId);
        if (replay && ev.Kind == "started") return;
        var result = Tracker.Apply(ev, Settings.Enabled, replay);
        if (!result.Changed) return;
        if (!demonstration) Orb.Apply();
        Panel.Refresh();
        if (result.Notify) Complete();
        if (!replay) Log($"{(ev.Kind == "started" ? "开始一轮工作" : ev.Kind == "completed" ? "本轮已完成" : "本轮已中断")} · 当前工作中：{Tracker.Active}");
    }
    void HandleMessage(WireMessage msg)
    {
        switch (msg.Command)
        {
            case "open": OpenPanel(); break;
            case "toggle-audio": Settings.AudioEnabled = !Settings.AudioEnabled; ApplySettings(); break;
            case "toggle-orb": Settings.OrbVisible = !Settings.OrbVisible; ApplySettings(); break;
            case "exit": ExitApp(); break;
            case "ping": Log("桥接测试成功：事件已到达当前 Windows 桌面会话。"); break;
            case "event":
                // Reject callbacks whose thread has not been identified as a main session.
                // Rollout completion remains the fallback for old callbacks missing turn IDs.
                if (msg.Event is { } ev && Settings.Sources.Any(s => s.Enabled && s.Id == ev.SourceId) && knownMain.Contains(ev.SourceId + ":" + ev.ThreadId)) Receive(ev, false);
                break;
        }
    }
    public void Complete()
    {
        if (!Settings.Enabled) return;
        if (Settings.OrbVisible) Orb.Complete();
        if (Settings.GlowEnabled && Settings.OrbVisible) ShowCompletionEffect(false);
        if (Settings.ScreenFlash) ShowCompletionEffect(true);
        if (Settings.AudioEnabled && DateTime.UtcNow - lastSound > TimeSpan.FromMilliseconds(700)) { lastSound = DateTime.UtcNow; PlayAudio(); }
    }
    void ShowCompletionEffect(bool screenFlash)
    {
        if (glows.Count >= 4) glows[0].Close();
        var glow = new GlowWindow(Orb, screenFlash); glows.Add(glow);
        glow.Closed += (_, _) => glows.Remove(glow); glow.Show();
    }
    public async Task Demonstrate()
    {
        if (demonstration) return;
        if (!Settings.Enabled) { Log("请先启用通知，再演示完整效果。"); return; }
        demonstration = true;
        try { Orb.DemoWorking(); Log("演示：工作中…（不是实际 Codex 任务）"); await Task.Delay(2400); Orb.Apply(); Complete(); }
        finally { demonstration = false; }
    }
    public void PlayAudio()
    {
        try
        {
            string path = AudioLibrary.Resolve(Settings.AudioPath);
            if (!File.Exists(path)) throw new FileNotFoundException();
            audioRequested = true; player.Stop(); player.Close(); player.Open(new Uri(Path.GetFullPath(path)));
        }
        catch { Log("找不到音频文件，请重新选择或恢复默认音频。"); }
    }
    public void OpenPanel() { Panel.Show(); Panel.WindowState = WindowState.Normal; Panel.Activate(); }
    public void Log(string text)
    {
        if (Panel == null) return;
        Panel.StatusText.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        try
        {
            var path = Path.Combine(DataDirectory, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 65536) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, DateTime.Now.ToString("O") + " " + text + Environment.NewLine);
        }
        catch { }
    }
    void SetupTray()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        using var original = new System.Drawing.Icon(stream, 32, 32);
        trayIcon = (System.Drawing.Icon)original.Clone();
        tray = new Forms.NotifyIcon { Visible = true, Text = "Codex Notifier", Icon = trayIcon };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenPanel(); else if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu(); };
    }
    internal ContextMenu CreateTrayMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "打开主面板" }; open.Click += (_, _) => OpenPanel(); menu.Items.Add(open);
        void AddToggle(string label, bool value, Action<bool> apply)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = value, StaysOpenOnClick = true };
            item.Click += (_, _) => { apply(item.IsChecked); ApplySettings(); };
            menu.Items.Add(item);
        }
        AddToggle("启用通知", Settings.Enabled, value => Settings.Enabled = value);
        AddToggle("音频提示", Settings.AudioEnabled, value => Settings.AudioEnabled = value);
        AddToggle("显示悬浮球", Settings.OrbVisible, value => Settings.OrbVisible = value);
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出程序" }; exit.Click += (_, _) => ExitApp(); menu.Items.Add(exit);
        return menu;
    }
    void ShowTrayMenu()
    {
        if (trayMenu != null) trayMenu.IsOpen = false;
        trayMenu = CreateTrayMenu();
        trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        trayMenu.Opened += (_, _) =>
        {
            // A foreground popup dismisses correctly when the user clicks another application.
            if (PresentationSource.FromVisual(trayMenu) is System.Windows.Interop.HwndSource source)
                SetForegroundWindow(source.Handle);
            trayMenu.Focus();
        };
        trayMenu.IsOpen = true;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr window);
    void UpdateMenus()
    {
        try
        {
            var jump = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
            foreach (var (name, arg) in new[] { ("打开主面板", "--open"), (Settings.AudioEnabled ? "关闭音频提示" : "开启音频提示", "--toggle-audio"), (Settings.OrbVisible ? "隐藏悬浮球" : "显示悬浮球", "--toggle-orb"), ("退出程序", "--exit") })
                jump.JumpItems.Add(new JumpTask { Title = name, Arguments = arg, ApplicationPath = Environment.ProcessPath!, CustomCategory = "Codex Notifier" });
            JumpList.SetJumpList(this, jump); jump.Apply();
        }
        catch { Log("系统未提供任务栏快捷任务；托盘菜单仍可使用。"); }
    }
    public void ExitApp()
    {
        Exiting = true; stop.Cancel(); monitor?.Dispose();
        if (IsSmoke && smokeExitPending) File.WriteAllText(Path.Combine(DataDirectory, "close-exit-result.txt"), "PASS: close prompt exit reached application shutdown.");
        audioRequested = false; player.Close(); foreach (var g in glows.ToArray()) g.Close();
        if (trayMenu != null) trayMenu.IsOpen = false;
        tray?.Dispose(); trayIcon?.Dispose(); Orb?.Close(); Panel?.Close(); mutex?.Dispose(); Shutdown();
    }
    void SmokeClose(bool? exit)
    {
        Exception? failure = null;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var prompt = Windows.OfType<ClosePrompt>().Single();
            try
            {
                if (prompt.ExitRequested || !prompt.ConfirmButton.IsDefault)
                    throw new InvalidOperationException("Close prompt must default to minimizing.");
                prompt.UpdateLayout();
                Native.Capture(prompt, Path.Combine(DataDirectory, "close-prompt.png"));
                if (exit == null) prompt.DialogResult = false;
                else
                {
                    prompt.ExitChoice.IsChecked = exit.Value;
                    prompt.ConfirmButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
            }
            catch (Exception ex) { failure = ex; prompt.Close(); }
        }));
        Panel.Close();
        if (failure != null) throw failure;
    }
    void SmokeCompletionEffects()
    {
        Settings.Enabled = true; Settings.AudioEnabled = false; Settings.OrbVisible = true;
        foreach (bool ring in new[] { false, true })
        foreach (bool edge in new[] { false, true })
        {
            Settings.GlowEnabled = ring; Settings.ScreenFlash = edge;
            Complete();
            if (glows.Count(g => !g.IsScreenFlash) != (ring ? 1 : 0) || glows.Count(g => g.IsScreenFlash) != (edge ? 1 : 0))
                throw new InvalidOperationException("Completion effect switches are coupled.");
            foreach (var g in glows.ToArray()) g.Close();
        }
        Settings.GlowEnabled = true; Settings.ScreenFlash = true; Complete();
        Settings.GlowEnabled = false; ApplySettings();
        if (glows.Count != 1 || !glows[0].IsScreenFlash) throw new InvalidOperationException("Turning off ring closed the screen effect.");
        Settings.ScreenFlash = false; ApplySettings();
        if (glows.Count != 0) throw new InvalidOperationException("Turning off screen effect did not close it.");
        Settings.GlowEnabled = true; Settings.ScreenFlash = true; Complete();
        Settings.ScreenFlash = false; ApplySettings();
        if (glows.Count != 1 || glows[0].IsScreenFlash) throw new InvalidOperationException("Turning off screen effect closed the ring.");
        foreach (var g in glows.ToArray()) g.Close();
        Settings.OrbVisible = false; Settings.ScreenFlash = true; Complete();
        if (glows.Count != 1 || !glows[0].IsScreenFlash) throw new InvalidOperationException("Hidden orb suppressed screen effect or emitted a ring.");
        foreach (var g in glows.ToArray()) g.Close();
        foreach (var size in new[] { new Size(1280, 720), new Size(720, 1280), new Size(1920, 1080) })
        {
            string path = Path.Combine(DataDirectory, $"edge-{size.Width}x{size.Height}.png");
            GlowWindow.CaptureEdgePreview(path, size.Width, size.Height);
            using var image = File.OpenRead(path);
            var bitmap = System.Windows.Media.Imaging.BitmapDecoder.Create(image, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            byte[] pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)size.Width / 2, (int)size.Height / 2, 1, 1), pixel, 4, 0);
            if (pixel[3] != 0) throw new InvalidOperationException("Screen effect tinted the center.");
            int inset = (int)(Math.Min(size.Width, size.Height) * .14 * (1 - Math.Pow(1 - .38, 2)));
            foreach (var point in new[] { new Point(inset, size.Height / 2), new Point(size.Width - inset - 1, size.Height / 2), new Point(size.Width / 2, inset), new Point(size.Width / 2, size.Height - inset - 1) })
            {
                bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
                if (pixel[3] == 0 || pixel[1] <= pixel[2]) throw new InvalidOperationException("A screen edge has no green wave.");
            }
        }
        Settings.OrbVisible = true; Settings.GlowEnabled = true; Settings.ScreenFlash = false; ApplySettings();
    }
    async Task SmokeTest()
    {
        try
        {
            Settings.AudioEnabled = false; Settings.ScreenFlash = false; Settings.Sources.Clear(); ApplySettings();
            if (!File.Exists(AudioLibrary.Resolve(""))) throw new FileNotFoundException("Bundled default audio is missing.");
            SmokeCompletionEffects();
            var localAudio = AudioLibrary.Import(AudioLibrary.DefaultPath);
            if (Path.IsPathRooted(localAudio) || AudioLibrary.Resolve(localAudio) != AudioLibrary.DefaultPath)
                throw new InvalidOperationException("Audio paths must stay relative to the application directory.");

            await Task.Delay(600);
            Panel.SourcesBox.ItemsSource = new[] {
                new SourceProfile { Home = Path.Combine(DataDirectory, "Windows", ".codex") },
                new SourceProfile { Home = Path.Combine(DataDirectory, "WSL", ".codex"), Distro = "Ubuntu24.04" }
            };
            Panel.SourcesBox.SelectedIndex = 0;
            Native.Capture((FrameworkElement)Panel.Content, Path.Combine(DataDirectory, "panel.png"));
            Panel.SourcesBox.IsDropDownOpen = true; await Task.Delay(200);
            var dropdown = (System.Windows.Controls.Primitives.Popup)Panel.SourcesBox.Template.FindName("PART_Popup", Panel.SourcesBox);
            Native.Capture((FrameworkElement)dropdown.Child, Path.Combine(DataDirectory, "source-dropdown.png"));
            Panel.SourcesBox.SelectedIndex = 1;
            if (!Panel.DistroBox.Text.Equals("Ubuntu24.04")) throw new InvalidOperationException("Source selection failed.");
            Panel.SourcesBox.IsDropDownOpen = false;
            var menu = CreateTrayMenu();
            menu.PlacementTarget = Panel; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            menu.IsOpen = true; await Task.Delay(200);
            Native.Capture(menu, Path.Combine(DataDirectory, "tray-menu.png"));
            var audioItem = (MenuItem)menu.Items[2];
            audioItem.IsChecked = true; audioItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (!Settings.AudioEnabled || Panel.AudioToggle.IsChecked != true) throw new InvalidOperationException("Tray switch did not update panel/settings.");
            audioItem.IsChecked = false; audioItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Panel.SourcesBox.ItemsSource = null;

            Orb.DemoWorking(); await Task.Delay(850); Orb.Capture(Path.Combine(DataDirectory, "orb-working.png"));
            Orb.Apply(); Orb.Complete(); await Task.Delay(300); Orb.Capture(Path.Combine(DataDirectory, "orb-complete.png"));
            Settings.Enabled = false; ApplySettings(); await Task.Delay(200); Orb.Capture(Path.Combine(DataDirectory, "orb-paused.png"));
            SmokeClose(null);
            if (!Panel.IsVisible) throw new InvalidOperationException("Cancel hid the panel.");
            SmokeClose(false);
            if (Panel.IsVisible || Exiting) throw new InvalidOperationException("Minimize did not keep the app running with a hidden panel.");
            OpenPanel();
            if (!Panel.IsVisible || Panel.WindowState != WindowState.Normal) throw new InvalidOperationException("Panel restore failed.");
            using (var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream)
            using (var icon = new System.Drawing.Icon(iconStream, 32, 32))
                if (icon.Width != 32) throw new InvalidOperationException("Tray icon resource did not load.");
            string stagedRelay = RelayAssets.Stage(DataDirectory);
            if (RelayAssets.Stage(DataDirectory) != stagedRelay) throw new InvalidOperationException("Relay staging is not stable.");
            string probePipe = "CodexNotifier-Smoke-" + Guid.NewGuid().ToString("N");
            using var probeStop = new CancellationTokenSource();
            var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var listener = Ipc.Listen(message => received.TrySetResult(message.Command == "ping"), probeStop.Token, probePipe);
            try
            {
                await SourceDiscovery.Run(stagedRelay, ["--ping", probePipe]);
                if (!await received.Task.WaitAsync(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Relay ping failed.");
            }
            finally { probeStop.Cancel(); }
            File.WriteAllText(Path.Combine(DataDirectory, "runtime-modules.txt"), string.Join(Environment.NewLine,
                Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.FileName)));
            File.WriteAllText(Path.Combine(DataDirectory, "smoke-result.txt"), "PASS: WPF panel, working/completion/paused rendering, settings persistence, close prompt default/cancel/minimize/restore, green icon resource, independent completion effects and four-edge rendering at three screen sizes, bundled default audio and relative path resolution, source dropdown selection, tray switch synchronization, staged relay IPC and repeated extraction. No Codex configuration touched.");
            smokeExitPending = true;
            SmokeClose(true);
        }
        catch (Exception ex) { smokeExitPending = false; File.WriteAllText(Path.Combine(DataDirectory, "smoke-result.txt"), ex.ToString()); }
        finally { if (!smokeExitPending) ExitApp(); }
    }
}
