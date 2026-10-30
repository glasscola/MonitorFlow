using System.IO;
using System.Text.Json;
using System.Windows;
using MonitorFlow.Services;

namespace MonitorFlow;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, "Local\\MonitorFlow.Desktop.v1", out bool first);
        if (!first)
        {
            MessageBox.Show("MonitorFlow가 이미 실행 중입니다. 작업 표시줄의 숨겨진 아이콘에서 열어주세요.", "MonitorFlow");
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            if (MainWindow is MainWindow main) main.EmergencyStop();
            MessageBox.Show("작업을 마치지 못했습니다. 마우스 보정을 껐습니다.\n\n" + args.Exception.Message, "MonitorFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        if (e.Args.Contains("--diagnostics"))
        {
            try
            {
                var displays = new DisplayService().GetDisplays();
                var profile = Core.LayoutEngine.CreateDefault(displays);
                using var router = new MouseRouter();
                router.Start(profile, displays);
                bool installed = router.IsRunning;
                router.Stop();
                var path = e.Args.LastOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ?? Path.Combine(AppContext.BaseDirectory, "diagnostics.json");
                File.WriteAllText(path, JsonSerializer.Serialize(new { Displays = displays, HookInstalled = installed, HookStopped = !router.IsRunning }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diagnostics-error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        MainWindow = new MainWindow(e.Args.Contains("--demo"));
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
