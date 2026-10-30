using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MonitorFlow;

public sealed class KeepLayoutWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining = 15;
    public KeepLayoutWindow()
    {
        Title = "화면 배치 확인"; Width = 430; Height = 245; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Topmost = true;
        var root = new StackPanel { Margin = new Thickness(26) };
        root.Children.Add(new TextBlock { Text = "이 배치를 유지할까요?", FontSize = 22, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = "모든 화면이 보이고 위치가 맞는지 확인하세요.", FontSize = 12, Margin = new Thickness(0, 12, 0, 0) });
        var countdown = new TextBlock { Text = "15초 후 이전 배치로 자동 복원됩니다.", Foreground = Brushes.SlateGray, FontSize = 12, Margin = new Thickness(0, 7, 0, 20) };
        root.Children.Add(countdown);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var revert = new Button { Content = "되돌리기", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        revert.Click += (_, _) => { DialogResult = false; };
        var keep = new Button { Content = "이 배치 유지", IsDefault = true, Style = (Style)FindResource("Primary") };
        keep.Click += (_, _) => { DialogResult = true; };
        row.Children.Add(revert); row.Children.Add(keep); root.Children.Add(row); Content = root;
        _timer.Tick += (_, _) => { _remaining--; countdown.Text = $"{_remaining}초 후 이전 배치로 자동 복원됩니다."; if (_remaining <= 0) { _timer.Stop(); DialogResult = false; } };
        Loaded += (_, _) => _timer.Start(); Closed += (_, _) => _timer.Stop();
    }
}

public sealed class IdentifyWindow : Window
{
    public IdentifyWindow(int number)
    {
        Title = "모니터 " + number; Width = 130; Height = 130; WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        ShowActivated = false; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; IsHitTestVisible = false;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(35, 82, 199)), CornerRadius = new CornerRadius(20),
            Child = new TextBlock { Text = number.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 68, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
        };
    }
}

public sealed class HelpWindow : Window
{
    public HelpWindow()
    {
        Title = "MonitorFlow 사용 안내"; Width = 550; Height = 615; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var body = new StackPanel { Margin = new Thickness(28) };
        body.Children.Add(new TextBlock { Text = "내 책상 그대로 연결하기", FontSize = 24, FontWeight = FontWeights.Bold });
        body.Children.Add(new TextBlock { Text = "해상도가 달라도 같은 높이에서 자연스럽게 넘어가세요.", Foreground = Brushes.SlateGray, Margin = new Thickness(0, 8, 0, 22) });
        Add(body, "01  화면 배치", "모니터를 드래그해 실제 위치에 놓으세요. 가장자리가 맞닿으면 자동으로 붙습니다. 가로·세로 정렬로 빠르게 시작할 수 있습니다.");
        Add(body, "02  실제 크기 맞추기", "크기가 같은 모니터는 ‘같은 크기로 맞춤’을 누르세요. 크기가 다르면 모니터를 선택해 가로·세로 비율을 맞추세요. 수치는 연결 기준의 상대값입니다.");
        Add(body, "03  적용 후 보정 켜기", "‘배치 적용하기’를 누르면 Windows 디스플레이 위치도 변경됩니다. 실제 해상도와 배율은 유지됩니다. 마우스 보정을 ON으로 켜면 설정한 크기를 기준으로 이동합니다.");
        Add(body, "필요할 때 바로 끄기", "OFF는 마우스 보정만 끕니다. 적용한 화면 위치는 유지됩니다. Ctrl+Alt+Shift+M 또는 트레이 메뉴로 즉시 끌 수 있습니다. 위치도 되돌리려면 ‘이전 배치 복원’을 누르세요.");
        Add(body, "실행과 연결 변경", "보정 중 창을 닫으면 트레이에서 계속 실행됩니다. 트레이의 ‘종료’는 보정도 끕니다. 다시 실행하면 보정은 OFF로 시작합니다. 모니터 연결·해상도가 바뀌면 보정을 자동 중지합니다.");
        Add(body, "키보드로 빠르게", "앱에서 Ctrl+Enter는 배치 적용, Ctrl+M은 보정 ON/OFF입니다. 배치 확인 창에서 Enter는 유지, Esc는 되돌리기입니다.");
        var close = new Button { Content = "시작하기", IsDefault = true, IsCancel = true, Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        close.Click += (_, _) => Close(); body.Children.Add(close);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private static void Add(Panel panel, string title, string text)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 18) });
    }
}
