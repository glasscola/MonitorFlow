using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MonitorFlow.Core;
using MonitorFlow.Services;
using Forms = System.Windows.Forms;

namespace MonitorFlow;

public partial class MainWindow : Window
{
    private readonly bool _demo;
    private readonly DisplayService _service = new();
    private readonly ProfileStore _store = new();
    private readonly MouseRouter _router = new();
    private IReadOnlyList<DisplayInfo> _displays = [];
    private LayoutProfile _draft = new(), _active = new();
    private SavedState? _undo;
    private string? _selected;
    private double _scale = 1, _offsetX, _offsetY;
    private Border? _dragTile;
    private System.Windows.Point _dragStart;
    private double _originalX, _originalY;
    private bool _applying, _exit, _dirty, _trayTip;
    private long _ignoreChangesUntil;
    private Forms.NotifyIcon? _tray;
    private HwndSource? _source;
    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public MainWindow(bool demo = false)
    {
        _demo = demo;
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(600, area.Width - 24));
        MinHeight = Math.Min(MinHeight, Math.Max(480, area.Height - 24));
        Width = Math.Min(Width, area.Width - 24); Height = Math.Min(Height, area.Height - 24);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
        _router.Faulted += error => Dispatcher.BeginInvoke(() => { EmergencyStop(); SetStatus("마우스 보정을 중지했습니다", error, true); });
        _router.EmergencyStopped += () => Dispatcher.BeginInvoke(() => { EmergencyStop(); SetStatus("단축키로 마우스 보정을 껐습니다", "Windows 기본 마우스 이동을 사용합니다."); });
        _displayTimer.Tick += (_, _) => { _displayTimer.Stop(); CheckExternalDisplays(); };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProc);
        bool hotkey = RegisterHotKey(_source!.Handle, 0x4D46, 0x0001 | 0x0002 | 0x0004 | 0x4000, 0x4D);
        SetupTray();
        LoadDisplays(true);
        if (_demo) { DemoLabel.Text = "미리보기 · 실제 화면 변경 없음"; Title = "MonitorFlow · 미리보기"; }
        else if (!hotkey) SetStatus("기본 보정 중지 단축키를 사용합니다", "보정 ON 중 Ctrl+Alt+Shift+M 또는 트레이 메뉴로 끌 수 있습니다.");
    }

    private void LoadDisplays(bool loadSaved)
    {
        EmergencyStop();
        _undo = null;
        try
        {
            _displays = _demo ? DemoDisplays() : _service.GetDisplays();
            if (_displays.Count == 0) throw new InvalidOperationException("활성 모니터가 없습니다. Windows에서 확장 모드를 확인해 주세요.");
            _draft = LayoutEngine.CreateDefault(_displays);
            _active = _draft.Clone();
            _dirty = false;
            if (loadSaved && !_demo)
            {
                var saved = _store.Load();
                if (saved != null && ProfileStore.SameHardware(saved.Displays, _displays) && LayoutEngine.Validate(saved.Profile, _displays) == null)
                {
                    _draft = saved.Profile.Clone();
                    if (ProfileStore.SamePositions(saved.Displays, _displays)) _active = _draft.Clone();
                    else _dirty = true;
                }
                var pending = _store.Load("pending");
                if (pending != null && ProfileStore.SameHardware(pending.Displays, _displays))
                {
                    _undo = pending;
                    SetStatus("확인되지 않은 배치 변경이 있습니다", "‘이전 배치 복원’으로 변경 전 위치를 되돌릴 수 있습니다.", true);
                }
                else SetStatus(_dirty ? "저장한 배치를 불러왔습니다" : "배치를 설정할 준비가 되었습니다", _dirty ? "Windows 배치가 달라졌습니다. 배치 적용하기를 눌러 반영하세요." : "화면을 배치하고 적용하세요. 마우스 보정은 OFF 상태로 시작합니다.");
            }
            else SetStatus("배치를 설정할 준비가 되었습니다", "화면을 배치하고 적용하세요. 해상도와 배율은 유지됩니다.");
            _selected = _displays.FirstOrDefault(d => d.IsPrimary)?.Id ?? _displays[0].Id;
            DisplayCount.Text = $"{_displays.Count}개 모니터 연결됨";
            UpdateSelection(); RenderEditor(true); UpdateButtons();
        }
        catch (Exception ex) { SetStatus("모니터를 불러오지 못했습니다", ex.Message, true); ApplyButton.IsEnabled = false; RouterToggle.IsEnabled = false; }
    }

    private static IReadOnlyList<DisplayInfo> DemoDisplays() => new[]
    {
        new DisplayInfo { Id = @"\\.\DISPLAY1", Name = "Ultra HD Display", HardwareId = "demo1", Bounds = new(0, 0, 3840, 2160), IsPrimary = true },
        new DisplayInfo { Id = @"\\.\DISPLAY2", Name = "Full HD Display", HardwareId = "demo2", Bounds = new(3840, 0, 1920, 1080) },
        new DisplayInfo { Id = @"\\.\DISPLAY3", Name = "QHD Display", HardwareId = "demo3", Bounds = new(5760, 0, 2560, 1440) }
    };

    private void RenderEditor(bool fit = false)
    {
        if (_draft.Monitors.Count == 0 || Editor.ActualWidth < 1 || Editor.ActualHeight < 1) return;
        if (fit)
        {
            var minX = _draft.Monitors.Min(p => p.X); var minY = _draft.Monitors.Min(p => p.Y);
            var maxX = _draft.Monitors.Max(p => p.Right); var maxY = _draft.Monitors.Max(p => p.Bottom);
            _scale = Math.Min((Editor.ActualWidth - 100) / (maxX - minX), (Editor.ActualHeight - 110) / (maxY - minY));
            _scale = Math.Clamp(_scale, 0.035, 2.5);
            _offsetX = (Editor.ActualWidth - (maxX - minX) * _scale) / 2 - minX * _scale;
            _offsetY = (Editor.ActualHeight - (maxY - minY) * _scale) / 2 - minY * _scale;
            ZoomLabel.Text = $"{_scale * 100:0}%";
        }
        Editor.Children.Clear();
        foreach (var p in _draft.Monitors)
        {
            var d = _displays.First(x => x.Id == p.Id);
            bool selected = p.Id == _selected;
            double w = Math.Max(12, p.Width * _scale), h = Math.Max(12, p.Height * _scale);
            var tile = new Border
            {
                Width = w, Height = h, CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(selected ? 3 : 2), BorderBrush = selected ? Brush("#143FBA") : Brush("#8798B5"),
                Background = new LinearGradientBrush(selected ? ColorOf("#3978F6") : ColorOf("#8094B4"), selected ? ColorOf("#2454CD") : ColorOf("#647997"), 55),
                Cursor = Cursors.SizeAll, Tag = p.Id, Focusable = true,
                ToolTip = $"{DisplayService.GetDisplayNumber(p.Id)} · {d.Name}\n{d.Bounds.Width} × {d.Bounds.Height} · 드래그로 이동"
            };
            System.Windows.Automation.AutomationProperties.SetName(tile, $"모니터 {DisplayService.GetDisplayNumber(p.Id)}, 드래그로 배치");
            var content = new Grid { ClipToBounds = true };
            if (w >= 75 && h >= 55)
            {
                var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                center.Children.Add(new TextBlock { Text = DisplayService.GetDisplayNumber(p.Id).ToString(), FontSize = Math.Clamp(h * .27, 20, 52), FontWeight = FontWeights.Light, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
                if (h >= 95) center.Children.Add(new TextBlock { Text = d.IsPrimary ? "주 모니터" : "확장 화면", FontSize = 10, Foreground = Brush("#DFE9FF"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
                content.Children.Add(center);
                if (w > 130 && h > 120) content.Children.Add(new TextBlock { Text = $"{d.Bounds.Width} × {d.Bounds.Height}", Foreground = Brush("#E0E9FF"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 10) });
            }
            tile.Child = content;
            tile.MouseLeftButtonDown += Tile_Down;
            tile.MouseMove += Tile_Move;
            tile.MouseLeftButtonUp += Tile_Up;
            Canvas.SetLeft(tile, _offsetX + p.X * _scale); Canvas.SetTop(tile, _offsetY + p.Y * _scale);
            Panel.SetZIndex(tile, selected ? 2 : 1); Editor.Children.Add(tile);
        }
    }

    private void Tile_Down(object sender, MouseButtonEventArgs e)
    {
        if (_applying) return;
        var tile = (Border)sender; _selected = (string)tile.Tag;
        UpdateSelection(); RenderEditor();
        _dragTile = Editor.Children.OfType<Border>().Single(b => (string)b.Tag == _selected);
        var p = SelectedPlacement(); if (p == null) return;
        _dragStart = e.GetPosition(Editor); _originalX = p.X; _originalY = p.Y;
        _dragTile.CaptureMouse(); Editor.Focus(); e.Handled = true;
    }
    private void Tile_Move(object sender, MouseEventArgs e)
    {
        if (_dragTile == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = SelectedPlacement(); if (p == null) return;
        var pos = e.GetPosition(Editor);
        p.X = Math.Round(_originalX + (pos.X - _dragStart.X) / _scale, 1);
        p.Y = Math.Round(_originalY + (pos.Y - _dragStart.Y) / _scale, 1);
        LayoutEngine.Snap(_draft, p.Id);
        Canvas.SetLeft(_dragTile, _offsetX + p.X * _scale); Canvas.SetTop(_dragTile, _offsetY + p.Y * _scale);
    }
    private void Tile_Up(object sender, MouseButtonEventArgs e)
    {
        if (_dragTile == null) return;
        _dragTile.ReleaseMouseCapture(); _dragTile = null;
        var p = SelectedPlacement(); if (p == null) return;
        if (p.X != _originalX || p.Y != _originalY) MarkDirty();
        RenderEditor(true); e.Handled = true;
    }
    private void Editor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_applying) return;
        var p = SelectedPlacement(); if (p == null) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key) { case Key.Left: p.X -= step; break; case Key.Right: p.X += step; break; case Key.Up: p.Y -= step; break; case Key.Down: p.Y += step; break; default: return; }
        MarkDirty(); RenderEditor(true); e.Handled = true;
    }
    private void Editor_BackgroundClick(object sender, MouseButtonEventArgs e) { Editor.Focus(); }
    private void Editor_SizeChanged(object sender, SizeChangedEventArgs e) { if (_dragTile == null) RenderEditor(true); }
    private void Fit_Click(object sender, RoutedEventArgs e) => RenderEditor(true);
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_applying) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        { if (ApplyButton.IsEnabled) Apply_Click(ApplyButton, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.M && RouterToggle.IsEnabled)
        { RouterToggle.IsChecked = RouterToggle.IsChecked != true; RouterToggle_Click(RouterToggle, new RoutedEventArgs()); e.Handled = true; }
    }
    private MonitorPlacement? SelectedPlacement() => _draft.Monitors.FirstOrDefault(p => p.Id == _selected);
    private void UpdateSelection()
    {
        var p = SelectedPlacement(); var d = _displays.FirstOrDefault(d => d.Id == _selected);
        if (p == null || d == null) return;
        SelectedNumber.Text = DisplayService.GetDisplayNumber(d.Id).ToString(); SelectedName.Text = d.Name.StartsWith("Generic", StringComparison.OrdinalIgnoreCase) ? $"디스플레이 {DisplayService.GetDisplayNumber(d.Id)}" : d.Name;
        SelectedPrimary.Text = d.IsPrimary ? "주 모니터" : "확장 모니터";
        SelectedResolution.Text = $"{d.Bounds.Width:N0} × {d.Bounds.Height:N0}";
        WidthInput.Text = p.Width.ToString("0.#", CultureInfo.InvariantCulture); HeightInput.Text = p.Height.ToString("0.#", CultureInfo.InvariantCulture);
    }
    private void Equalize_Click(object sender, RoutedEventArgs e)
    {
        // Use the selected monitor as the size reference while retaining list/display identities.
        var p = SelectedPlacement(); if (p == null) return;
        var sorted = _draft.Monitors.OrderBy(m => m.Id == p.Id ? 0 : 1).ToList();
        _draft.Monitors = sorted; LayoutEngine.EqualizeSizes(_draft);
        MarkDirty(); UpdateSelection(); RenderEditor(true);
    }
    private void Horizontal_Click(object sender, RoutedEventArgs e) => Arrange(false);
    private void Vertical_Click(object sender, RoutedEventArgs e) => Arrange(true);
    private void Arrange(bool vertical)
    {
        double offset = 0;
        foreach (var p in _draft.Monitors.OrderBy(p => vertical ? p.Y : p.X).ThenBy(p => DisplayService.GetDisplayNumber(p.Id)))
        { p.X = vertical ? 0 : offset; p.Y = vertical ? offset : 0; offset += vertical ? p.Height : p.Width; }
        MarkDirty(); RenderEditor(true);
    }
    private void Resize_Click(object sender, RoutedEventArgs e)
    {
        var p = SelectedPlacement(); if (p == null) return;
        if (!double.TryParse(WidthInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) || !double.TryParse(HeightInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) || !double.IsFinite(w) || !double.IsFinite(h) || w < 40 || w > 4000 || h < 40 || h > 4000)
        { SetStatus("크기를 확인해 주세요", "가로와 세로는 40~4,000 사이의 숫자로 입력하세요.", true); return; }
        LayoutEngine.Resize(_draft, p.Id, w, h); MarkDirty(); UpdateSelection(); RenderEditor(true);
    }

    private void MarkDirty()
    {
        _dirty = true;
        var issue = LayoutEngine.Validate(_draft, _displays);
        SetStatus(issue == null ? "적용하지 않은 변경이 있습니다" : "배치를 조금 조정해 주세요", issue ?? "배치 적용하기를 누르면 Windows 위치와 보정 기준이 함께 반영됩니다.", issue != null);
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        ApplyButton.IsEnabled = !_applying && _displays.Count > 0 && LayoutEngine.Validate(_draft, _displays) == null;
        UndoButton.IsEnabled = !_applying && _undo != null;
        RouterToggle.IsEnabled = !_applying && _displays.Count > 1;
    }
    private void SetStatus(string title, string detail, bool warning = false)
    { StatusText.Text = title; StatusText.Foreground = Brush(warning ? "#AD6713" : "#17243B"); StatusDetail.Text = detail; }

    private void RouterToggle_Click(object sender, RoutedEventArgs e)
    {
        if (RouterToggle.IsChecked != true) { EmergencyStop(); SetStatus("마우스 보정 OFF", "Windows 기본 마우스 이동을 사용합니다. 적용한 모니터 위치는 유지됩니다."); return; }
        try
        {
            if (!_demo && !ProfileStore.SamePositions(_displays, _service.GetDisplays())) { LoadDisplays(false); SetStatus("모니터 구성이 변경되었습니다", "새 배치를 확인하고 다시 적용해 주세요.", true); return; }
            if (!_demo) _router.Start(_active, _displays);
            RouterLabel.Text = "ON · 화면 크기에 맞춰 이동";
            if (_tray != null) _tray.Text = "MonitorFlow · 마우스 보정 ON";
            SetStatus(_demo ? "미리보기: 마우스 보정 ON" : "마우스 보정 ON", _dirty ? "마지막 적용한 크기를 사용 중입니다. 편집한 설정은 배치 적용 후 반영됩니다." : "화면 사이를 같은 높이로 연결합니다. Ctrl+Alt+Shift+M으로 즉시 끌 수 있습니다.");
        }
        catch (Exception ex) { EmergencyStop(); SetStatus("마우스 보정을 켜지 못했습니다", ex.Message, true); }
    }
    public void EmergencyStop()
    {
        _router.Stop(); RouterToggle.IsChecked = false; RouterLabel.Text = "OFF · Windows 기본 이동";
        if (_tray != null) _tray.Text = "MonitorFlow · 마우스 보정 OFF";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        bool wanted = RouterToggle.IsChecked == true;
        var oldActive = _active.Clone(); var oldDisplays = _displays;
        bool changedNative = false, confirmed = false, nativeApplied = false, pendingWritten = false;
        var previousUndo = _undo;
        try
        {
            var issue = LayoutEngine.Validate(_draft, _displays); if (issue != null) throw new InvalidOperationException(issue);
            var positions = LayoutEngine.ProjectToWindows(_draft, _displays);
            if (!_demo && !ProfileStore.SamePositions(_displays, _service.GetDisplays())) { LoadDisplays(false); SetStatus("Windows 배치가 변경되었습니다", "새로 불러온 배치에서 다시 편집해 주세요.", true); return; }
            _applying = true; UpdateButtons(); EmergencyStop();
            changedNative = _displays.Any(d => positions[d.Id] != d.Bounds);
            _undo = new SavedState { Displays = _displays.ToList(), Profile = oldActive };
            if (!_demo)
            {
                _store.Save(oldActive, _displays, "pending");
                pendingWritten = true;
                if (changedNative) { nativeApplied = true; _service.ApplyPositions(positions); }
                _displays = _service.GetDisplays();
            }
            else _displays = _displays.Select(d => new DisplayInfo { Id = d.Id, Name = d.Name, HardwareId = d.HardwareId, IsPrimary = d.IsPrimary, Bounds = positions[d.Id] }).ToArray();
            if (changedNative && !_demo)
            {
                var dialog = new KeepLayoutWindow { Owner = this };
                if (dialog.ShowDialog() != true)
                {
                    _service.Restore(oldDisplays); nativeApplied = false; _displays = _service.GetDisplays(); _active = oldActive; _dirty = true;
                    _store.ClearPending();
                    _undo = previousUndo;
                    SetStatus("이전 Windows 위치로 되돌렸습니다", "편집 중인 배치는 남아 있습니다. 위치를 조정하고 다시 적용하세요.");
                    if (wanted) StartActive();
                    return;
                }
            }
            confirmed = true; _active = _draft.Clone(); _dirty = false;
            if (!_demo) { _store.Save(_active, _displays); _store.ClearPending(); }
            if (wanted) StartActive();
            SetStatus(_demo ? "미리보기 배치를 적용했습니다" : "배치를 적용했습니다", $"Windows 위치 동기화 완료 · 해상도 유지 · 마우스 보정 {(wanted ? "ON" : "OFF")}");
            UpdateSelection(); RenderEditor(true);
        }
        catch (Exception ex)
        {
            EmergencyStop();
            string detail = ex.Message;
            if (!confirmed)
            {
                _active = oldActive;
                if (nativeApplied && !_demo)
                {
                    try { _service.Restore(oldDisplays); _store.ClearPending(); _undo = previousUndo; }
                    catch (Exception restoreError) { detail += " 복원 오류: " + restoreError.Message; }
                }
                else
                {
                    _undo = previousUndo;
                    if (!_demo && pendingWritten) { try { _store.ClearPending(); } catch { } }
                }
            }
            if (!_demo)
            {
                try { _displays = _service.GetDisplays(); } catch { }
            }
            SetStatus(confirmed ? "배치는 적용했지만 추가 작업이 실패했습니다" : "배치를 적용하지 못했습니다", detail, true);
        }
        finally { _applying = false; _ignoreChangesUntil = Environment.TickCount64 + 1500; UpdateButtons(); }
    }
    private void StartActive()
    {
        if (!_demo && _displays.Count > 1) _router.Start(_active, _displays);
        RouterToggle.IsChecked = _displays.Count > 1; RouterLabel.Text = _displays.Count > 1 ? "ON · 화면 크기에 맞춰 이동" : "OFF · Windows 기본 이동";
        if (_tray != null) _tray.Text = "MonitorFlow · 마우스 보정 " + (RouterToggle.IsChecked == true ? "ON" : "OFF");
    }
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo == null || _applying) return;
        try
        {
            _applying = true; EmergencyStop(); UpdateButtons();
            if (!_demo) { _service.Restore(_undo.Displays); _displays = _service.GetDisplays(); }
            else _displays = _undo.Displays;
            _draft = _undo.Profile.Clone(); _active = _draft.Clone(); _dirty = false;
            if (!_demo) { _store.Save(_active, _displays); _store.ClearPending(); }
            _undo = null; UpdateSelection(); RenderEditor(true);
            SetStatus("이전 배치로 복원했습니다", "Windows 위치와 화면 크기 기준을 되돌렸습니다. 마우스 보정은 OFF입니다.");
        }
        catch (Exception ex) { SetStatus("배치를 복원하지 못했습니다", ex.Message, true); }
        finally { _applying = false; _ignoreChangesUntil = Environment.TickCount64 + 1500; UpdateButtons(); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && MessageBox.Show(this, "편집 중인 배치를 버리고 모니터를 다시 검색할까요?", "모니터 다시 검색", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _undo = null; LoadDisplays(false);
    }
    private void CheckExternalDisplays()
    {
        if (_demo) return;
        if (_applying || Environment.TickCount64 < _ignoreChangesUntil) { _displayTimer.Start(); return; }
        try
        {
            var current = _service.GetDisplays();
            if (ProfileStore.SamePositions(current, _displays)) return;
            _undo = null; LoadDisplays(false);
            SetStatus("모니터 변경을 감지했습니다", "안전을 위해 보정을 껐습니다. 새 배치를 확인하고 다시 적용하세요.", true);
        }
        catch { EmergencyStop(); SetStatus("모니터 정보를 확인할 수 없습니다", "연결을 확인한 뒤 모니터 다시 검색을 눌러주세요.", true); }
    }
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == 0x4D46) { EmergencyStop(); SetStatus("단축키로 마우스 보정을 껐습니다", "Windows 기본 마우스 이동을 사용합니다."); handled = true; }
        if (msg is 0x007E or 0x0219) { _displayTimer.Stop(); _displayTimer.Start(); }
        if (msg == 0x0011) { _exit = true; EmergencyStop(); }
        return IntPtr.Zero;
    }

    private void Identify_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in _displays)
        {
            var badge = new IdentifyWindow(DisplayService.GetDisplayNumber(d.Id));
            badge.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(badge).Handle;
                if (!_demo) SetWindowPos(hwnd, new IntPtr(-1), d.Bounds.X + d.Bounds.Width / 2 - 65, d.Bounds.Y + d.Bounds.Height / 2 - 65, 130, 130, 0x0010 | 0x0040);
            };
            badge.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += (_, _) => { timer.Stop(); badge.Close(); }; timer.Start();
            if (_demo) break;
        }
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private void WindowsSettings_Click(object sender, RoutedEventArgs e)
    { try { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); } catch (Exception ex) { SetStatus("Windows 설정을 열지 못했습니다", ex.Message, true); } }
    private void Help_Click(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();
    private void SetupTray()
    {
        if (_demo) return;
        _tray = new Forms.NotifyIcon { Visible = true, Text = "MonitorFlow · 마우스 보정 OFF", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("MonitorFlow 열기", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("마우스 보정 끄기", null, (_, _) => Dispatcher.Invoke(() => { EmergencyStop(); SetStatus("마우스 보정 OFF", "Windows 기본 마우스 이동을 사용합니다."); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(() => { _exit = true; Close(); }));
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exit && _router.IsRunning && _tray != null)
        {
            e.Cancel = true; Hide();
            if (!_trayTip) { _tray.ShowBalloonTip(2500, "MonitorFlow가 보정 중입니다", "트레이 아이콘으로 다시 열거나 종료할 수 있습니다.", Forms.ToolTipIcon.Info); _trayTip = true; }
            return;
        }
        if (_applying && !_exit) { e.Cancel = true; return; }
        _displayTimer.Stop(); _router.Dispose();
        if (_source != null) { UnregisterHotKey(_source.Handle, 0x4D46); _source.RemoveHook(WindowProc); }
        _tray?.Dispose();
    }
    private static SolidColorBrush Brush(string hex) => new(ColorOf(hex));
    private static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
