using System.IO;
using System.Text.Json;
using MonitorFlow.Core;

namespace MonitorFlow.Services;

public sealed class SavedState
{
    public int Version { get; set; } = 1;
    public LayoutProfile Profile { get; set; } = new();
    public List<DisplayInfo> Displays { get; set; } = [];
}

public sealed class ProfileStore
{
    private readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorFlow");
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    public SavedState? Load(string name = "layout")
    {
        try
        {
            var path = Path.Combine(_folder, name + ".json");
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(path));
            return state?.Version == 1 && state.Profile?.Monitors is { Count: > 0 } && state.Displays is { Count: > 0 } ? state : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public void Save(LayoutProfile profile, IReadOnlyList<DisplayInfo> displays, string name = "layout")
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, name + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new SavedState { Profile = profile, Displays = displays.ToList() }, _options));
        File.Move(temp, path, true);
    }
    public void ClearPending()
    {
        var path = Path.Combine(_folder, "pending.json");
        if (File.Exists(path)) File.Delete(path);
    }
    public static LayoutProfile Clone(LayoutProfile profile) => new() { Monitors = profile.Monitors.Select(p => new MonitorPlacement { Id = p.Id, X = p.X, Y = p.Y, Width = p.Width, Height = p.Height }).ToList() };
    public static bool SameHardware(IReadOnlyList<DisplayInfo> a, IReadOnlyList<DisplayInfo> b) => a.Count == b.Count && a.All(x => b.Any(y => x.Id == y.Id && x.HardwareId == y.HardwareId && x.Bounds.Width == y.Bounds.Width && x.Bounds.Height == y.Bounds.Height));
    public static bool SamePositions(IReadOnlyList<DisplayInfo> a, IReadOnlyList<DisplayInfo> b) => SameHardware(a, b) && a.All(x => b.Any(y => x.Id == y.Id && x.Bounds == y.Bounds));
}
