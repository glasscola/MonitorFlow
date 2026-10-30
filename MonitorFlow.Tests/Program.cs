using MonitorFlow.Core;

var count = 0;
void Test(string name, Action action) { action(); count++; Console.WriteLine($"PASS {name}"); }
void Require(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
DisplayInfo D(string id, int x, int y, int w, int h, bool primary = false) => new() { Id = id, Name = id, Bounds = new(x, y, w, h), IsPrimary = primary };
MonitorPlacement M(string id, double x, double y, double w = 320, double h = 180) => new() { Id = id, X = x, Y = y, Width = w, Height = h };
LayoutProfile P(params MonitorPlacement[] monitors) => new() { Monitors = monitors.ToList() };
var mixed = new[] { D("1", 0, 0, 3840, 2160, true), D("2", 3840, 0, 1920, 1080) };

Test("Default mixed resolution displays have equal virtual sizes and touch", () => {
    var p = LayoutEngine.CreateDefault(mixed);
    Require(p.Monitors[0].Width == p.Monitors[1].Width && p.Monitors[0].Height == p.Monitors[1].Height);
    Require(LayoutEngine.Validate(p, mixed) == null);
});
Test("Windows projection keeps resolutions and anchors primary", () => {
    var p = P(M("1", 0, 0), M("2", 320, 0)); var projected = LayoutEngine.ProjectToWindows(p, mixed);
    Require(projected["1"] == mixed[0].Bounds); Require(projected["2"] == new RectI(3840, 540, 1920, 1080));
});
Test("Swapping monitor sides updates Windows positions", () => {
    var p = P(M("1", 0, 0), M("2", -320, 0)); var projected = LayoutEngine.ProjectToWindows(p, mixed);
    Require(projected["1"].X == 0 && projected["2"].X == -1920 && projected["2"].Y == 540);
});
Test("Mixed DPI right crossover keeps physical height", () => {
    var p = P(M("1", 0, 0), M("2", 320, 0));
    Require(CursorMapper.TryMap(p, mixed, "1", 3839, 1620, 2, 0, out var t));
    Require(t.Id == "2" && t.Y is >= 809 and <= 811 && t.X >= 3840);
});
Test("Left crossover and negative native coordinates", () => {
    var d = new[] { D("1", 0, 0, 3840, 2160, true), D("2", -1920, 540, 1920, 1080) }; var p = P(M("1", 0, 0), M("2", -320, 0));
    Require(CursorMapper.TryMap(p, d, "1", 0, 1080, -2, 0, out var t));
    Require(t.Id == "2" && t.X < 0 && t.Y is >= 1079 and <= 1081);
});
Test("Top and bottom crossover", () => {
    var d = new[] { D("1", 0, 0, 3840, 2160, true), D("2", 960, -1080, 1920, 1080) }; var p = P(M("1", 0, 0), M("2", 0, -180));
    Require(CursorMapper.TryMap(p, d, "1", 1920, 0, 0, -2, out var up) && up.Id == "2" && up.X is >= 1919 and <= 1921);
    Require(CursorMapper.TryMap(p, d, "2", 1920, -1, 0, 2, out var down) && down.Id == "1");
});
Test("Blocked Windows seam can cross with a raw delta", () => {
    var p = P(M("1", 0, 0), M("2", 320, 0));
    Require(CursorMapper.TryMap(p, mixed, "1", 3839, 2000, 1, 0, out var t) && t.Id == "2");
    Require(t.Y is >= 999 and <= 1001);
});
Test("No neighbor clamps instead of inventing a jump", () => {
    var p = P(M("1", 0, 0), M("2", 320, 90));
    Require(CursorMapper.TryMap(p, mixed, "1", 3839, 200, 5, 0, out var t));
    Require(t.Id == "1" && t.X == 3839);
});
Test("Motion within a display is untouched", () => {
    Require(!CursorMapper.TryMap(P(M("1", 0, 0), M("2", 320, 0)), mixed, "1", 100, 100, 5, 2, out _));
});
Test("Four monitor grid preserves every native size", () => {
    var d = new[] { D("1", 0, 0, 1920, 1080, true), D("2", 1920, 0, 1920, 1080), D("3", 0, 1080, 1920, 1080), D("4", 1920, 1080, 1920, 1080) };
    var p = LayoutEngine.CreateDefault(d); Require(LayoutEngine.Validate(p, d) == null); var r = LayoutEngine.ProjectToWindows(p, d);
    Require(r.Count == 4 && r["4"] == d[3].Bounds);
});
Test("Three monitor layout can route across more than one seam", () => {
    var d = new[] { D("1", 0, 0, 1920, 1080, true), D("2", 1920, 0, 1920, 1080), D("3", 3840, 0, 1920, 1080) }; var p = P(M("1", 0, 0), M("2", 320, 0), M("3", 640, 0));
    Require(CursorMapper.TryMap(p, d, "1", 1900, 540, 2200, 0, out var t) && t.Id == "3");
});
Test("Offsets map using shared physical coordinates", () => {
    var p = P(M("1", 0, 0), M("2", 320, 90));
    Require(CursorMapper.TryMap(p, mixed, "1", 3839, 1620, 2, 0, out var t));
    Require(t.Id == "2" && t.Y is >= 269 and <= 271);
});
Test("Overlaps, disconnected, mismatched, and nonfinite profiles are rejected", () => {
    Require(LayoutEngine.Validate(P(M("1", 0, 0), M("2", 100, 0)), mixed) != null);
    Require(LayoutEngine.Validate(P(M("1", 0, 0), M("2", 400, 0)), mixed) != null);
    Require(LayoutEngine.Validate(P(M("1", 0, 0)), mixed) != null);
    Require(LayoutEngine.Validate(P(M("1", double.NaN, 0), M("2", 320, 0)), mixed) != null);
});
Test("Corner contact alone is not a usable seam", () => Require(LayoutEngine.Validate(P(M("1", 0, 0), M("2", 320, 180)), mixed) != null));
Test("Snap joins nearby rectangles", () => {
    var p = P(M("1", 0, 0), M("2", 329, 4)); LayoutEngine.Snap(p, "2"); Require(p.Monitors[1].X == 320 && p.Monitors[1].Y == 0);
});
Test("Four-way corner follows two touching seams", () => {
    var d = new[] { D("1", 0, 0, 1920, 1080, true), D("2", 1920, 0, 1920, 1080), D("3", 0, 1080, 1920, 1080), D("4", 1920, 1080, 1920, 1080) };
    var p = P(M("1", 0, 0), M("2", 320, 0), M("3", 0, 180), M("4", 320, 180));
    Require(CursorMapper.TryMap(p, d, "1", 1919, 1079, 5, 5, out var t) && t.Id == "4");
});
Test("Equalize preserves connected layout", () => {
    var p = P(M("1", 0, 0), M("2", 320, 0, 640, 360)); LayoutEngine.EqualizeSizes(p);
    Require(p.Monitors[0].Width == p.Monitors[1].Width && LayoutEngine.Validate(p, mixed) == null);
});
Test("Resize reflows neighbors, retains dimensions, and anchors selected position", () => {
    var d = new[] { D("1", 0, 0, 1920, 1080, true), D("2", 1920, 0, 2560, 1440), D("3", 4480, 0, 1920, 1080) };
    var p = P(M("1", -320, 0), M("2", 0, 0), M("3", 320, 0));
    LayoutEngine.Resize(p, "2", 640, 360);
    Require(p.Monitors[1].X == 0 && p.Monitors[1].Y == 0 && p.Monitors[1].Width == 640 && p.Monitors[1].Height == 360);
    Require(p.Monitors[0].Width == 320 && p.Monitors[2].Height == 180 && p.Monitors[2].X == 640);
    Require(LayoutEngine.Validate(p, d) == null);
});
Test("Impossible Windows mixed-resolution grid is explicitly rejected", () => {
    var d = new[] { D("1", 0, 0, 3840, 2160, true), D("2", 3840, 0, 1920, 1080), D("3", 0, 2160, 3840, 2160), D("4", 3840, 2160, 3840, 2160) };
    var p = P(M("1", 0, 0), M("2", 320, 0), M("3", 0, 180), M("4", 320, 180));
    try { LayoutEngine.ProjectToWindows(p, d); throw new Exception("Expected projection error"); } catch (InvalidOperationException) { }
});
Console.WriteLine($"{count} geometry tests passed.");
