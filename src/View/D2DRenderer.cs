using System.Numerics;
using MaxScanning.Core;
using MaxScanning.Settings;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWFactoryType = Vortice.DirectWrite.FactoryType;

namespace MaxScanning.View;

public sealed class RenderState
{
    public FileTree Tree;
    public List<Tile> Tiles;
    public int HoverTile = -1;
    public int SelectedNode = -1;
    public float HoverAmount = 1f;     // 0..1 animated (fade in/out)
    public Matrix3x2 Transform = Matrix3x2.Identity;
    public string Message;             // centered overlay text (scanning / empty)
    public float Progress = -1;        // 0..100 progress bar overlay
    public Func<int, bool> IsDimmed;   // optional
}

/// <summary>GPU (Direct2D) treemap renderer. One solid brush recoloured per tile, one shared cushion gradient.</summary>
public sealed class D2DRenderer : IDisposable
{
    readonly IntPtr _hwnd;
    readonly ID2D1Factory _factory;
    readonly IDWriteFactory _dw;
    ID2D1HwndRenderTarget _rt;
    ID2D1SolidColorBrush _brush, _textBrush, _shadowBrush;
    ID2D1LinearGradientBrush _cushion;
    IDWriteTextFormat _tf, _tfBold, _tfBig;
    IDWriteInlineObject _ell, _ellBold;
    float _fontPx;
    int _w, _h;

    public D2DRenderer(IntPtr hwnd, int w, int h)
    {
        _hwnd = hwnd;
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory>(D2DFactoryType.SingleThreaded);
        _dw = DWrite.DWriteCreateFactory<IDWriteFactory>(DWFactoryType.Shared);
        Resize(w, h);
    }

    public void SetFont(float pt, int dpi)
    {
        float px = pt * dpi / 72f;
        if (Math.Abs(px - _fontPx) < 0.01f && _tf != null) return;
        _fontPx = px;
        _ell?.Dispose(); _ellBold?.Dispose(); _tf?.Dispose(); _tfBold?.Dispose(); _tfBig?.Dispose();
        _tf = MakeFormat(px, FontWeight.Normal);
        _tfBold = MakeFormat(px, FontWeight.SemiBold);
        _tfBig = MakeFormat(px * 1.6f, FontWeight.SemiBold);
        _ell = _dw.CreateEllipsisTrimmingSign(_tf);
        _ellBold = _dw.CreateEllipsisTrimmingSign(_tfBold);
        var trim = new Trimming { Granularity = TrimmingGranularity.Character };
        _tf.SetTrimming(trim, _ell);
        _tfBold.SetTrimming(trim, _ellBold);
    }

    public float FontPx => _fontPx;
    public float LineHeight => _fontPx * 1.35f;

    IDWriteTextFormat MakeFormat(float px, FontWeight weight)
    {
        var f = _dw.CreateTextFormat("Segoe UI", null, weight, Vortice.DirectWrite.FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, px, "");
        f.WordWrapping = WordWrapping.NoWrap;
        f.ParagraphAlignment = ParagraphAlignment.Near;
        f.TextAlignment = TextAlignment.Leading;
        return f;
    }

    public void Resize(int w, int h)
    {
        _w = Math.Max(1, w); _h = Math.Max(1, h);
        if (_rt == null) CreateTarget();
        else _rt.Resize(new SizeI(_w, _h));
    }

    void CreateTarget()
    {
        DisposeTarget();
        var props = new RenderTargetProperties { DpiX = 96, DpiY = 96 };
        var hprops = new HwndRenderTargetProperties { Hwnd = _hwnd, PixelSize = new SizeI(_w, _h), PresentOptions = PresentOptions.Immediately };
        _rt = _factory.CreateHwndRenderTarget(props, hprops);
        _rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _rt.AntialiasMode = AntialiasMode.Aliased;
        _brush = _rt.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _textBrush = _rt.CreateSolidColorBrush(new Color4(0.08f, 0.08f, 0.1f, 1));
        _shadowBrush = _rt.CreateSolidColorBrush(new Color4(0, 0, 0, 0.35f));
        var stops = new[]
        {
            new GradientStop { Position = 0f, Color = new Color4(1, 1, 1, 0.38f) },
            new GradientStop { Position = 0.45f, Color = new Color4(1, 1, 1, 0.0f) },
            new GradientStop { Position = 1f, Color = new Color4(0, 0, 0, 0.22f) },
        };
        var coll = _rt.CreateGradientStopCollection(stops);
        _cushion = _rt.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(1, 1) }, coll);
        coll.Dispose();
    }

    void DisposeTarget()
    {
        _cushion?.Dispose(); _brush?.Dispose(); _textBrush?.Dispose(); _shadowBrush?.Dispose(); _rt?.Dispose();
        _cushion = null; _brush = null; _textBrush = null; _shadowBrush = null; _rt = null;
    }

    static Color4 C(int argb, float light = 0f)
    {
        float r = ((argb >> 16) & 255) / 255f, g = ((argb >> 8) & 255) / 255f, b = (argb & 255) / 255f;
        if (light > 0) { r += (1 - r) * light; g += (1 - g) * light; b += (1 - b) * light; }
        else if (light < 0) { r *= 1 + light; g *= 1 + light; b *= 1 + light; }
        return new Color4(r, g, b, 1f);
    }

    static readonly int[] TagColors = { 0, unchecked((int)0xFFE04848), unchecked((int)0xFF3FB950), unchecked((int)0xFF3C7EE8), unchecked((int)0xFFE8C53C) };

    static int Blend(int a, int b, float t)
    {
        int ch(int s) => (int)(((a >> s) & 255) * (1 - t) + ((b >> s) & 255) * t);
        return unchecked((int)0xFF000000) | (ch(16) << 16) | (ch(8) << 8) | ch(0);
    }

    public void Render(RenderState s, Config cfg, Func<int, long> sizeOf)
    {
        if (_rt == null) CreateTarget();
        try
        {
            _rt.BeginDraw();
            _rt.Clear(C(unchecked((int)0xFF2B2D31)));
            _rt.Transform = s.Transform;
            if (s.Tree != null && s.Tiles != null && s.Tiles.Count > 0) DrawTiles(s, cfg, sizeOf);
            _rt.Transform = Matrix3x2.Identity;
            if (!string.IsNullOrEmpty(s.Message)) DrawOverlay(s.Message, s.Progress);
            _rt.EndDraw();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            DisposeTarget(); // device lost / D2DERR_RECREATE_TARGET
        }
    }

    void DrawTiles(RenderState s, Config cfg, Func<int, long> sizeOf)
    {
        var t = s.Tree;
        var tiles = s.Tiles;
        // highlight set: hovered tile + N ancestor levels
        int hl0 = s.HoverTile, hlLevels = Math.Max(0, cfg.HighlightLevels);
        Span<int> hlSet = stackalloc int[16];
        int hlN = 0;
        for (int k = hl0, lv = 0; k >= 0 && lv <= hlLevels && hlN < 16; k = tiles[k].Parent, lv++) hlSet[hlN++] = k;
        float lh = LineHeight;
        float pad = 3;

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var r = tile.R;
            if (r.Width < 1 || r.Height < 1) continue;
            int node = tile.Node;
            int color;
            if (node < 0) color = Blend(cfg.ColorFolder, cfg.ColorFile, 0.5f);
            else color = t.Kind[node] switch
            {
                NodeKind.Root => cfg.ColorDrive,
                NodeKind.Dir => cfg.ColorFolder,
                NodeKind.Free => cfg.ColorFree,
                NodeKind.Unknown => cfg.ColorUnknown,
                NodeKind.Stream => cfg.ColorStream,
                _ => cfg.ColorFile
            };
            if (node >= 0 && t.Tag[node] != 0) color = Blend(color, TagColors[t.Tag[node]], 0.55f);
            float light = -cfg.LevelContrast * (tile.Depth % 2 == 0 ? 0 : 1) - cfg.LevelContrast * 0.5f * Math.Min(tile.Depth, 6) * 0.5f;
            bool hl = false;
            for (int k = 0; k < hlN; k++) if (hlSet[k] == i) { hl = true; break; }
            if (hl) light += cfg.HighlightHover * s.HoverAmount * (i == hl0 ? 1f : 0.6f);
            else if (hl0 >= 0) light -= cfg.HighlightLeave * s.HoverAmount * 0.5f;
            if (node >= 0 && s.IsDimmed != null && s.IsDimmed(node)) light -= 0.25f;

            var rect = new RawRectF(r.Left, r.Top, r.Right, r.Bottom);
            _brush.Color = C(color, light);
            _rt.FillRectangle(rect, _brush);

            bool leaf = !tile.Container;
            if (cfg.CushionShading && leaf && r.Width > 3 && r.Height > 3)
            {
                _cushion.StartPoint = new Vector2(r.Left, r.Top);
                _cushion.EndPoint = new Vector2(r.Right, r.Bottom);
                _rt.FillRectangle(rect, _cushion);
            }
            else if (cfg.CushionShading && tile.Header)
            {
                var hdr = new RawRectF(r.Left, r.Top, r.Right, Math.Min(r.Bottom, tile.Inner.Top));
                _cushion.StartPoint = new Vector2(r.Left, r.Top);
                _cushion.EndPoint = new Vector2(r.Left, hdr.Bottom + (hdr.Bottom - hdr.Top));
                _rt.FillRectangle(hdr, _cushion);
            }

            // border
            _brush.Color = C(color, -cfg.BorderContrast);
            _rt.DrawRectangle(new RawRectF(r.Left + 0.5f, r.Top + 0.5f, r.Right - 0.5f, r.Bottom - 0.5f), _brush, 1f);

            // labels
            if (r.Width > 24 && r.Height > lh * 0.9f && (tile.Header || leaf || tile.Depth == 0 && !tile.Container))
            {
                string name = node < 0 ? $"{tile.RestCount:N0} {L.T("small items")}" : t.Name(node);
                string size = Util.FormatSize(tile.Size);
                if (tile.Header)
                {
                    var tr = new RawRectF(r.Left + pad, r.Top + 1, r.Right - pad, r.Top + lh + 1);
                    _rt.PushAxisAlignedClip(new RawRectF(r.Left, r.Top, r.Right, r.Top + lh + 2), AntialiasMode.Aliased);
                    _rt.DrawText(name + "   " + size, _tfBold, tr, _textBrush);
                    _rt.PopAxisAlignedClip();
                }
                else if (leaf)
                {
                    _rt.PushAxisAlignedClip(rect, AntialiasMode.Aliased);
                    var tr = new RawRectF(r.Left + pad, r.Top + 1, r.Right - pad, r.Top + 1 + lh);
                    _rt.DrawText(name, node >= 0 && t.IsDirLike(node) ? _tfBold : _tf, tr, _textBrush);
                    if (r.Height > lh * 2.1f)
                        _rt.DrawText(size, _tf, new RawRectF(r.Left + pad, r.Top + 1 + lh, r.Right - pad, r.Top + 1 + lh * 2), _textBrush);
                    _rt.PopAxisAlignedClip();
                }
            }
            // selection
            if (node >= 0 && node == s.SelectedNode)
            {
                _brush.Color = new Color4(1f, 0.55f, 0.1f, 1f);
                _rt.DrawRectangle(new RawRectF(r.Left + 1.5f, r.Top + 1.5f, r.Right - 1.5f, r.Bottom - 1.5f), _brush, 3f);
            }
        }
    }

    void DrawOverlay(string msg, float progress)
    {
        float w = Math.Min(_w - 40, 560), h = progress >= 0 ? 74 : 54;
        float x = (_w - w) / 2, y = (_h - h) / 2;
        _brush.Color = new Color4(0.12f, 0.12f, 0.14f, 0.88f);
        _rt.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x, y, x + w, y + h), 8, 8), _brush);
        _brush.Color = new Color4(0.95f, 0.95f, 0.97f, 1f);
        _rt.DrawText(msg, _tfBold, new RawRectF(x + 16, y + 14, x + w - 16, y + 40), _brush);
        if (progress >= 0)
        {
            var bar = new RawRectF(x + 16, y + 46, x + w - 16, y + 56);
            _brush.Color = new Color4(0.3f, 0.3f, 0.34f, 1f);
            _rt.FillRectangle(bar, _brush);
            _brush.Color = new Color4(0.25f, 0.6f, 1f, 1f);
            _rt.FillRectangle(new RawRectF(bar.Left, bar.Top, bar.Left + (bar.Right - bar.Left) * Math.Clamp(progress, 0, 100) / 100f, bar.Bottom), _brush);
        }
    }

    public void Dispose()
    {
        DisposeTarget();
        _ell?.Dispose(); _ellBold?.Dispose(); _tf?.Dispose(); _tfBold?.Dispose(); _tfBig?.Dispose();
        _dw?.Dispose(); _factory?.Dispose();
    }
}
