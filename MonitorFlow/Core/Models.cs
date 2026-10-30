namespace MonitorFlow.Core;

public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public sealed class DisplayInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public RectI Bounds { get; set; }
    public bool IsPrimary { get; set; }
    public string HardwareId { get; set; } = "";
}

public sealed class MonitorPlacement
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 180;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public MonitorPlacement Clone() => new() { Id = Id, X = X, Y = Y, Width = Width, Height = Height };
}

public sealed class LayoutProfile
{
    public List<MonitorPlacement> Monitors { get; set; } = [];
    public LayoutProfile Clone() => new() { Monitors = Monitors.Select(m => m.Clone()).ToList() };
}

public readonly record struct CursorTransition(string Id, int X, int Y);
