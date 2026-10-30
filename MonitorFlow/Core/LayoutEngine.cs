namespace MonitorFlow.Core;

/// <summary>Pure geometry shared by the editor, Windows layout projection, and mouse router.</summary>
public static class LayoutEngine
{
    internal const double Epsilon = 0.02;
    private const double SnapDistance = 18;

    public static LayoutProfile CreateDefault(IReadOnlyList<DisplayInfo> displays)
    {
        var profile = new LayoutProfile();
        if (displays.Count == 0) return profile;
        var source = new LayoutProfile
        {
            Monitors = displays.Select(d => new MonitorPlacement
            {
                Id = d.Id, X = d.Bounds.X, Y = d.Bounds.Y,
                Width = d.Bounds.Width, Height = d.Bounds.Height
            }).ToList()
        };
        var primaryId = displays.FirstOrDefault(d => d.IsPrimary)?.Id ?? displays[0].Id;
        profile.Monitors = Reflow(source, _ => (320d, 180d), primaryId);
        if (Validate(profile, displays) != null)
        {
            // Some mixed-DPI native grids cannot be represented by equal rectangles.
            // Keep every display reachable and editable instead of inventing overlaps.
            profile.Monitors = displays.OrderBy(d => d.Bounds.X).ThenBy(d => d.Bounds.Y)
                .Select((d, i) => new MonitorPlacement { Id = d.Id, X = i * 320, Y = 0 }).ToList();
            var anchor = profile.Monitors.Single(m => m.Id == primaryId).X;
            foreach (var m in profile.Monitors) m.X -= anchor;
        }
        return profile;
    }

    public static void EqualizeSizes(LayoutProfile profile)
    {
        if (profile.Monitors.Count == 0) return;
        var first = profile.Monitors[0];
        var width = Math.Clamp(first.Width, 40, 4000);
        var height = Math.Clamp(first.Height, 40, 4000);
        var next = Reflow(profile, _ => (width, height), first.Id);
        if (next.Count != profile.Monitors.Count || HasOverlap(next) || !Connected(next))
            next = profile.Monitors.Select((m, i) => new MonitorPlacement { Id = m.Id, X = i * width, Y = 0, Width = width, Height = height }).ToList();
        profile.Monitors = next;
    }

    public static void Resize(LayoutProfile profile, string id, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 10 || height < 10 || width > 10000 || height > 10000)
            throw new ArgumentOutOfRangeException(nameof(width), "모니터 크기에 올바른 숫자를 입력해 주세요.");
        var selected = profile.Monitors.FirstOrDefault(m => m.Id == id);
        if (selected == null) return;
        var oldX = selected.X; var oldY = selected.Y;
        var sizes = profile.Monitors.ToDictionary(m => m.Id, m => (m.Width, m.Height));
        sizes[id] = (width, height);
        var resized = Reflow(profile, key => sizes[key], id);
        foreach (var monitor in resized) { monitor.X += oldX; monitor.Y += oldY; }
        profile.Monitors = resized;
    }

    public static void Snap(LayoutProfile profile, string movedId)
    {
        var moved = profile.Monitors.FirstOrDefault(m => m.Id == movedId);
        if (moved == null) return;
        (double X, double Y, double Score)? best = null;
        foreach (var other in profile.Monitors.Where(m => m.Id != movedId))
        {
            var alignedY = new[] { other.Y, other.Bottom - moved.Height, other.Y + (other.Height - moved.Height) / 2 }.OrderBy(y => Math.Abs(y - moved.Y)).First();
            var alignedX = new[] { other.X, other.Right - moved.Width, other.X + (other.Width - moved.Width) / 2 }.OrderBy(x => Math.Abs(x - moved.X)).First();
            var ys = new[] { Math.Abs(alignedY - moved.Y) <= SnapDistance ? alignedY : moved.Y };
            var xs = new[] { Math.Abs(alignedX - moved.X) <= SnapDistance ? alignedX : moved.X };
            foreach (var x in new[] { other.Right, other.X - moved.Width })
            foreach (var y in ys)
                Consider(x, y, Math.Min(y + moved.Height, other.Bottom) - Math.Max(y, other.Y) > Epsilon);
            foreach (var y in new[] { other.Bottom, other.Y - moved.Height })
            foreach (var x in xs)
                Consider(x, y, Math.Min(x + moved.Width, other.Right) - Math.Max(x, other.X) > Epsilon);
        }
        if (best is { } value) { moved.X = value.X; moved.Y = value.Y; }
        void Consider(double x, double y, bool overlaps)
        {
            var dx = Math.Abs(x - moved.X); var dy = Math.Abs(y - moved.Y);
            if (!overlaps || dx > SnapDistance || dy > SnapDistance) return;
            var score = dx * dx + dy * dy;
            if (best == null || score < best.Value.Score) best = (x, y, score);
        }
    }

    public static string? Validate(LayoutProfile profile, IReadOnlyList<DisplayInfo> displays)
    {
        if (profile.Monitors.Count == 0) return "연결된 모니터가 없습니다.";
        if (profile.Monitors.Any(m => string.IsNullOrWhiteSpace(m.Id)) || profile.Monitors.Select(m => m.Id).Distinct().Count() != profile.Monitors.Count)
            return "모니터 정보가 중복되었거나 올바르지 않습니다. 새로고침해 주세요.";
        if (profile.Monitors.Count != displays.Count || profile.Monitors.Any(m => displays.All(d => d.Id != m.Id)))
            return "연결된 모니터가 변경되었습니다. 새로고침해 주세요.";
        if (profile.Monitors.Any(m => !double.IsFinite(m.X) || !double.IsFinite(m.Y) || !double.IsFinite(m.Width) || !double.IsFinite(m.Height) || m.Width < 10 || m.Height < 10 || m.Width > 10000 || m.Height > 10000 || Math.Abs(m.X) > 100000 || Math.Abs(m.Y) > 100000))
            return "모니터 크기와 위치에 올바른 숫자를 입력해 주세요.";
        if (HasOverlap(profile.Monitors)) return "모니터가 겹쳐 있습니다. 가장자리가 맞닿도록 배치해 주세요.";
        if (!Connected(profile.Monitors)) return "떨어진 모니터가 있습니다. 모든 모니터의 가장자리를 이어 주세요.";
        return null;
    }

    public static Dictionary<string, RectI> ProjectToWindows(LayoutProfile profile, IReadOnlyList<DisplayInfo> displays)
    {
        var error = Validate(profile, displays);
        if (error != null) throw new InvalidOperationException(error);
        if (displays.Any(d => d.Bounds.Width <= 0 || d.Bounds.Height <= 0)) throw new InvalidOperationException("모니터 해상도를 읽을 수 없습니다.");
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        var native = displays.ToDictionary(d => d.Id);
        var projected = Reflow(profile, id => ((double)native[id].Bounds.Width, native[id].Bounds.Height), primary.Id);
        var result = projected.ToDictionary(m => m.Id, m => new RectI((int)Math.Round(m.X), (int)Math.Round(m.Y), native[m.Id].Bounds.Width, native[m.Id].Bounds.Height));
        if (HasOverlap(result.Select(kv => new MonitorPlacement { Id = kv.Key, X = kv.Value.X, Y = kv.Value.Y, Width = kv.Value.Width, Height = kv.Value.Height }).ToList()))
            throw new InvalidOperationException("해상도 차이로 Windows 배치가 겹칩니다. 모니터를 한 줄 또는 한 열로 배치하거나 높이를 조절해 주세요.");
        if (result.Values.Any(r => Math.Abs((long)r.X) > 30000 || Math.Abs((long)r.Y) > 30000 || Math.Abs((long)r.Right) > 32760 || Math.Abs((long)r.Bottom) > 32760))
            throw new InvalidOperationException("Windows 화면 배치 범위를 벗어났습니다. 모니터를 더 가깝게 배치해 주세요.");
        return result;
    }

    // Reflow dimensions while preserving adjacency and normalized shared-edge anchors.
    private static List<MonitorPlacement> Reflow(LayoutProfile source, Func<string, (double Width, double Height)> sizes, string anchorId)
    {
        if (source.Monitors.Count == 0) return [];
        var anchor = source.Monitors.FirstOrDefault(m => m.Id == anchorId) ?? source.Monitors[0];
        var (aw, ah) = sizes(anchor.Id);
        var placed = new Dictionary<string, MonitorPlacement> { [anchor.Id] = new() { Id = anchor.Id, Width = aw, Height = ah } };
        var pending = new Queue<MonitorPlacement>(); pending.Enqueue(anchor);
        while (pending.Count > 0)
        {
            var a = pending.Dequeue(); var targetA = placed[a.Id];
            foreach (var b in source.Monitors.Where(m => !placed.ContainsKey(m.Id)))
            {
                var edge = SharedEdge(a, b);
                if (edge == Edge.None) continue;
                var (bw, bh) = sizes(b.Id);
                var targetB = new MonitorPlacement { Id = b.Id, Width = bw, Height = bh };
                if (edge is Edge.Left or Edge.Right)
                {
                    var middle = (Math.Max(a.Y, b.Y) + Math.Min(a.Bottom, b.Bottom)) / 2;
                    targetB.X = edge == Edge.Right ? targetA.Right : targetA.X - bw;
                    targetB.Y = targetA.Y + (middle - a.Y) / a.Height * targetA.Height - (middle - b.Y) / b.Height * bh;
                }
                else
                {
                    var middle = (Math.Max(a.X, b.X) + Math.Min(a.Right, b.Right)) / 2;
                    targetB.Y = edge == Edge.Bottom ? targetA.Bottom : targetA.Y - bh;
                    targetB.X = targetA.X + (middle - a.X) / a.Width * targetA.Width - (middle - b.X) / b.Width * bw;
                }
                placed.Add(b.Id, targetB); pending.Enqueue(b);
            }
        }
        // Caller validates/falls back for disconnected source layouts.
        foreach (var missing in source.Monitors.Where(m => !placed.ContainsKey(m.Id)))
        {
            var (w, h) = sizes(missing.Id);
            placed.Add(missing.Id, new MonitorPlacement { Id = missing.Id, X = missing.X, Y = missing.Y, Width = w, Height = h });
        }
        return source.Monitors.Select(m => placed[m.Id]).ToList();
    }

    internal enum Edge { None, Left, Right, Top, Bottom }
    internal static Edge SharedEdge(MonitorPlacement a, MonitorPlacement b)
    {
        if (Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y) > Epsilon)
        {
            if (Math.Abs(a.Right - b.X) < Epsilon) return Edge.Right;
            if (Math.Abs(a.X - b.Right) < Epsilon) return Edge.Left;
        }
        if (Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > Epsilon)
        {
            if (Math.Abs(a.Bottom - b.Y) < Epsilon) return Edge.Bottom;
            if (Math.Abs(a.Y - b.Bottom) < Epsilon) return Edge.Top;
        }
        return Edge.None;
    }
    private static bool HasOverlap(IReadOnlyList<MonitorPlacement> monitors)
    {
        for (var i = 0; i < monitors.Count; i++) for (var j = i + 1; j < monitors.Count; j++)
            if (Math.Min(monitors[i].Right, monitors[j].Right) - Math.Max(monitors[i].X, monitors[j].X) > Epsilon && Math.Min(monitors[i].Bottom, monitors[j].Bottom) - Math.Max(monitors[i].Y, monitors[j].Y) > Epsilon) return true;
        return false;
    }
    private static bool Connected(IReadOnlyList<MonitorPlacement> monitors)
    {
        if (monitors.Count == 0) return true;
        var reached = new HashSet<string> { monitors[0].Id }; var q = new Queue<MonitorPlacement>(); q.Enqueue(monitors[0]);
        while (q.Count > 0)
        {
            var a = q.Dequeue();
            foreach (var b in monitors) if (!reached.Contains(b.Id) && SharedEdge(a, b) != Edge.None) { reached.Add(b.Id); q.Enqueue(b); }
        }
        return reached.Count == monitors.Count;
    }
}
