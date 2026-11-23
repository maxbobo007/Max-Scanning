using System.Drawing;
using MaxScanning.Core;

namespace MaxScanning.View;

public struct Tile
{
    public int Node;        // -1 for "rest" aggregate
    public RectangleF R;    // full tile rect
    public RectangleF Inner;// child area (containers)
    public int Depth;       // 0 = view root
    public int Parent;      // index into tile list (-1 for root)
    public bool Header;     // has a title strip
    public bool Container;  // children laid out inside
    public long Size;
    public int RestCount;   // for rest tiles: number of aggregated small items
}

public sealed class LayoutSettings
{
    public int MaxDepth = 3;          // detail level
    public float MinCell = 6;         // px; smaller items are aggregated into a "rest" tile
    public float Aspect = 1f;         // >1 favours wide tiles, <1 tall tiles
    public float HeaderHeight = 18;
    public float Padding = 2;
    public bool SortBySize = true;
    public bool ShowFree = true, ShowUnknown = true;
}

/// <summary>
/// Squarified treemap (Bruls/Huizing/van Wijk) with SpaceSniffer-style nested boxes: each container gets a header
/// strip and its children inside. Children arrive pre-sorted by size so layout is O(visible) — tiny tails are
/// collapsed into a single aggregate tile instead of being laid out one by one.
/// </summary>
public static class TreemapLayout
{
    public static List<Tile> Build(FileTree t, int root, RectangleF area, LayoutSettings s, Func<int, long> sizeOf, Func<int, bool> visible)
    {
        var tiles = new List<Tile>(4096);
        if (root < 0 || area.Width < 2 || area.Height < 2) return tiles;
        tiles.Add(new Tile { Node = root, R = area, Depth = 0, Parent = -1, Size = sizeOf(root) });
        var queue = new Queue<int>();
        queue.Enqueue(0);
        var kids = new List<(int node, long size)>(1024);
        var rects = new List<RectangleF>(1024);

        while (queue.Count > 0)
        {
            int ti = queue.Dequeue();
            var tile = tiles[ti];
            if (tile.Node < 0) continue;
            int node = tile.Node;
            bool isContainer = t.ChildCount.Length > node && t.ChildCount[node] > 0;
            if (!isContainer || tile.Depth >= s.MaxDepth) continue;

            // header + inner area
            var r = tile.R;
            float hh = s.HeaderHeight;
            bool header = tile.Depth > 0 && r.Height > hh * 2.2f && r.Width > 30;
            if (tile.Depth == 0) header = false;
            var inner = header
                ? new RectangleF(r.X + s.Padding, r.Y + hh, r.Width - 2 * s.Padding, r.Height - hh - s.Padding)
                : new RectangleF(r.X + (tile.Depth == 0 ? 0 : s.Padding), r.Y + (tile.Depth == 0 ? 0 : s.Padding),
                                 r.Width - (tile.Depth == 0 ? 0 : 2 * s.Padding), r.Height - (tile.Depth == 0 ? 0 : 2 * s.Padding));
            if (inner.Width < s.MinCell || inner.Height < s.MinCell) continue;

            // gather visible children (sorted by size desc already, unless name sort requested)
            kids.Clear();
            long total = 0;
            foreach (int c in t.ChildrenOf(node))
            {
                if (!visible(c)) continue;
                long sz = sizeOf(c);
                if (sz <= 0) continue;
                kids.Add((c, sz));
                total += sz;
            }
            if (kids.Count == 0 || total <= 0) continue;
            if (!s.SortBySize) kids.Sort((a, b) => t.NameSpan(a.node).CompareTo(t.NameSpan(b.node), StringComparison.OrdinalIgnoreCase));
            else kids.Sort((a, b) => b.size.CompareTo(a.size)); // filtered sizes may change order

            // collapse tail: items whose area would be below MinCell² go into one rest tile
            double areaPerByte = inner.Width * (double)inner.Height / total;
            double minArea = s.MinCell * (double)s.MinCell;
            int keep = kids.Count;
            long rest = 0;
            if (s.SortBySize)
            {
                for (int k = 0; k < kids.Count; k++)
                    if (kids[k].size * areaPerByte < minArea) { keep = k; break; }
                for (int k = keep; k < kids.Count; k++) rest += kids[k].size;
            }
            int restCount = kids.Count - keep;
            if (keep == 0 && rest > 0)
            {
                tile.Container = true; tile.Header = header; tile.Inner = inner; tiles[ti] = tile;
                tiles.Add(new Tile { Node = -1, R = inner, Depth = tile.Depth + 1, Parent = ti, Size = rest, RestCount = restCount });
                continue;
            }

            var sizes = new List<double>(keep + 1);
            for (int k = 0; k < keep; k++) sizes.Add(kids[k].size);
            if (rest > 0) sizes.Add(rest);
            rects.Clear();
            Squarify(sizes, inner, s.Aspect, rects);

            tile.Container = true; tile.Header = header; tile.Inner = inner;
            tiles[ti] = tile;
            for (int k = 0; k < rects.Count; k++)
            {
                var rr = rects[k];
                if (rr.Width < 1 || rr.Height < 1) continue;
                if (k < keep)
                {
                    int idx = tiles.Count;
                    tiles.Add(new Tile { Node = kids[k].node, R = rr, Depth = tile.Depth + 1, Parent = ti, Size = kids[k].size });
                    if (rr.Width >= s.MinCell * 2 && rr.Height >= s.MinCell * 2) queue.Enqueue(idx);
                }
                else tiles.Add(new Tile { Node = -1, R = rr, Depth = tile.Depth + 1, Parent = ti, Size = rest, RestCount = restCount });
            }
        }
        return tiles;
    }

    /// <summary>Classic squarify on sizes sorted desc. Writes one rect per size, in order.</summary>
    public static void Squarify(List<double> sizes, RectangleF r, float aspect, List<RectangleF> output)
    {
        double total = 0;
        foreach (var v in sizes) total += v;
        if (total <= 0) return;
        double x = r.X, y = r.Y, w = r.Width, h = r.Height;
        double scale = w * h / total;
        int i = 0, n = sizes.Count;
        while (i < n)
        {
            // lay a row along the shorter side (aspect-weighted)
            bool horizontal = w * aspect >= h; // row stacked vertically in a column on the left when wide
            double side = horizontal ? h : w;
            int j = i;
            double rowSum = 0, best = double.MaxValue;
            while (j < n)
            {
                double s2 = rowSum + sizes[j] * scale;
                double worst = Worst(sizes, i, j, s2, side, scale, aspect, horizontal);
                if (worst > best && j > i) break;
                best = worst; rowSum = s2; j++;
            }
            double thick = rowSum / side;
            double pos = horizontal ? y : x;
            for (int k = i; k < j; k++)
            {
                double len = sizes[k] * scale / thick;
                if (horizontal) output.Add(new RectangleF((float)x, (float)pos, (float)thick, (float)len));
                else output.Add(new RectangleF((float)pos, (float)y, (float)len, (float)thick));
                pos += len;
            }
            if (horizontal) { x += thick; w -= thick; } else { y += thick; h -= thick; }
            if (w <= 0.01 || h <= 0.01) { for (int k = j; k < n; k++) output.Add(RectangleF.Empty); break; }
            i = j;
        }
    }

    static double Worst(List<double> sizes, int i, int j, double rowSum, double side, double scale, float aspect, bool horizontal)
    {
        double thick = rowSum / side;
        double worst = 0;
        // ratio is monotonic in item area, so only the largest (i) and smallest (j) items matter
        for (int k = i; k <= j; k = k == j ? j + 1 : j)
        {
            double len = sizes[k] * scale / thick;
            double rw = horizontal ? thick : len, rh = horizontal ? len : thick;
            rw *= aspect;
            double ratio = rw > rh ? rw / rh : rh / rw;
            if (ratio > worst) worst = ratio;
        }
        return worst;
    }

    /// <summary>Deepest tile containing the point (tiles are appended parents-first, so scan backwards).</summary>
    public static int HitTest(List<Tile> tiles, PointF p)
    {
        for (int i = tiles.Count - 1; i >= 0; i--)
            if (tiles[i].R.Contains(p)) return i;
        return -1;
    }
}
