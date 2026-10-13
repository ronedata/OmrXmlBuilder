using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace OmrXmlBuilder
{
    /// <summary>
    /// Parses an OMR config XML back into an OmrProject — the inverse of
    /// XmlConfigBuilder. Used by the Check-OMR mode (read a generated file
    /// end-to-end) and by "Open XML" (edit an existing config in the designer).
    /// Tolerant of the hand-written reference configs: attribute casing,
    /// ".45"-style floats and missing optional attributes are all accepted.
    /// </summary>
    public static class ConfigReader
    {
        public static OmrProject Load(string path)
        {
            var doc = XDocument.Load(path);
            var pageTag = doc.Element("Page") ?? throw new FormatException("<Page> root not found.");
            var design = pageTag.Element("design") ?? throw new FormatException("<design> not found.");

            var p = new OmrProject();
            p.Pages.Clear();
            p.PageName = (string)pageTag.Attribute("name") ?? "UMS";
            p.DesignFor = (string)design.Attribute("designFor") ?? "custom_omr";
            p.MappingStyle = (string)design.Attribute("mappingStyle") ?? "grid-y-read-generate";
            p.GridX = I(design.Attribute("gridX"), 54);
            p.GridY = I(design.Attribute("gridY"), 58);
            p.BlackPixelPercent = I(design.Attribute("blackPixelPercent"), 60);

            var rt = design.Element("rotate180Detection");
            if (rt != null) p.RotationTop = ParseElement(rt, ElementKind.RotationTop);
            var rb = design.Element("rotate180DetectionAlt");
            if (rb != null) p.RotationBottom = ParseElement(rb, ElementKind.RotationBottom);
            var oi = design.Element("omrInfo");
            if (oi != null) p.OmrInfo = ParseElement(oi, ElementKind.OmrInfo);

            // some configs (Academic_top_10 …) put barcode / qrCode imageParts at the
            // DESIGN level — shared by every page — instead of inside each <omr>.
            // Collect them here so they still map; they are added to each page below.
            var designImageParts = new List<OmrElement>();
            foreach (var e in design.Elements("imagePart"))
                designImageParts.Add(ParseElement(e, ElementKind.ImagePart));

            foreach (var omr in design.Elements("omr"))
            {
                var page = new OmrPage { OmrNo = I(omr.Attribute("omrNo"), p.Pages.Count + 1) };
                // design-level barcode/qrCode belong to every page — clone them in first
                foreach (var ip in designImageParts) page.Elements.Add(ip.Clone());
                foreach (var e in omr.Elements())
                {
                    switch (e.Name.LocalName)
                    {
                        case "region": page.Elements.Add(ParseElement(e, ElementKind.DataRegion)); break;
                        case "imagePart": page.Elements.Add(ParseElement(e, ElementKind.ImagePart)); break;
                        // reference configs sometimes nest an omrInfo inside a page
                        case "omrInfo": page.Elements.Add(ParseElement(e, ElementKind.OmrInfo)); break;
                    }
                }
                p.Pages.Add(page);
            }
            if (p.Pages.Count == 0)
            {
                // no <omr> tags at all — still surface any design-level imageParts
                var page = new OmrPage { OmrNo = 1 };
                foreach (var ip in designImageParts) page.Elements.Add(ip.Clone());
                p.Pages.Add(page);
            }
            return p;
        }

        private static OmrElement ParseElement(XElement e, ElementKind kind)
        {
            if (Bo(e.Attribute("isVirtual"), false))
                return new OmrElement
                {
                    Kind = ElementKind.Virtual,
                    MemberName = (string)e.Attribute("memberName") ?? "field",
                    DefaultValue = (string)e.Attribute("defaultValue") ?? "",
                    Comment = PrecedingComment(e)
                };

            var el = new OmrElement
            {
                Kind = kind,
                MemberName = (string)e.Attribute("memberName") ?? (string)e.Attribute("id") ?? e.Name.LocalName,
                Comment = PrecedingComment(e),
                Direction = ((string)e.Attribute("direction") ?? "y").ToLowerInvariant(),
                CharSet = (string)e.Attribute("charSet") ?? "1",
                CharSetSeparators = (string)e.Attribute("charSetSeparators") ?? "",
                Lines = I(e.Attribute("lines"), 1),
                UseSeparator = Bo(e.Attribute("useSeparator"), false),
                UseErrorChar = Bo(e.Attribute("useErrorChar"), false)
            };

            var rcx = e.Attribute("resultCountCellX");
            var rcy = e.Attribute("resultCountCellY");
            el.HasResultCount = rcx != null || rcy != null;
            if (rcx != null) el.ResultCountCellX = F(rcx, 1f);
            if (rcy != null) el.ResultCountCellY = F(rcy, 1f);

            var bp = e.Attribute("blackPixelPercent");
            el.HasBlackPixel = bp != null;
            if (bp != null) el.BlackPixelPercent = D(bp, 0);

            var jo = e.Attribute("joinOutput");
            el.HasJoinOutput = jo != null;
            if (jo != null) el.JoinOutput = Bo(jo, false);

            var js = e.Attribute("joinSeparator");
            el.HasJoinSeparator = js != null;
            if (js != null) el.JoinSeparator = js.Value;

            var ls = e.Attribute("lineStart");
            el.HasLineStart = ls != null;
            if (ls != null) el.LineStart = I(ls, 1);

            var sc = e.Element("startCircle");
            el.StartX = I(sc?.Attribute("x"), 1);
            el.StartY = I(sc?.Attribute("y"), 1);
            var sp = e.Element("startPadding");
            el.PaddingX = F(sp?.Attribute("x"), 0f);
            el.PaddingY = F(sp?.Attribute("y"), 0f);

            var spc = e.Element("spacing");
            el.HasSpacing = spc != null;
            if (spc != null)
            {
                el.SpacingX = F(spc.Attribute("x"), 0.5f);
                el.SpacingY = F(spc.Attribute("y"), 0.5f);
            }
            else
            {
                el.SpacingX = 0f;
                el.SpacingY = 0f;
            }

            var br = e.Element("borderRemovePercent");
            el.HasBorderRemove = br != null;
            if (br != null)
            {
                el.UseBR = Bo(br.Attribute("useBR"), true);
                el.BorderTop = F(br.Attribute("top"), 0f);
                el.BorderLeft = F(br.Attribute("left"), 0f);
                el.BorderBottom = F(br.Attribute("bottom"), 0f);
                el.BorderRight = F(br.Attribute("right"), 0f);
            }
            return el;
        }

        /// <summary>The XML comment directly above the element, if any.</summary>
        private static string PrecedingComment(XElement e)
        {
            var prev = e.PreviousNode;
            while (prev is XText) prev = prev.PreviousNode;   // skip whitespace
            return (prev as XComment)?.Value.Trim() ?? "";
        }

        private static int I(XAttribute a, int def)
            => a != null && int.TryParse(a.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;

        private static float F(XAttribute a, float def)
            => a != null && float.TryParse(a.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;

        private static double D(XAttribute a, double def)
            => a != null && double.TryParse(a.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

        private static bool Bo(XAttribute a, bool def)
            => a != null ? a.Value.Trim().ToLowerInvariant() == "true" : def;
    }
}
