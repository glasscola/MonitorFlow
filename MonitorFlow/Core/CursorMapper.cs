namespace MonitorFlow.Core;

/// <summary>Routes a physical mouse delta through the editor's monitor topology.</summary>
public static class CursorMapper
{
    public static bool TryMap(LayoutProfile profile, IReadOnlyList<DisplayInfo> displays, string sourceId,
        double sourceX, double sourceY, double deltaX, double deltaY, out CursorTransition transition)
    {
        transition = default;
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY) || (deltaX == 0 && deltaY == 0)) return false;
        var source = displays.FirstOrDefault(d => d.Id == sourceId);
        var current = profile.Monitors.FirstOrDefault(m => m.Id == sourceId);
        if (source == null || current == null || source.Bounds.Width <= 0 || source.Bounds.Height <= 0 || current.Width <= 0 || current.Height <= 0) return false;
        // Pixel centers avoid a one-pixel dead zone at each boundary.
        var x = current.X + (Math.Clamp(sourceX - source.Bounds.X, 0, source.Bounds.Width - 1) + 0.5) / source.Bounds.Width * current.Width;
        var y = current.Y + (Math.Clamp(sourceY - source.Bounds.Y, 0, source.Bounds.Height - 1) + 0.5) / source.Bounds.Height * current.Height;
        var dx = deltaX / source.Bounds.Width * current.Width;
        var dy = deltaY / source.Bounds.Height * current.Height;
        var crossed = false;
        // One unusually large hardware delta can cross several displays.
        for (var hop = 0; hop < Math.Max(4, profile.Monitors.Count * 2); hop++)
        {
            var endX = x + dx; var endY = y + dy;
            if (endX >= current.X && endX < current.Right && endY >= current.Y && endY < current.Bottom)
                return crossed && Finish(current, displays, endX, endY, out transition);
            var tx = dx > 0 ? (current.Right - x) / dx : dx < 0 ? (current.X - x) / dx : double.PositiveInfinity;
            var ty = dy > 0 ? (current.Bottom - y) / dy : dy < 0 ? (current.Y - y) / dy : double.PositiveInfinity;
            var t = Math.Clamp(Math.Min(tx, ty), 0, 1);
            var crossX = x + t * dx; var crossY = y + t * dy;
            var horizontal = tx <= ty;
            var edge = horizontal ? (dx > 0 ? LayoutEngine.Edge.Right : LayoutEngine.Edge.Left) : (dy > 0 ? LayoutEngine.Edge.Bottom : LayoutEngine.Edge.Top);
            var neighbor = profile.Monitors.FirstOrDefault(m => m.Id != current.Id && LayoutEngine.SharedEdge(current, m) == edge &&
                (horizontal ? crossY >= m.Y - 1e-8 && crossY < m.Bottom - 1e-8 : crossX >= m.X - 1e-8 && crossX < m.Right - 1e-8));
            // At a four-way corner try the other edge; direct diagonal jumps are never invented.
            if (neighbor == null && Math.Abs(tx - ty) < 1e-8)
            {
                horizontal = !horizontal;
                edge = horizontal ? (dx > 0 ? LayoutEngine.Edge.Right : LayoutEngine.Edge.Left) : (dy > 0 ? LayoutEngine.Edge.Bottom : LayoutEngine.Edge.Top);
                neighbor = profile.Monitors.FirstOrDefault(m => m.Id != current.Id && LayoutEngine.SharedEdge(current, m) == edge &&
                    (horizontal ? crossY >= m.Y - 1e-8 && crossY <= m.Bottom + 1e-8 : crossX >= m.X - 1e-8 && crossX <= m.Right + 1e-8));
            }
            if (neighbor == null)
            {
                // Returning a clamped transition also cancels a native crossing into the wrong monitor.
                return Finish(current, displays, Math.Clamp(endX, current.X, current.Right - 1e-8), Math.Clamp(endY, current.Y, current.Bottom - 1e-8), out transition);
            }
            crossed = true;
            dx *= 1 - t; dy *= 1 - t;
            x = crossX; y = crossY;
            current = neighbor;
            // Nudge into the new rectangle when the remaining delta is exactly zero.
            x = Math.Clamp(x, current.X + 1e-9, current.Right - 1e-9);
            y = Math.Clamp(y, current.Y + 1e-9, current.Bottom - 1e-9);
        }
        return Finish(current, displays, x, y, out transition);
    }

    private static bool Finish(MonitorPlacement target, IReadOnlyList<DisplayInfo> displays, double vx, double vy, out CursorTransition transition)
    {
        transition = default;
        var native = displays.FirstOrDefault(d => d.Id == target.Id);
        if (native == null || native.Bounds.Width <= 0 || native.Bounds.Height <= 0) return false;
        transition = new CursorTransition(target.Id,
            native.Bounds.X + Math.Clamp((int)Math.Floor((vx - target.X) / target.Width * native.Bounds.Width), 0, native.Bounds.Width - 1),
            native.Bounds.Y + Math.Clamp((int)Math.Floor((vy - target.Y) / target.Height * native.Bounds.Height), 0, native.Bounds.Height - 1));
        return true;
    }
}
