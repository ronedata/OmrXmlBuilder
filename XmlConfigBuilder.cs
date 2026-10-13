using System.Globalization;
using System.Security;
using System.Text;

namespace OmrXmlBuilder
{
    /// <summary>
    /// Serializes an OmrProject to OMR config XML, matching the layout of the
    /// reference files so DynamicOmrFiller can read it back unchanged.
    /// </summary>
    public static class XmlConfigBuilder
    {
        public static string Build(OmrProject p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"<Page name=\"{Esc(p.PageName)}\">");
            sb.AppendLine();
            sb.AppendLine($"  <design designFor=\"{Esc(p.DesignFor)}\" mappingStyle=\"{Esc(p.MappingStyle)}\" gridX=\"{p.GridX}\" gridY=\"{p.GridY}\" blackPixelPercent=\"{p.BlackPixelPercent}\">");
            sb.AppendLine();

            // Design-level rotation markers
            if (p.RotationTop != null) WriteMarker(sb, p.RotationTop, "rotate180Detection", 2);
            if (p.RotationBottom != null) WriteMarker(sb, p.RotationBottom, "rotate180DetectionAlt", 2);

            // Design-level omrInfo (page detection)
            if (p.OmrInfo != null) WriteOmrInfo(sb, p.OmrInfo, 2);

            // Each omr page
            foreach (var page in p.Pages)
            {
                sb.AppendLine($"    <omr omrNo=\"{page.OmrNo}\">");
                sb.AppendLine();
                foreach (var e in page.Elements)
                    WriteElement(sb, e, 3);
                sb.AppendLine("    </omr>");
                sb.AppendLine();
            }

            sb.AppendLine("  </design>");
            sb.AppendLine("</Page>");
            return sb.ToString();
        }

        private static void WriteElement(StringBuilder sb, OmrElement e, int indentLevel)
        {
            switch (e.Kind)
            {
                case ElementKind.Virtual: WriteVirtual(sb, e, indentLevel); break;
                case ElementKind.ImagePart: WriteImagePart(sb, e, indentLevel); break;
                case ElementKind.OmrInfo: WriteOmrInfo(sb, e, indentLevel); break;
                case ElementKind.RotationTop: WriteMarker(sb, e, "rotate180Detection", indentLevel); break;
                case ElementKind.RotationBottom: WriteMarker(sb, e, "rotate180DetectionAlt", indentLevel); break;
                default: WriteRegion(sb, e, indentLevel); break;
            }
        }

        private static void WriteVirtual(StringBuilder sb, OmrElement e, int lvl)
        {
            string ind = Ind(lvl);
            Comment(sb, e, ind);
            sb.AppendLine($"{ind}<region isVirtual=\"true\" defaultValue=\"{Esc(e.DefaultValue)}\" memberName=\"{Esc(e.MemberName)}\" />");
            sb.AppendLine();
        }

        private static void WriteImagePart(StringBuilder sb, OmrElement e, int lvl)
        {
            string ind = Ind(lvl);
            Comment(sb, e, ind);
            sb.Append($"{ind}<imagePart id=\"{Esc(e.MemberName)}\" direction=\"{Esc(e.Direction)}\" charSet=\"{Esc(e.CharSet)}\" lines=\"{e.Lines}\"");
            if (e.HasResultCount)
                sb.Append($" resultCountCellX=\"{F(e.ResultCountCellX)}\" resultCountCellY=\"{F(e.ResultCountCellY)}\"");
            sb.AppendLine(">");
            sb.AppendLine($"{ind}  <startCircle x=\"{e.StartX}\" y=\"{e.StartY}\" />");
            sb.AppendLine($"{ind}  <startPadding x=\"{F(e.PaddingX)}\" y=\"{F(e.PaddingY)}\" />");
            WriteBorder(sb, e, ind + "  ");
            sb.AppendLine($"{ind}</imagePart>");
            sb.AppendLine();
        }

        private static void WriteMarker(StringBuilder sb, OmrElement e, string tag, int lvl)
        {
            string ind = Ind(lvl);
            Comment(sb, e, ind);
            sb.Append($"{ind}<{tag} direction=\"{Esc(e.Direction)}\" charSet=\"{Esc(e.CharSet)}\" lines=\"{e.Lines}\"");
            if (e.HasResultCount)
                sb.Append($" resultCountCellX=\"{F(e.ResultCountCellX)}\" resultCountCellY=\"{F(e.ResultCountCellY)}\"");
            if (e.HasBlackPixel)
                sb.Append($" blackPixelPercent=\"{F(e.BlackPixelPercent)}\"");
            sb.AppendLine(">");
            sb.AppendLine($"{ind}  <startCircle x=\"{e.StartX}\" y=\"{e.StartY}\" />");
            sb.AppendLine($"{ind}  <startPadding x=\"{F(e.PaddingX)}\" y=\"{F(e.PaddingY)}\" />");
            WriteBorder(sb, e, ind + "  ");
            sb.AppendLine($"{ind}</{tag}>");
            sb.AppendLine();
        }

        private static void WriteOmrInfo(StringBuilder sb, OmrElement e, int lvl)
        {
            string ind = Ind(lvl);
            Comment(sb, e, ind);
            // virtual omrInfo (fixed page number, nothing read from the sheet)
            if (e.Kind == ElementKind.Virtual)
            {
                sb.AppendLine($"{ind}<omrInfo isVirtual=\"true\" defaultValue=\"{Esc(e.DefaultValue)}\" memberName=\"{Esc(e.MemberName)}\" />");
                sb.AppendLine();
                return;
            }
            sb.Append($"{ind}<omrInfo direction=\"{Esc(e.Direction)}\" charSet=\"{Esc(e.CharSet)}\"");
            if (!string.IsNullOrEmpty(e.CharSetSeparators))
                sb.Append($" charSetSeparators=\"{Esc(e.CharSetSeparators)}\"");
            sb.Append($" lines=\"{e.Lines}\" useSeparator=\"{B(e.UseSeparator)}\" useErrorChar=\"{B(e.UseErrorChar)}\"");
            if (e.HasResultCount)
                sb.Append($" resultCountCellX=\"{F(e.ResultCountCellX)}\" resultCountCellY=\"{F(e.ResultCountCellY)}\"");
            if (e.HasBlackPixel)
                sb.Append($" blackPixelPercent=\"{F(e.BlackPixelPercent)}\"");
            sb.AppendLine($" memberName=\"{Esc(e.MemberName)}\">");
            sb.AppendLine($"{ind}  <startCircle x=\"{e.StartX}\" y=\"{e.StartY}\" />");
            sb.AppendLine($"{ind}  <startPadding x=\"{F(e.PaddingX)}\" y=\"{F(e.PaddingY)}\" />");
            if (e.HasSpacing)
                sb.AppendLine($"{ind}  <spacing x=\"{F(e.SpacingX)}\" y=\"{F(e.SpacingY)}\" />");
            WriteBorder(sb, e, ind + "  ");
            sb.AppendLine($"{ind}</omrInfo>");
            sb.AppendLine();
        }

        private static void WriteRegion(StringBuilder sb, OmrElement e, int lvl)
        {
            string ind = Ind(lvl);
            Comment(sb, e, ind);
            sb.Append($"{ind}<region direction=\"{Esc(e.Direction)}\" charSet=\"{Esc(e.CharSet)}\"");
            if (!string.IsNullOrEmpty(e.CharSetSeparators))
                sb.Append($" charSetSeparators=\"{Esc(e.CharSetSeparators)}\"");
            sb.Append($" lines=\"{e.Lines}\"");
            if (e.HasResultCount)
                sb.Append($" resultCountCellX=\"{F(e.ResultCountCellX)}\" resultCountCellY=\"{F(e.ResultCountCellY)}\"");
            sb.Append($" useSeparator=\"{B(e.UseSeparator)}\" useErrorChar=\"{B(e.UseErrorChar)}\"");
            if (e.HasJoinOutput)
                sb.Append($" joinOutput=\"{B(e.JoinOutput)}\"");
            if (e.HasJoinSeparator)
                sb.Append($" joinSeparator=\"{Esc(e.JoinSeparator)}\"");
            if (e.HasLineStart)
                sb.Append($" lineStart=\"{e.LineStart}\"");
            if (e.HasBlackPixel)
                sb.Append($" blackPixelPercent=\"{F(e.BlackPixelPercent)}\"");
            sb.AppendLine($" memberName=\"{Esc(e.MemberName)}\">");
            sb.AppendLine($"{ind}  <startCircle x=\"{e.StartX}\" y=\"{e.StartY}\" />");
            sb.AppendLine($"{ind}  <startPadding x=\"{F(e.PaddingX)}\" y=\"{F(e.PaddingY)}\" />");
            if (e.HasSpacing)
                sb.AppendLine($"{ind}  <spacing x=\"{F(e.SpacingX)}\" y=\"{F(e.SpacingY)}\" />");
            WriteBorder(sb, e, ind + "  ");
            sb.AppendLine($"{ind}</region>");
            sb.AppendLine();
        }

        private static void WriteBorder(StringBuilder sb, OmrElement e, string ind)
        {
            if (!e.HasBorderRemove) return;
            sb.AppendLine($"{ind}<borderRemovePercent useBR=\"{B(e.UseBR)}\" top=\"{F(e.BorderTop)}\" left=\"{F(e.BorderLeft)}\" bottom=\"{F(e.BorderBottom)}\" right=\"{F(e.BorderRight)}\" />");
        }

        private static void Comment(StringBuilder sb, OmrElement e, string ind)
        {
            if (!string.IsNullOrEmpty(e.Comment))
                sb.AppendLine($"{ind}<!-- {e.Comment} -->");
        }

        private static string Ind(int level) => new string(' ', level * 2);
        private static string B(bool v) => v ? "true" : "false";
        private static string Esc(string s) => s == null ? "" : SecurityElement.Escape(s);

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
