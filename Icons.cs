using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace OmrXmlBuilder
{
    /// <summary>
    /// Toolbar icons drawn with GDI+ instead of shipping image files — keeps the exe
    /// self-contained (one file to copy) and keeps every glyph crisp at 16×16.
    /// </summary>
    internal static class Icons
    {
        private static readonly Color Ink = Color.FromArgb(70, 70, 78);
        private static readonly Color Accent = Color.FromArgb(28, 110, 200);
        private static readonly Color Good = Color.FromArgb(0, 145, 65);
        private static readonly Color Warn = Color.FromArgb(195, 60, 40);

        private static Icon _app;
        private static bool _appTried;

        /// <summary>
        /// The application icon (app.ico, drawn by tools\MakeAppIcon.ps1 in this same
        /// palette). Read from the exe's own embedded copy so each window picks the
        /// size it needs out of the .ico — 16 px for the title bar, 32 px for the
        /// taskbar — instead of rescaling one bitmap. Null if the resource is missing,
        /// which just leaves the form on the default Windows icon.
        /// </summary>
        public static Icon App
        {
            get
            {
                if (_appTried) return _app;
                _appTried = true;
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("OmrXmlBuilder.app.ico"))
                    if (s != null) _app = new Icon(s);
                return _app;
            }
        }

        public static Bitmap Get(string kind)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                Draw(g, kind);
            }
            return bmp;
        }

        private static void Draw(Graphics g, string kind)
        {
            switch (kind)
            {
                case "image":       // photo frame with a hill and sun
                    Rect(g, Ink, 1.5f, 3f, 13f, 10.5f);
                    Fill(g, Accent, new[] { new PointF(3f, 12.5f), new PointF(7f, 7f), new PointF(11f, 12.5f) });
                    using (var b = new SolidBrush(Warn)) g.FillEllipse(b, 10f, 5f, 2.6f, 2.6f);
                    break;

                case "xml":         // page with < > chevrons
                    Rect(g, Ink, 2.5f, 1.5f, 11f, 13f);
                    using (var p = new Pen(Accent, 1.4f))
                    {
                        g.DrawLines(p, new[] { new PointF(7f, 5.5f), new PointF(5f, 8f), new PointF(7f, 10.5f) });
                        g.DrawLines(p, new[] { new PointF(9.5f, 5.5f), new PointF(11.5f, 8f), new PointF(9.5f, 10.5f) });
                    }
                    break;

                case "detect":      // grid with a magnifier ring
                    Grid(g, Color.FromArgb(150, Ink), 1.5f, 1.5f, 9f);
                    using (var p = new Pen(Accent, 1.6f))
                    {
                        g.DrawEllipse(p, 7.5f, 7.5f, 5.5f, 5.5f);
                        g.DrawLine(p, 12.5f, 12.5f, 15f, 15f);
                    }
                    break;

                case "grid":        // plain grid (show / hide toggle)
                    Grid(g, Accent, 1.5f, 1.5f, 13f);
                    break;

                case "rot180":      // half-circle arrow = 180° turn
                    Arc(g, Warn, 2.5f, 2.5f, 11f, 11f, 20f, 300f, true);
                    break;

                case "deskew":      // tilted sheet (dashed) snapping upright (solid)
                    using (var p = new Pen(Warn, 1.2f) { DashStyle = DashStyle.Dash })
                    {
                        var st = g.Save();
                        g.TranslateTransform(8f, 8f); g.RotateTransform(-20f); g.TranslateTransform(-8f, -8f);
                        g.DrawRectangle(p, 3f, 2f, 10f, 12f);
                        g.Restore(st);
                    }
                    Rect(g, Accent, 3.5f, 2.5f, 9f, 11f);
                    break;

                case "autoskew":    // deskew + auto dot
                    Draw(g, "deskew");
                    Dot(g, Good);
                    break;

                case "autorot":     // rotate + auto dot
                    Arc(g, Ink, 2.5f, 2.5f, 11f, 11f, 20f, 300f, true);
                    Dot(g, Good);
                    break;

                case "undo":        // arrow curving back to the left
                    using (var p = new Pen(Accent, 1.8f)) g.DrawArc(p, 3f, 5f, 11f, 9f, 190f, 160f);
                    Fill(g, Accent, new[] { new PointF(1.5f, 5f), new PointF(7.5f, 4.5f), new PointF(4f, 9.5f) });
                    break;

                case "zoomin":
                case "zoomout":     // magnifier with + / −
                    using (var p = new Pen(Accent, 1.6f))
                    {
                        g.DrawEllipse(p, 1.5f, 1.5f, 9.5f, 9.5f);
                        g.DrawLine(p, 10.5f, 10.5f, 14.5f, 14.5f);
                        g.DrawLine(p, 4f, 6.3f, 8.8f, 6.3f);
                        if (kind == "zoomin") g.DrawLine(p, 6.4f, 4f, 6.4f, 8.7f);
                    }
                    break;

                case "fit":         // frame with outward corner arrows
                    Rect(g, Ink, 1.5f, 1.5f, 13f, 13f);
                    using (var p = new Pen(Accent, 1.5f))
                    {
                        g.DrawLine(p, 5f, 5f, 11f, 11f);
                        g.DrawLines(p, new[] { new PointF(5f, 8f), new PointF(5f, 5f), new PointF(8f, 5f) });
                        g.DrawLines(p, new[] { new PointF(11f, 8f), new PointF(11f, 11f), new PointF(8f, 11f) });
                    }
                    break;

                case "generate":    // page with an export arrow
                    Rect(g, Ink, 2.5f, 1.5f, 9f, 10f);
                    using (var p = new Pen(Good, 1.8f))
                    {
                        g.DrawLine(p, 11f, 8f, 11f, 14.5f);
                        g.DrawLines(p, new[] { new PointF(8.5f, 12f), new PointF(11f, 14.8f), new PointF(13.5f, 12f) });
                    }
                    break;

                case "check":       // sheet with a tick
                    Rect(g, Ink, 1.5f, 1.5f, 10f, 13f);
                    using (var p = new Pen(Color.FromArgb(140, Ink), 1f))
                    {
                        g.DrawLine(p, 3.5f, 5f, 9.5f, 5f);
                        g.DrawLine(p, 3.5f, 7.5f, 9.5f, 7.5f);
                    }
                    using (var p = new Pen(Good, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(p, new[] { new PointF(7f, 11f), new PointF(9.5f, 14f), new PointF(15f, 6.5f) });
                    break;

                case "reset":       // trash bin — clears everything (distinct from 180°)
                    using (var p = new Pen(Warn, 1.4f))
                    {
                        g.DrawLine(p, 2.5f, 4.5f, 13.5f, 4.5f);          // lid
                        g.DrawLine(p, 6.5f, 4.5f, 6.5f, 2.5f);
                        g.DrawLine(p, 6.5f, 2.5f, 9.5f, 2.5f);
                        g.DrawLine(p, 9.5f, 2.5f, 9.5f, 4.5f);
                        g.DrawLines(p, new[] { new PointF(4f, 5.5f), new PointF(5f, 14f),   // body
                                               new PointF(11f, 14f), new PointF(12f, 5.5f) });
                        g.DrawLine(p, 6.5f, 7f, 6.8f, 12f);              // ribs
                        g.DrawLine(p, 9.5f, 7f, 9.2f, 12f);
                    }
                    break;

                case "help":
                    using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
                    using (var b = new SolidBrush(Accent))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("?", f, b, new RectangleF(0, 0, 16, 16), sf);
                    break;
            }
        }

        // ---- small drawing helpers ----
        private static void Rect(Graphics g, Color c, float x, float y, float w, float h)
        {
            using (var p = new Pen(c, 1.4f)) g.DrawRectangle(p, x, y, w, h);
        }

        private static void Fill(Graphics g, Color c, PointF[] pts)
        {
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        private static void Grid(Graphics g, Color c, float x, float y, float size)
        {
            using (var p = new Pen(c, 1.2f))
            {
                g.DrawRectangle(p, x, y, size, size);
                for (int i = 1; i <= 2; i++)
                {
                    float o = size / 3f * i;
                    g.DrawLine(p, x + o, y, x + o, y + size);
                    g.DrawLine(p, x, y + o, x + size, y + o);
                }
            }
        }

        /// <summary>Arc with an arrowhead at its end — the base of every rotate glyph.</summary>
        private static void Arc(Graphics g, Color c, float x, float y, float w, float h, float start, float sweep, bool head)
        {
            using (var p = new Pen(c, 1.8f)) g.DrawArc(p, x, y, w, h, start, sweep);
            if (!head) return;
            float cx = x + w / 2f, cy = y + h / 2f;
            double a = (start + sweep) * System.Math.PI / 180.0;
            float ex = cx + (float)System.Math.Cos(a) * w / 2f;
            float ey = cy + (float)System.Math.Sin(a) * h / 2f;
            Fill(g, c, new[]
            {
                new PointF(ex - 3f, ey - 1.5f), new PointF(ex + 3f, ey - 1.5f), new PointF(ex, ey + 3.5f)
            });
        }

        /// <summary>Green corner dot marking the "automatic" toggles.</summary>
        private static void Dot(Graphics g, Color c)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, 10.5f, 10.5f, 5f, 5f);
            using (var p = new Pen(Color.White, 1f)) g.DrawEllipse(p, 10.5f, 10.5f, 5f, 5f);
        }
    }
}
