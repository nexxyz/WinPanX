using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using WinPanX.Agent.Audio;
using WinPanX.Agent.Configuration;
using WinPanX.Agent.Runtime;
using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Tray;

internal sealed class WinPanXTrayContext : ApplicationContext
{
    private const string StartupRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "WinPanX";

    private readonly string _configPath;
    private readonly string _manualPath;
    private readonly string _logPath;
    private readonly Icon? _ownedTrayIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly SemaphoreSlim _runtimeControl = new(1, 1);
    private readonly SynchronizationContext _uiContext;
    private CancellationTokenSource? _runtimeCts;
    private Task? _runtimeTask;
    private bool _routingEnabled;
    private ToolStripMenuItem? _routingToggleMenuItem;
    private ToolStripMenuItem? _startupToggleMenuItem;
    private int _shutdownStarted;

    public WinPanXTrayContext(string configPath, string manualPath, string logPath)
    {
        _configPath = configPath;
        _manualPath = manualPath;
        _logPath = logPath;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_uiContext);

        _ownedTrayIcon = TryLoadTrayIcon();
        _notifyIcon = new NotifyIcon
        {
            Text = "WinPanX",
            Icon = _ownedTrayIcon ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;

        StartRuntime();
        UpdateStartupToggleVisualState();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _routingToggleMenuItem = new ToolStripMenuItem("Routing enabled")
        {
            Checked = true,
            CheckOnClick = false
        };
        _routingToggleMenuItem.Click += (_, _) => _ = ToggleRoutingAsync();

        _startupToggleMenuItem = new ToolStripMenuItem("Run on startup")
        {
            Checked = IsRunOnStartupEnabled(),
            CheckOnClick = false
        };
        _startupToggleMenuItem.Click += (_, _) => ToggleRunOnStartup();

        menu.Items.Add(_routingToggleMenuItem);
        menu.Items.Add(_startupToggleMenuItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Open manual", null, (_, _) => OpenFile(_manualPath));
        menu.Items.Add("Open config", null, (_, _) => OpenFile(_configPath));
        menu.Items.Add("Open log", null, (_, _) => OpenFile(_logPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _ = ExitAsync());
        return menu;
    }

    private void StartRuntime()
    {
        _runtimeCts?.Dispose();
        _runtimeCts = new CancellationTokenSource();
        _runtimeTask = Task.Run(() => RunRuntimeAsync(_runtimeCts.Token), CancellationToken.None);
        _routingEnabled = true;
        UpdateRoutingToggleVisualState();
    }

    private async Task StopRuntimeAsync()
    {
        if (_runtimeTask is null)
        {
            _runtimeCts?.Dispose();
            _runtimeCts = null;
            _routingEnabled = false;
            UpdateRoutingToggleVisualState();
            return;
        }

        _runtimeCts?.Cancel();
        try
        {
            await _runtimeTask;
        }
        catch (Exception ex)
        {
            SimpleLog.Warn($"Runtime task ended with exception during stop: {ex.Message}");
        }

        _runtimeTask = null;
        _runtimeCts?.Dispose();
        _runtimeCts = null;
        _routingEnabled = false;
        UpdateRoutingToggleVisualState();
    }

    private async Task ToggleRoutingAsync()
    {
        if (Volatile.Read(ref _shutdownStarted) == 1)
        {
            return;
        }

        await _runtimeControl.WaitAsync();
        try
        {
            if (_routingEnabled)
            {
                await StopRuntimeAsync();
                SimpleLog.Info("Routing disabled by user.");
            }
            else
            {
                StartRuntime();
                SimpleLog.Info("Routing enabled by user.");
            }
        }
        finally
        {
            _runtimeControl.Release();
        }
    }

    private async Task RunRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = WinPanXConfigLoader.LoadOrCreateDefault(_configPath);
            IAppTracker appTracker = new CoreAudioAppTracker(config);
            var panController = new SessionChannelPanController();

            await using var runtime = new RuntimeCoordinator(
                config,
                appTracker,
                panController);

            SimpleLog.Info("Starting WinPanX.");
            await runtime.RunAsync(cancellationToken);
            SimpleLog.Info("Runtime loop completed.");
        }
        catch (OperationCanceledException)
        {
            SimpleLog.Info("Shutdown requested.");
        }
        catch (Exception ex)
        {
            SimpleLog.Error($"Fatal runtime error: {ex}");
            _uiContext.Post(_ =>
            {
                _routingEnabled = false;
                UpdateRoutingToggleVisualState();

                if (Volatile.Read(ref _shutdownStarted) == 0)
                {
                    try
                    {
                        _notifyIcon.ShowBalloonTip(
                            4000,
                            "WinPanX",
                            "Runtime failed to start. Use 'Open log' from the tray menu.",
                            ToolTipIcon.Error);
                    }
                    catch
                    {
                    }
                }
            }, null);
        }
    }

    private async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1)
        {
            return;
        }

        _notifyIcon.Visible = false;

        await _runtimeControl.WaitAsync();
        try
        {
            await StopRuntimeAsync();
        }
        finally
        {
            _runtimeControl.Release();
        }

        _runtimeControl.Dispose();
        ExitThread();
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            SimpleLog.Warn($"Cannot open missing file: {path}");
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "open"
            });
            return;
        }
        catch (Exception ex)
        {
            SimpleLog.Warn($"Shell open failed for '{path}': {ex.Message}. Trying Open With dialog.");
        }

        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            if (TryOpenWithNotepad(path))
            {
                return;
            }
        }

        if (TryOpenWithDialog(path))
        {
            return;
        }

        SimpleLog.Warn($"SHOpenWithDialog failed for '{path}'.");
    }

    private static bool TryOpenWithNotepad(string path)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            SimpleLog.Warn($"Notepad fallback failed for '{path}': {ex.Message}");
            return false;
        }
    }

    private static bool TryOpenWithDialog(string path)
    {
        var info = new OpenAsInfo
        {
            File = path,
            Class = null,
            Flags = OpenAsInfoFlags.OaifExec
        };

        var hr = SHOpenWithDialog(IntPtr.Zero, ref info);
        if (hr == 0)
        {
            return true;
        }

        SimpleLog.Warn($"SHOpenWithDialog returned 0x{hr:X8} for '{path}'.");
        return false;
    }

    private void ToggleRunOnStartup()
    {
        try
        {
            var currentlyEnabled = IsRunOnStartupEnabled();
            SetRunOnStartup(!currentlyEnabled);
            SimpleLog.Info(!currentlyEnabled ? "Run on startup enabled." : "Run on startup disabled.");
        }
        catch (Exception ex)
        {
            SimpleLog.Warn($"Failed to toggle run on startup: {ex.Message}");
        }

        UpdateStartupToggleVisualState();
    }

    private static bool IsRunOnStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRunKeyPath, writable: false);
        var value = key?.GetValue(StartupValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    private void SetRunOnStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupRunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Failed to open startup registry key.");

        if (enabled)
        {
            key.SetValue(StartupValueName, BuildStartupCommand(), RegistryValueKind.String);
            return;
        }

        key.DeleteValue(StartupValueName, throwOnMissingValue: false);
    }

    private string BuildStartupCommand()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "WinPanX.Agent.exe");
        if (!File.Exists(exePath))
        {
            exePath = Environment.ProcessPath ?? exePath;
        }

        return $"{QuoteArg(exePath)} {QuoteArg(_configPath)}";
    }

    private static string QuoteArg(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private void UpdateRoutingToggleVisualState()
    {
        if (_routingToggleMenuItem is null)
        {
            return;
        }

        _routingToggleMenuItem.Checked = _routingEnabled;
        _routingToggleMenuItem.Text = _routingEnabled ? "Routing enabled" : "Routing disabled";
    }

    private void UpdateStartupToggleVisualState()
    {
        if (_startupToggleMenuItem is null)
        {
            return;
        }

        _startupToggleMenuItem.Checked = IsRunOnStartupEnabled();
    }

    private static Icon? TryLoadTrayIcon()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Assets", "WinPanX.ico"),
            Path.Combine(baseDir, "WinPanX.ico")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return new Icon(candidate);
                }
            }
            catch (Exception ex)
            {
                SimpleLog.Warn($"Failed to load tray icon '{candidate}': {ex.Message}");
            }
        }

        SimpleLog.Warn("Custom tray icon not found in output; using default system icon.");
        return null;
    }

    private void OnNotifyIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        try
        {
            _notifyIcon.ContextMenuStrip?.Show(Cursor.Position);
        }
        catch (Exception ex)
        {
            SimpleLog.Warn($"Failed to open tray menu on left click: {ex.Message}");
        }
    }

    protected override void ExitThreadCore()
    {
        try
        {
            _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _ownedTrayIcon?.Dispose();
        }
        catch
        {
        }

        base.ExitThreadCore();
    }

    [Flags]
    private enum OpenAsInfoFlags : uint
    {
        OaifExec = 0x00000004
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string File;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Class;

        public OpenAsInfoFlags Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo info);
}


