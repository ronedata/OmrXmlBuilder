using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace OmrXmlBuilder
{
    /// <summary>
    /// Grid coordinate system detected from an OMR sheet.
    /// XLines / YLines hold the pixel positions of the vertical / horizontal
    /// grid lines. startCircle x/y in the XML are 1-based indices into these lists,
    /// exactly matching how DynamicOmrFiller READS them back.
    /// </summary>
    public class GridSystemData
    {
        public List<float> XLines { get; set; } = new List<float>();
        public List<float> YLines { get; set; } = new List<float>();
        public float StepX { get; set; }
        public float StepY { get; set; }

        /// <summary>Number of usable grid columns (cells), written as gridX.</summary>
        public int GridX => Math.Max(1, XLines.Count - 1);
        /// <summary>Number of usable grid rows (cells), written as gridY.</summary>
        public int GridY => Math.Max(1, YLines.Count - 1);

        public bool IsValid => XLines.Count >= 2 && YLines.Count >= 2;

        // Coordinate scheme outside the grid: NO ZERO — indices run …, -2, -1, 1, 2, …
        // -1 = exactly one cell above/left of line 1 (barcode/qrCode above the grid).
        /// <summary>Pixel position of the (1-based) vertical grid line.
        /// Negative indices extrapolate above/left of line 1 by StepX; index 0
        /// never occurs (treated as line 1 defensively).</summary>
        public float LineX(int index1Based)
        {
            if (XLines.Count == 0) return 0f;
            if (index1Based < 1) return XLines[0] + index1Based * StepX;   // -1 → one step left, 0 → line 1
            int i = index1Based - 1;
            if (i >= XLines.Count) return XLines[XLines.Count - 1] + (i - (XLines.Count - 1)) * StepX;
            return XLines[i];
        }

        public float LineY(int index1Based)
        {
            if (YLines.Count == 0) return 0f;
            if (index1Based < 1) return YLines[0] + index1Based * StepY;   // -1 → one step above, 0 → line 1
            int i = index1Based - 1;
            if (i >= YLines.Count) return YLines[YLines.Count - 1] + (i - (YLines.Count - 1)) * StepY;
            return YLines[i];
        }

        /// <summary>Map an image pixel X to the nearest grid column index.
        /// Outside the grid the index extrapolates as …, -2, -1 (left) or count+1, … (right) — never 0.</summary>
        public int NearestX(float pixelX)
        {
            if (XLines.Count == 0) return 1;
            if (pixelX < XLines[0] && StepX > 0)
            {
                int k = (int)Math.Round((XLines[0] - pixelX) / StepX);   // cells left of line 1
                return k <= 0 ? 1 : -k;
            }
            if (pixelX > XLines[XLines.Count - 1] && StepX > 0)
                return XLines.Count + (int)Math.Round((pixelX - XLines[XLines.Count - 1]) / StepX);
            return NearestIndex(XLines, pixelX) + 1;
        }

        public int NearestY(float pixelY)
        {
            if (YLines.Count == 0) return 1;
            if (pixelY < YLines[0] && StepY > 0)
            {
                int k = (int)Math.Round((YLines[0] - pixelY) / StepY);   // cells above line 1
                return k <= 0 ? 1 : -k;
            }
            if (pixelY > YLines[YLines.Count - 1] && StepY > 0)
                return YLines.Count + (int)Math.Round((pixelY - YLines[YLines.Count - 1]) / StepY);
            return NearestIndex(YLines, pixelY) + 1;
        }

        /// <summary>
        /// Mirror this grid for a 180° page turn. Used instead of re-detecting on the
        /// flipped bitmap: detection anchors each line on the timing mark's TOP edge,
        /// which becomes the BOTTOM edge once the sheet is upside down, so a fresh
        /// detection lands ~0.4 cell off (measured) and every mapped element drifts.
        /// Mirroring is exact — the cells keep covering the same physical paper.
        /// </summary>
        public GridSystemData Rotated180(int imageWidth, int imageHeight)
        {
            var g = new GridSystemData { StepX = StepX, StepY = StepY };
            for (int i = XLines.Count - 1; i >= 0; i--) g.XLines.Add(imageWidth - 1 - XLines[i]);
            for (int i = YLines.Count - 1; i >= 0; i--) g.YLines.Add(imageHeight - 1 - YLines[i]);
            return g;
        }

        private static int NearestIndex(List<float> lines, float value)
        {
            if (lines == null || lines.Count == 0) return 0;
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < lines.Count; i++)
            {
                float d = Math.Abs(lines[i] - value);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }
    }

    /// <summary>
    /// Detects the timing-mark grid on an OMR sheet. This is the same algorithm
    /// used by DynamicOmrFiller (FindMarkerRect / FindTimingMarks) so both tools
    /// agree on grid-cell coordinates. Run on a copy of the bitmap.
    /// </summary>
    public static class GridDetector
    {
        /// <summary>
        /// Locate the three anchor markers (left-top, right-top, left-bottom).
        /// The bottom search window is wide enough to follow a skewed sheet, and is
        /// biased toward the left-top marker's column so it never latches onto
        /// unrelated print.
        /// </summary>
        public static bool FindCorners(Bitmap source, out Rectangle lt, out Rectangle rt, out Rectangle lb)
        {
            using (Bitmap bmp = To24bpp(source))
                return FindCornersCore(bmp, out lt, out rt, out lb);
        }

        private static bool FindCornersCore(Bitmap bmp, out Rectangle lt, out Rectangle rt, out Rectangle lb)
        {
            int w = bmp.Width, h = bmp.Height;
            lt = FindMarkerRect(bmp, 0, 0, w / 5, h / 4, true);
            rt = FindMarkerRect(bmp, w - (w / 5), 0, w, h / 4, true);
            lb = Rectangle.Empty;
            if (lt.Width == 0 || rt.Width == 0) return false;

            // skew moves the bottom marker sideways (h * tan θ), so search a band
            // ~w/10 wide but score against the top marker's centre line
            int pad = Math.Max(20, w / 10);
            lb = FindMarkerRect(bmp, Math.Max(0, lt.X - pad), h * 3 / 4, Math.Min(w, lt.Right + pad), h, false,
                                lt.X + lt.Width / 2, h - 1);
            return lb.Width > 0;
        }

        /// <summary>Sheet rotation in degrees, from the top edge and the left edge of
        /// the marker triangle (positive = clockwise on screen). 0 = perfectly straight.</summary>
        public static float SkewAngleDeg(Rectangle lt, Rectangle rt, Rectangle lb)
        {
            float ltx = lt.X + lt.Width / 2f, lty = lt.Y + lt.Height / 2f;
            float rtx = rt.X + rt.Width / 2f, rty = rt.Y + rt.Height / 2f;
            float lbx = lb.X + lb.Width / 2f, lby = lb.Y + lb.Height / 2f;
            double aTop = Math.Atan2(rty - lty, rtx - ltx);    // top edge tilt  →  +θ
            double aLeft = Math.Atan2(lbx - ltx, lby - lty);   // left edge tilt →  -θ
            return (float)((aTop - aLeft) / 2.0 * 180.0 / Math.PI);
        }

        /// <summary>
        /// Straighten a skewed scan so the timing grid is axis-aligned again — the
        /// same idea as the checker's "Auto Skew Correct". Returns a NEW bitmap (the
        /// caller owns it) or null when the sheet is already straight, the markers
        /// were not found, or the tilt is too large to be plain skew.
        /// </summary>
        public static Bitmap Deskew(Bitmap src, out float angleDeg)
        {
            angleDeg = 0f;
            if (src == null) return null;
            if (!FindCorners(src, out Rectangle lt, out Rectangle rt, out Rectangle lb)) return null;
            float a = SkewAngleDeg(lt, rt, lb);
            if (float.IsNaN(a) || Math.Abs(a) < 0.05f || Math.Abs(a) > 15f) return null;
            angleDeg = a;
            return Rotate(src, -a);
        }

        /// <summary>Rotate about the centre, keeping the canvas size (margins absorb it).</summary>
        public static Bitmap Rotate(Bitmap src, float angleDeg)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);
            using (var g = Graphics.FromImage(dst))
            {
                g.Clear(Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.TranslateTransform(src.Width / 2f, src.Height / 2f);
                g.RotateTransform(angleDeg);
                g.TranslateTransform(-src.Width / 2f, -src.Height / 2f);
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
            return dst;
        }

        public static GridSystemData Detect(Bitmap source, out string log)
        {
            var sb = new System.Text.StringBuilder();
            // Work on a 24bpp copy so the unsafe pixel math has a known stride.
            using (Bitmap bmp = To24bpp(source))
            {
                int w = bmp.Width, h = bmp.Height;

                if (!FindCornersCore(bmp, out Rectangle lt, out Rectangle rt, out Rectangle lb))
                {
                    sb.AppendLine($"LT marker: {lt}, RT marker: {rt}, LB marker: {lb}");
                    sb.AppendLine("Corner markers not found.");
                    log = sb.ToString();
                    return null;
                }
                sb.AppendLine($"LT marker: {lt}, RT marker: {rt}");
                sb.AppendLine($"LB marker: {lb}");

                float skew = SkewAngleDeg(lt, rt, lb);
                sb.AppendLine($"skew={skew:F2}°");
                if (Math.Abs(skew) > 0.3f)
                    sb.AppendLine("WARNING: sheet is skewed — Deskew করলে grid অনেক নিখুঁত হবে.");

                // Follow the left timing column from the top marker down to the bottom
                // marker instead of scanning one fixed X, so a tilted sheet still lands
                // inside the marks. Sampling the darkest pixel across a small window
                // absorbs the residual tilt.
                int scanTopX = lt.X + lt.Width / 2;
                int scanBotX = lb.X + lb.Width / 2;
                int halfWin = Math.Max(2, lt.Width / 4);
                List<Rectangle> gridRows = FindTimingMarks(bmp, scanTopX, scanBotX, lt.Bottom + 5, lb.Top - 5, halfWin);
                sb.AppendLine($"scanX={scanTopX}..{scanBotX} (±{halfWin}), timing marks found={gridRows.Count}");
                if (gridRows.Count < 2)
                {
                    sb.AppendLine("Not enough timing marks to calculate step size.");
                    log = sb.ToString();
                    return null;
                }

                GridSystemData data = new GridSystemData();
                data.YLines.Add(lt.Bottom);
                foreach (var row in gridRows) data.YLines.Add(row.Top);
                data.YLines.Add(lb.Top);

                float totalDistY = gridRows.Last().Top - gridRows.First().Top;
                data.StepY = totalDistY / (gridRows.Count - 1);

                // X lines: anchor to the two top corner markers and divide the span
                // evenly. The column count is derived from the (near-square) cell
                // size, then StepX is recomputed from the exact span so the first
                // line sits on the left marker and the last on the right marker —
                // no drift accumulates across wide sheets.
                float spanX = rt.Left - lt.Right;
                int cols = Math.Max(1, (int)Math.Round(spanX / data.StepY));
                data.StepX = spanX / cols;
                for (int i = 0; i <= cols; i++)
                    data.XLines.Add(lt.Right + i * data.StepX);

                sb.AppendLine($"StepX={data.StepX:F2}, StepY={data.StepY:F2}, cols={cols}, XLines={data.XLines.Count}, YLines={data.YLines.Count}");
                sb.AppendLine($"Suggested gridX={data.GridX}, gridY={data.GridY}");
                log = sb.ToString();
                return data;
            }
        }

        private static Bitmap To24bpp(Bitmap src)
        {
            Bitmap bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
            return bmp;
        }

        /// <summary>Walk the timing-mark column from (xTop, startY) to (xBottom, endY),
        /// taking the darkest pixel within ±halfWindow of the interpolated centre.
        /// On a straight sheet this behaves exactly like the old fixed-X scan.</summary>
        private static List<Rectangle> FindTimingMarks(Bitmap bmp, int xTop, int xBottom, int startY, int endY, int halfWindow)
        {
            List<Rectangle> found = new List<Rectangle>();
            if (startY >= endY) return found;
            BitmapData bData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            bool inBlock = false; int blockStart = -1; int lastCx = xTop;
            int span = Math.Max(1, endY - 1 - startY);
            unsafe
            {
                byte* ptr = (byte*)bData.Scan0;
                for (int y = startY; y < endY; y++)
                {
                    int cx = (int)Math.Round(xTop + (xBottom - xTop) * ((float)(y - startY) / span));
                    lastCx = cx;
                    byte* row = ptr + (y * bData.Stride);
                    byte darkest = 255;
                    for (int dx = -halfWindow; dx <= halfWindow; dx++)
                    {
                        int x = cx + dx;
                        if (x < 0 || x >= bmp.Width) continue;
                        byte v = row[x * 3];
                        if (v < darkest) darkest = v;
                    }
                    if (darkest < 100) { if (!inBlock) { inBlock = true; blockStart = y; } }
                    else if (inBlock) { if (y - blockStart > 3) found.Add(new Rectangle(cx - 10, blockStart, 20, y - blockStart)); inBlock = false; }
                }
                if (inBlock && endY - blockStart > 3)
                    found.Add(new Rectangle(lastCx - 10, blockStart, 20, endY - blockStart));
            }
            bmp.UnlockBits(bData);
            return found;
        }

        private static Rectangle FindMarkerRect(Bitmap bmp, int startX, int startY, int endX, int endY, bool isTopDown,
                                                int wantX = int.MinValue, int wantY = int.MinValue)
        {
            int regionWidth = Math.Max(0, endX - startX);
            int regionHeight = Math.Max(0, endY - startY);
            if (regionWidth == 0 || regionHeight == 0) return Rectangle.Empty;

            BitmapData bData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            bool[] visited = new bool[regionWidth * regionHeight];
            Rectangle bestRect = Rectangle.Empty;
            double bestScore = double.MaxValue;
            bool isRightSide = startX > (bmp.Width / 2);
            // caller may aim the search (skewed sheets: bottom marker sits off-column)
            int targetX = wantX != int.MinValue ? wantX : (isRightSide ? endX - 1 : startX);
            int targetY = wantY != int.MinValue ? wantY : (isTopDown ? startY : endY - 1);

            unsafe
            {
                byte* ptr = (byte*)bData.Scan0;
                int[] stack = new int[regionWidth * regionHeight];

                for (int ry = 0; ry < regionHeight; ry++)
                {
                    int y = startY + ry;
                    byte* row = ptr + (y * bData.Stride);
                    for (int rx = 0; rx < regionWidth; rx++)
                    {
                        int visitIndex = (ry * regionWidth) + rx;
                        if (visited[visitIndex]) continue;
                        visited[visitIndex] = true;

                        int x = startX + rx;
                        if (row[x * 3] >= 80) continue;

                        int stackCount = 0;
                        stack[stackCount++] = visitIndex;
                        int minRx = rx, maxRx = rx, minRy = ry, maxRy = ry;
                        int blackPixels = 0;

                        while (stackCount > 0)
                        {
                            int current = stack[--stackCount];
                            int currentRx = current % regionWidth;
                            int currentRy = current / regionWidth;
                            int currentX = startX + currentRx;
                            int currentY = startY + currentRy;
                            byte* currentRow = ptr + (currentY * bData.Stride);
                            if (currentRow[currentX * 3] >= 80) continue;

                            blackPixels++;
                            if (currentRx < minRx) minRx = currentRx;
                            if (currentRx > maxRx) maxRx = currentRx;
                            if (currentRy < minRy) minRy = currentRy;
                            if (currentRy > maxRy) maxRy = currentRy;

                            if (currentRx > 0)
                            {
                                int left = current - 1;
                                if (!visited[left]) { visited[left] = true; stack[stackCount++] = left; }
                            }
                            if (currentRx + 1 < regionWidth)
                            {
                                int right = current + 1;
                                if (!visited[right]) { visited[right] = true; stack[stackCount++] = right; }
                            }
                            if (currentRy > 0)
                            {
                                int up = current - regionWidth;
                                if (!visited[up]) { visited[up] = true; stack[stackCount++] = up; }
                            }
                            if (currentRy + 1 < regionHeight)
                            {
                                int down = current + regionWidth;
                                if (!visited[down]) { visited[down] = true; stack[stackCount++] = down; }
                            }
                        }

                        int width = (maxRx - minRx) + 1;
                        int height = (maxRy - minRy) + 1;
                        if (blackPixels < 80 || width < 10 || height < 10) continue;

                        double aspectRatio = (double)Math.Max(width, height) / Math.Max(1, Math.Min(width, height));
                        double fillRatio = (double)blackPixels / (width * height);
                        double sizePenalty = Math.Abs(width - 36) + Math.Abs(height - 36);
                        int markerCenterX = startX + minRx + (width / 2);
                        int markerCenterY = startY + minRy + (height / 2);
                        double distancePenalty = Math.Abs(markerCenterX - targetX) + Math.Abs(markerCenterY - targetY);
                        double score = distancePenalty + (Math.Abs(aspectRatio - 1.0) * 200) + (Math.Abs(fillRatio - 1.0) * 150) + (sizePenalty * 2);

                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestRect = new Rectangle(startX + minRx, startY + minRy, width, height);
                        }
                    }
                }
            }
            bmp.UnlockBits(bData);
            return bestRect;
        }
    }
}
