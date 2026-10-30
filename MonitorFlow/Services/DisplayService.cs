using System.ComponentModel;
using System.Runtime.InteropServices;
using MonitorFlow.Core;

namespace MonitorFlow.Services;

/// <summary>Reads active native-pixel display bounds and changes positions only.</summary>
public sealed class DisplayService
{
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        Exception? enumerationError = null;
        NativeMethods.MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            try
            {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = "" };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return true;
            string device = info.szDevice;
            var mode = ReadMode(device);
            var monitorDevice = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
            string name = $"디스플레이 {GetDisplayNumber(device)}";
            string hardwareId = device;
            if (NativeMethods.EnumDisplayDevices(device, 0, ref monitorDevice, 1))
            {
                if (!string.IsNullOrWhiteSpace(monitorDevice.DeviceString)) name = monitorDevice.DeviceString;
                if (!string.IsNullOrWhiteSpace(monitorDevice.DeviceID)) hardwareId = monitorDevice.DeviceID;
            }
            displays.Add(new DisplayInfo
            {
                Id = device, Name = name, HardwareId = hardwareId,
                Bounds = new RectI(mode.dmPositionX, mode.dmPositionY, checked((int)mode.dmPelsWidth), checked((int)mode.dmPelsHeight)),
                IsPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0
            });
            return true;
            }
            catch (Exception ex) { enumerationError = ex; return false; }
        };
        bool success = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        if (enumerationError is not null) throw new InvalidOperationException("모니터 정보를 읽는 중 오류가 발생했습니다.", enumerationError);
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "연결된 모니터를 읽지 못했습니다.");
        return displays.OrderBy(d => GetDisplayNumber(d.Id)).ToArray();
    }

    public static int GetDisplayNumber(string id)
    {
        int last = id.Length - 1;
        while (last >= 0 && char.IsDigit(id[last])) last--;
        return int.TryParse(id[(last + 1)..], out int number) ? number : 0;
    }

    public void ApplyPositions(IReadOnlyDictionary<string, RectI> positions)
    {
        var before = GetDisplays();
        if (positions.Count != before.Count || before.Any(d => !positions.ContainsKey(d.Id)))
            throw new InvalidOperationException("모니터 연결이 변경되었습니다. 새로 고침 후 다시 적용해 주세요.");
        var primary = before.FirstOrDefault(d => d.IsPrimary);
        if (primary is not null && (positions[primary.Id].X != 0 || positions[primary.Id].Y != 0))
            throw new InvalidOperationException("주 모니터는 Windows 좌표 (0, 0)에 있어야 합니다.");
        foreach (var display in before)
        {
            var target = positions[display.Id];
            if (target.Width != display.Bounds.Width || target.Height != display.Bounds.Height)
                throw new InvalidOperationException("이 프로그램은 해상도를 변경하지 않습니다. 모니터 정보를 새로 고침해 주세요.");
        }
        try
        {
            ApplyCore(positions);
            var after = GetDisplays();
            if (after.Count != before.Count || before.Any(d => !after.Any(a => a.Id == d.Id && a.Bounds.Width == d.Bounds.Width && a.Bounds.Height == d.Bounds.Height)))
                throw new InvalidOperationException("적용 중 모니터 구성이 변경되어 이전 위치로 복원했습니다.");
            foreach (var actual in after)
            {
                if (!positions.TryGetValue(actual.Id, out var target) || actual.Bounds.X != target.X || actual.Bounds.Y != target.Y)
                    throw new InvalidOperationException("Windows가 요청한 배치를 적용하지 못했습니다. 이전 위치로 복원했습니다.");
            }
        }
        catch (Exception applyError)
        {
            try { ApplyCore(before.ToDictionary(d => d.Id, d => d.Bounds)); }
            catch (Exception rollbackError)
            {
                throw new AggregateException("배치 적용과 자동 복원에 실패했습니다. Windows 디스플레이 설정에서 위치를 확인해 주세요.", applyError, rollbackError);
            }
            throw;
        }
    }

    public void Restore(IReadOnlyList<DisplayInfo> snapshot)
    {
        var connected = GetDisplays();
        if (connected.Count != snapshot.Count || connected.Any(d => !snapshot.Any(s => s.Id == d.Id && s.Bounds.Width == d.Bounds.Width && s.Bounds.Height == d.Bounds.Height)))
            throw new InvalidOperationException("연결 또는 해상도가 바뀌어 이전 배치를 그대로 복원할 수 없습니다. Windows 디스플레이 설정을 확인해 주세요.");
        ApplyPositions(snapshot.ToDictionary(d => d.Id, d => d.Bounds));
    }

    private static NativeMethods.DEVMODE ReadMode(string id)
    {
        var mode = new NativeMethods.DEVMODE { dmSize = checked((ushort)Marshal.SizeOf<NativeMethods.DEVMODE>()), dmDeviceName = "", dmFormName = "" };
        if (!NativeMethods.EnumDisplaySettingsEx(id, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode, 0))
            throw new InvalidOperationException($"모니터 {GetDisplayNumber(id)}의 현재 설정을 읽지 못했습니다.");
        return mode;
    }

    private static void ApplyCore(IReadOnlyDictionary<string, RectI> positions)
    {
        foreach (var (id, bounds) in positions)
        {
            var mode = ReadMode(id);
            mode.dmFields = NativeMethods.DM_POSITION;
            mode.dmPositionX = bounds.X; mode.dmPositionY = bounds.Y;
            int result = NativeMethods.ChangeDisplaySettingsEx(id, ref mode, IntPtr.Zero, NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET, IntPtr.Zero);
            if (result != 0) throw new InvalidOperationException($"모니터 {GetDisplayNumber(id)}의 위치 변경을 Windows가 거부했습니다 ({DescribeResult(result)}).");
        }
        int commit = NativeMethods.CommitDisplaySettings(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (commit != 0) throw new InvalidOperationException($"디스플레이 배치를 적용하지 못했습니다 ({DescribeResult(commit)}).");
    }

    private static string DescribeResult(int result) => result switch
    {
        1 => "다시 시작 필요", -1 => "드라이버 오류", -2 => "지원하지 않는 배치", -3 => "설정을 저장할 수 없음",
        -4 => "잘못된 옵션", -5 => "잘못된 설정", -6 => "연결 모드 오류", _ => result.ToString()
    };
}
