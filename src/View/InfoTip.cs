using System.Drawing;

namespace MaxScanning.View;

/// <summary>Non-activating tooltip window with multi-line info (name / path / sizes / dates).</summary>
public sealed class InfoTip : Form
{
    string _title = "", _body = "";
    readonly Font _bold, _normal;

    public InfoTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(255, 255, 252, 236);
        DoubleBuffered = true;
        TopMost = true;
        _bold = new Font("Segoe UI", 9f, FontStyle.Bold);
        _normal = new Font("Segoe UI", 9f);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000020 /*TRANSPARENT*/;
            cp.ClassStyle |= 0x00020000; // drop shadow
            return cp;
        }
    }

    public void ShowAt(Point screen, string title, string body)
    {
        if (title != _title || body != _body)
        {
            _title = title; _body = body;
            var g = CreateGraphics();
            var s1 = TextRenderer.MeasureText(g, _title, _bold, new Size(900, 2000), TextFormatFlags.NoPrefix);
            var s2 = TextRenderer.MeasureText(g, _body, _normal, new Size(900, 2000), TextFormatFlags.NoPrefix);
            g.Dispose();
            Size = new Size(Math.Max(s1.Width, s2.Width) + 16, s1.Height + s2.Height + 14);
            Invalidate();
        }
        var wa = Screen.FromPoint(screen).WorkingArea;
        int x = screen.X + 18, y = screen.Y + 20;
        if (x + Width > wa.Right) x = screen.X - Width - 8;
        if (y + Height > wa.Bottom) y = screen.Y - Height - 8;
        Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
        if (!Visible) Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var pen = new Pen(Color.FromArgb(160, 150, 120));
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        var s1 = TextRenderer.MeasureText(g, _title, _bold, new Size(900, 2000), TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _title, _bold, new Point(8, 6), Color.Black, TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _body, _normal, new Rectangle(8, 8 + s1.Height, Width - 12, Height), Color.FromArgb(40, 40, 40), TextFormatFlags.NoPrefix);
    }
}
