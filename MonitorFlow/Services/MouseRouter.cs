using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using MonitorFlow.Core;

namespace MonitorFlow.Services;

/// <summary>
/// Routes crossings through the app's physical layout. A dedicated message thread keeps
/// low-level hook callbacks independent from drawing or modal windows on the UI thread.
/// Raw mouse input supplies outward intent at Windows-clamped screen edges.
/// </summary>
public sealed class MouseRouter : IDisposable
{
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private HwndSource? _window;
    private IntPtr _hook, _keyboardHook, _rawBuffer;
    private NativeMethods.HookProc? _callback, _keyboardCallback;
    private LayoutProfile _profile = new();
    private IReadOnlyList<DisplayInfo> _displays = Array.Empty<DisplayInfo>();
    private NativeMethods.POINT _last;
    private bool _hasLast, _relocating;
    private volatile bool _running;
    private volatile bool _stopRequested;
    private long _lastWarp;
    private uint _lastMappedInputTime;
    private bool _hasMappedInputTime;
    private int _warpX, _warpY;
    public bool IsRunning => _running && !_stopRequested;
    public event Action<string>? Faulted;
    public event Action? EmergencyStopped;

    public void Start(LayoutProfile profile, IReadOnlyList<DisplayInfo> displays)
    {
        Stop();
        if (_thread?.IsAlive == true)
            throw new InvalidOperationException("이전 마우스 보정 엔진이 종료 중입니다. 잠시 후 다시 시도해 주세요.");
        var issue = LayoutEngine.Validate(profile, displays);
        if (issue is not null) throw new InvalidOperationException(issue);
        _profile = new LayoutProfile
        {
            Monitors = profile.Monitors.Select(p => new MonitorPlacement
            { Id = p.Id, X = p.X, Y = p.Y, Width = p.Width, Height = p.Height }).ToList()
        };
        _displays = displays.Select(d => new DisplayInfo
        { Id = d.Id, Name = d.Name, HardwareId = d.HardwareId, Bounds = d.Bounds, IsPrimary = d.IsPrimary }).ToArray();
        var ready = new ManualResetEventSlim();
        Exception? failure = null;
        bool readySignaled = false;
        _stopRequested = false;
        _hasMappedInputTime = false;
        _lastWarp = 0;
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                var parameters = new HwndSourceParameters("MonitorFlow.MouseRouter")
                { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 };
                _window = new HwndSource(parameters);
                _window.AddHook(WindowProc);
                _rawBuffer = Marshal.AllocHGlobal(256);
                var devices = new[] { new NativeMethods.RAWINPUTDEVICE
                { usUsagePage = 1, usUsage = 2, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = _window.Handle } };
                if (!NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "화면 가장자리의 마우스 입력을 받을 수 없습니다.");
                _callback = Hook;
                _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "마우스 보정을 시작하지 못했습니다.");
                _keyboardCallback = KeyboardHook;
                _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardCallback, NativeMethods.GetModuleHandle(null), 0);
                if (_keyboardHook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "비상 중지 단축키를 준비하지 못해 마우스 보정을 시작하지 않았습니다.");
                _hasLast = NativeMethods.GetCursorPos(out _last);
                _running = !_stopRequested;
                readySignaled = true;
                ready.Set();
                if (!_stopRequested) Dispatcher.Run();
            }
            catch (Exception ex)
            {
                if (!readySignaled) { failure = ex; readySignaled = true; ready.Set(); }
                else { try { Faulted?.Invoke($"마우스 보정이 중지되었습니다: {ex.Message}"); } catch { } }
            }
            finally
            {
                _running = false;
                if (_hook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                if (_keyboardHook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
                if (_window is not null)
                {
                    var devices = new[] { new NativeMethods.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = NativeMethods.RIDEV_REMOVE } };
                    NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());
                    _window.RemoveHook(WindowProc); _window.Dispose(); _window = null;
                }
                if (_rawBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_rawBuffer); _rawBuffer = IntPtr.Zero; }
                _callback = null; _keyboardCallback = null;
            }
        }) { IsBackground = true, Name = "MonitorFlow cursor routing" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            Stop();
            throw new InvalidOperationException("마우스 보정 엔진이 응답하지 않습니다. 다시 시도해 주세요.");
        }
        ready.Dispose();
        if (failure is not null) { Stop(); throw new InvalidOperationException(failure.Message, failure); }
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr pointer)
    {
        if (code >= 0 && _running && !_stopRequested && message.ToInt32() is 0x0100 or 0x0104)
        {
            try
            {
                var key = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(pointer);
                // Modifiers are already down when M arrives. Injected keyboard input is
                // accepted too, so accessibility tools can invoke the same emergency exit.
                if (key.vkCode == 0x4D && (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0
                    && (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0
                    && (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0)
                {
                    _running = false; _stopRequested = true;
                    _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
                    try { EmergencyStopped?.Invoke(); } catch { }
                }
            }
            catch (Exception ex) { Fail(ex); }
        }
        // Observation only: never take ownership of another app's shortcut or keyboard input.
        return NativeMethods.CallNextHookEx(_keyboardHook, code, message, pointer);
    }

    public void Stop()
    {
        _running = false;
        _stopRequested = true;
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(3000);
        // Never let a new Start reuse mutable native handles from a still-exiting run.
        if (thread is null || !thread.IsAlive) { _thread = null; _dispatcher = null; }
        _hasLast = false;
    }

    private IntPtr Hook(int code, IntPtr message, IntPtr pointer)
    {
        if (code < 0 || !_running || _stopRequested || message.ToInt32() != NativeMethods.WM_MOUSEMOVE || _relocating)
            return NativeMethods.CallNextHookEx(_hook, code, message, pointer);
        try
        {
            var input = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(pointer);
            if ((input.flags & NativeMethods.LLMHF_INJECTED) != 0)
            {
                _last = input.pt; _hasLast = true;
                return NativeMethods.CallNextHookEx(_hook, code, message, pointer);
            }
            if (input.pt.X == _warpX && input.pt.Y == _warpY && Environment.TickCount64 - _lastWarp < 50)
            {
                _last = input.pt; _hasLast = true;
                return NativeMethods.CallNextHookEx(_hook, code, message, pointer);
            }
            if (!_hasLast) _hasLast = NativeMethods.GetCursorPos(out _last);
            var source = FindDisplay(_last.X, _last.Y);
            if (source is null && NativeMethods.GetCursorPos(out _last)) source = FindDisplay(_last.X, _last.Y);
            if (source is not null)
            {
                double dx = input.pt.X - _last.X, dy = input.pt.Y - _last.Y;
                if (CursorMapper.TryMap(_profile, _displays, source.Id, _last.X, _last.Y, dx, dy, out var transition))
                {
                    if (Warp(transition.X, transition.Y))
                    {
                        _lastMappedInputTime = input.time; _hasMappedInputTime = true;
                        return new IntPtr(1);
                    }
                }
                // Windows can connect edges that are not neighbours in the physical layout.
                // Keep those crossings on the source edge so the virtual layout stays authoritative.
                else if (!Contains(source.Bounds, input.pt.X, input.pt.Y))
                {
                    int x = Math.Clamp(input.pt.X, source.Bounds.X, source.Bounds.Right - 1);
                    int y = Math.Clamp(input.pt.Y, source.Bounds.Y, source.Bounds.Bottom - 1);
                    if (Warp(x, y))
                    {
                        _lastMappedInputTime = input.time; _hasMappedInputTime = true;
                        return new IntPtr(1);
                    }
                }
            }
            _last = input.pt; _hasLast = true;
        }
        catch (Exception ex) { Fail(ex); }
        return NativeMethods.CallNextHookEx(_hook, code, message, pointer);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_INPUT || !_running || _stopRequested || _relocating || _rawBuffer == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            uint size = 256, headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
            uint read = NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, _rawBuffer, ref size, headerSize);
            if (read == uint.MaxValue || read < headerSize + Marshal.SizeOf<NativeMethods.RAWMOUSE>()) return IntPtr.Zero;
            var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(_rawBuffer);
            if (header.dwType != 0) return IntPtr.Zero;
            var mouse = Marshal.PtrToStructure<NativeMethods.RAWMOUSE>(IntPtr.Add(_rawBuffer, (int)headerSize));
            // Absolute tablet/remote desktop input has no relative movement intent.
            if ((mouse.usFlags & 1) != 0 || (mouse.lLastX == 0 && mouse.lLastY == 0)) return IntPtr.Zero;
            uint inputTime = unchecked((uint)NativeMethods.GetMessageTime());
            // Both delivery paths describe the same hardware event. In particular, queued
            // WM_INPUT must not repeat a crossing that the synchronous hook already routed.
            if (_hasMappedInputTime && unchecked((int)(inputTime - _lastMappedInputTime)) <= 0) return IntPtr.Zero;
            if (!NativeMethods.GetCursorPos(out var current)) return IntPtr.Zero;
            var source = FindDisplay(current.X, current.Y);
            if (source is null) return IntPtr.Zero;
            var bounds = source.Bounds;
            // Only supplement a clamped edge; ordinary pointer speed/acceleration stays with Windows.
            bool outward = (current.X <= bounds.X && mouse.lLastX < 0)
                || (current.X >= bounds.Right - 1 && mouse.lLastX > 0)
                || (current.Y <= bounds.Y && mouse.lLastY < 0)
                || (current.Y >= bounds.Bottom - 1 && mouse.lLastY > 0);
            if (outward && Environment.TickCount64 - _lastWarp > 12
                && CursorMapper.TryMap(_profile, _displays, source.Id, current.X, current.Y,
                    mouse.lLastX, mouse.lLastY, out var transition))
            {
                if (Warp(transition.X, transition.Y)) { _lastMappedInputTime = inputTime; _hasMappedInputTime = true; }
            }
        }
        catch (Exception ex) { Fail(ex); }
        // WM_INPUT's foreground cleanup belongs to DefWindowProc; do not swallow it.
        return IntPtr.Zero;
    }

    private bool Warp(int x, int y)
    {
        _relocating = true;
        try
        {
            if (!NativeMethods.SetCursorPos(x, y)) return false;
            _last = new NativeMethods.POINT { X = x, Y = y }; _hasLast = true;
            _warpX = x; _warpY = y; _lastWarp = Environment.TickCount64;
            return true;
        }
        finally { _relocating = false; }
    }

    private DisplayInfo? FindDisplay(int x, int y)
    {
        for (int i = 0; i < _displays.Count; i++) if (Contains(_displays[i].Bounds, x, y)) return _displays[i];
        return null;
    }
    private static bool Contains(RectI r, int x, int y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;
    private void Fail(Exception error)
    {
        _running = false;
        _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        try { Faulted?.Invoke($"마우스 보정이 중지되었습니다: {error.Message}"); } catch { }
    }
    public void Dispose() => Stop();
}
