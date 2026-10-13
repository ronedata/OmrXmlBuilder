using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace OmrXmlBuilder
{
    public enum ElementKind
    {
        RotationTop,    // <rotate180Detection>     (design level)
        RotationBottom, // <rotate180DetectionAlt>  (design level)
        OmrInfo,        // <omrInfo>                (design level, page detection)
        Virtual,        // <region isVirtual="true">
        ImagePart,      // <imagePart>
        DataRegion      // <region> (rollNo, reg, answer, SAQ, quality, custom)
    }

    /// <summary>PropertyGrid dropdown offering the two read directions.</summary>
    public class DirectionConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
            => new StandardValuesCollection(new[] { "x", "y" });
    }

    /// <summary>
    /// One OMR element. A single flexible class covers every tag because the
    /// XML format reuses the same building blocks (startCircle / startPadding /
    /// spacing / borderRemovePercent) everywhere. The Kind decides which tag and
    /// which attributes are emitted by XmlConfigBuilder.
    /// </summary>
    public class OmrElement
    {
        [Browsable(false)]
        public ElementKind Kind { get; set; }

        [Category("1. Identity")]
        [DisplayName("Member / Id name")]
        [Description("memberName for regions, or id for imageParts (barcode, qrCode, saq_1 ...).")]
        public string MemberName { get; set; } = "field";

        [Category("1. Identity")]
        [DisplayName("Comment")]
        [Description("Optional XML comment shown above this element.")]
        public string Comment { get; set; } = "";

        /// <summary>Canvas-only visibility — hidden elements stay in the project
        /// and in the generated XML, they just aren't drawn or hit-tested.</summary>
        [Browsable(false)]
        public bool Hidden { get; set; }

        /// <summary>Canvas-only lock. A locked element can still be selected — that
        /// is how you unlock it — but it cannot be dragged, resized, nudged or
        /// flipped by an accidental click. Not part of the generated XML.</summary>
        [Browsable(false)]
        public bool Locked { get; set; }

        // ---- Position (grid-cell indices, 1-based) ----
        [Category("2. Position (grid cells)")]
        [DisplayName("startCircle X")]
        [Description("Grid column index: …, -2, -1, 1, 2, … (0 হয় না). -1 = column 1-এর এক cell বাঁয়ে.")]
        public int StartX { get; set; } = 1;

        [Category("2. Position (grid cells)")]
        [DisplayName("startCircle Y")]
        [Description("Grid row index: …, -2, -1, 1, 2, … (0 হয় না). -1 = row 1-এর এক cell উপরে (barcode/qrCode). এক cell-এর কম হলে y=1 রেখে startPadding negative করুন.")]
        public int StartY { get; set; } = 1;

        [Category("2. Position (grid cells)")]
        [DisplayName("startPadding X")]
        public float PaddingX { get; set; } = 0f;

        [Category("2. Position (grid cells)")]
        [DisplayName("startPadding Y")]
        public float PaddingY { get; set; } = 0f;

        // ---- Reading layout ----
        [Category("3. Layout")]
        [DisplayName("direction")]
        [Description("'y' = read down a column, 'x' = read across a row.")]
        [TypeConverter(typeof(DirectionConverter))]
        public string Direction { get; set; } = "y";

        [Category("3. Layout")]
        [DisplayName("charSet")]
        [Description("Allowed marks. e.g. 0123456789, ABCD, 1, S,H,E, 1,2")]
        public string CharSet { get; set; } = "1";

        [Category("3. Layout")]
        [DisplayName("charSetSeparators")]
        [Description("Separator used inside charSet (e.g. ',').")]
        public string CharSetSeparators { get; set; } = "";

        [Category("3. Layout")]
        [DisplayName("lines")]
        [Description("Number of repeated columns (direction y) or rows (direction x).")]
        public int Lines { get; set; } = 1;

        [Category("3. Layout")]
        [DisplayName("spacing X")]
        public float SpacingX { get; set; } = 0.5f;

        [Category("3. Layout")]
        [DisplayName("spacing Y")]
        public float SpacingY { get; set; } = 0.5f;

        [Category("3. Layout")]
        [DisplayName("has <spacing>")]
        [Description("Emit a <spacing> tag (off for imageParts / markers).")]
        public bool HasSpacing { get; set; } = true;

        // ---- Read flags ----
        [Category("4. Read flags")]
        [DisplayName("useSeparator")]
        public bool UseSeparator { get; set; } = false;

        [Category("4. Read flags")]
        [DisplayName("useErrorChar")]
        public bool UseErrorChar { get; set; } = true;

        [Category("4. Read flags")]
        [DisplayName("joinOutput")]
        public bool JoinOutput { get; set; } = false;

        [Category("4. Read flags")]
        [DisplayName("emit joinOutput")]
        public bool HasJoinOutput { get; set; } = false;

        [Category("4. Read flags")]
        [DisplayName("joinSeparator")]
        public string JoinSeparator { get; set; } = "";

        [Category("4. Read flags")]
        [DisplayName("emit joinSeparator")]
        public bool HasJoinSeparator { get; set; } = false;

        [Category("4. Read flags")]
        [DisplayName("lineStart")]
        public int LineStart { get; set; } = 1;

        [Category("4. Read flags")]
        [DisplayName("emit lineStart")]
        public bool HasLineStart { get; set; } = false;

        // ---- result count cell (size of an image/result area) ----
        [Category("5. Result area")]
        [DisplayName("resultCountCellX")]
        public float ResultCountCellX { get; set; } = 1f;

        [Category("5. Result area")]
        [DisplayName("resultCountCellY")]
        public float ResultCountCellY { get; set; } = 1f;

        [Category("5. Result area")]
        [DisplayName("emit resultCountCell")]
        public bool HasResultCount { get; set; } = false;

        [Category("5. Result area")]
        [DisplayName("blackPixelPercent")]
        public double BlackPixelPercent { get; set; } = 0;

        [Category("5. Result area")]
        [DisplayName("emit blackPixelPercent")]
        public bool HasBlackPixel { get; set; } = false;

        // ---- border remove ----
        [Category("6. Border remove")]
        [DisplayName("emit <borderRemovePercent>")]
        public bool HasBorderRemove { get; set; } = true;

        [Category("6. Border remove")]
        [DisplayName("useBR")]
        public bool UseBR { get; set; } = true;

        [Category("6. Border remove")]
        [DisplayName("top")]
        public float BorderTop { get; set; } = 21f;

        [Category("6. Border remove")]
        [DisplayName("left")]
        public float BorderLeft { get; set; } = 21f;

        [Category("6. Border remove")]
        [DisplayName("bottom")]
        public float BorderBottom { get; set; } = 21f;

        [Category("6. Border remove")]
        [DisplayName("right")]
        public float BorderRight { get; set; } = 21f;

        // ---- virtual ----
        [Category("7. Virtual")]
        [DisplayName("defaultValue")]
        [Description("For isVirtual regions: the fixed value written to output.")]
        public string DefaultValue { get; set; } = "X";

        /// <summary>Shallow copy — all values are primitives/strings, so this is a full clone.</summary>
        public OmrElement Clone() => (OmrElement)MemberwiseClone();

        /// <summary>
        /// What the canvas and the element list call this element. The rotation
        /// markers carry no memberName in the XML, so a loaded config leaves them
        /// named after their tag ("rotate180Detection"); show the short palette
        /// names instead. Display only — MemberName and the XML are untouched.
        /// </summary>
        [Browsable(false)]
        public string DisplayName
        {
            get
            {
                switch (Kind)
                {
                    case ElementKind.RotationTop: return "Rot. Top";
                    case ElementKind.RotationBottom: return "Rot. Bottom";
                    default: return MemberName;
                }
            }
        }

        public override string ToString()
        {
            string tag;
            switch (Kind)
            {
                case ElementKind.RotationTop: tag = "rotate180Detection"; break;
                case ElementKind.RotationBottom: tag = "rotate180DetectionAlt"; break;
                case ElementKind.OmrInfo: tag = "omrInfo"; break;
                case ElementKind.Virtual: tag = "virtual"; break;
                case ElementKind.ImagePart: tag = "imagePart"; break;
                default: tag = "region"; break;
            }
            if (Kind == ElementKind.Virtual)
                return $"[{tag}] {MemberName} = {DefaultValue}";
            return $"[{tag}] {DisplayName}  (x={StartX}, y={StartY})";
        }
    }

    public class OmrPage
    {
        public int OmrNo { get; set; }
        public List<OmrElement> Elements { get; set; } = new List<OmrElement>();
        public override string ToString() => $"OMR Page {OmrNo}";
    }

    public class OmrProject
    {
        public string PageName { get; set; } = "UMS";
        public string DesignFor { get; set; } = "custom_omr";
        public string MappingStyle { get; set; } = "grid-y-read-generate";
        public int GridX { get; set; } = 54;
        public int GridY { get; set; } = 58;
        public int BlackPixelPercent { get; set; } = 60;

        // design-level elements
        public OmrElement RotationTop { get; set; }
        public OmrElement RotationBottom { get; set; }
        public OmrElement OmrInfo { get; set; }

        public List<OmrPage> Pages { get; set; } = new List<OmrPage>();

        public OmrProject()
        {
            Pages.Add(new OmrPage { OmrNo = 1 });
        }

        public int NextSaqNumber()
        {
            int max = 0;
            foreach (var pg in Pages)
                foreach (var e in pg.Elements)
                    if (e.Kind == ElementKind.DataRegion && e.MemberName != null && e.MemberName.StartsWith("SaqAnswer"))
                    {
                        int n;
                        if (int.TryParse(e.MemberName.Substring("SaqAnswer".Length), out n) && n > max) max = n;
                    }
            return max + 1;
        }
    }

    /// <summary>
    /// Factory for new elements pre-filled with the conventions seen in the
    /// reference configs (Academic_top_10 and College_Admission_15_SAQ).
    /// </summary>
    public static class RegionTemplates
    {
        public static OmrElement RollNo() => new OmrElement
        {
            Kind = ElementKind.DataRegion, MemberName = "rollNo", Comment = "ROLL NO",
            Direction = "y", CharSet = "0123456789", Lines = 11,
            PaddingX = 0.5f, PaddingY = 0f, SpacingX = 0.5f, SpacingY = 0.5f,
            UseSeparator = false, UseErrorChar = true,
            HasBorderRemove = true, UseBR = true, BorderTop = 14, BorderLeft = 14, BorderBottom = 14, BorderRight = 14
        };

        public static OmrElement RegNo() => new OmrElement
        {
            Kind = ElementKind.DataRegion, MemberName = "registrationNo", Comment = "REGISTRATION NUMBER",
            Direction = "y", CharSet = "0123456789", Lines = 7,
            PaddingX = 0.5f, PaddingY = 0f, SpacingX = 0.5f, SpacingY = 0.5f,
            UseSeparator = false, UseErrorChar = true,
            HasBorderRemove = true, UseBR = true, BorderTop = 14, BorderLeft = 14, BorderBottom = 14, BorderRight = 14
        };

        public static OmrElement Answer() => new OmrElement
        {
            Kind = ElementKind.DataRegion, MemberName = "answer", Comment = "ANSWER",
            Direction = "x", CharSet = "ABCD", Lines = 10,
            PaddingX = 0.03f, PaddingY = 0.03f, SpacingX = 0.5f, SpacingY = 0.5f,
            UseSeparator = true, UseErrorChar = false,
            HasJoinOutput = true, JoinOutput = false, HasLineStart = true, LineStart = 1,
            HasBorderRemove = true, UseBR = true, BorderTop = 21, BorderLeft = 21, BorderBottom = 21, BorderRight = 21
        };

        public static OmrElement QuestionQuality() => new OmrElement
        {
            Kind = ElementKind.DataRegion, MemberName = "QuestionQuality", Comment = "QUESTION QUALITY",
            Direction = "y", CharSet = "S,H,E", CharSetSeparators = ",", Lines = 1,
            PaddingX = 0.56f, PaddingY = 0f, SpacingX = 1f, SpacingY = 1f,
            UseSeparator = true, UseErrorChar = false,
            HasBorderRemove = true, UseBR = true, BorderTop = 21, BorderLeft = 21, BorderBottom = 21, BorderRight = 21
        };

        public static OmrElement Barcode() => new OmrElement
        {
            Kind = ElementKind.ImagePart, MemberName = "barcode", Comment = "BARCODE",
            Direction = "x", CharSet = "1", Lines = 1,
            HasResultCount = true, ResultCountCellX = 16.8f, ResultCountCellY = 5.5f,
            PaddingX = 0f, PaddingY = 0f, HasSpacing = false,
            HasBorderRemove = true, UseBR = false, BorderTop = 0, BorderLeft = 0, BorderBottom = 0, BorderRight = 0
        };

        public static OmrElement QrCode() => new OmrElement
        {
            Kind = ElementKind.ImagePart, MemberName = "qrCode", Comment = "QR CODE",
            Direction = "x", CharSet = "1", Lines = 1,
            HasResultCount = true, ResultCountCellX = 8f, ResultCountCellY = 7.5f,
            PaddingX = 0.5f, PaddingY = 0f, HasSpacing = false,
            HasBorderRemove = true, UseBR = false, BorderTop = 0, BorderLeft = 0, BorderBottom = 0, BorderRight = 0
        };

        /// <summary>The SAQ image box (where the printed question goes).</summary>
        public static OmrElement SaqBox(int n) => new OmrElement
        {
            Kind = ElementKind.ImagePart, MemberName = "saq_" + n, Comment = "QUESTION-" + n.ToString("D3"),
            Direction = "x", CharSet = "1", Lines = 1,
            HasResultCount = true, ResultCountCellX = 58.5f, ResultCountCellY = 7f,
            PaddingX = 0.5f, PaddingY = 0.5f, HasSpacing = false,
            HasBorderRemove = true, UseBR = true, BorderTop = 0, BorderLeft = 0, BorderBottom = 0, BorderRight = 0
        };

        /// <summary>The SAQ answer region (the written-answer line).</summary>
        public static OmrElement SaqAnswer(int n) => new OmrElement
        {
            Kind = ElementKind.DataRegion, MemberName = "SaqAnswer" + n, Comment = "",
            Direction = "y", CharSet = "1", Lines = 1,
            HasResultCount = true, ResultCountCellX = 57f, ResultCountCellY = 4.8f,
            UseErrorChar = true, UseSeparator = false,
            HasJoinOutput = true, JoinOutput = false, HasJoinSeparator = true, JoinSeparator = "",
            HasBlackPixel = true, BlackPixelPercent = 0.8,
            PaddingX = 0f, PaddingY = 0.5f, HasSpacing = false,
            HasBorderRemove = true, UseBR = true, BorderTop = 2.25f, BorderLeft = 2.5f, BorderBottom = 1.0f, BorderRight = 2.5f
        };

        public static OmrElement Virtual(string name, string value) => new OmrElement
        {
            Kind = ElementKind.Virtual, MemberName = name, DefaultValue = value, Comment = name.ToUpper()
        };

        public static OmrElement RotationTop() => new OmrElement
        {
            Kind = ElementKind.RotationTop, MemberName = "rotationTop", Comment = "Auto Rotation - TOP",
            Direction = "x", CharSet = "1", Lines = 1,
            HasResultCount = true, ResultCountCellX = 0.5f, ResultCountCellY = 1f,
            HasBlackPixel = true, BlackPixelPercent = 33,
            PaddingX = 0f, PaddingY = 0f, HasSpacing = false,
            HasBorderRemove = true, UseBR = false, BorderTop = 0, BorderLeft = 0, BorderBottom = 0, BorderRight = 0
        };

        public static OmrElement RotationBottom() => new OmrElement
        {
            Kind = ElementKind.RotationBottom, MemberName = "rotationBottom", Comment = "Auto Rotation - BOTTOM",
            Direction = "x", CharSet = "1", Lines = 1,
            HasResultCount = true, ResultCountCellX = 0.5f, ResultCountCellY = 1f,
            HasBlackPixel = true, BlackPixelPercent = 33,
            PaddingX = 0.5f, PaddingY = 0.35f, HasSpacing = false,
            HasBorderRemove = true, UseBR = false, BorderTop = 0, BorderLeft = 0, BorderBottom = 0, BorderRight = 0
        };

        public static OmrElement OmrInfo() => new OmrElement
        {
            Kind = ElementKind.OmrInfo, MemberName = "omrNo", Comment = "OMR NO",
            Direction = "y", CharSet = "1,2", CharSetSeparators = ",", Lines = 1,
            UseSeparator = false, UseErrorChar = true,
            HasResultCount = true, ResultCountCellX = 0.5f, ResultCountCellY = 1f,
            HasBlackPixel = true, BlackPixelPercent = 33,
            PaddingX = 0.8f, PaddingY = 0f, HasSpacing = true, SpacingX = 1f, SpacingY = 1f,
            HasBorderRemove = false
        };

        public static OmrElement Custom() => new OmrElement
        {
            Kind = ElementKind.DataRegion, MemberName = "custom", Comment = "CUSTOM",
            Direction = "y", CharSet = "0123456789", Lines = 1,
            PaddingX = 0f, PaddingY = 0f, SpacingX = 0.5f, SpacingY = 0.5f,
            UseSeparator = false, UseErrorChar = true,
            HasBorderRemove = true, UseBR = true, BorderTop = 21, BorderLeft = 21, BorderBottom = 21, BorderRight = 21
        };
    }
}
