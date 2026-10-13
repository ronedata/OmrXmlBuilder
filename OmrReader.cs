using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace OmrXmlBuilder
{
    /// <summary>One reading position (bubble / mark) of a region.</summary>
    public class BubbleRead
    {
        public RectangleF Rect;      // reading area in image px (after borderRemove)
        public double BlackPct;
        public bool Filled;
        public int LineIndex;
        public string Ch;            // the charSet entry this position represents
    }

    /// <summary>Read result of one element (region / imagePart / marker).</summary>
    public class RegionRead
    {
        public OmrElement El;
        public string Value = "";
        public List<BubbleRead> Bubbles = new List<BubbleRead>();
        public RectangleF Box;       // whole-area footprint (imagePart / result areas)
        public bool IsArea;          // true = Box is the reading area (no bubbles)
        public double AreaBlackPct;
        public double Threshold;     // blackPixelPercent needed to count as filled
        public double MaxPct;        // strongest black% actually found (bubbles: max cell)
    }

    public class CheckResult
    {
        public bool GridOk;
        public string Log = "";
        public GridSystemData Grid;
        public bool Rotated;
        public string OmrNo = "?";
        public OmrPage Page;
        public List<RegionRead> Regions = new List<RegionRead>();
    }

    /// <summary>
    /// Reads a filled OMR sheet using a config — the same grid/spacing math the
    /// designer draws and DynamicOmrFiller fills with, so what you see mapped is
    /// exactly what gets read. This is a mapping-verification reader; the
    /// commercial OslOmr.Core engine remains the production authority.
    /// </summary>
    public static class OmrReader
    {
        /// <summary>Check a sheet. NOTE: rotates <paramref name="bmp"/> in place
        /// when the top rotation marker fails and autoRotate is on.</summary>
        public static CheckResult Check(Bitmap bmp, OmrProject proj, bool autoRotate)
        {
            var res = new CheckResult();
            var grid = TryDetect(bmp, res);
            if (grid == null) return res;

            var gray = Gray.From(bmp);

            // ---- rotation detection: top marker empty → sheet is upside down ----
            if (autoRotate && proj.RotationTop != null && proj.RotationTop.Kind != ElementKind.Virtual)
            {
                var box = AreaBox(proj.RotationTop, grid);
                double pct = gray.BlackPercent(box);
                double thr = proj.RotationTop.HasBlackPixel ? proj.RotationTop.BlackPixelPercent : 33;
                if (pct < thr)
                {
                    bmp.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    res.Rotated = true;
                    grid = TryDetect(bmp, res);
                    if (grid == null) return res;
                    gray = Gray.From(bmp);
                }
            }
            res.Grid = grid;
            res.GridOk = true;

            // ---- omrInfo → which OMR page is this sheet ----
            if (proj.OmrInfo != null)
            {
                if (proj.OmrInfo.Kind == ElementKind.Virtual)
                {
                    res.OmrNo = proj.OmrInfo.DefaultValue;
                }
                else
                {
                    var rr = ReadBubbles(proj.OmrInfo, grid, gray, proj);
                    res.OmrNo = rr.Value == "" ? "?" : rr.Value;
                    rr.Value = res.OmrNo;   // member column already says "omrNo"
                    res.Regions.Add(rr);
                }
            }

            // overlay boxes for the rotation markers
            foreach (var m in new[] { proj.RotationTop, proj.RotationBottom })
                if (m != null && m.Kind != ElementKind.Virtual)
                    res.Regions.Add(AreaRead(m, grid, gray, proj, bmp));

            // ---- pick the page (omrNo match, else the first) and read it ----
            res.Page = proj.Pages.FirstOrDefault(pg => pg.OmrNo.ToString() == res.OmrNo)
                       ?? proj.Pages.FirstOrDefault();
            if (res.Page == null) return res;

            foreach (var el in res.Page.Elements)
            {
                if (el.Kind == ElementKind.Virtual)
                {
                    res.Regions.Add(new RegionRead { El = el, Value = el.DefaultValue });
                }
                else if (el.Kind == ElementKind.ImagePart || el.HasResultCount && CharCount(el) <= 1)
                {
                    // whole-area elements: barcode/qrCode/saq boxes, SAQ answer areas
                    res.Regions.Add(AreaRead(el, grid, gray, proj, bmp));
                }
                else
                {
                    res.Regions.Add(ReadBubbles(el, grid, gray, proj));
                }
            }
            return res;
        }

        /// <summary>
        /// Which way up the sheet is, decided by the two rotation markers of the config:
        /// whichever of rotate180Detection (top) / rotate180DetectionAlt (bottom) actually
        /// carries ink says which end is the top, and every other element is then read
        /// from that orientation. +1 = upright, -1 = upside down, 0 = cannot tell.
        /// One grid detection serves both measurements.
        /// </summary>
        public static int OrientationFromMarkers(Bitmap bmp, OmrProject proj)
        {
            if (bmp == null) return 0;
            var top = proj?.RotationTop;
            var alt = proj?.RotationBottom;
            bool hasTop = top != null && top.Kind != ElementKind.Virtual && top.HasResultCount;
            bool hasAlt = alt != null && alt.Kind != ElementKind.Virtual && alt.HasResultCount;
            if (!hasTop || !hasAlt) return 0;   // both are needed to tell the ends apart

            Bitmap straight = null;
            try
            {
                // straighten first — on a skewed sheet the marker areas are measured
                // in the wrong place and either marker can read as blank
                try { straight = GridDetector.Deskew(bmp, out _); } catch { straight = null; }
                Bitmap use = straight ?? bmp;
                GridSystemData g;
                try { g = GridDetector.Detect(use, out _); }
                catch { return 0; }
                if (g == null || !g.IsValid) return 0;

                var gray = Gray.From(use);
                double topInk = gray.BlackPercent(Shrink(AreaBox(top, g), top));
                double altInk = gray.BlackPercent(Shrink(AreaBox(alt, g), alt));
                bool topMarked = topInk >= (top.HasBlackPixel ? top.BlackPixelPercent : 33);
                bool altMarked = altInk >= (alt.HasBlackPixel ? alt.BlackPixelPercent : 33);

                if (topMarked && !altMarked) return 1;
                if (altMarked && !topMarked) return -1;
                if (topMarked && altMarked) return topInk >= altInk ? 1 : -1;   // both inked → stronger wins
                return 0;                                                       // neither → cannot tell
            }
            finally { straight?.Dispose(); }
        }

        /// <summary>
        /// True when the sheet is upside down. Prefers the two-marker test above; when the
        /// config has only a top marker it falls back to measuring that one before and
        /// after a flip. Needs a config — the marker's corner differs per design, so there
        /// is no safe design-independent guess. Returns false when it cannot tell.
        /// </summary>
        public static bool IsUpsideDown(Bitmap bmp, OmrProject proj)
        {
            int orientation = OrientationFromMarkers(bmp, proj);
            if (orientation != 0) return orientation < 0;

            var marker = proj?.RotationTop;
            if (bmp == null || marker == null || marker.Kind == ElementKind.Virtual || !marker.HasResultCount)
                return false;
            double thr = marker.HasBlackPixel ? marker.BlackPixelPercent : 33;

            double upright = MarkerInk(bmp, marker);
            if (upright < 0 || upright >= thr) return false;   // unreadable, or already correct

            using (var flipped = (Bitmap)bmp.Clone())
            {
                flipped.RotateFlip(RotateFlipType.Rotate180FlipNone);
                double turned = MarkerInk(flipped, marker);
                return turned >= thr && turned > upright;
            }
        }

        /// <summary>Ink % inside the marker's reading area, or -1 if the grid fails.
        /// Straightens first: on a skewed sheet the grid drifts and the marker area
        /// would be measured in the wrong place.</summary>
        private static double MarkerInk(Bitmap bmp, OmrElement marker)
        {
            Bitmap straight = null;
            try
            {
                try { straight = GridDetector.Deskew(bmp, out _); } catch { straight = null; }
                Bitmap use = straight ?? bmp;
                GridSystemData g;
                try { g = GridDetector.Detect(use, out _); }
                catch { return -1; }
                if (g == null || !g.IsValid) return -1;
                return Gray.From(use).BlackPercent(Shrink(AreaBox(marker, g), marker));
            }
            finally { straight?.Dispose(); }
        }

        private static GridSystemData TryDetect(Bitmap bmp, CheckResult res)
        {
            try
            {
                var g = GridDetector.Detect(bmp, out string log);
                res.Log += log;
                if (g != null && g.IsValid) return g;
            }
            catch (Exception ex) { res.Log += "\nDetect error: " + ex.Message; }
            return null;
        }

        // ------------------------------------------------------------ reading
        private static RegionRead ReadBubbles(OmrElement el, GridSystemData grid, Gray gray, OmrProject proj)
        {
            var rr = new RegionRead { El = el };
            string[] entries = CharEntries(el);
            int lines = Math.Max(1, el.Lines);
            double thr = el.HasBlackPixel ? el.BlackPixelPercent : proj.BlackPixelPercent;
            rr.Threshold = thr;

            var lineValues = new List<string>();
            for (int li = 0; li < lines; li++)
            {
                var filled = new List<string>();
                for (int ci = 0; ci < entries.Length; ci++)
                {
                    RectangleF cell = Shrink(CellRect(el, grid, li, ci), el);
                    double pct = gray.BlackPercent(cell);
                    bool f = pct >= thr;
                    rr.Bubbles.Add(new BubbleRead { Rect = cell, BlackPct = pct, Filled = f, LineIndex = li, Ch = entries[ci] });
                    if (pct > rr.MaxPct) rr.MaxPct = pct;
                    if (f) filled.Add(entries[ci]);
                }
                string v = filled.Count == 0 ? ""
                         : filled.Count == 1 ? filled[0]
                         : el.UseErrorChar ? "*" : string.Join("", filled);
                lineValues.Add(v);
            }

            // answer blocks show per-question values; everything else concatenates
            rr.Value = el.HasLineStart
                ? string.Join(" ", lineValues.Select((v, i) => $"{el.LineStart + i}:{(v == "" ? "-" : v)}"))
                : string.Join("", lineValues);
            return rr;
        }

        private static RegionRead AreaRead(OmrElement el, GridSystemData grid, Gray gray, OmrProject proj, Bitmap src)
        {
            var rr = new RegionRead { El = el, IsArea = true, Box = AreaBox(el, grid) };
            rr.AreaBlackPct = gray.BlackPercent(Shrink(rr.Box, el));
            double thr = el.HasBlackPixel ? el.BlackPixelPercent : proj.BlackPixelPercent;
            rr.Threshold = thr;
            rr.MaxPct = rr.AreaBlackPct;
            if (el.Kind == ElementKind.ImagePart)
            {
                // decode ONLY real code areas (id contains barcode/qr) — saq question
                // images would give false-positive reads from printed text patterns
                string id = (el.MemberName ?? "").ToLowerInvariant();
                if (id.Contains("barcode") || id.Contains("qr"))
                {
                    if (!ZXingAvailable)
                    {
                        rr.Value = "[zxing.dll missing]";   // library ছাড়া decode হয় না — app চলতে থাকে
                    }
                    else
                    {
                        string decoded;
                        try { decoded = TryDecode(src, rr.Box); }
                        catch { decoded = null; rr.Value = "[zxing.dll missing]"; return rr; }
                        rr.Value = decoded != null
                            ? decoded
                            : $"[no code — {rr.AreaBlackPct:F1}% ink]";
                    }
                }
                else
                {
                    rr.Value = $"[image — {rr.AreaBlackPct:F1}% ink]";
                }
            }
            else
            {
                rr.Value = rr.AreaBlackPct >= thr ? $"marked ({rr.AreaBlackPct:F1}%)" : $"blank ({rr.AreaBlackPct:F1}%)";
            }
            return rr;
        }

        // zxing.dll optional: exe-র পাশে না থাকলে decode বাদ যায়, app crash করে না
        private static bool? _zxingAvailable;
        private static bool ZXingAvailable
        {
            get
            {
                if (_zxingAvailable == null)
                {
                    try { _zxingAvailable = Type.GetType("ZXing.BarcodeReader, zxing") != null; }
                    catch { _zxingAvailable = false; }
                }
                return _zxingAvailable.Value;
            }
        }

        /// <summary>Decode a barcode/QR from the crop; null when nothing readable.
        /// The crop is inflated a little so slight mapping offsets still decode.
        /// NoInlining: ZXing type reference stays INSIDE this method, so a missing
        /// zxing.dll only faults here (caught by the caller), never the whole app.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string TryDecode(Bitmap src, RectangleF box)
        {
            try
            {
                var r = Rectangle.Round(box);
                r.Inflate(r.Width / 8 + 4, r.Height / 8 + 4);
                r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
                if (r.Width < 8 || r.Height < 8) return null;
                using (var crop = src.Clone(r, PixelFormat.Format24bppRgb))
                {
                    var reader = new ZXing.BarcodeReader { AutoRotate = true };
                    reader.Options.TryHarder = true;
                    reader.Options.TryInverted = true;
                    var result = reader.Decode(crop);
                    return result?.Text;
                }
            }
            catch { return null; }
        }

        /// <summary>Reading cell of one position — mirrors the designer's DrawReadingDots math.</summary>
        private static RectangleF CellRect(OmrElement el, GridSystemData g, int li, int ci)
        {
            float ox = g.LineX(el.StartX) + el.PaddingX * g.StepX;
            float oy = g.LineY(el.StartY) + el.PaddingY * g.StepY;
            bool dirX = el.Direction != null && el.Direction.ToLowerInvariant() == "x";
            float bx = ox + (dirX ? ci : li) * g.StepX * (1f + el.SpacingX);
            float by = oy + (dirX ? li : ci) * g.StepY * (1f + el.SpacingY);
            float w = g.StepX, h = g.StepY;
            if (el.HasResultCount)   // omrInfo-style marks are smaller than a full cell
            {
                w = el.ResultCountCellX * g.StepX;
                h = el.ResultCountCellY * g.StepY;
            }
            return new RectangleF(bx, by, w, h);
        }

        private static RectangleF AreaBox(OmrElement el, GridSystemData g)
        {
            float x = g.LineX(el.StartX) + el.PaddingX * g.StepX;
            float y = g.LineY(el.StartY) + el.PaddingY * g.StepY;
            float w = (el.HasResultCount ? el.ResultCountCellX : 1f) * g.StepX;
            float h = (el.HasResultCount ? el.ResultCountCellY : 1f) * g.StepY;
            return new RectangleF(x, y, w, h);
        }

        private static RectangleF Shrink(RectangleF r, OmrElement el)
        {
            if (!el.HasBorderRemove || !el.UseBR) return r;
            float l = r.Width * el.BorderLeft / 100f, ri = r.Width * el.BorderRight / 100f;
            float t = r.Height * el.BorderTop / 100f, b = r.Height * el.BorderBottom / 100f;
            return new RectangleF(r.X + l, r.Y + t,
                Math.Max(1f, r.Width - l - ri), Math.Max(1f, r.Height - t - b));
        }

        private static string[] CharEntries(OmrElement el)
        {
            if (string.IsNullOrEmpty(el.CharSet)) return new[] { "1" };
            if (!string.IsNullOrEmpty(el.CharSetSeparators) && el.CharSet.Contains(el.CharSetSeparators))
                return el.CharSet.Split(el.CharSetSeparators[0]);
            return el.CharSet.Select(c => c.ToString()).ToArray();
        }

        private static int CharCount(OmrElement el) => CharEntries(el).Length;

        // ------------------------------------------------------------ overlay
        // Same palette as OMR Checker: reading areas magenta, hits green, errors red.
        public static Bitmap Render(Bitmap src, CheckResult res, bool drawReading, bool drawGrid)
        {
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.DrawImage(src, 0, 0, src.Width, src.Height);
                if (res.Grid == null) return bmp;

                if (drawGrid)
                    using (var pen = new Pen(Color.FromArgb(120, 128, 128, 128), 1f))
                    {
                        float top = res.Grid.YLines.First(), bot = res.Grid.YLines.Last();
                        float lef = res.Grid.XLines.First(), rig = res.Grid.XLines.Last();
                        foreach (var x in res.Grid.XLines) g.DrawLine(pen, x, top, x, bot);
                        foreach (var y in res.Grid.YLines) g.DrawLine(pen, lef, y, rig, y);
                    }

                using (var reading = new Pen(Color.Magenta, 1.5f))
                using (var hit = new Pen(Color.FromArgb(0, 170, 0), 2.5f))
                using (var hitFill = new SolidBrush(Color.FromArgb(60, 0, 200, 0)))
                using (var lblBr = new SolidBrush(Color.Magenta))
                using (var font = new Font("Arial", 9f, FontStyle.Bold))
                {
                    foreach (var rr in res.Regions)
                    {
                        if (rr.El.Kind == ElementKind.Virtual) continue;
                        if (rr.IsArea)
                        {
                            if (drawReading) g.DrawRectangle(reading, rr.Box.X, rr.Box.Y, rr.Box.Width, rr.Box.Height);
                            g.DrawString(rr.El.MemberName, font, lblBr, rr.Box.X, rr.Box.Y - 14);
                            continue;
                        }
                        foreach (var b in rr.Bubbles)
                        {
                            if (drawReading) g.DrawRectangle(reading, b.Rect.X, b.Rect.Y, b.Rect.Width, b.Rect.Height);
                            if (b.Filled)
                            {
                                g.FillRectangle(hitFill, b.Rect);
                                g.DrawRectangle(hit, b.Rect.X, b.Rect.Y, b.Rect.Width, b.Rect.Height);
                            }
                        }
                        if (rr.Bubbles.Count > 0)
                        {
                            var first = rr.Bubbles[0].Rect;
                            g.DrawString(rr.El.MemberName, font, lblBr, first.X, first.Y - 15);
                        }
                    }
                }
            }
            return bmp;
        }

        // --------------------------------------------------- grayscale sampling
        private sealed class Gray
        {
            private byte[] _data; private int _w, _h, _stride;
            private const byte BlackThreshold = 100;   // same cutoff GridDetector uses

            public static Gray From(Bitmap src)
            {
                var g = new Gray { _w = src.Width, _h = src.Height };
                using (var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
                {
                    using (var gr = Graphics.FromImage(bmp)) gr.DrawImage(src, 0, 0, src.Width, src.Height);
                    var bd = bmp.LockBits(new Rectangle(0, 0, g._w, g._h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    g._stride = bd.Stride;
                    g._data = new byte[bd.Stride * g._h];
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, g._data, 0, g._data.Length);
                    bmp.UnlockBits(bd);
                }
                return g;
            }

            /// <summary>% of pixels darker than the threshold inside the rect (image px).</summary>
            public double BlackPercent(RectangleF r)
            {
                int x0 = Math.Max(0, (int)Math.Floor(r.Left));
                int x1 = Math.Min(_w - 1, (int)Math.Ceiling(r.Right) - 1);
                int y0 = Math.Max(0, (int)Math.Floor(r.Top));
                int y1 = Math.Min(_h - 1, (int)Math.Ceiling(r.Bottom) - 1);
                if (x1 < x0 || y1 < y0) return 0;
                long black = 0, total = 0;
                for (int y = y0; y <= y1; y++)
                {
                    int row = y * _stride;
                    for (int x = x0; x <= x1; x++)
                    {
                        if (_data[row + x * 3] < BlackThreshold) black++;
                        total++;
                    }
                }
                return total == 0 ? 0 : 100.0 * black / total;
            }
        }
    }
}
