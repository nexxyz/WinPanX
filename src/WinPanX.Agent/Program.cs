using NAudio.CoreAudioApi;
using WinPanX.Agent.Runtime;
using WinPanX.Agent.Tray;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--list-render-devices", StringComparison.OrdinalIgnoreCase))
        {
            ListRenderDevices();
            return;
        }

        var defaultConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinPanX");
        var configPath = args.Length > 0
            ? args[0]
            : Path.Combine(defaultConfigDirectory, "winpanx.json");

        var manualPath = Path.Combine(AppContext.BaseDirectory, "MANUAL.md");
        var logPath = Path.Combine(Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory, "winpanx.log");
        SimpleLog.Initialize(logPath);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new WinPanXTrayContext(configPath, manualPath, logPath);
        Application.Run(context);
    }

    private static void ListRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        Console.WriteLine("Active render devices:");
        foreach (var device in devices)
        {
            using (device)
            {
                var isDefault = string.Equals(device.ID, defaultRender.ID, StringComparison.OrdinalIgnoreCase)
                    ? " [default]"
                    : string.Empty;
                Console.WriteLine($"- {device.FriendlyName}{isDefault}");
                Console.WriteLine($"  ID: {device.ID}");
            }
        }
    }
}

