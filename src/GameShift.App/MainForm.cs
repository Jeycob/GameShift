using GameShift.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;

namespace GameShift.App;

internal sealed class MainForm : Form
{
    private readonly bool preview;
    private readonly bool startMinimized;
    private readonly string folder = ControlFiles.DefaultDirectory;
    private readonly SwitchPolicy policy = new();
    private readonly PriorityController priorities = new();
    private readonly SemaphoreSlim cycle = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HttpClient loadHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ModelResumeCoordinator resume;
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "GameShift", Visible = false };
    private readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(12), Font = new Font("Segoe UI", 11), BackColor = Color.FromArgb(30, 48, 62) };
    private readonly Label modeLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), Text = "AI / game switch" };
    private readonly TextBox endpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox keyName = new() { Dock = DockStyle.Fill };
    private readonly Label unload = new() { AutoSize = true, Text = "Gaming always unloads model RAM + VRAM; waits for a safe boundary.", Padding = new Padding(0, 6, 0, 0) };
    private readonly CheckBox autoStart = new() { AutoSize = true, Text = "Start at sign-in, minimized to the notification area" };
    private readonly NumericUpDown delay = new() { Minimum = 5, Maximum = 600, Value = 30, Width = 90 };
    private readonly ListBox games = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly ListBox aiApps = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), BackColor = Color.FromArgb(22, 32, 43), ForeColor = Color.FromArgb(224, 234, 242) };
    private Settings settings = new();
    private GateCommand? command;
    private bool exiting;
    private string? startupWarning;
    private string CommandPath => Path.Combine(folder, "control.json");
    private string AckPath => Path.Combine(folder, "harness-ack.json");
    private string SettingsPath => Path.Combine(folder, "settings.json");

    public MainForm(bool preview, bool minimized = false)
    {
        resume = new((configuration, cancellation) => new StrataClient(loadHttp).EnsureLoadedAsync(configuration, cancellation));
        this.preview = preview;
        startMinimized = minimized;
        Text = preview ? "GameShift — UI preview (no changes)" : "GameShift";
        ClientSize = new Size(910, 700);
        MinimumSize = new Size(800, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(17, 26, 36);
        ForeColor = Color.FromArgb(230, 238, 244);
        BuildUi();
        if (!preview) LoadState();
        DisplaySettings();
        timer.Tick += async (_, _) => await RunCycleAsync();
        Shown += async (_, _) =>
        {
            if (preview) { SetStatus("Preview only — no processes, registry, control files or model APIs are changed."); return; }
            tray.Visible = true;
            if (startMinimized) Hide();
            if (startupWarning != null) AddLog(startupWarning);
            timer.Start();
            await RunCycleAsync();
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
            { exiting = true; timer.Stop(); lifetime.Cancel(); return; }
            if (!exiting && !preview) { e.Cancel = true; Hide(); }
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        root.RowStyles.Add(new(SizeType.Absolute, 40));
        root.RowStyles.Add(new(SizeType.Absolute, 50));
        root.RowStyles.Add(new(SizeType.Absolute, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 114));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 46));
        root.RowStyles.Add(new(SizeType.Absolute, 42));
        root.RowStyles.Add(new(SizeType.Absolute, 102));
        Controls.Add(root);
        root.Controls.Add(modeLabel, 0, 0);
        var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        foreach (var (label, mode) in new[] { ("AI — resume", SwitchMode.Ai), ("Background", SwitchMode.Background), ("Gaming — pause AI", SwitchMode.Gaming), ("Automatic", SwitchMode.Automatic) })
        {
            var button = Button(label, async () => await ChangeModeAsync(mode));
            button.Width = 185;
            modes.Controls.Add(button);
        }
        root.Controls.Add(modes, 0, 1);
        root.Controls.Add(status, 0, 2);
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(0, 8, 0, 0) };
        options.ColumnStyles.Add(new(SizeType.Absolute, 175)); options.ColumnStyles.Add(new(SizeType.Percent, 100)); options.ColumnStyles.Add(new(SizeType.Absolute, 145));
        options.Controls.Add(new Label { Text = "Native Strata URL", AutoSize = true }, 0, 0);
        options.Controls.Add(endpoint, 1, 0);
        options.Controls.Add(Button("Test connection", ProbeAsync), 2, 0);
        options.Controls.Add(new Label { Text = "API key variable name", AutoSize = true }, 0, 1);
        options.Controls.Add(keyName, 1, 1);
        options.Controls.Add(new Label { Text = "Not the secret itself", AutoSize = true, ForeColor = Color.LightSteelBlue }, 2, 1);
        options.Controls.Add(new Label { Text = "Resume after game (s)", AutoSize = true }, 0, 2);
        options.Controls.Add(delay, 1, 2);
        root.Controls.Add(options, 0, 3);
        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        paths.ColumnStyles.Add(new(SizeType.Percent, 50)); paths.ColumnStyles.Add(new(SizeType.Percent, 50));
        paths.Controls.Add(PathPanel("Games — exact executable paths", games), 0, 0);
        paths.Controls.Add(PathPanel("AI CPU priority — optional exact paths", aiApps), 1, 0);
        root.Controls.Add(paths, 0, 4);
        var apply = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        apply.Controls.Add(unload); apply.Controls.Add(Button("Save settings", SaveAsync));
        root.Controls.Add(apply, 0, 5);
        var startup = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        startup.Controls.Add(autoStart);
        startup.Controls.Add(Button("Apply sign-in option", SetAutostartAsync));
        root.Controls.Add(startup, 0, 6);
        root.Controls.Add(log, 0, 7);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open GameShift", null, (_, _) => ShowMain());
        foreach (var mode in Enum.GetValues<SwitchMode>()) menu.Items.Add(mode.ToString(), null, async (_, _) => await SafeAsync(() => ChangeModeAsync(mode)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (keep any AI hold)", null, async (_, _) => await ExitAsync());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowMain();
    }

    private Button Button(string text, Func<Task> action)
    {
        var result = new Button { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(34, 65, 86), ForeColor = Color.White, Margin = new Padding(0, 0, 8, 4) };
        result.Click += async (_, _) => await SafeAsync(action);
        return result;
    }

    private Control PathPanel(string title, ListBox list)
    {
        list.BackColor = Color.FromArgb(28, 38, 49); list.ForeColor = ForeColor;
        var group = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 4, 8, 0) };
        group.RowStyles.Add(new(SizeType.Absolute, 28)); group.RowStyles.Add(new(SizeType.Percent, 100)); group.RowStyles.Add(new(SizeType.Absolute, 38));
        group.Controls.Add(new Label { Text = title, AutoSize = true }, 0, 0); group.Controls.Add(list, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        buttons.Controls.Add(Button("Add .exe…", () =>
        {
            using var dialog = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK && !list.Items.Cast<string>().Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase)) list.Items.Add(dialog.FileName);
            return Task.CompletedTask;
        }));
        buttons.Controls.Add(Button("Remove", () => { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); return Task.CompletedTask; }));
        group.Controls.Add(buttons, 0, 2);
        return group;
    }

    private void LoadState()
    {
        try { settings = ControlFiles.Read<Settings>(SettingsPath) ?? new Settings(); settings.Validate(); }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException or NullReferenceException)
        { settings = new Settings { Mode = SwitchMode.Gaming }; startupWarning = "Settings could not be loaded. Keeping AI paused until you explicitly choose a mode. " + e.Message; }
        try
        {
            command = ControlFiles.Read<GateCommand>(CommandPath);
            if (command != null && (command.Version != 1 || string.IsNullOrWhiteSpace(command.CommandId))) throw new InvalidDataException("Invalid gate command.");
            if (command?.Blocked == true)
            {
                if (settings.Mode == SwitchMode.Automatic && settings.GamePaths.Length > 0)
                {
                    policy.RestartCooldown(DateTimeOffset.UtcNow);
                    startupWarning = "Automatic mode restored. Existing hold stays until games exit, cooldown expires and Strata is ready.";
                }
                else { settings = settings with { Mode = SwitchMode.Gaming }; startupWarning = "An existing AI hold was preserved. Choose AI or Automatic explicitly when ready."; }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { settings = settings with { Mode = SwitchMode.Gaming }; command = null; startupWarning = "Unreadable gate file: startup is fail-closed. " + e.Message; }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            autoStart.Checked = key?.GetValue("GameShift") is string;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { startupWarning = "Sign-in setting could not be read: " + e.Message; }
    }

    private void DisplaySettings()
    {
        endpoint.Text = settings.StrataUrl; keyName.Text = settings.ApiKeyEnvironmentVariable;
        delay.Value = settings.ResumeDelaySeconds;
        games.Items.AddRange(settings.GamePaths); aiApps.Items.AddRange(settings.AiProcessPaths);
        modeLabel.Text = "GameShift  ·  " + settings.Mode;
    }

    private Settings ReadUi(SwitchMode? mode = null)
    {
        var next = settings with { Mode = mode ?? settings.Mode, StrataUrl = endpoint.Text.Trim(), ApiKeyEnvironmentVariable = keyName.Text.Trim(), ResumeDelaySeconds = (int)delay.Value, GamePaths = games.Items.Cast<string>().ToArray(), AiProcessPaths = aiApps.Items.Cast<string>().ToArray() };
        next.Validate(); return next;
    }

    private async Task ChangeModeAsync(SwitchMode mode)
    {
        if (preview) { SetStatus("UI preview: " + mode + ". No action was taken."); return; }
        var next = ReadUi(mode);
        if (mode == SwitchMode.Automatic && next.GamePaths.Length == 0) throw new ArgumentException("Choose at least one game executable before enabling Automatic.");
        await cycle.WaitAsync(lifetime.Token);
        try { settings = next; ControlFiles.Write(SettingsPath, settings); modeLabel.Text = "GameShift  ·  " + settings.Mode; AddLog("Selected " + mode + "."); }
        finally { cycle.Release(); }
        await RunCycleAsync();
    }

    private async Task SaveAsync()
    {
        if (preview) { SetStatus("Preview: settings were not saved."); return; }
        var next = ReadUi();
        await cycle.WaitAsync(lifetime.Token);
        try { settings = next; ControlFiles.Write(SettingsPath, settings); AddLog("Settings saved. Gaming always unloads RAM + VRAM. Exact executable paths only."); }
        finally { cycle.Release(); }
        await RunCycleAsync();
    }

    private async Task RunCycleAsync()
    {
        if (preview || exiting || !await cycle.WaitAsync(0)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var game = settings.Mode == SwitchMode.Automatic && PriorityController.GameRunning(settings.GamePaths);
            var state = policy.Evaluate(settings.Mode, game, now, settings.ResumeDelaySeconds);
            var blocked = state == WorkState.Gaming;
            if (blocked) resume.Hold();
            var failures = priorities.Apply(settings.AiProcessPaths, state != WorkState.Ai);
            var priorityNote = failures > 0 ? " Some selected processes could not be inspected/adjusted; no admin escalation was attempted." : "";
            if (!blocked && command?.Blocked == true)
            {
                var prepared = resume.Poll(settings, now, lifetime.Token);
                if (!prepared.Ready) { SetStatus("RESUMING: " + prepared.Message + priorityNote); return; }
                if (settings.Mode == SwitchMode.Automatic && PriorityController.GameRunning(settings.GamePaths))
                {
                    policy.RestartCooldown(DateTimeOffset.UtcNow);
                    resume.Hold();
                    SetStatus("GAMING: a game restarted while Strata was loading. Harness stays held; unloading will retry.");
                    return;
                }
                PublishCommand(false, state, DateTimeOffset.UtcNow);
                AddLog("Strata readiness confirmed. Original waiting Harness requests released; no new task/session was created.");
            }
            if (command is null || command.Blocked != blocked) PublishCommand(blocked, state, now);
            if (!blocked)
            {
                SetStatus((state == WorkState.Background ? "BACKGROUND: selected AI CPU processes use BelowNormal priority. This does NOT release VRAM or limit GPU usage." : "AI: original waiting Harness requests are allowed to continue; previous CPU priorities are restored where possible.") + priorityNote);
                return;
            }
            var hold = command ?? throw new InvalidOperationException("The Harness hold could not be published.");
            var ack = ControlFiles.Read<GateAck>(AckPath);
            if (!ControlFiles.CanUnload(hold, ack, now))
            {
                var active = ack?.CommandId == hold.CommandId && now - ack.ObservedAt <= TimeSpan.FromSeconds(8) ? $" Active Harness streams: {ack.ActiveStreams}." : " No fresh matching Harness acknowledgement. Install/start the gaming-gate plugin.";
                SetStatus("GAMING: RAM + VRAM release pending." + active + " Waiting; model memory is NOT confirmed released." + priorityNote);
                return;
            }
            // Recheck on every cycle, not just once: another client may have
            // reloaded the model. Never display a cached successful unload.
            var result = await new StrataClient(http).EnsureUnloadedAsync(settings, lifetime.Token);
            SetStatus("GAMING: " + result.Message + " Harness hold stays active; other clients must remain idle." + priorityNote);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        { SetStatus("Not confirmed: " + e.Message + " No forced pause or termination was performed. Any existing Harness hold remains in place."); AddLog("Cycle needs attention: " + e.GetType().Name); }
        finally { cycle.Release(); }
    }

    private void PublishCommand(bool blocked, WorkState state, DateTimeOffset now)
    {
        var next = new GateCommand(1, Guid.NewGuid().ToString("N"), blocked, blocked ? "gaming" : state.ToString().ToLowerInvariant(), now);
        ControlFiles.Write(CommandPath, next);
        command = next;
        AddLog(blocked ? "Harness hold requested. Existing output is allowed to finish." : "Harness hold released.");
    }

    private async Task ProbeAsync()
    {
        if (preview) { SetStatus("Preview: no network request was sent."); return; }
        var next = ReadUi();
        var cancellation = lifetime.Token;
        var message = await new StrataClient(http).ProbeAsync(next, cancellation);
        if (!exiting && !IsDisposed && !cancellation.IsCancellationRequested) SetStatus(message);
    }

    private Task SetAutostartAsync()
    {
        if (preview) { SetStatus("Preview: sign-in setting was not changed."); return Task.CompletedTask; }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (autoStart.Checked)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application path is unavailable.");
            if (!Path.GetFileName(executable).Equals("GameShift.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use the published GameShift.exe before enabling sign-in startup.");
            key.SetValue("GameShift", "\"" + executable + "\" --minimized", RegistryValueKind.String);
        }
        else key.DeleteValue("GameShift", false);
        AddLog(autoStart.Checked ? "Current-user sign-in startup enabled (no administrator rights)." : "GameShift sign-in startup removed.");
        return Task.CompletedTask;
    }

    private void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void SetStatus(string text) { if (!IsDisposed) status.Text = text; }
    private void AddLog(string text)
    {
        if (IsDisposed) return;
        var lines = log.Lines.TakeLast(80).Append(DateTime.Now.ToString("HH:mm:ss") + "  " + text).ToArray();
        log.Lines = lines; log.SelectionStart = log.TextLength; log.ScrollToCaret();
    }
    private async Task SafeAsync(Func<Task> action)
    {
        try { if (!exiting) await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { SetStatus(e.Message); AddLog("Action was not completed: " + e.GetType().Name); }
    }
    private async Task ExitAsync()
    {
        if (command?.Blocked == true && MessageBox.Show(this, "AI is held for gaming. Exit and KEEP this hold? Resume later by opening GameShift and choosing AI. CPU priority changes will be restored.", "Keep AI paused?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        exiting = true; timer.Stop(); lifetime.Cancel();
        await cycle.WaitAsync();
        try { priorities.Dispose(); tray.Visible = false; Close(); }
        finally { cycle.Release(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { exiting = true; lifetime.Cancel(); resume.Dispose(); timer.Dispose(); tray.Dispose(); priorities.Dispose(); http.Dispose(); loadHttp.Dispose(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
