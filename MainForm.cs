using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OmrXmlBuilder
{
    public class MainForm : Form
    {
        private OmrProject _project = new OmrProject();
        private GridSystemData _grid;   // active page's grid — ALWAYS in upright sheet space
        private float _offX;            // active page's X offset on the composite canvas (screen px)
        private float _offY = BandTop;  // active page's Y offset (row-wrapped layout)
        private bool _flip;             // active page is displayed turned end-for-end
        private float _imgW, _imgH;     // active page's bitmap size (needed to mirror)

        // Pages whose bitmap is displayed upside down (Rotate 180°). The grid and every
        // element coordinate stay in UPRIGHT sheet space; only drawing and mouse input are
        // mirrored, so a turned view still puts each element on the same piece of paper.
        private readonly HashSet<OmrPage> _flipped = new HashSet<OmrPage>();

        /// <summary>Mirror a rectangle end-for-end within an image of the given size.</summary>
        private static RectangleF Mirror(RectangleF r, float w, float h)
            => new RectangleF(w - (r.X + r.Width), h - (r.Y + r.Height), r.Width, r.Height);

        // per-page image + detected grid (multi-image mapping: one sheet per OMR page)
        private readonly Dictionary<OmrPage, Bitmap> _pageImages = new Dictionary<OmrPage, Bitmap>();
        private readonly Dictionary<OmrPage, GridSystemData> _pageGrids = new Dictionary<OmrPage, GridSystemData>();

        // grid layout: pages with an image flow left→right, wrapping to a new row
        // after PerRow sheets. Each sheet has its OWN zoom; OffX/OffY are SCREEN px.
        private class PageView
        {
            public OmrPage Page; public Bitmap Bmp; public GridSystemData Grid;
            public float OffX;          // screen px
            public float OffY;          // screen px (each row reserves its own BandTop)
            public bool Flip;           // bitmap shown turned end-for-end
            public float Zoom = 1f;     // per-sheet zoom
            public float W => Bmp.Width * Zoom;
            public float H => Bmp.Height * Zoom;
        }
        private readonly List<PageView> _views = new List<PageView>();
        private readonly Dictionary<OmrPage, float> _pageZoom = new Dictionary<OmrPage, float>();
        private readonly HashSet<OmrPage> _selPages = new HashSet<OmrPage>();   // sheets selected for zooming
        private const int PerRow = 4;        // sheets per canvas row (multi-image wrap)
        private const float PageGap = 40f;   // gap between sheets (screen px)
        private const float BandTop = 26f;   // ruler band above each sheet for column numbers (screen px)
        private const float BandLeft = 26f;  // ruler band left of each sheet for row numbers (screen px)

        private PageView ActiveView { get { var pg = CurrentPage(); return _views.FirstOrDefault(v => v.Page == pg); } }
        private PageView ViewAt(float x, float y) =>
            _views.FirstOrDefault(v => x >= v.OffX && x <= v.OffX + v.W && y >= v.OffY && y <= v.OffY + v.H);
        private float TotalW { get { float w = 0; foreach (var v in _views) w = Math.Max(w, v.OffX + v.W); return w; } }
        private float TotalH { get { float h = 0; foreach (var v in _views) h = Math.Max(h, v.OffY + v.H); return h; } }
        private float _zoom = 1f;
        private bool _showGrid = true;
        private bool _autoSkew = true;     // straighten scans as they are opened
        private bool _autoRotate = true;   // flip upside-down scans (needs a config)
        private string _pendingType;
        private readonly List<OmrElement> _listItems = new List<OmrElement>();

        // Custom region — what the prompt asked for, kept so the next Custom
        // click re-opens with the same values (usually several similar fields).
        private string _customName = "Custom name";
        private string _customCharSet = "0123456789";
        private int _customLines = 11;

        // drag-to-move state — one or MANY selected elements move together
        private List<OmrElement> _dragEls;
        private List<PointF> _dragOrig;        // each element's origin at grab (sheet-local px)
        private float _dragCurX, _dragCurY;    // cursor position at grab (sheet-local px)
        private bool _didDrag;

        // pan state (middle / right mouse button drags the image)
        private bool _panning;
        private Point _panStartMouse;     // screen coords at pan start
        private Point _panStartLoc;       // image location at pan start

        // drag-to-resize state (edge / corner handles of the selected element)
        // delta-based: pull right → width/X grows, pull down → height/Y grows, any handle.
        private OmrElement _resizeEl;
        private float _resStartW, _resStartH;     // footprint size in grid cells at grab time
        private float _resMouseX, _resMouseY;     // mouse position in image (un-zoomed) px at grab time

        // controls
        private Panel _canvasPanel;
        private PictureBox _pic;
        private PropertyGrid _propGrid;
        private ListView _lstElements;         // checkbox = show/hide; multi-select = group move/remove
        private bool _suppressCheck;           // guards ItemCheck during list rebuilds
        private HashSet<OmrElement> _paintSel; // selection snapshot used during painting

        /// <summary>First selected element (or null).</summary>
        private OmrElement SelEl()
            => _lstElements != null && _lstElements.SelectedItems.Count > 0
               ? _lstElements.SelectedItems[0].Tag as OmrElement : null;

        /// <summary>App version (major.minor.patch) read from AssemblyInfo — the one
        /// place the version is defined. Shown in the title bar and the About box.</summary>
        public static string AppVersion
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        /// <summary>All selected elements, list order.</summary>
        private List<OmrElement> SelEls()
        {
            var l = new List<OmrElement>();
            if (_lstElements == null) return l;
            foreach (ListViewItem it in _lstElements.SelectedItems)
                if (it.Tag is OmrElement e) l.Add(e);
            return l;
        }

        /// <summary>Select exactly this element in the list (null clears).</summary>
        private void SelectElement(OmrElement el)
        {
            if (_lstElements == null) return;
            _lstElements.SelectedIndices.Clear();
            if (el == null) return;
            foreach (ListViewItem it in _lstElements.Items)
                if (ReferenceEquals(it.Tag, el)) { it.Selected = true; it.EnsureVisible(); break; }
        }
        private ComboBox _cmbPage;
        private ToolStripStatusLabel _lblStatus;
        private TextBox _txtPageName, _txtDesignFor, _txtGridX, _txtGridY, _txtBlackPixel;
        private ComboBox _cmbVirtual;
        private TextBox _txtMcqCount, _txtMcqOptions;
        private bool _suppressMcq;   // guards the MCQ input boxes against feedback loops
        private TextBox _txtSaqNo;
        private bool _suppressSaqNo; // guards the SAQ-number box against feedback loops
        private TextBox _txtOmrNo;
        private bool _suppressOmrNo; // guards the omrNo box against feedback loops

        // tooltips replace the old tips box — every control explains itself on hover
        private readonly ToolTip _tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 200 };
        private Panel _propPanel;    // collapsible container of the PropertyGrid
        private Button _propHeader;
        private bool _propExpanded;  // collapsed by default

        public MainForm()
        {
            Text = $"OMR XML Builder  v{AppVersion}  —  click an OMR sheet to generate config XML";
            Icon = Icons.App;
            Width = 1300;
            Height = 860;
            StartPosition = FormStartPosition.CenterScreen;
            BuildToolbar();
            BuildLayout();
            RefreshPageCombo();
            RefreshElementList();
        }

        // ----------------------------------------------------------------- UI
        private void BuildToolbar()
        {
            var ts = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                ImageScalingSize = new Size(16, 16),
                Padding = new Padding(2, 1, 2, 1)
            };
            ts.Items.Add(MakeButton("Image", OnOpenImage,
                "Open Image — OMR sheet image খুলুন\nএকাধিক select করলে প্রতিটা আলাদা page হয়\nফাঁকা canvas-এ double-click করলেও খোলে", "image"));
            ts.Items.Add(MakeButton("XML", (s, e) => OnOpenXml(),
                "Open XML — পুরনো config designer-এ খুলে এডিট করুন", "xml"));
            ts.Items.Add(MakeButton("Detect", (s, e) => DetectGrid(true),
                "Detect Grid — corner marker / timing mark থেকে grid আবার detect করুন", "detect"));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(MakeButton("180°", (s, e) => RotateActiveImage(),
                "Rotate 180° — active sheet-এর image ঘোরে, grid ও mapping অপরিবর্তিত\n(Rotation Bottom marker বসানোর জন্য; আবার চাপলে ফিরে আসে)", "rot180"));
            ts.Items.Add(MakeButton("Deskew", (s, e) => DeskewActiveImage(),
                "বাঁকা scan সোজা করুন — corner marker দেখে কোণ মেপে ঘোরায়,\nতারপর grid আবার detect হয় (বাঁকা থাকলে grid এলোমেলো হয়)", "deskew"));
            var chkSkew = new ToolStripButton("A-Skew", Icons.Get("autoskew"))
            { Checked = true, CheckOnClick = true, ToolTipText = "Auto Skew — image open করার সময় নিজে নিজেই skew ঠিক করবে" };
            chkSkew.CheckedChanged += (s, e) => _autoSkew = chkSkew.Checked;
            ts.Items.Add(chkSkew);
            var chkAutoRot = new ToolStripButton("A-Rot", Icons.Get("autorot"))
            {
                Checked = true, CheckOnClick = true,
                ToolTipText = "Auto Rotate — Top না Bottom, যে marker-এ কালি আছে সেই প্রান্ত থেকে\nelement গুলো বসবে (omrNo, barcode, qrCode, rollNo …)।\nছবি ঘোরে না, শুধু grid-এর হিসাব ঘোরে — Open XML করার পর কাজ করে"
            };
            chkAutoRot.CheckedChanged += (s, e) => _autoRotate = chkAutoRot.Checked;
            ts.Items.Add(chkAutoRot);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(MakeButton("Undo", (s, e) => Undo(),
                "Undo (Ctrl+Z) — শেষ পরিবর্তন ফেরান\nmove / resize / nudge / add / remove / property সব", "undo"));
            var chk = new ToolStripButton("Grid", Icons.Get("grid"))
            { Checked = true, CheckOnClick = true, ToolTipText = "Show Grid — detect করা grid-এর লাল দাগ দেখান / লুকান" };
            chk.CheckedChanged += (s, e) => { _showGrid = chk.Checked; _pic?.Invalidate(); };
            ts.Items.Add(chk);
            ts.Items.Add(MakeButton("", (s, e) => ZoomSelected(1.25f),
                "Zoom in — mouse wheel-ও কাজ করে\nCtrl+click-এ একাধিক sheet select করে একসাথে zoom", "zoomin"));
            ts.Items.Add(MakeButton("", (s, e) => ZoomSelected(1f / 1.25f), "Zoom out", "zoomout"));
            ts.Items.Add(MakeButton("Fit", (s, e) => FitZoom(), "সব sheet window-এ ফিট করুন", "fit"));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(MakeButton("Generate", (s, e) => GenerateXml(),
                "Generate XML — mapping থেকে config XML বানিয়ে দেখুন / save করুন", "generate"));
            ts.Items.Add(MakeButton("Check", (s, e) => OpenCheckOmr(),
                "Check OMR — বর্তমান image + XML নিয়েই খোলে ও সাথে সাথে পড়ে\n(magenta reading area, সবুজ detected mark; black % ও দরকারি % দেখায়)", "check"));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(MakeButton("Reset", (s, e) => ResetAll(),
                "সব image, page, element মুছে নতুন প্রোজেক্ট (confirm চাইবে)", "reset"));

            // "?" — hover shows tips, click shows the About / version box
            var help = new ToolStripButton("", Icons.Get("help")) { Alignment = ToolStripItemAlignment.Right };
            help.Click += (s, e) => ShowAbout();
            help.ToolTipText =
                $"OMR XML Builder v{AppVersion}   (click → About)\n\n" +
                "Mouse / Keyboard টিপস:\n" +
                "• Element টেনে ধরুন → সরান (সূক্ষ্মভাবে)\n" +
                "• সাদা handle টানুন → ডানে = spacing X, নিচে = Y\n" +
                "• Element-এ double-click → direction x ↔ y বদলায়\n" +
                "• Arrow key → 0.1 cell nudge (Shift = 0.5)\n" +
                "• Ctrl+D duplicate · Ctrl+Z undo · Ctrl+L lock/unlock\n" +
                "• Locked element ([L], কাটা-কাটা border) সরে না, resize হয় না\n" +
                "• Wheel → selected sheet zoom · Ctrl+click sheet → একাধিক select\n" +
                "• Middle/Right-drag বা ফাঁকা জায়গায় drag → image সরান\n" +
                "• ফাঁকা জায়গায় double-click → image open\n" +
                "• Grid-এর উপরে টানলে → y=1 + negative padding, তারপর -1, -2 …\n" +
                "   (0 কখনো হয় না — barcode/qrCode grid-এর উপরে বসাতে)";
            ts.Items.Add(help);
            Controls.Add(ts);
        }

        /// <summary>About / version dialog (opened from the ? button).</summary>
        private void ShowAbout()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var full = asm.GetName().Version.ToString();
            string zxing = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zxing.dll"))
                ? "zxing.dll পাওয়া গেছে — barcode/QR decode চালু"
                : "zxing.dll নেই — barcode/QR decode বন্ধ";
            MessageBox.Show(this,
                $"OMR XML Builder\n\nVersion {AppVersion}   (build {full})\n" +
                ".NET Framework 4.8  ·  WinForms\n\n" +
                zxing + "\n\n" +
                "OMR sheet-এ click করে config XML বানানোর টুল।\n" +
                "পরিবর্তনের তালিকা: CHANGELOG.md",
                "About — OMR XML Builder", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private ToolStripButton MakeButton(string text, EventHandler h, string tip = null, string icon = null)
        {
            var b = icon == null ? new ToolStripButton(text) : new ToolStripButton(text, Icons.Get(icon));
            b.DisplayStyle = icon == null ? ToolStripItemDisplayStyle.Text
                           : string.IsNullOrEmpty(text) ? ToolStripItemDisplayStyle.Image
                           : ToolStripItemDisplayStyle.ImageAndText;
            if (tip != null) b.ToolTipText = tip;
            b.Click += h;
            return b;
        }

        private void BuildLayout()
        {
            // right tools panel
            var right = new Panel { Dock = DockStyle.Right, Width = 390 };
            Controls.Add(right);

            // collapsible Properties section — collapsed by default, click header to open
            _propPanel = new Panel { Dock = DockStyle.Bottom, Height = 26 };
            _propHeader = new Button
            {
                Dock = DockStyle.Top, Height = 26,
                Text = "  ▸  Properties  (বিস্তারিত এডিট — click করে খুলুন)",
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(235, 238, 243)
            };
            _propHeader.FlatAppearance.BorderSize = 0;
            _propHeader.Click += (s, e) => TogglePropPanel();
            _tip.SetToolTip(_propHeader, "Selected element-এর সব property (position, spacing, charSet, border …)");

            _propGrid = new PropertyGrid { Dock = DockStyle.Fill };
            _propGrid.PropertyValueChanged += (s, e) =>
            {
                // undo entry that restores the edited property's OLD value:
                // capture the post-change state, then patch the changed element's
                // snapshot back to the pre-change value
                if (_propGrid.SelectedObject is OmrElement el)
                {
                    var st = CaptureUndo("edit: " + (e.ChangedItem?.Label ?? "property"));
                    if (st.Snapshots.TryGetValue(el, out OmrElement snap))
                        try { e.ChangedItem?.PropertyDescriptor?.SetValue(snap, e.OldValue); } catch { }
                    Push(st);
                }
                RefreshElementList(true);
                _pic?.Invalidate();
            };
            _propPanel.Controls.Add(_propGrid);
            _propPanel.Controls.Add(_propHeader);
            right.Controls.Add(_propPanel);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };
            right.Controls.Add(flow);
            flow.BringToFront();

            flow.Controls.Add(BuildDesignGroup());
            flow.Controls.Add(BuildPaletteGroup());
            flow.Controls.Add(BuildPagesGroup());

            // canvas (free-pan: image position is moved directly, no scrollbars)
            _canvasPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = Color.FromArgb(60, 60, 65) };
            _canvasPanel.MouseWheel += Canvas_MouseWheel;
            _canvasPanel.DoubleClick += (s, e) => OnOpenImage(s, e);   // double-click empty canvas to open
            Controls.Add(_canvasPanel);
            _canvasPanel.BringToFront();

            _pic = new PictureBox { SizeMode = PictureBoxSizeMode.StretchImage, Location = new Point(0, 0) };
            _pic.Paint += Pic_Paint;
            _pic.MouseClick += Pic_MouseClick;
            _pic.MouseDown += Pic_MouseDown;
            _pic.MouseMove += Pic_MouseMove;
            _pic.MouseUp += Pic_MouseUp;
            _pic.MouseDoubleClick += Pic_MouseDoubleClick;
            _pic.MouseWheel += Canvas_MouseWheel;
            _pic.TabStop = true;   // let the canvas take keyboard focus for arrow-key nudging
            _pic.PreviewKeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                    e.IsInputKey = true;   // route arrows to KeyDown instead of control navigation
            };
            _pic.KeyDown += Canvas_KeyDown;
            _canvasPanel.Controls.Add(_pic);

            // status bar — a Spring status label fills the whole strip and never clips
            var status = new StatusStrip();
            _lblStatus = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            status.Items.Add(_lblStatus);
            Controls.Add(status);
            SetStatus("Open an OMR image to begin.");
        }

        private GroupBox BuildDesignGroup()
        {
            var gb = new GroupBox { Text = "Design Settings", Width = 360, Height = 170 };
            int y = 22;
            _txtPageName = AddLabeledText(gb, "Page name", _project.PageName, ref y);
            _txtDesignFor = AddLabeledText(gb, "designFor", _project.DesignFor, ref y);
            _txtGridX = AddLabeledText(gb, "gridX", _project.GridX.ToString(), ref y);
            _txtGridY = AddLabeledText(gb, "gridY", _project.GridY.ToString(), ref y);
            _txtBlackPixel = AddLabeledText(gb, "blackPixelPercent", _project.BlackPixelPercent.ToString(), ref y);
            return gb;
        }

        private TextBox AddLabeledText(Control parent, string label, string value, ref int y)
        {
            parent.Controls.Add(new Label { Text = label, Left = 10, Top = y + 3, Width = 130 });
            var tb = new TextBox { Left = 145, Top = y, Width = 200, Text = value };
            parent.Controls.Add(tb);
            y += 28;
            return tb;
        }

        // Add-Element palette — mapping workflow-এর ক্রমে সাজানো:
        // rotation → omrNo → hasSaq → barcode/qr → roll/reg → answer/saq → quality
        private GroupBox BuildPaletteGroup()
        {
            var gb = new GroupBox { Text = "Add Element  (click button, then click on sheet)", Width = 360 };
            int y = 20;

            // ১. Auto Rotation
            gb.Controls.Add(PaletteButton("Rot. Top", "rotTop", 10, y, 165));
            gb.Controls.Add(PaletteButton("Rot. Bottom", "rotBottom", 185, y, 165));
            y += 33;

            // ২. OMR No
            gb.Controls.Add(PaletteButton("OMR No", "omrInfo", 10, y, 165));
            y += 33;

            // ৩. hasSaq (virtual)
            var hasSaq = new Button { Text = "hasSaq  (virtual)", Left = 10, Top = y, Width = 165, Height = 30 };
            _tip.SetToolTip(hasSaq, "Virtual region hasSaq=true — SAQ-ওয়ালা design-এ লাগে\n(sheet-এ কিছু পড়ে না, output-এ fixed value যায়)");
            hasSaq.Click += (s, e) => AddVirtualNamed("hasSaq", "true");
            gb.Controls.Add(hasSaq);
            y += 33;

            // ৪. Barcode / QR Code
            gb.Controls.Add(PaletteButton("Barcode", "barcode", 10, y, 165));
            gb.Controls.Add(PaletteButton("QR Code", "qrcode", 185, y, 165));
            y += 33;

            // ৫. Roll / Registration
            gb.Controls.Add(PaletteButton("Roll No", "rollNo", 10, y, 165));
            gb.Controls.Add(PaletteButton("Reg. Number", "regNo", 185, y, 165));
            y += 33;

            // ৬. MCQ Answer / SAQ Answer — MCQ-এর প্রশ্ন সংখ্যা ও option ঠিক নিচেই
            // MCQ Answer asks for the two numbers first (like Custom), then places.
            var mcq = new Button { Text = "MCQ Answer", Left = 10, Top = y, Width = 165, Height = 30 };
            _tip.SetToolTip(mcq,
                "MCQ answer block — click করলে প্রশ্ন সংখ্যা ও option চাইবে,\n" +
                "তারপর sheet-এর উপর জায়গামতো click করুন\n" +
                "(নিচের ঘর দুটোতেও একই মান বসে — বসানো block-এ সরাসরি এডিট করা যায়)");
            mcq.Click += (s, e) =>
            {
                if (!PromptMcqAnswer()) { _pendingType = null; SetStatus("MCQ Answer বাতিল হলো."); return; }
                _pendingType = "answer";
                SetStatus($"Placing MCQ Answer ({_txtMcqCount.Text} প্রশ্ন × {_txtMcqOptions.Text} option) — click the start-circle position on the sheet.");
            };
            gb.Controls.Add(mcq);
            gb.Controls.Add(PaletteButton("SAQ Answer", "saq", 185, y, 165));
            y += 33;

            gb.Controls.Add(new Label { Text = "প্রশ্ন সংখ্যা", Left = 22, Top = y + 3, Width = 70 });
            _txtMcqCount = new TextBox { Left = 95, Top = y, Width = 45, Text = "10" };
            gb.Controls.Add(_txtMcqCount);
            gb.Controls.Add(new Label { Text = "option", Left = 155, Top = y + 3, Width = 45 });
            _txtMcqOptions = new TextBox { Left = 202, Top = y, Width = 40, Text = "4" };
            gb.Controls.Add(_txtMcqOptions);
            gb.Controls.Add(new Label { Text = "(A,B,C,D…)", Left = 252, Top = y + 3, Width = 80, ForeColor = Color.Gray });
            _tip.SetToolTip(_txtMcqCount, "Selected MCQ Answer block-এর প্রশ্ন সংখ্যা (lines)\nনতুন block বসানোর সময়ও এই মান ব্যবহার হয়");
            _tip.SetToolTip(_txtMcqOptions, "Option সংখ্যা — 4 হলে charSet ABCD, 5 হলে ABCDE");
            _txtMcqCount.TextChanged += (s, e) => OnMcqInputChanged();
            _txtMcqOptions.TextChanged += (s, e) => OnMcqInputChanged();
            y += 33;

            // SAQ number — select করা saq_N/SaqAnswerN জোড়ার নম্বর টাইপ করে বদলান
            gb.Controls.Add(new Label { Text = "SAQ no", Left = 22, Top = y + 3, Width = 70 });
            _txtSaqNo = new TextBox { Left = 95, Top = y, Width = 45, Enabled = false };
            gb.Controls.Add(_txtSaqNo);
            gb.Controls.Add(new Label { Text = "(নতুন নম্বর লিখে Enter)", Left = 155, Top = y + 3, Width = 190, ForeColor = Color.Gray });
            _tip.SetToolTip(_txtSaqNo,
                "Selected SAQ জোড়ার (saq_N + SaqAnswerN) নম্বর\nনতুন নম্বর লিখে Enter — অন্য জোড়ার সাথে মিললে দুটো swap হয়");
            _txtSaqNo.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplySaqNo(); e.Handled = e.SuppressKeyPress = true; } };
            _txtSaqNo.Leave += (s, e) => ApplySaqNo();
            y += 33;

            // ৭. Question Quality (+ custom region)
            gb.Controls.Add(PaletteButton("Q. Quality", "quality", 10, y, 165));
            // Custom asks for memberName / charSet / lines first — those differ every time.
            var custom = new Button { Text = "Custom", Left = 185, Top = y, Width = 165, Height = 30 };
            _tip.SetToolTip(custom,
                "নিজের মতো region — click করলে memberName, charSet ও lines চাইবে,\n" +
                "তারপর sheet-এর উপর জায়গামতো click করুন");
            custom.Click += (s, e) =>
            {
                if (!PromptCustomRegion()) { _pendingType = null; SetStatus("Custom region বাতিল হলো."); return; }
                _pendingType = "custom";
                SetStatus($"Placing '{_customName}' ({_customCharSet} × {_customLines} lines) — click the start-circle position on the sheet.");
            };
            gb.Controls.Add(custom);
            y += 33;

            // অন্যান্য virtual (unit, group, shiftCode, quota, setcode)
            gb.Controls.Add(new Label { Text = "Virtual:", Left = 10, Top = y + 4, Width = 50 });
            _cmbVirtual = new ComboBox { Left = 62, Top = y, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbVirtual.Items.AddRange(new object[] { "unit", "group", "shiftCode", "quota", "setcode", "hasSaq" });
            _cmbVirtual.SelectedIndex = 0;
            gb.Controls.Add(_cmbVirtual);
            var addV = new Button { Text = "+ Add", Left = 185, Top = y - 1, Width = 80 };
            addV.Click += (s, e) => AddVirtual();
            _tip.SetToolTip(addV, "Virtual region — sheet-এ কিছু পড়ে না, output-এ fixed value যায়");
            gb.Controls.Add(addV);
            y += 33;

            gb.Height = y + 8;
            return gb;
        }

        // Properties section open/close (collapsed by default)
        private void TogglePropPanel()
        {
            _propExpanded = !_propExpanded;
            _propPanel.Height = _propExpanded ? 306 : 26;
            _propHeader.Text = _propExpanded
                ? "  ▾  Properties  (click করে বন্ধ করুন)"
                : "  ▸  Properties  (বিস্তারিত এডিট — click করে খুলুন)";
        }

        // charSet letters for N options: 4 -> "ABCD", 5 -> "ABCDE".
        private static string OptionsToCharSet(int n)
        {
            n = Math.Max(1, Math.Min(26, n));
            var chars = new char[n];
            for (int i = 0; i < n; i++) chars[i] = (char)('A' + i);
            return new string(chars);
        }

        // Live-apply the input boxes to the selected MCQ Answer element.
        private void OnMcqInputChanged()
        {
            if (_suppressMcq) return;
            if (!(SelEl() is OmrElement el) || !IsMcqAnswer(el)) return;

            PushUndo("mcq: " + el.MemberName, true);
            if (int.TryParse(_txtMcqCount.Text, out int q) && q >= 1) el.Lines = q;
            if (int.TryParse(_txtMcqOptions.Text, out int o) && o >= 1) el.CharSet = OptionsToCharSet(o);

            _propGrid.Refresh();
            RefreshElementList(true);
            _pic.Invalidate();
            SetStatus($"MCQ '{el.MemberName}': {CharCount(el)} options × {el.Lines} questions.");
        }

        // Reflect the given element's values into the MCQ input boxes (no feedback).
        private void SyncMcqBoxes(OmrElement el)
        {
            if (_txtMcqCount == null || _txtMcqOptions == null) return;
            _suppressMcq = true;
            bool isMcq = el != null && IsMcqAnswer(el);
            _txtMcqCount.Enabled = _txtMcqOptions.Enabled = isMcq;
            if (isMcq)
            {
                _txtMcqCount.Text = el.Lines.ToString();
                _txtMcqOptions.Text = CharCount(el).ToString();
            }
            _suppressMcq = false;
        }

        private Button PaletteButton(string text, string type, int left, int top, int width)
        {
            var b = new Button { Text = text, Left = left, Top = top, Width = width, Height = 30 };
            _tip.SetToolTip(b, $"'{text}' বসাতে click করুন, তারপর sheet-এর উপর জায়গামতো click\n(বসানোর পর টেনে বা arrow key দিয়ে সূক্ষ্মভাবে সরানো যায়)");
            b.Click += (s, e) =>
            {
                _pendingType = type;
                SetStatus($"Placing '{text}' — click the start-circle position on the sheet.");
            };
            return b;
        }

        private GroupBox BuildPagesGroup()
        {
            var gb = new GroupBox { Text = "OMR Pages & Elements  (✓ = show / hide,  [L] = locked)", Width = 360, Height = 288 };
            _cmbPage = new ComboBox { Left = 10, Top = 22, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbPage.SelectedIndexChanged += (s, e) => ShowCurrentPage();   // swap image + grid + element list
            gb.Controls.Add(_cmbPage);

            var addPage = new Button { Text = "+ Page", Left = 170, Top = 21, Width = 80 };
            addPage.Click += (s, e) => AddPage();
            _tip.SetToolTip(addPage, "নতুন OMR page (<omr omrNo=..>) যোগ করুন");
            gb.Controls.Add(addPage);
            var delPage = new Button { Text = "- Page", Left = 255, Top = 21, Width = 80 };
            delPage.Click += (s, e) => RemovePage();
            _tip.SetToolTip(delPage, "Active page মুছুন (Ctrl+Z দিলে image সহ ফিরে আসে)");
            gb.Controls.Add(delPage);

            // omrNo of the active page — editable
            gb.Controls.Add(new Label { Text = "omrNo", Left = 10, Top = 55, Width = 50 });
            _txtOmrNo = new TextBox { Left = 60, Top = 52, Width = 55, Text = "1" };
            _txtOmrNo.TextChanged += (s, e) => OnOmrNoChanged();
            _tip.SetToolTip(_txtOmrNo, "Active sheet-এর omrNo — XML-এর <omr omrNo=..>");
            gb.Controls.Add(_txtOmrNo);

            // reorder the selected element within its page (= XML output order)
            var upEl = new Button { Text = "▲ Up", Left = 170, Top = 50, Width = 80 };
            upEl.Click += (s, e) => MoveSelectedElement(-1);
            _tip.SetToolTip(upEl, "Selected element উপরে — list order = XML output order");
            gb.Controls.Add(upEl);
            var downEl = new Button { Text = "▼ Down", Left = 255, Top = 50, Width = 80 };
            downEl.Click += (s, e) => MoveSelectedElement(+1);
            _tip.SetToolTip(downEl, "Selected element নিচে — list order = XML output order");
            gb.Controls.Add(downEl);

            // checkbox = element show/hide (XML output is unaffected);
            // Ctrl/Shift+click = multi-select → একসাথে move (drag/arrow) বা remove
            _lstElements = new ListView
            {
                Left = 10, Top = 82, Width = 340, Height = 103,
                View = View.Details, CheckBoxes = true, MultiSelect = true,
                FullRowSelect = true, HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.None
            };
            _lstElements.Columns.Add("", 315);
            _lstElements.SelectedIndexChanged += (s, e) =>
            {
                var el = SelEl();
                if (el != null) _propGrid.SelectedObject = el;
                SyncMcqBoxes(el);     // fill the MCQ boxes from the selected element
                SyncSaqBox(el);       // fill the SAQ-number box likewise
                _pic?.Invalidate();   // redraw so handles/highlight follow the selection
            };
            _lstElements.ItemCheck += (s, e) =>
            {
                if (_suppressCheck) return;
                if (!(_lstElements.Items[e.Index].Tag is OmrElement el)) return;
                PushUndo("visibility: " + el.MemberName, true);
                el.Hidden = e.NewValue != CheckState.Checked;
                _pic?.Invalidate();
                SetStatus(el.Hidden
                    ? $"'{el.MemberName}' hidden — canvas-এ দেখাবে না (XML-এ থাকবে)."
                    : $"'{el.MemberName}' visible.");
            };
            _tip.SetToolTip(_lstElements,
                "Checkbox = element show/hide (XML-এ ঠিকই থাকবে)\n" +
                "[L] = locked — সরানো / resize করা যাবে না (Ctrl+L দিয়ে toggle)\n" +
                "Ctrl/Shift+click = একাধিক select → একসাথে drag/arrow-move, Delete = remove\n" +
                "Arrow key = 0.1 cell nudge (Shift = 0.5)");
            gb.Controls.Add(_lstElements);

            var dupEl = new Button { Text = "Duplicate (Ctrl+D)", Left = 10, Top = 190, Width = 165 };
            dupEl.Click += (s, e) => DuplicateSelectedElement();
            _tip.SetToolTip(dupEl, "Selected element-এর কপি — original-এর ঠিক পরে বসে\nMCQ answer হলে lineStart নিজে নিজে এগিয়ে যায়");
            gb.Controls.Add(dupEl);
            var delEl = new Button { Text = "Remove element", Left = 185, Top = 190, Width = 165 };
            delEl.Click += (s, e) => RemoveSelectedElement();
            _tip.SetToolTip(delEl, "Selected element মুছুন (Ctrl+Z-এ ফিরে আসে)");
            gb.Controls.Add(delEl);

            var lockEl = new Button { Text = "🔒 Lock / Unlock  (Ctrl+L)", Left = 10, Top = 220, Width = 340 };
            lockEl.Click += (s, e) => ToggleLockSelected();
            _tip.SetToolTip(lockEl,
                "Selected element lock / unlock — locked element টেনে, handle দিয়ে,\n" +
                "arrow key দিয়ে সরানো যায় না, double-click-এ direction-ও বদলায় না।\n" +
                "List-এ [L] লেখা থাকে, canvas-এ কাটা-কাটা border দেখায়।\n" +
                "Select করা যায় (তাই unlock-ও করা যায়) আর Properties থেকে এডিট করা যায়।");
            gb.Controls.Add(lockEl);

            var copyPage = new Button { Text = "Copy ALL elements → অন্য page…", Left = 10, Top = 250, Width = 340 };
            copyPage.Click += (s, e) => CopyPageElementsTo();
            _tip.SetToolTip(copyPage,
                "Active page-এর সব element অন্য page-এ (বা নতুন page-এ) copy করুন\nsaq_N / SaqAnswerN নম্বর নিজে থেকে continue হয় (যেমন 1-12 → 13-24)");
            gb.Controls.Add(copyPage);
            return gb;
        }

        // ------------------------------------------------------------- actions
        private void OnOpenImage(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
                Multiselect = true,
                Title = "Open OMR image(s) — select multiple for multi-page mapping"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    // each file goes to the next page WITHOUT an image (never overwrites an
                    // existing sheet) — new pages are created as needed, so every opened
                    // image becomes its own <omr omrNo=..> in the generated XML
                    var files = dlg.FileNames;
                    int idx = Math.Max(0, _project.Pages.IndexOf(CurrentPage()));
                    OmrPage firstAssigned = null;
                    string skewNote = "";
                    foreach (var file in files)
                    {
                        while (idx < _project.Pages.Count &&
                               _pageImages.TryGetValue(_project.Pages[idx], out Bitmap taken) && taken != null)
                            idx++;
                        if (idx >= _project.Pages.Count)
                        {
                            int no = _project.Pages.Count == 0 ? 1 : _project.Pages.Max(p => p.OmrNo) + 1;
                            _project.Pages.Add(new OmrPage { OmrNo = no });
                        }
                        var pg = _project.Pages[idx];
                        var bytes = File.ReadAllBytes(file);
                        var opened = (Bitmap)Image.FromStream(new MemoryStream(bytes));
                        if (_autoSkew)
                        {
                            try
                            {
                                var straight = GridDetector.Deskew(opened, out float sa);
                                if (straight != null)
                                {
                                    opened.Dispose();
                                    opened = straight;
                                    skewNote = $"  (skew {sa:F2}° সোজা করা হয়েছে)";
                                }
                            }
                            catch { }
                        }
                        _pageImages[pg] = opened;
                        _pageGrids.Remove(pg);   // detect the grid fresh for this image
                        // with a config loaded, anchor the grid at the inked rotation marker
                        if (OrientPageGrid(pg, opened, _project))
                            skewNote += "  (Bottom marker-এ কালি — element গুলো ওই প্রান্ত থেকে বসেছে)";
                        if (firstAssigned == null) firstAssigned = pg;
                        idx++;
                    }
                    RefreshPageCombo();
                    if (firstAssigned != null) _cmbPage.SelectedItem = firstAssigned;   // triggers ShowCurrentPage
                    ShowCurrentPage();
                    FitZoom();
                    SetStatus(files.Length == 1
                        ? $"Opened {Path.GetFileName(files[0])} → OMR Page {firstAssigned?.OmrNo}.{skewNote}"
                        : $"Opened {files.Length} images — one page each. Click a sheet to work on it.{skewNote}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not open image:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>Refresh the composite layout and point _grid/_offX/_zoom at the active page.</summary>
        private void ShowCurrentPage()
        {
            RebuildViews();
            var av = ActiveView;
            _grid = av?.Grid;
            _offX = av?.OffX ?? 0f;
            _offY = av?.OffY ?? BandTop;
            _flip = av?.Flip ?? false;
            _imgW = av?.Bmp.Width ?? 0f;
            _imgH = av?.Bmp.Height ?? 0f;
            if (av != null) _zoom = av.Zoom;
            if (av != null) _selPages.Add(av.Page);   // active sheet is always part of the zoom selection
            if (_grid != null && _grid.IsValid)
            {
                _txtGridX.Text = _grid.GridX.ToString();
                _txtGridY.Text = _grid.GridY.ToString();
            }
            SyncOmrNoBox();
            _pic.Invalidate();
            RefreshElementList();
        }

        /// <summary>Lay out all pages that have an image — left to right, wrapping to a
        /// new row after PerRow sheets; detect each grid once.</summary>
        private void RebuildViews()
        {
            _views.Clear();
            float x = 0f, y = 0f, rowH = 0f;
            int inRow = 0;
            foreach (var pg in _project.Pages)
            {
                if (!_pageImages.TryGetValue(pg, out Bitmap bmp) || bmp == null) continue;
                if (!_pageGrids.TryGetValue(pg, out GridSystemData g))
                {
                    try { g = DetectUpright(bmp, _flipped.Contains(pg), out _); } catch { g = null; }
                    _pageGrids[pg] = g;   // cache (even failures — Detect Grid button retries)
                }
                if (!_pageZoom.TryGetValue(pg, out float z)) { z = 1f; _pageZoom[pg] = z; }

                if (inRow == PerRow) { x = 0f; y += rowH + PageGap; rowH = 0f; inRow = 0; }
                x += BandLeft;   // reserve the row-number ruler left of the sheet
                _views.Add(new PageView
                {
                    Page = pg, Bmp = bmp, Grid = g, OffX = x, OffY = y + BandTop, Zoom = z,
                    Flip = _flipped.Contains(pg)
                });
                x += bmp.Width * z + PageGap;
                rowH = Math.Max(rowH, BandTop + bmp.Height * z);
                inRow++;
            }
            if (_views.Count > 0)
                _pic.Size = new Size((int)Math.Ceiling(TotalW), (int)Math.Ceiling(TotalH));
            _pic.Invalidate();
        }

        private void DetectGrid(bool announce)
        {
            var page = CurrentPage();
            Bitmap bmp = null;
            if (page != null) _pageImages.TryGetValue(page, out bmp);
            if (bmp == null) { if (announce) MessageBox.Show("Open an image first."); return; }
            try
            {
                var g = DetectUpright(bmp, _flipped.Contains(page), out string log);
                _pageGrids[page] = g;   // remember per page (always upright space)
                if (g == null || !g.IsValid)
                {
                    SetStatus("Grid detection failed — check the corner markers / timing marks.");
                    if (announce) MessageBox.Show("Grid detection failed.\n\n" + log, "Detect Grid");
                }
                else
                {
                    _project.GridX = g.GridX;
                    _project.GridY = g.GridY;
                    SetStatus($"Grid detected: gridX={g.GridX}, gridY={g.GridY}, step={g.StepX:F1}px. Now place elements.");
                }
                ShowCurrentPage();   // rebuild views so _grid/_offX pick up the new grid
            }
            catch (Exception ex)
            {
                MessageBox.Show("Grid detection error:\n" + ex.Message);
            }
        }

        /// <summary>Load an existing config XML into the designer for editing —
        /// pages/elements replace the current project; sheet images are opened
        /// separately with Open Image as usual.</summary>
        private void OnOpenXml()
        {
            using (var dlg = new OpenFileDialog { Filter = "OMR config XML|*.xml", Title = "Open OMR config XML" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var proj = ConfigReader.Load(dlg.FileName);

                    // already-opened images survive: re-attach them (and their grids)
                    // to the loaded pages in the same order — image আগে খুলে পরে XML
                    // খুললেও canvas ঠিক থাকবে
                    var keptImages = new List<Bitmap>();
                    var keptGrids = new List<GridSystemData>();
                    foreach (var pg in _project.Pages)
                        if (_pageImages.TryGetValue(pg, out Bitmap b) && b != null)
                        {
                            keptImages.Add(b);
                            keptGrids.Add(_pageGrids.TryGetValue(pg, out GridSystemData gg) ? gg : null);
                        }

                    _pageImages.Clear();
                    _pageGrids.Clear();
                    _pageZoom.Clear();
                    _selPages.Clear();
                    _flipped.Clear();
                    _undo.Clear();          // undo history belongs to the old project
                    _pendingUndo = null;
                    _project = proj;

                    int flipped = 0;
                    for (int i = 0; i < keptImages.Count; i++)
                    {
                        if (i < proj.Pages.Count)
                        {
                            var pg = proj.Pages[i];
                            var bmp = keptImages[i];
                            _pageImages[pg] = bmp;
                            if (keptGrids[i] != null) _pageGrids[pg] = keptGrids[i];
                            else _pageGrids.Remove(pg);
                            // config known now: anchor the grid at the inked rotation marker
                            if (OrientPageGrid(pg, bmp, proj)) flipped++;
                        }
                        else keptImages[i].Dispose();   // more images than pages — drop the extras
                    }
                    string flipNote = flipped > 0
                        ? $"  ({flipped}টা sheet-এ Bottom marker-এ কালি — element গুলো ওই প্রান্ত থেকে বসানো হয়েছে)" : "";

                    _txtPageName.Text = proj.PageName;
                    _txtDesignFor.Text = proj.DesignFor;
                    _txtGridX.Text = proj.GridX.ToString();
                    _txtGridY.Text = proj.GridY.ToString();
                    _txtBlackPixel.Text = proj.BlackPixelPercent.ToString();

                    _propGrid.SelectedObject = null;
                    RefreshPageCombo();
                    ShowCurrentPage();
                    if (_pageImages.Count > 0) FitZoom();
                    SetStatus(_pageImages.Count > 0
                        ? $"Loaded {Path.GetFileName(dlg.FileName)} — {proj.Pages.Count} page(s), আগের {_pageImages.Count}টা image রাখা হয়েছে.{flipNote}"
                        : $"Loaded {Path.GetFileName(dlg.FileName)} — {proj.Pages.Count} page(s). এবার Open Image দিয়ে sheet খুলুন.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not read config XML:\n" + ex.Message, "Open XML");
                }
            }
        }

        /// <summary>
        /// Lay a page out from whichever rotation marker carries the ink. When the BOTTOM
        /// (rotate180DetectionAlt) marker is the inked one the sheet was fed upside down,
        /// so the scan is turned end-for-end and every element — omrNo, barcode, qrCode,
        /// rollNo … — then starts from that marker's end and lands on the real content.
        /// Turning the SCAN is what makes this work: mirroring only the grid would send
        /// cell (32,46) to the mirrored cell, not to the same piece of paper.
        /// Needs a config, so it only takes effect once an XML is loaded.
        /// </summary>
        private bool OrientPageGrid(OmrPage pg, Bitmap bmp, OmrProject proj)
        {
            if (!_autoRotate || pg == null || bmp == null) return false;
            try
            {
                if (OmrReader.OrientationFromMarkers(bmp, proj) >= 0) return false;   // top marker inked / unknown
                // the ink is at the bottom marker: show the sheet from that end. The
                // bitmap is left untouched — only the view flag turns, so element
                // coordinates keep meaning the same paper.
                _flipped.Add(pg);
                _pageGrids.Remove(pg);   // re-detect (DetectUpright handles the turn)
                return true;
            }
            catch { return false; }
        }

        /// <summary>Straighten the active sheet: the corner markers give the tilt, the
        /// image is rotated back by it and the grid re-detected. Mapping coordinates are
        /// grid-cell based, so elements stay where they belong.</summary>
        private void DeskewActiveImage()
        {
            var page = CurrentPage();
            Bitmap bmp = null;
            if (page != null) _pageImages.TryGetValue(page, out bmp);
            if (bmp == null) { SetStatus("Deskew — আগে একটা image open করুন."); return; }
            try
            {
                var straight = GridDetector.Deskew(bmp, out float angle);
                if (straight == null)
                {
                    SetStatus("Deskew — image ইতিমধ্যে সোজা, অথবা corner marker পাওয়া যায়নি.");
                    return;
                }
                bmp.Dispose();
                _pageImages[page] = straight;
                _pageGrids.Remove(page);   // straightened image → detect the grid again
                ShowCurrentPage();
                SetStatus($"Image {angle:F2}° ঘুরিয়ে সোজা করা হলো — grid আবার detect হয়েছে.");
            }
            catch (Exception ex) { SetStatus("Deskew সমস্যা: " + ex.Message); }
        }

        /// <summary>Rotate the active sheet's bitmap 180° IN PLACE. The detected grid
        /// and every mapped element keep their coordinates — only the picture flips
        /// underneath, giving the upside-down view needed to place the
        /// Rotation-Bottom (rotate180DetectionAlt) marker. Press again to restore.</summary>
        private void RotateActiveImage()
        {
            var page = CurrentPage();
            Bitmap bmp = null;
            if (page != null) _pageImages.TryGetValue(page, out bmp);
            if (bmp == null) { SetStatus("Rotate — আগে একটা image open করুন."); return; }

            bmp.RotateFlip(RotateFlipType.Rotate180FlipNone);
            // only the VIEW turns: the grid and every element coordinate stay in upright
            // sheet space, and drawing/mouse input mirror through this flag — so each
            // element keeps sitting on the same piece of paper
            if (!_flipped.Add(page)) _flipped.Remove(page);
            ShowCurrentPage();
            SetStatus(_flipped.Contains(page)
                ? $"OMR Page {page.OmrNo} উল্টো করে দেখানো হচ্ছে — element গুলো ওই প্রান্ত থেকেই বসছে. আবার চাপলে সোজা হবে."
                : $"OMR Page {page.OmrNo} আবার সোজা করা হলো.");
        }

        private OmrPage CurrentPage()
        {
            if (_cmbPage.SelectedItem is OmrPage p) return p;
            return _project.Pages.FirstOrDefault();
        }

        // Returns the topmost element whose footprint contains the scaled point, or null.
        private OmrElement HitTest(float scaledX, float scaledY)
        {
            if (_grid == null || !_grid.IsValid) return null;
            var candidates = new List<OmrElement>();
            var page = CurrentPage();
            if (page != null) candidates.AddRange(page.Elements);
            if (_project.OmrInfo != null) candidates.Add(_project.OmrInfo);
            if (_project.RotationTop != null) candidates.Add(_project.RotationTop);
            if (_project.RotationBottom != null) candidates.Add(_project.RotationBottom);

            // iterate in reverse so most-recently-added wins on overlap
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                var el = candidates[i];
                if (el.Kind == ElementKind.Virtual || el.Hidden) continue;
                RectangleF box = FootprintPixels(el);
                // pad the hit area a little so the start marker is grabbable too
                box.Inflate(6, 6);
                if (box.Contains(scaledX, scaledY)) return el;
            }
            return null;
        }

        private void Pic_MouseDown(object sender, MouseEventArgs e)
        {
            _didDrag = false;
            _pic.Focus();                                  // so arrow-key nudging works after a click

            // middle / right button always pans (grab-and-drag), independent of the grid
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
            {
                StartPan();
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            var vClick = ViewAt(e.X, e.Y);
            if (vClick != null)
            {
                // Ctrl+click: toggle this sheet in/out of the zoom selection (multi-zoom)
                if (ModifierKeys.HasFlag(Keys.Control))
                {
                    if (!_selPages.Add(vClick.Page)) _selPages.Remove(vClick.Page);
                    _pic.Invalidate();
                    return;
                }
                // clicking on a different sheet makes it the active page (single selection)
                if (vClick.Page != CurrentPage())
                {
                    _selPages.Clear();
                    _cmbPage.SelectedItem = vClick.Page;   // ShowCurrentPage updates _grid/_offX/_zoom
                }
            }

            if (_pendingType != null) return;              // left click in placement mode places an element

            // with a valid grid, left button can grab a resize handle or an element to move
            if (_grid != null && _grid.IsValid)
            {
                var sel = SelEl();
                if (sel != null && sel.Kind != ElementKind.Virtual && !sel.Hidden && !sel.Locked)
                {
                    if (CornerHandleRect(sel).Contains(e.X, e.Y) ||
                        RightHandleRect(sel).Contains(e.X, e.Y) ||
                        BottomHandleRect(sel).Contains(e.X, e.Y))
                    {
                        _pendingUndo = CaptureUndo("resize: " + sel.MemberName);
                        _resizeEl = sel;
                        var box = FootprintPixels(sel);              // scaled px
                        _resStartW = box.Width / _zoom / _grid.StepX;   // → grid cells
                        _resStartH = box.Height / _zoom / _grid.StepY;
                        var rp = ToUpright(e.X, e.Y);                  // upright sheet px
                        _resMouseX = rp.X;
                        _resMouseY = rp.Y;
                        _pic.Cursor = Cursors.SizeNWSE;
                        return;
                    }
                }

                var el = HitTest(e.X, e.Y);
                if (el != null)
                {
                    // a locked element still selects (that is how you unlock it), it
                    // just never becomes the thing being dragged
                    if (el.Locked)
                    {
                        SelectElement(el);
                        _propGrid.SelectedObject = el;
                        _pic.Invalidate();
                        SetStatus($"'{el.DisplayName}' locked — সরানো যাবে না (Ctrl+L দিয়ে unlock করুন).");
                        return;
                    }

                    // grabbing an element that is part of the multi-selection moves the
                    // WHOLE selection together; otherwise the selection becomes just it
                    var group = SelEls();
                    group.RemoveAll(x => x.Kind == ElementKind.Virtual || x.Locked);
                    if (!group.Contains(el)) { SelectElement(el); group = new List<OmrElement> { el }; }

                    _pendingUndo = CaptureUndo(group.Count == 1
                        ? "move: " + el.MemberName
                        : $"move: {group.Count} elements");
                    _dragEls = group;
                    _dragOrig = new List<PointF>();
                    foreach (var x in group)
                        _dragOrig.Add(new PointF(
                            _grid.LineX(x.StartX) + x.PaddingX * _grid.StepX,
                            _grid.LineY(x.StartY) + x.PaddingY * _grid.StepY));
                    var grab = ToUpright(e.X, e.Y);      // upright sheet px at grab
                    _dragCurX = grab.X;
                    _dragCurY = grab.Y;
                    _propGrid.SelectedObject = el;
                    _pic.Cursor = Cursors.SizeAll;
                    return;
                }
            }

            // click on empty area (no element under the cursor): deselect everything
            if (SelEls().Count > 0)
            {
                SelectElement(null);
                _propGrid.SelectedObject = null;
                _pic.Invalidate();
                SetStatus("Selection cleared.");
            }
            // left button on empty area (or before grid detection) pans the image
            StartPan();
        }

        private void StartPan()
        {
            if (_views.Count == 0) return;
            _panning = true;
            _panStartMouse = Cursor.Position;
            _panStartLoc = _pic.Location;
            _pic.Cursor = Cursors.Hand;
            SetStatus("Panning — drag to move the image.");
        }

        private void Pic_MouseMove(object sender, MouseEventArgs e)
        {
            // panning the image with the middle / right button (grab-and-drag)
            if (_panning)
            {
                Point now = Cursor.Position;
                int dx = now.X - _panStartMouse.X;
                int dy = now.Y - _panStartMouse.Y;
                _pic.Location = new Point(_panStartLoc.X + dx, _panStartLoc.Y + dy);
                return;
            }

            var vc = ViewAt(e.X, e.Y);
            if (vc != null && vc.Grid != null && vc.Grid.IsValid)
            {
                float hx = (e.X - vc.OffX) / vc.Zoom, hy = (e.Y - vc.OffY) / vc.Zoom;
                if (vc.Flip) { hx = vc.Bmp.Width - 1 - hx; hy = vc.Bmp.Height - 1 - hy; }
                SetStatus($"OMR Page {vc.Page.OmrNo}   grid cell  X={vc.Grid.NearestX(hx)}  Y={vc.Grid.NearestY(hy)}"
                          + (vc.Flip ? "   (turned view)" : ""));
            }

            // active resize drag — pull right grows X, pull down grows Y (delta-based)
            if (_resizeEl != null && e.Button == MouseButtons.Left)
            {
                var rn = ToUpright(e.X, e.Y);
                float wCells = _resStartW + (rn.X - _resMouseX) / _grid.StepX;
                float hCells = _resStartH + (rn.Y - _resMouseY) / _grid.StepY;
                CommitPendingUndo();   // first real change → the grab-time snapshot becomes undoable
                ApplyResize(_resizeEl, wCells, hCells, true, true);
                _didDrag = true;
                _propGrid.Refresh();
                _pic.Invalidate();
                return;
            }

            // hover feedback: show the matching resize cursor over the selected element's handles
            if (_dragEls == null && _resizeEl == null && e.Button == MouseButtons.None)
            {
                var s = SelEl();
                Cursor cur = Cursors.Default;
                if (s != null && s.Kind != ElementKind.Virtual && !s.Hidden && _grid != null && _grid.IsValid)
                {
                    if (CornerHandleRect(s).Contains(e.X, e.Y)) cur = Cursors.SizeNWSE;
                    else if (RightHandleRect(s).Contains(e.X, e.Y)) cur = Cursors.SizeWE;
                    else if (BottomHandleRect(s).Contains(e.X, e.Y)) cur = Cursors.SizeNS;
                }
                _pic.Cursor = cur;
            }

            if (_dragEls == null || e.Button != MouseButtons.Left) return;

            // move the whole group by the cursor delta; each element keeps its offset
            var dragPt = ToUpright(e.X, e.Y);
            float gdx = dragPt.X - _dragCurX;
            float gdy = dragPt.Y - _dragCurY;
            bool changed = false;
            for (int i = 0; i < _dragEls.Count; i++)
            {
                var el2 = _dragEls[i];
                PxToCell(true, _dragOrig[i].X + gdx, out int nx, out float npx);
                PxToCell(false, _dragOrig[i].Y + gdy, out int ny, out float npy);
                if (nx != el2.StartX || ny != el2.StartY ||
                    Math.Abs(npx - el2.PaddingX) > 0.001f || Math.Abs(npy - el2.PaddingY) > 0.001f)
                {
                    if (!changed) CommitPendingUndo();   // first real change → snapshot becomes undoable
                    el2.StartX = nx; el2.StartY = ny;
                    el2.PaddingX = npx; el2.PaddingY = npy;
                    changed = true;
                }
            }
            if (changed)
            {
                _didDrag = true;
                _propGrid.Refresh();
                _pic.Invalidate();
                SetStatus(_dragEls.Count == 1
                    ? $"Moved '{_dragEls[0].MemberName}' to X={_dragEls[0].StartX}+{_dragEls[0].PaddingX:F2}, Y={_dragEls[0].StartY}+{_dragEls[0].PaddingY:F2}."
                    : $"{_dragEls.Count}টা element একসাথে সরানো হচ্ছে (Δ {gdx / _grid.StepX:F2}, {gdy / _grid.StepY:F2} cell).");
            }
        }

        // Split an image-pixel position into a grid cell index + fractional padding (cells).
        // The index scheme has NO ZERO: …, -2, -1, 1, 2, … — so above/left of the grid
        // -1 means exactly one cell out (barcode/qrCode: y=-1 + padding 0.5 ≈ "দেড় cell উপরে").
        private void PxToCell(bool horizontal, float px, out int start1Based, out float pad)
        {
            var lines = horizontal ? _grid.XLines : _grid.YLines;
            float step = horizontal ? _grid.StepX : _grid.StepY;
            if (px < lines[0])
            {
                // above/left of line 1 — production convention (v1_CAP_Weekly config):
                // এক cell-এর কম হলে index 1 থেকে negative padding (y=1, pad=-0.9),
                // পুরো cell পেরোলে negative index (y=-1, pad=-0.3 → 1.3 cell উপরে)
                float rel = (px - lines[0]) / step;          // negative, e.g. -1.3
                int cell = rel <= -1f ? (int)rel : 1;        // truncate toward zero — 0 কখনো নয়
                start1Based = cell;
                pad = (float)Math.Round(rel - (cell == 1 ? 0f : cell), 2);
            }
            else
            {
                // inside the grid the timing-mark lines aren't perfectly uniform — index them
                int idx = 0;
                for (int i = 0; i < lines.Count; i++) { if (lines[i] <= px) idx = i; else break; }
                start1Based = idx + 1;
                pad = (float)Math.Round((px - lines[idx]) / step, 2);
            }
        }

        private void Pic_MouseUp(object sender, MouseEventArgs e)
        {
            _pendingUndo = null;   // grab that never changed anything → nothing to undo
            if (_panning)
            {
                _panning = false;
                _pic.Cursor = Cursors.Default;
                return;
            }
            if (_resizeEl != null)
            {
                _resizeEl = null;
                _pic.Cursor = Cursors.Default;
                RefreshElementList(true);
            }
            if (_dragEls != null)
            {
                _dragEls = null;
                _dragOrig = null;
                _pic.Cursor = Cursors.Default;
                RefreshElementList(true);
            }
        }

        // Arrow keys nudge the selected element's startPadding for sub-cell alignment.
        private void Canvas_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.D)
            {
                DuplicateSelectedElement();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }

            // Delete = remove the whole selection
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelectedElement();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }

            var els = SelEls();
            els.RemoveAll(x => x.Kind == ElementKind.Virtual);
            int lockedOut = els.RemoveAll(x => x.Locked);
            if (els.Count == 0)
            {
                // say so rather than letting the arrow keys look broken
                if (lockedOut > 0)
                    SetStatus("Locked element — arrow key দিয়ে সরানো যাবে না (Ctrl+L দিয়ে unlock করুন).");
                return;
            }
            float d = e.Shift ? 0.5f : 0.1f;
            // a burst of nudges on the same selection = one undo step
            string nudgeTag = "nudge: " + (els.Count == 1 ? els[0].MemberName : els.Count + " elements");
            UndoState pre = (_undo.Count == 0 || _undo[_undo.Count - 1].Tag != nudgeTag) ? CaptureUndo(nudgeTag) : null;
            bool handled = true;
            foreach (var el in els)
                switch (e.KeyCode)
                {
                    case Keys.Left: el.PaddingX = Round2(el.PaddingX - d); break;
                    case Keys.Right: el.PaddingX = Round2(el.PaddingX + d); break;
                    case Keys.Up: el.PaddingY = Round2(el.PaddingY - d); break;
                    case Keys.Down: el.PaddingY = Round2(el.PaddingY + d); break;
                    default: handled = false; break;
                }
            if (!handled) return;
            if (pre != null) Push(pre);
            e.Handled = e.SuppressKeyPress = true;
            _propGrid.Refresh();
            _pic.Invalidate();
            SetStatus(els.Count == 1
                ? $"'{els[0].MemberName}' padding X={els[0].PaddingX:F2}, Y={els[0].PaddingY:F2}  (arrow nudge {d:F1})."
                : $"{els.Count}টা element একসাথে nudge হলো ({d:F1} cell).");
        }

        private static float Round2(float v) => (float)Math.Round(v, 2);

        // Small square grab handles on the selected element's footprint (scaled px).
        private const float HandleSize = 10f;
        private static RectangleF HandleAt(float cx, float cy) =>
            new RectangleF(cx - HandleSize / 2f, cy - HandleSize / 2f, HandleSize, HandleSize);
        private RectangleF CornerHandleRect(OmrElement el) { var b = FootprintPixels(el); return HandleAt(b.Right, b.Bottom); }
        private RectangleF RightHandleRect(OmrElement el) { var b = FootprintPixels(el); return HandleAt(b.Right, (b.Top + b.Bottom) / 2f); }
        private RectangleF BottomHandleRect(OmrElement el) { var b = FootprintPixels(el); return HandleAt((b.Left + b.Right) / 2f, b.Bottom); }

        // Resize the element to the dragged width/height (grid cells) along the chosen axes.
        // doX = drag pulled the right edge, doY = drag pulled the bottom edge.
        private void ApplyResize(OmrElement el, float wCells, float hCells, bool doX, bool doY)
        {
            if (el.HasResultCount)
            {
                // image / result areas resize freely (fractional cells, 0.05 steps,
                // down to a tenth of a cell so half-cell markers are possible)
                if (doX) el.ResultCountCellX = Math.Max(0.1f, (float)Math.Round(wCells * 20f) / 20f);
                if (doY) el.ResultCountCellY = Math.Max(0.1f, (float)Math.Round(hCells * 20f) / 20f);
                SetStatus($"Resized '{el.MemberName}' to {el.ResultCountCellX:F1} × {el.ResultCountCellY:F1} cells.");
            }
            else
            {
                // bubble grids: counts (chars × lines) stay fixed — set by typing.
                // dragging changes the GAP between bubbles (spacing) so the reading
                // circles line up with the printed bubbles on the sheet.
                int chars = Math.Max(1, CharCount(el));
                int lines = Math.Max(1, el.Lines);
                bool dirX = el.Direction != null && el.Direction.ToLower() == "x";

                // width spans the char axis for direction x, the line axis for direction y
                if (doX) el.SpacingX = SpacingRound((dirX ? wCells / chars : wCells / lines) - 1f);
                if (doY) el.SpacingY = SpacingRound((dirX ? hCells / lines : hCells / chars) - 1f);
                SetStatus($"'{el.MemberName}' spacing X={el.SpacingX:F2}  Y={el.SpacingY:F2}  (gap between bubbles).");
            }
        }

        // Spacing can't go negative (bubbles would overlap); keep two decimals for clean XML.
        private static float SpacingRound(float s)
        {
            if (s < 0f) s = 0f;
            return (float)Math.Round(s, 2);
        }

        private static bool IsMcqAnswer(OmrElement el)
        {
            return el.Kind == ElementKind.DataRegion && !el.HasResultCount
                   && el.MemberName != null && el.MemberName.ToLower().Contains("answer");
        }

        private void Pic_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;      // middle/right = pan, never placement
            if (_didDrag) { _didDrag = false; return; }    // a drag, not a placement click
            if (_pendingType == null) return;
            if (ViewAt(e.X, e.Y) == null) return;           // clicked the gap between sheets — ignore
            if (_grid == null || !_grid.IsValid) { MessageBox.Show("Detect the grid first."); return; }

            var click = ToUpright(e.X, e.Y);               // upright sheet coordinates
            int gx = _grid.NearestX(click.X);
            int gy = _grid.NearestY(click.Y);
            var page = CurrentPage();

            OmrElement el = null;
            bool designLevel = false;

            switch (_pendingType)
            {
                case "rollNo": el = RegionTemplates.RollNo(); break;
                case "regNo": el = RegionTemplates.RegNo(); break;
                case "answer":
                    el = RegionTemplates.Answer();
                    // seed from the MCQ input boxes so it's placed at the requested size
                    if (int.TryParse(_txtMcqCount.Text, out int mq) && mq >= 1) el.Lines = mq;
                    if (int.TryParse(_txtMcqOptions.Text, out int mo) && mo >= 1) el.CharSet = OptionsToCharSet(mo);
                    break;
                case "quality": el = RegionTemplates.QuestionQuality(); break;
                case "barcode": el = RegionTemplates.Barcode(); break;
                case "qrcode": el = RegionTemplates.QrCode(); break;
                case "custom": el = BuildCustomRegion(); break;
                case "rotTop": el = RegionTemplates.RotationTop(); designLevel = true; break;
                case "rotBottom": el = RegionTemplates.RotationBottom(); designLevel = true; break;
                case "omrInfo": el = RegionTemplates.OmrInfo(); designLevel = true; break;
                case "saq":
                {
                    PushUndo("add: saq");
                    int n = _project.NextSaqNumber();
                    var box = RegionTemplates.SaqBox(n); box.StartX = gx; box.StartY = gy;
                    var ans = RegionTemplates.SaqAnswer(n); ans.StartX = gx + 2; ans.StartY = gy + 1;
                    InsertByRank(page.Elements, box);
                    InsertByRank(page.Elements, ans);
                    _pendingType = null;
                    RefreshElementList();
                    SelectElement(ans);
                    _pic.Invalidate();
                    SetStatus($"Added SAQ block #{n}.");
                    return;
                }
            }

            if (el == null) return;
            PushUndo("add: " + el.MemberName);
            el.StartX = gx;
            el.StartY = gy;

            if (designLevel)
            {
                if (el.Kind == ElementKind.RotationTop) _project.RotationTop = el;
                else if (el.Kind == ElementKind.RotationBottom) _project.RotationBottom = el;
                else if (el.Kind == ElementKind.OmrInfo) _project.OmrInfo = el;
            }
            else
            {
                InsertByRank(page.Elements, el);
            }

            _pendingType = null;
            RefreshElementList();
            SelectElement(el);
            _pic.Invalidate();
            SetStatus($"Placed '{el.MemberName}' at grid X={gx}, Y={gy}.");
        }

        /// <summary>
        /// Ask for the three things that actually differ between one custom region
        /// and the next — memberName, charSet and lines. Everything else comes from
        /// RegionTemplates.Custom() and stays editable in the PropertyGrid.
        /// Returns false when the user cancels, so nothing is left pending.
        /// </summary>
        private bool PromptCustomRegion()
        {
            using (var f = new Form
            {
                Text = "Custom region",
                Icon = Icons.App,
                Width = 340, Height = 215,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false, MinimizeBox = false
            })
            {
                f.Controls.Add(new Label { Text = "memberName", Left = 12, Top = 18, Width = 90 });
                var txtName = new TextBox { Left = 108, Top = 15, Width = 200, Text = _customName };
                f.Controls.Add(txtName);

                f.Controls.Add(new Label { Text = "charSet", Left = 12, Top = 52, Width = 90 });
                var txtChars = new TextBox { Left = 108, Top = 49, Width = 200, Text = _customCharSet };
                f.Controls.Add(txtChars);

                f.Controls.Add(new Label { Text = "lines", Left = 12, Top = 86, Width = 90 });
                var txtLines = new TextBox { Left = 108, Top = 83, Width = 60, Text = _customLines.ToString() };
                f.Controls.Add(txtLines);
                f.Controls.Add(new Label
                {
                    Text = "(কয়টা column / row)", Left = 174, Top = 86, Width = 140, ForeColor = Color.Gray
                });

                f.Controls.Add(new Label
                {
                    Text = "charSet-এ কমা থাকলে (S,H,E) charSetSeparators নিজে থেকেই ',' হবে।",
                    Left = 12, Top = 112, Width = 300, Height = 30, ForeColor = Color.Gray
                });

                var ok = new Button { Text = "Add", Left = 128, Top = 145, Width = 85, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 223, Top = 145, Width = 85, DialogResult = DialogResult.Cancel };
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                // Keep the dialog open on bad input instead of silently placing a default.
                ok.Click += (s, e) =>
                {
                    if (txtName.Text.Trim().Length == 0)
                    {
                        MessageBox.Show(f, "memberName খালি রাখা যাবে না।", "Custom region");
                        f.DialogResult = DialogResult.None;
                        txtName.Focus();
                        return;
                    }
                    if (txtChars.Text.Length == 0)
                    {
                        MessageBox.Show(f, "charSet খালি রাখা যাবে না।", "Custom region");
                        f.DialogResult = DialogResult.None;
                        txtChars.Focus();
                        return;
                    }
                    if (!int.TryParse(txtLines.Text.Trim(), out int n) || n < 1)
                    {
                        MessageBox.Show(f, "lines একটা ধনাত্মক সংখ্যা হতে হবে।", "Custom region");
                        f.DialogResult = DialogResult.None;
                        txtLines.Focus();
                        return;
                    }
                };

                if (f.ShowDialog(this) != DialogResult.OK) return false;

                _customName = txtName.Text.Trim();
                _customCharSet = txtChars.Text;
                _customLines = int.Parse(txtLines.Text.Trim());
                return true;
            }
        }

        /// <summary>
        /// Ask for the MCQ block's size before placing it — same idea as the Custom
        /// prompt. The two palette input boxes stay the source of truth (they also
        /// live-edit an already-placed block), so this just fills them in.
        /// Returns false when the user cancels.
        /// </summary>
        private bool PromptMcqAnswer()
        {
            int.TryParse(_txtMcqCount.Text, out int q0);
            int.TryParse(_txtMcqOptions.Text, out int o0);
            if (q0 < 1) q0 = 10;
            if (o0 < 1) o0 = 4;

            using (var f = new Form
            {
                Text = "MCQ Answer",
                Icon = Icons.App,
                Width = 340, Height = 200,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false, MinimizeBox = false
            })
            {
                f.Controls.Add(new Label { Text = "প্রশ্ন সংখ্যা", Left = 12, Top = 18, Width = 90 });
                var txtQ = new TextBox { Left = 108, Top = 15, Width = 60, Text = q0.ToString() };
                f.Controls.Add(txtQ);
                f.Controls.Add(new Label { Text = "(lines)", Left = 174, Top = 18, Width = 60, ForeColor = Color.Gray });

                f.Controls.Add(new Label { Text = "option", Left = 12, Top = 52, Width = 90 });
                var txtO = new TextBox { Left = 108, Top = 49, Width = 60, Text = o0.ToString() };
                f.Controls.Add(txtO);
                var lblSet = new Label { Left = 174, Top = 52, Width = 140, ForeColor = Color.Gray };
                f.Controls.Add(lblSet);

                // show the charSet the option count will produce, live
                EventHandler show = (s, e) =>
                    lblSet.Text = int.TryParse(txtO.Text.Trim(), out int n) && n >= 1 && n <= 26
                        ? "charSet: " + OptionsToCharSet(n)
                        : "";
                txtO.TextChanged += show;
                show(null, EventArgs.Empty);

                var ok = new Button { Text = "Add", Left = 128, Top = 108, Width = 85, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 223, Top = 108, Width = 85, DialogResult = DialogResult.Cancel };
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                ok.Click += (s, e) =>
                {
                    if (!int.TryParse(txtQ.Text.Trim(), out int q) || q < 1)
                    {
                        MessageBox.Show(f, "প্রশ্ন সংখ্যা একটা ধনাত্মক সংখ্যা হতে হবে।", "MCQ Answer");
                        f.DialogResult = DialogResult.None;
                        txtQ.Focus();
                        return;
                    }
                    if (!int.TryParse(txtO.Text.Trim(), out int o) || o < 1 || o > 26)
                    {
                        MessageBox.Show(f, "option ১ থেকে ২৬-এর মধ্যে হতে হবে (A…Z)।", "MCQ Answer");
                        f.DialogResult = DialogResult.None;
                        txtO.Focus();
                        return;
                    }
                };

                if (f.ShowDialog(this) != DialogResult.OK) return false;

                // write back without re-applying to whatever happens to be selected —
                // this is about the block being placed, not the one already on the sheet
                _suppressMcq = true;
                _txtMcqCount.Text = txtQ.Text.Trim();
                _txtMcqOptions.Text = txtO.Text.Trim();
                _suppressMcq = false;
                return true;
            }
        }

        /// <summary>A Custom region built from the last prompt answers.</summary>
        private OmrElement BuildCustomRegion()
        {
            var el = RegionTemplates.Custom();
            el.MemberName = _customName;
            el.Comment = _customName;
            el.CharSet = _customCharSet;
            el.Lines = _customLines;
            // "S,H,E" without a separator would be read as five single chars, so
            // infer the separator the same way the Q. Quality template sets it.
            el.CharSetSeparators = _customCharSet.Contains(",") ? "," : "";
            return el;
        }

        /// <summary>
        /// Lock / unlock every selected element. Mixed selection locks the lot —
        /// only an all-locked selection unlocks, which is what makes one button
        /// unambiguous.
        /// </summary>
        private void ToggleLockSelected()
        {
            var els = SelEls();
            if (els.Count == 0) { SetStatus("আগে element select করুন (list বা canvas থেকে)."); return; }

            bool lockThem = els.Any(x => !x.Locked);
            PushUndo(lockThem ? "lock: " + els.Count : "unlock: " + els.Count);
            foreach (var el in els) el.Locked = lockThem;

            RefreshElementList(true);
            _pic?.Invalidate();
            string who = els.Count == 1 ? $"'{els[0].DisplayName}'" : $"{els.Count}টা element";
            SetStatus(lockThem
                ? $"{who} locked — সরানো / resize করা যাবে না (আবার Ctrl+L দিলে খুলবে)."
                : $"{who} unlocked.");
        }

        private void AddVirtual()
        {
            string name = _cmbVirtual.SelectedItem?.ToString() ?? "unit";
            string def;
            switch (name)
            {
                case "unit": def = "A"; break;
                case "shiftCode": def = "1"; break;
                case "hasSaq": def = "true"; break;
                default: def = "X"; break;
            }
            AddVirtualNamed(name, def);
        }

        private void AddVirtualNamed(string name, string def)
        {
            PushUndo("add: virtual " + name);
            var el = RegionTemplates.Virtual(name, def);
            InsertByRank(CurrentPage().Elements, el);
            RefreshElementList();
            SelectElement(el);
            SetStatus($"Added virtual '{name}' = {def}.");
        }

        private void AddPage()
        {
            PushUndo("page add");
            int no = _project.Pages.Count == 0 ? 1 : _project.Pages.Max(p => p.OmrNo) + 1;
            _project.Pages.Add(new OmrPage { OmrNo = no });
            RefreshPageCombo();
            _cmbPage.SelectedIndex = _cmbPage.Items.Count - 1;
        }

        private void RemovePage()
        {
            if (_project.Pages.Count <= 1) { MessageBox.Show("At least one page is required."); return; }
            PushUndo("page remove");
            var p = CurrentPage();
            _project.Pages.Remove(p);
            // image/grid caches stay keyed by the page instance so Undo can bring
            // the page back complete — they are only released on Reset / Open XML
            _selPages.Remove(p);
            RefreshPageCombo();
            _cmbPage.SelectedIndex = 0;
        }

        // ------------------------------------------------------------- SAQ numbering
        /// <summary>N of saq_N / SaqAnswerN, or 0 when the element is not SAQ.</summary>
        private static int SaqNumberOf(OmrElement el)
        {
            string nm = el?.MemberName ?? "";
            if (nm.StartsWith("saq_") && int.TryParse(nm.Substring(4), out int a)) return a;
            if (nm.StartsWith("SaqAnswer") && int.TryParse(nm.Substring("SaqAnswer".Length), out int b)) return b;
            return 0;
        }

        /// <summary>Make room for number m: every SAQ ≥ m becomes +1, project-wide.
        /// Renames descending so numbers never collide mid-shift.</summary>
        private void ShiftSaqNumbersFrom(int m)
        {
            var hits = new List<OmrElement>();
            foreach (var pg in _project.Pages)
                foreach (var el in pg.Elements)
                    if (SaqNumberOf(el) >= m) hits.Add(el);
            foreach (var el in hits.OrderByDescending(SaqNumberOf))
            {
                int nk = SaqNumberOf(el) + 1;
                if (el.MemberName.StartsWith("saq_")) { el.MemberName = "saq_" + nk; el.Comment = "QUESTION-" + nk.ToString("D3"); }
                else el.MemberName = "SaqAnswer" + nk;
            }
        }

        /// <summary>Rename the SAQ pair numbered 'from' to 'to' (both halves, any page).</summary>
        private bool RenameSaqPair(int from, int to)
        {
            bool any = false;
            foreach (var pg in _project.Pages)
                foreach (var el in pg.Elements)
                {
                    string nm = el.MemberName ?? "";
                    if (nm == "saq_" + from)
                    {
                        el.MemberName = "saq_" + to;
                        if (to > 0) el.Comment = "QUESTION-" + to.ToString("D3");
                        any = true;
                    }
                    else if (nm == "SaqAnswer" + from)
                    {
                        el.MemberName = "SaqAnswer" + to;
                        any = true;
                    }
                }
            return any;
        }

        /// <summary>Reflect the selected element's SAQ number into the input box.</summary>
        private void SyncSaqBox(OmrElement el)
        {
            if (_txtSaqNo == null) return;
            _suppressSaqNo = true;
            int n = SaqNumberOf(el);
            _txtSaqNo.Enabled = n > 0;
            _txtSaqNo.Text = n > 0 ? n.ToString() : "";
            _suppressSaqNo = false;
        }

        /// <summary>Apply the typed SAQ number to the selected pair (swap on conflict).</summary>
        private void ApplySaqNo()
        {
            if (_suppressSaqNo) return;
            var sel = SelEl();
            int n = SaqNumberOf(sel);
            if (n <= 0) return;
            if (!int.TryParse(_txtSaqNo.Text.Trim(), out int m) || m < 1 || m == n) return;

            PushUndo($"saq renumber {n} → {m}");
            bool taken = RenameSaqPair(m, -1);   // stash the pair that owns m (if any)
            RenameSaqPair(n, m);
            if (taken) RenameSaqPair(-1, n);     // swap: old #m becomes #n
            RefreshElementList(true);
            _propGrid.Refresh();
            _pic.Invalidate();
            SetStatus(taken
                ? $"SAQ #{n} ↔ #{m} — নম্বর swap হলো."
                : $"SAQ #{n} → #{m} renumber হলো.");
        }

        private void DuplicateSelectedElement()
        {
            if (!(SelEl() is OmrElement sel)) { SetStatus("Duplicate — আগে একটা element select করুন."); return; }
            if (sel.Kind == ElementKind.RotationTop || sel.Kind == ElementKind.RotationBottom || sel.Kind == ElementKind.OmrInfo)
            {
                SetStatus("Design-level element (rotation / omrInfo) duplicate করা যায় না — একটাই থাকে.");
                return;
            }

            // SAQ pair: duplicating EITHER half copies BOTH, renumbered to the next
            // SAQ number (saq_1/SaqAnswer1 → saq_2/SaqAnswer2), placed one block below
            string nm = sel.MemberName ?? "";
            bool isSaqBox = nm.StartsWith("saq_");
            bool isSaqAns = nm.StartsWith("SaqAnswer");
            if (isSaqBox || isSaqAns)
            {
                int.TryParse(isSaqBox ? nm.Substring(4) : nm.Substring("SaqAnswer".Length), out int n);
                var page = CurrentPage();
                var box = page.Elements.FirstOrDefault(x => x.MemberName == "saq_" + n);
                var ans = page.Elements.FirstOrDefault(x => x.MemberName == "SaqAnswer" + n);
                if (box == null && ans == null) return;

                PushUndo("duplicate: saq " + n);
                // copy gets the number RIGHT AFTER the source; existing ≥ n+1 shift up
                int m = n + 1;
                ShiftSaqNumbersFrom(m);

                // vertical gap between consecutive SAQ blocks on this page (fallback: 7 rows)
                int dy = 7;
                var boxes = page.Elements
                    .Where(x => x.MemberName != null && x.MemberName.StartsWith("saq_"))
                    .OrderBy(x => x.StartY).ToList();
                if (boxes.Count >= 2)
                    dy = Math.Max(1, boxes[boxes.Count - 1].StartY - boxes[boxes.Count - 2].StartY);

                OmrElement newSel = null;
                foreach (var part in new[] { box, ans })
                {
                    if (part == null) continue;
                    var c = part.Clone();
                    c.Hidden = false;
                    c.Locked = false;       // a fresh copy is meant to be positioned
                    c.StartY = part.StartY + dy;
                    if (part == box) { c.MemberName = "saq_" + m; c.Comment = "QUESTION-" + m.ToString("D3"); }
                    else c.MemberName = "SaqAnswer" + m;
                    InsertByRank(page.Elements, c);
                    newSel = c;
                }
                RefreshElementList();
                SelectElement(newSel);
                _pic.Invalidate();
                SetStatus($"SAQ #{n} duplicate → saq_{m} / SaqAnswer{m}  ({dy} row নিচে, drag করে ঠিক করুন).");
                return;
            }

            PushUndo("duplicate: " + sel.MemberName);
            var copy = sel.Clone();
            copy.Hidden = false;            // a copy of a hidden element should be visible
            copy.Locked = false;            // ...and a copy of a locked one movable
            copy.StartX = sel.StartX + 2;   // offset so the copy is visible next to the original

            // MCQ answer blocks: continue the question numbering in the copy
            if (IsMcqAnswer(sel) && sel.HasLineStart)
                copy.LineStart = sel.LineStart + Math.Max(1, sel.Lines);

            // the copy goes right after the original (keeps answer blocks in sequence)
            var elems = CurrentPage().Elements;
            int at = elems.IndexOf(sel);
            elems.Insert(at < 0 ? elems.Count : at + 1, copy);
            RefreshElementList();
            SelectElement(copy);
            _pic.Invalidate();
            SetStatus(IsMcqAnswer(copy)
                ? $"Duplicated '{copy.MemberName}' — lineStart {copy.LineStart} (drag to position)."
                : $"Duplicated '{copy.MemberName}' (drag to position).");
        }

        // ------------------------------------------------------------- undo
        // Snapshot-stack undo: before every mutating operation the full element
        // state (values + list membership + page order) is captured. Undo copies
        // the values back INTO THE SAME instances, so page-image/grid caches and
        // list selections keyed by reference all survive.
        private class UndoState
        {
            public string Tag;
            public List<OmrPage> PageOrder = new List<OmrPage>();
            public Dictionary<OmrPage, List<OmrElement>> PageEls = new Dictionary<OmrPage, List<OmrElement>>();
            public Dictionary<OmrPage, int> OmrNos = new Dictionary<OmrPage, int>();
            public Dictionary<OmrElement, OmrElement> Snapshots = new Dictionary<OmrElement, OmrElement>();
            public OmrElement RotTop, RotBottom, Info;
        }

        private readonly List<UndoState> _undo = new List<UndoState>();
        private UndoState _pendingUndo;   // captured at drag/resize grab, pushed on first real change
        private const int UndoCap = 100;

        private UndoState CaptureUndo(string tag)
        {
            var s = new UndoState { Tag = tag, RotTop = _project.RotationTop, RotBottom = _project.RotationBottom, Info = _project.OmrInfo };
            foreach (var el in new[] { s.RotTop, s.RotBottom, s.Info })
                if (el != null) s.Snapshots[el] = el.Clone();
            foreach (var pg in _project.Pages)
            {
                s.PageOrder.Add(pg);
                s.OmrNos[pg] = pg.OmrNo;
                s.PageEls[pg] = new List<OmrElement>(pg.Elements);
                foreach (var el in pg.Elements) s.Snapshots[el] = el.Clone();
            }
            return s;
        }

        private void Push(UndoState s)
        {
            _undo.Add(s);
            if (_undo.Count > UndoCap) _undo.RemoveAt(0);
        }

        /// <summary>Capture+push. coalesce=true skips when the top entry has the same
        /// tag, so bursts (arrow nudges, textbox keystrokes) become ONE undo step.</summary>
        private void PushUndo(string tag, bool coalesce = false)
        {
            if (coalesce && _undo.Count > 0 && _undo[_undo.Count - 1].Tag == tag) return;
            Push(CaptureUndo(tag));
        }

        private void CommitPendingUndo()
        {
            if (_pendingUndo == null) return;
            Push(_pendingUndo);
            _pendingUndo = null;
        }

        private void Undo()
        {
            if (_undo.Count == 0) { SetStatus("Undo — ফেরানোর কিছু নেই."); return; }
            var s = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _pendingUndo = null;

            _project.RotationTop = s.RotTop;
            _project.RotationBottom = s.RotBottom;
            _project.OmrInfo = s.Info;
            _project.Pages.Clear();
            _project.Pages.AddRange(s.PageOrder);
            foreach (var pg in s.PageOrder)
            {
                pg.OmrNo = s.OmrNos[pg];
                pg.Elements.Clear();
                pg.Elements.AddRange(s.PageEls[pg]);
            }
            foreach (var kv in s.Snapshots) CopyInto(kv.Value, kv.Key);

            _suppressMcq = true; _suppressOmrNo = true;
            RefreshPageCombo();
            ShowCurrentPage();
            _suppressMcq = false; _suppressOmrNo = false;
            SyncOmrNoBox();
            SyncMcqBoxes(SelEl());
            SyncSaqBox(SelEl());
            _propGrid.Refresh();
            _pic.Invalidate();
            SetStatus($"Undo — '{s.Tag}' ফেরানো হলো  ({_undo.Count} ধাপ বাকি).");
        }

        private static void CopyInto(OmrElement from, OmrElement to)
        {
            foreach (var p in typeof(OmrElement).GetProperties())
                if (p.CanRead && p.CanWrite) p.SetValue(to, p.GetValue(from, null), null);
        }

        // form-wide Ctrl+Z — but text editors (textboxes, property grid) keep their own
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Z) || keyData == (Keys.Control | Keys.L))
            {
                Control c = this;
                while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
                if (!(c is TextBoxBase) && !_propGrid.ContainsFocus)
                {
                    if (keyData == (Keys.Control | Keys.Z)) Undo(); else ToggleLockSelected();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------- reset
        /// <summary>Start over: every image, page and element is discarded.</summary>
        private void ResetAll()
        {
            if (MessageBox.Show(this,
                    "সব image, page ও element মুছে গিয়ে নতুন প্রোজেক্ট শুরু হবে।\nReset করবেন?",
                    "Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            foreach (var bmp in _pageImages.Values) bmp?.Dispose();
            _pageImages.Clear();
            _pageGrids.Clear();
            _pageZoom.Clear();
            _selPages.Clear();
            _flipped.Clear();
            _views.Clear();
            _undo.Clear();
            _pendingUndo = null;

            _project = new OmrProject();
            _grid = null; _offX = 0f; _zoom = 1f;
            _pendingType = null;
            _dragEls = null; _dragOrig = null; _resizeEl = null;

            _propGrid.SelectedObject = null;
            _txtPageName.Text = _project.PageName;
            _txtDesignFor.Text = _project.DesignFor;
            _txtGridX.Text = _project.GridX.ToString();
            _txtGridY.Text = _project.GridY.ToString();
            _txtBlackPixel.Text = _project.BlackPixelPercent.ToString();

            _pic.Size = new Size(1, 1);
            RefreshPageCombo();
            RefreshElementList();
            _pic.Invalidate();
            SetStatus("Reset হয়ে গেছে — Open Image দিয়ে নতুন করে শুরু করুন.");
        }

        /// <summary>Move the selected element up/down within its page — this is the
        /// order XmlConfigBuilder writes, so the generated XML follows the list.</summary>
        private void MoveSelectedElement(int delta)
        {
            if (!(SelEl() is OmrElement el)) { SetStatus("Move — আগে একটা element select করুন."); return; }
            var list = CurrentPage()?.Elements;
            int i = list?.IndexOf(el) ?? -1;
            if (i < 0) { SetStatus("Design-level element (rotation / omrInfo) সবসময় উপরে থাকে — move হয় না."); return; }
            int j = i + delta;
            if (j < 0 || j >= list.Count) return;
            PushUndo("reorder: " + el.MemberName);
            list[i] = list[j];
            list[j] = el;
            RefreshElementList(true);
            _pic.Invalidate();
            SetStatus($"'{el.MemberName}' moved {(delta < 0 ? "up" : "down")} — XML-এ একই order-এ বের হবে.");
        }

        // Canonical element order within a page, matching the reference config
        // (v1_CAP_Weekly_50_MCQ_35_SAQ): virtuals, barcode, qrCode, rollNo,
        // registrationNo, answer, QuestionQuality, SAQ pairs, everything else.
        private static int OrderRank(OmrElement el)
        {
            if (el.Kind == ElementKind.Virtual) return 0;
            string n = el.MemberName ?? "";
            if (n.StartsWith("saq_") || n.StartsWith("SaqAnswer")) return 7;
            if (el.Kind == ElementKind.ImagePart)
            {
                if (n.ToLower().StartsWith("barcode")) return 1;
                if (n.ToLower().StartsWith("qr")) return 2;
                return 8;
            }
            if (n == "rollNo") return 3;
            if (n == "registrationNo") return 4;
            if (n.ToLower().Contains("answer")) return 5;
            if (n == "QuestionQuality") return 6;
            return 8;
        }

        // New elements slot in AFTER the last element of the same-or-lower rank,
        // so the default list (and XML) order matches the reference config.
        // Manual ▲/▼ moves are kept — this only decides the insert position.
        private static void InsertByRank(List<OmrElement> list, OmrElement el)
        {
            int rank = OrderRank(el);
            int pos = 0;
            for (int i = 0; i < list.Count; i++)
                if (OrderRank(list[i]) <= rank) pos = i + 1;
            list.Insert(pos, el);
        }

        /// <summary>Copy EVERY element of the active page to another (or a new) page.
        /// saq_N / SaqAnswerN pairs are renumbered continuing from the project's max,
        /// matching the reference configs (page 2: saq 1-12 → page 3: saq 13-24).</summary>
        private void CopyPageElementsTo()
        {
            var src = CurrentPage();
            if (src == null || src.Elements.Count == 0) { SetStatus("Copy — active page-এ কোনো element নেই."); return; }

            OmrPage target = null;
            bool makeNew = false;
            using (var f = new Form
            {
                Text = "Copy page elements",
                Icon = Icons.App,
                Width = 330, Height = 140,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false, MinimizeBox = false
            })
            {
                f.Controls.Add(new Label
                {
                    Text = $"OMR Page {src.OmrNo}-এর {src.Elements.Count}টা element কোন page-এ copy হবে?",
                    Left = 10, Top = 8, Width = 300, Height = 30
                });
                var cmb = new ComboBox { Left = 10, Top = 42, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (var p in _project.Pages) if (p != src) cmb.Items.Add(p);
                cmb.Items.Add("+ নতুন Page");
                cmb.SelectedIndex = 0;
                f.Controls.Add(cmb);
                var ok = new Button { Text = "Copy", Left = 210, Top = 41, Width = 85, DialogResult = DialogResult.OK };
                f.Controls.Add(ok);
                f.AcceptButton = ok;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                target = cmb.SelectedItem as OmrPage;
                makeNew = target == null;
            }

            PushUndo($"copy page {src.OmrNo} elements");
            if (makeNew)
            {
                int no = _project.Pages.Count == 0 ? 1 : _project.Pages.Max(p => p.OmrNo) + 1;
                target = new OmrPage { OmrNo = no };
                _project.Pages.Add(target);
            }

            // clone all, renumbering saq_N / SaqAnswerN pairs together
            int nextSaq = _project.NextSaqNumber();
            var saqMap = new Dictionary<int, int>();
            int Renumber(int n) { if (!saqMap.TryGetValue(n, out int m)) { m = nextSaq++; saqMap[n] = m; } return m; }

            foreach (var el in src.Elements)
            {
                var copy = el.Clone();
                copy.Hidden = false;
                copy.Locked = false;
                string name = copy.MemberName ?? "";
                if (name.StartsWith("saq_") && int.TryParse(name.Substring(4), out int bn))
                {
                    int m = Renumber(bn);
                    copy.MemberName = "saq_" + m;
                    copy.Comment = "QUESTION-" + m.ToString("D3");
                }
                else if (name.StartsWith("SaqAnswer") && int.TryParse(name.Substring("SaqAnswer".Length), out int an))
                {
                    copy.MemberName = "SaqAnswer" + Renumber(an);
                }
                InsertByRank(target.Elements, copy);
            }

            RefreshPageCombo();
            _cmbPage.SelectedItem = target;   // jump to the target page to show the result
            ShowCurrentPage();
            SetStatus($"OMR Page {src.OmrNo} → Page {target.OmrNo}: {src.Elements.Count}টা element copy হলো" +
                      (saqMap.Count > 0 ? $" (saq {saqMap.Values.Min()}–{saqMap.Values.Max()} নম্বর পেল)." : "."));
        }

        private void RemoveSelectedElement()
        {
            var els = SelEls();
            if (els.Count == 0) return;
            PushUndo(els.Count == 1 ? "remove: " + els[0].MemberName : $"remove: {els.Count} elements");
            foreach (var el in els)
            {
                if (_project.RotationTop == el) _project.RotationTop = null;
                else if (_project.RotationBottom == el) _project.RotationBottom = null;
                else if (_project.OmrInfo == el) _project.OmrInfo = null;
                else CurrentPage().Elements.Remove(el);
            }
            _propGrid.SelectedObject = null;
            RefreshElementList();
            _pic.Invalidate();
            SetStatus(els.Count == 1
                ? $"'{els[0].MemberName}' remove হলো (Ctrl+Z-এ ফিরবে)."
                : $"{els.Count}টা element remove হলো (Ctrl+Z-এ ফিরবে).");
        }

        // ------------------------------------------------------------- refresh
        private void RefreshPageCombo()
        {
            var prev = _cmbPage.SelectedItem as OmrPage;
            _cmbPage.Items.Clear();
            foreach (var p in _project.Pages) _cmbPage.Items.Add(p);
            if (prev != null && _project.Pages.Contains(prev)) _cmbPage.SelectedItem = prev;
            else if (_cmbPage.Items.Count > 0) _cmbPage.SelectedIndex = 0;
        }

        // ------------------------------------------------------------- omrNo edit
        private void OnOmrNoChanged()
        {
            if (_suppressOmrNo) return;
            var pg = CurrentPage();
            if (pg == null) return;
            if (int.TryParse(_txtOmrNo.Text, out int no) && no >= 1 && no != pg.OmrNo)
            {
                PushUndo("omrNo", true);
                pg.OmrNo = no;
                _suppressOmrNo = true;
                RefreshPageCombo();   // combo items show "OMR Page {no}" — rebuild the labels
                _suppressOmrNo = false;
                _pic.Invalidate();
                SetStatus($"Active sheet's omrNo set to {no}.");
            }
        }

        private void SyncOmrNoBox()
        {
            if (_txtOmrNo == null) return;
            _suppressOmrNo = true;
            var pg = CurrentPage();
            _txtOmrNo.Text = pg != null ? pg.OmrNo.ToString() : "";
            _suppressOmrNo = false;
        }

        private void RefreshElementList(bool keepSelection = false)
        {
            var prevSel = SelEls();
            _listItems.Clear();
            if (_project.RotationTop != null) _listItems.Add(_project.RotationTop);
            if (_project.RotationBottom != null) _listItems.Add(_project.RotationBottom);
            if (_project.OmrInfo != null) _listItems.Add(_project.OmrInfo);
            var page = CurrentPage();
            if (page != null) _listItems.AddRange(page.Elements);

            _suppressCheck = true;
            _lstElements.BeginUpdate();
            _lstElements.Items.Clear();
            foreach (var el in _listItems)
                _lstElements.Items.Add(new ListViewItem((el.Locked ? "[L] " : "") + el.ToString())
                { Tag = el, Checked = !el.Hidden });
            if (keepSelection && prevSel.Count > 0)
                foreach (ListViewItem it in _lstElements.Items)
                    if (it.Tag is OmrElement e && prevSel.Contains(e)) it.Selected = true;
            _lstElements.EndUpdate();
            _suppressCheck = false;
        }

        // ------------------------------------------------------------- zoom
        private void Canvas_MouseWheel(object sender, MouseEventArgs e)
        {
            if (_views.Count == 0) return;
            if (e is HandledMouseEventArgs he) he.Handled = true;
            ZoomSelected(e.Delta > 0 ? 1.15f : 1f / 1.15f);
        }

        /// <summary>Point _grid/_offX/_zoom at the active sheet after a layout change.</summary>
        private void SyncActive()
        {
            var av = ActiveView;
            _grid = av?.Grid;
            _offX = av?.OffX ?? 0f;
            _offY = av?.OffY ?? BandTop;
            _flip = av?.Flip ?? false;
            _imgW = av?.Bmp.Width ?? 0f;
            _imgH = av?.Bmp.Height ?? 0f;
            if (av != null) _zoom = av.Zoom;
        }

        /// <summary>Detect the grid in UPRIGHT sheet space. When the page is being shown
        /// turned, detection runs on an un-turned copy so the stored grid always describes
        /// the sheet the right way up — that is what keeps element coordinates stable.</summary>
        private static GridSystemData DetectUpright(Bitmap bmp, bool flipped, out string log)
        {
            if (!flipped) return GridDetector.Detect(bmp, out log);
            using (var upright = (Bitmap)bmp.Clone())
            {
                upright.RotateFlip(RotateFlipType.Rotate180FlipNone);
                return GridDetector.Detect(upright, out log);
            }
        }

        /// <summary>Cursor position (screen px) → UPRIGHT sheet pixels for the active page.</summary>
        private PointF ToUpright(float screenX, float screenY)
        {
            float ix = (screenX - _offX) / _zoom;
            float iy = (screenY - _offY) / _zoom;
            return _flip ? new PointF(_imgW - 1 - ix, _imgH - 1 - iy) : new PointF(ix, iy);
        }

        /// <summary>Zoom the selected sheet(s) — Ctrl+click selects several — by a factor.</summary>
        private void ZoomSelected(float factor)
        {
            if (_views.Count == 0) return;
            var targets = _views.Where(v => _selPages.Contains(v.Page)).ToList();
            if (targets.Count == 0 && ActiveView != null) targets.Add(ActiveView);
            if (targets.Count == 0) return;

            // anchor: keep the point under the cursor stable when it's over a zooming sheet
            Point cp = _canvasPanel.PointToClient(Cursor.Position);
            float picX = cp.X - _pic.Left, picY = cp.Y - _pic.Top;
            var anchor = ViewAt(picX, picY);
            bool useAnchor = anchor != null && targets.Contains(anchor);
            float aImgX = 0, aImgY = 0;
            if (useAnchor) { aImgX = (picX - anchor.OffX) / anchor.Zoom; aImgY = (picY - anchor.OffY) / anchor.Zoom; }

            foreach (var v in targets)
                _pageZoom[v.Page] = Math.Max(0.05f, Math.Min(8f, v.Zoom * factor));

            RebuildViews();
            SyncActive();

            if (useAnchor)
            {
                var a2 = _views.FirstOrDefault(v => v.Page == anchor.Page);
                if (a2 != null)
                    _pic.Location = new Point(
                        (int)(cp.X - (a2.OffX + aImgX * a2.Zoom)),
                        (int)(cp.Y - (a2.OffY + aImgY * a2.Zoom)));
            }
        }

        private void FitZoom()
        {
            if (_views.Count == 0) return;
            // rows of PerRow sheets: fit the widest row horizontally and all rows vertically
            float maxRowW = 0, sumRowH = 0;
            int rows = 0;
            for (int i = 0; i < _views.Count; i += PerRow)
            {
                float rowW = 0, rowH = 0;
                int n = Math.Min(PerRow, _views.Count - i);
                for (int k = 0; k < n; k++)
                {
                    rowW += _views[i + k].Bmp.Width;
                    rowH = Math.Max(rowH, _views[i + k].Bmp.Height);
                }
                float gaps = PageGap * (n - 1) + BandLeft * n;
                maxRowW = Math.Max(maxRowW, rowW + gaps / 1f);   // gaps don't scale with zoom
                sumRowH += rowH;
                rows++;
            }
            // solve zoom from width (gaps are fixed screen px, so subtract the worst-case gaps)
            float worstGaps = PageGap * (Math.Min(PerRow, _views.Count) - 1) + BandLeft * Math.Min(PerRow, _views.Count);
            float sumW = 0;
            for (int i = 0; i < Math.Min(PerRow, _views.Count); i++) sumW += _views[i].Bmp.Width;
            float zx = (_canvasPanel.ClientSize.Width - worstGaps) / Math.Max(1f, sumW);
            float zy = (_canvasPanel.ClientSize.Height - rows * BandTop - PageGap * (rows - 1)) / Math.Max(1f, sumRowH);
            float z = Math.Max(0.05f, Math.Min(zx, zy));
            foreach (var v in _views) _pageZoom[v.Page] = z;
            RebuildViews();
            SyncActive();
            CenterImage();
        }

        private void CenterImage()
        {
            if (_views.Count == 0) return;
            int x = (_canvasPanel.ClientSize.Width - _pic.Width) / 2;
            int y = (_canvasPanel.ClientSize.Height - _pic.Height) / 2;
            _pic.Location = new Point(Math.Max(0, x), Math.Max(0, y));
        }

        // double-click an element to flip its read direction (x ↔ y);
        // double-click empty canvas to open another image
        private void Pic_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            var el = (_grid != null && _grid.IsValid) ? HitTest(e.X, e.Y) : null;
            if (el != null)
            {
                if (el.Locked)
                {
                    SelectElement(el);
                    _propGrid.SelectedObject = el;
                    SetStatus($"'{el.DisplayName}' locked — direction বদলাবে না (Ctrl+L দিয়ে unlock করুন).");
                    return;
                }
                PushUndo("direction: " + el.MemberName);
                el.Direction = (el.Direction != null && el.Direction.ToLower() == "x") ? "y" : "x";
                SelectElement(el);
                _propGrid.SelectedObject = el;
                _propGrid.Refresh();
                _pic.Invalidate();
                SetStatus($"'{el.MemberName}' direction = {el.Direction}  (double-click toggles x/y).");
                return;
            }
            OnOpenImage(sender, e);
        }

        // ------------------------------------------------------------- drawing
        private void Pic_Paint(object sender, PaintEventArgs e)
        {
            if (_views.Count == 0) return;
            var g = e.Graphics;
            // smooth scaling so zoom in/out never pixelates the scan
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var active = ActiveView;
            _paintSel = new HashSet<OmrElement>(SelEls());   // thick border for every selected element
            foreach (var v in _views)
            {
                g.DrawImage(v.Bmp, v.OffX, v.OffY, v.W, v.H);

                if (_showGrid && v.Grid != null && v.Grid.IsValid)
                {
                    g.SmoothingMode = SmoothingMode.None;   // crisp 1px grid lines
                    DrawGrid(g, v);
                }
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var v in _views)
            {
                if (v.Grid == null || !v.Grid.IsValid) continue;
                bool isActive = v == active;

                // design-level elements appear on every sheet; page elements on their own sheet
                if (_project.RotationTop != null) DrawElement(g, _project.RotationTop, Color.DarkOrange, v, isActive);
                if (_project.RotationBottom != null) DrawElement(g, _project.RotationBottom, Color.DarkOrange, v, isActive);
                if (_project.OmrInfo != null) DrawElement(g, _project.OmrInfo, Color.Purple, v, isActive);
                foreach (var el in v.Page.Elements)
                    DrawElement(g, el, ColorFor(el), v, isActive);
            }

            // highlight: solid border on the active sheet, dashed on other zoom-selected sheets
            if (_views.Count > 1)
                foreach (var v in _views)
                {
                    if (v == active)
                    {
                        using (var pen = new Pen(Color.DodgerBlue, 2f))
                            g.DrawRectangle(pen, v.OffX, v.OffY, v.W, v.H);
                    }
                    else if (_selPages.Contains(v.Page))
                    {
                        using (var pen = new Pen(Color.DodgerBlue, 2f) { DashStyle = DashStyle.Dash })
                            g.DrawRectangle(pen, v.OffX, v.OffY, v.W, v.H);
                    }
                }
        }

        private void DrawGrid(Graphics g, PageView v)
        {
            var grid = v.Grid;
            float ox = v.OffX;        // screen px (sheet's left edge)
            float z = v.Zoom;         // per-sheet zoom
            float oy0 = v.OffY;       // sheet's top edge on the canvas (row layout)

            // grid lines live in upright sheet space; mirror them for a turned page so
            // they stay on the timing marks of the picture actually being shown
            float MX(float x) => v.Flip ? v.Bmp.Width - 1 - x : x;
            float MY(float y) => v.Flip ? v.Bmp.Height - 1 - y : y;
            float top = oy0 + Math.Min(MY(grid.YLines.First()), MY(grid.YLines.Last())) * z;
            float bottom = oy0 + Math.Max(MY(grid.YLines.First()), MY(grid.YLines.Last())) * z;
            float left = ox + Math.Min(MX(grid.XLines.First()), MX(grid.XLines.Last())) * z;
            float rightX = ox + Math.Max(MX(grid.XLines.First()), MX(grid.XLines.Last())) * z;

            using (var pen = new Pen(Color.FromArgb(70, Color.Red), 1f))
            {
                // the grid stops AT the black markers — no cells are drawn outside the
                // timing-mark frame. Each line spans the full grid, edge to edge.
                // (rounded to whole pixels for crisp 1px rendering)
                int topI = (int)Math.Round(top), botI = (int)Math.Round(bottom);
                int leftI = (int)Math.Round(left), rightI = (int)Math.Round(rightX);
                for (int i = 0; i < grid.XLines.Count; i++)
                {
                    int x = (int)Math.Round(ox + MX(grid.XLines[i]) * z);
                    g.DrawLine(pen, x, topI, x, botI);
                }
                for (int i = 0; i < grid.YLines.Count; i++)
                {
                    int y = (int)Math.Round(oy0 + MY(grid.YLines[i]) * z);
                    g.DrawLine(pen, leftI, y, rightI, y);
                }
            }

            // ---- ruler bands OUTSIDE the image: column numbers on top, row numbers at left ----
            float cellW = grid.StepX * z;
            float cellH = grid.StepY * z;
            // when cells get too narrow, label every 5th cell instead of disappearing
            int stepC = cellW >= 9f ? 1 : cellW >= 4f ? 5 : 10;
            int stepR = cellH >= 9f ? 1 : cellH >= 4f ? 5 : 10;

            var prevHint = g.TextRenderingHint;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var band = new SolidBrush(Color.FromArgb(245, Color.White)))
            using (var font = new Font("Arial", 8.25f, FontStyle.Bold))
            using (var br = new SolidBrush(Color.RoyalBlue))
            using (var fmtC = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            using (var fmtR = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                // band backgrounds (white ruler strips at the canvas edges)
                g.FillRectangle(band, ox, oy0 - BandTop, v.W, BandTop - 2);
                g.FillRectangle(band, ox - BandLeft, oy0, BandLeft - 2, v.H);

                // numbers hug the GRID itself (like the reference sheet):
                // columns sit just above the corner-marker row, rows just left of the timing marks
                float colY = top - grid.StepY * z - 4f;                          // above the marker row
                if (colY < oy0 - BandTop + 12f) colY = oy0 - BandTop + 12f;      // never clipped into the row above
                float rowX = left - grid.StepX * z - 4f;                         // left of the timing marks
                if (rowX < 12f) rowX = 12f;

                // column numbers "01 02 03…" — vertical (rotated), above the grid
                for (int i = 0; i < grid.XLines.Count - 1; i += stepC)
                {
                    float cx = ox + MX((grid.XLines[i] + grid.XLines[i + 1]) / 2f) * z;
                    var state = g.Save();
                    g.TranslateTransform(cx, colY);
                    g.RotateTransform(-90f);
                    g.DrawString((i + 1).ToString("00"), font, br, 0, 0, fmtC);
                    g.Restore(state);
                }

                // row numbers "01 02 03…" — horizontal, beside the timing marks
                for (int i = 0; i < grid.YLines.Count - 1; i += stepR)
                {
                    float cy = oy0 + MY((grid.YLines[i] + grid.YLines[i + 1]) / 2f) * z;
                    g.DrawString((i + 1).ToString("00"), font, br, rowX, cy, fmtR);
                }
            }
            g.TextRenderingHint = prevHint;
        }

        private Color ColorFor(OmrElement el)
        {
            switch (el.Kind)
            {
                case ElementKind.ImagePart: return Color.SeaGreen;
                case ElementKind.Virtual: return Color.Gray;
                default:
                    if (el.MemberName != null && el.MemberName.StartsWith("Saq")) return Color.Teal;
                    return Color.RoyalBlue;
            }
        }

        // matches OMR Checker's DrawReadingAreaColor (255,255,0,255)
        private static readonly Color ReadingColor = Color.Magenta;

        private void DrawElement(Graphics g, OmrElement el, Color color, PageView v, bool isActive)
        {
            if (el.Kind == ElementKind.Virtual || el.Hidden) return;
            if (v.Grid == null || !v.Grid.IsValid) return;

            RectangleF box = FootprintPixelsFor(el, v.Grid, v.OffX, v.OffY, v.Zoom, v.Flip, v.Bmp.Width, v.Bmp.Height);
            bool inSelection = _paintSel != null && _paintSel.Contains(el);
            using (var pen = new Pen(color, inSelection ? 3f : 1.5f))
            using (var fill = new SolidBrush(Color.FromArgb(30, color)))
            using (var br = new SolidBrush(color))
            using (var font = new Font("Arial", 8f, FontStyle.Bold))
            {
                // locked elements get a dashed outline, so the lock is visible on the
                // sheet and not only in the element list
                if (el.Locked) pen.DashStyle = DashStyle.Dash;

                g.FillRectangle(fill, box);
                g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                // start circle marker (origin)
                g.FillEllipse(br, box.X - 4, box.Y - 4, 8, 8);
                g.DrawString((el.Locked ? "[L] " : "") + el.DisplayName, font, br, box.X + 2, box.Y - 14);

                // resize handles: only on the ACTIVE sheet's primary selected element,
                // and never on a locked one — there is nothing to grab
                if (isActive && !el.Locked && ReferenceEquals(el, SelEl()))
                {
                    using (var hb = new SolidBrush(Color.White))
                        foreach (var h in new[] { RightHandleRect(el), BottomHandleRect(el), CornerHandleRect(el) })
                        {
                            g.FillRectangle(hb, h.X, h.Y, h.Width, h.Height);
                            g.DrawRectangle(pen, h.X, h.Y, h.Width, h.Height);
                        }
                }
            }

            // For bubble-grid regions, draw each individual reading position
            // (same math DynamicOmrFiller uses to place dots) so alignment is verifiable.
            if (el.Kind == ElementKind.DataRegion && !el.HasResultCount)
                DrawReadingDots(g, el, v);

            // omrInfo-style repeated marks: charSet-এ যতগুলো entry, direction বরাবর
            // ততগুলো ছোট box — checker page detection-এ ঠিক এগুলোই পড়ে
            if (el.HasResultCount && CharCount(el) > 1)
                DrawRepeatedMarks(g, el, color, v);
        }

        private void DrawReadingDots(Graphics g, OmrElement el, PageView v)
        {
            int chars = CharCount(el);
            int lines = Math.Max(1, el.Lines);
            if ((long)chars * lines > 2000) return; // safety cap

            float originX = v.Grid.LineX(el.StartX);   // sheet-local px
            float originY = v.Grid.LineY(el.StartY);
            float stepX = v.Grid.StepX, stepY = v.Grid.StepY;
            float dot = Math.Max(3f, stepX * 0.35f) * v.Zoom;

            using (var pen = new Pen(ReadingColor, 1.2f))
            {
                for (int li = 0; li < lines; li++)
                {
                    for (int ci = 0; ci < chars; ci++)
                    {
                        float bx, by;
                        if (el.Direction != null && el.Direction.ToLower() == "x")
                        {
                            bx = originX + el.PaddingX * stepX + ci * stepX * (1f + el.SpacingX);
                            by = originY + el.PaddingY * stepY + li * stepY * (1f + el.SpacingY);
                        }
                        else
                        {
                            bx = originX + el.PaddingX * stepX + li * stepX * (1f + el.SpacingX);
                            by = originY + el.PaddingY * stepY + ci * stepY * (1f + el.SpacingY);
                        }
                        float ix = bx + stepX * 0.5f, iy = by + stepY * 0.5f;   // upright sheet px
                        if (v.Flip) { ix = v.Bmp.Width - 1 - ix; iy = v.Bmp.Height - 1 - iy; }
                        float cx = v.OffX + ix * v.Zoom;
                        float cy = v.OffY + iy * v.Zoom;
                        g.DrawEllipse(pen, cx - dot / 2f, cy - dot / 2f, dot, dot);
                    }
                }
            }
        }

        /// <summary>Footprint of an element on the ACTIVE sheet, in screen pixels.</summary>
        private RectangleF FootprintPixels(OmrElement el)
            => FootprintPixelsFor(el, _grid, _offX, _offY, _zoom, _flip, _imgW, _imgH);

        /// <summary>Approximate footprint of an element in screen pixels on a given sheet.
        /// The box is worked out in upright sheet space and mirrored as a whole when the
        /// page is shown turned — mirroring only the origin would put it a box-width off.</summary>
        private RectangleF FootprintPixelsFor(OmrElement el, GridSystemData grid,
                                              float offX, float offY, float zoom,
                                              bool flip, float imgW, float imgH)
        {
            float ix = grid.LineX(el.StartX) + el.PaddingX * grid.StepX;   // upright sheet px
            float iy = grid.LineY(el.StartY) + el.PaddingY * grid.StepY;

            float wCells, hCells;
            if (el.HasResultCount)
            {
                wCells = el.ResultCountCellX;
                hCells = el.ResultCountCellY;
            }
            else
            {
                int chars = CharCount(el);
                if (el.Direction == "x")
                {
                    wCells = Math.Max(1, chars) * (1f + el.SpacingX);
                    hCells = Math.Max(1, el.Lines) * (1f + el.SpacingY);
                }
                else
                {
                    wCells = Math.Max(1, el.Lines) * (1f + el.SpacingX);
                    hCells = Math.Max(1, chars) * (1f + el.SpacingY);
                }
            }
            // allow sub-cell footprints (rotation / omrInfo use half a grid cell);
            // keep a tiny minimum so the box stays visible and grabbable
            float iw = Math.Max(grid.StepX * 0.1f, wCells * grid.StepX);
            float ih = Math.Max(grid.StepY * 0.1f, hCells * grid.StepY);

            var box = new RectangleF(ix, iy, iw, ih);
            if (flip) box = Mirror(box, imgW, imgH);
            return new RectangleF(offX + box.X * zoom, offY + box.Y * zoom, box.Width * zoom, box.Height * zoom);
        }

        private int CharCount(OmrElement el) => CharEntriesOf(el).Length;

        /// <summary>charSet entries: "1,2,3" (separator ',') → 1|2|3, "ABCD" → A|B|C|D.</summary>
        private static string[] CharEntriesOf(OmrElement el)
        {
            if (string.IsNullOrEmpty(el.CharSet)) return new[] { "1" };
            if (!string.IsNullOrEmpty(el.CharSetSeparators) && el.CharSet.Contains(el.CharSetSeparators))
                return el.CharSet.Split(el.CharSetSeparators[0]);
            return el.CharSet.Select(c => c.ToString()).ToArray();
        }

        /// <summary>One resultCountCell-sized box per charSet entry, repeated along the
        /// read direction with spacing — mirrors OmrReader.CellRect so the designer
        /// shows exactly what the checker will read (e.g. omrNo marks 1, 2, 3).</summary>
        private void DrawRepeatedMarks(Graphics g, OmrElement el, Color color, PageView v)
        {
            string[] entries = CharEntriesOf(el);
            int lines = Math.Max(1, el.Lines);
            if ((long)entries.Length * lines > 500) return;   // safety cap

            float ox = v.Grid.LineX(el.StartX) + el.PaddingX * v.Grid.StepX;   // sheet-local px
            float oy = v.Grid.LineY(el.StartY) + el.PaddingY * v.Grid.StepY;
            bool dirX = el.Direction != null && el.Direction.ToLower() == "x";
            float w = el.ResultCountCellX * v.Grid.StepX * v.Zoom;
            float h = el.ResultCountCellY * v.Grid.StepY * v.Zoom;

            using (var pen = new Pen(color, 1.2f) { DashStyle = DashStyle.Dash })
            using (var br = new SolidBrush(color))
            using (var font = new Font("Arial", 7.5f, FontStyle.Bold))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                for (int li = 0; li < lines; li++)
                    for (int ci = 0; ci < entries.Length; ci++)
                    {
                        float bx = ox + (dirX ? ci : li) * v.Grid.StepX * (1f + el.SpacingX);
                        float by = oy + (dirX ? li : ci) * v.Grid.StepY * (1f + el.SpacingY);
                        var cell = new RectangleF(bx, by, el.ResultCountCellX * v.Grid.StepX, el.ResultCountCellY * v.Grid.StepY);
                        if (v.Flip) cell = Mirror(cell, v.Bmp.Width, v.Bmp.Height);
                        float sx = v.OffX + cell.X * v.Zoom;
                        float sy = v.OffY + cell.Y * v.Zoom;
                        if (ci > 0 || li > 0)                       // প্রথম box টা মূল footprint-ই
                            g.DrawRectangle(pen, sx, sy, w, h);
                        // কোন mark = কোন মান (1, 2, 3 …) — box-এর মাঝে label
                        g.DrawString(entries[ci], font, br, sx + w / 2f, sy + h / 2f, fmt);
                    }
            }
        }

        // ------------------------------------------------------------- output
        private void SyncSettings()
        {
            _project.PageName = _txtPageName.Text.Trim();
            _project.DesignFor = _txtDesignFor.Text.Trim();
            if (int.TryParse(_txtGridX.Text, out int gx)) _project.GridX = gx;
            if (int.TryParse(_txtGridY.Text, out int gy)) _project.GridY = gy;
            if (int.TryParse(_txtBlackPixel.Text, out int bp)) _project.BlackPixelPercent = bp;
        }

        private void GenerateXml()
        {
            SyncSettings();
            string xml = XmlConfigBuilder.Build(_project);
            ShowXmlDialog(xml);
        }

        /// <summary>Check OMR pre-loaded with THIS project: the current mapping is
        /// generated to a temp XML and the active sheet's image saved beside it, so
        /// the check window opens already showing the result.</summary>
        private void OpenCheckOmr()
        {
            string xmlPath = null, imgPath = null;
            try
            {
                bool hasElements = _project.OmrInfo != null || _project.RotationTop != null || _project.RotationBottom != null
                                   || _project.Pages.Any(p => p.Elements.Count > 0);
                if (hasElements)
                {
                    SyncSettings();
                    xmlPath = Path.Combine(Path.GetTempPath(), "OmrXmlBuilder_check.xml");
                    File.WriteAllText(xmlPath, XmlConfigBuilder.Build(_project));
                }
                var page = CurrentPage();
                if (page != null && _pageImages.TryGetValue(page, out Bitmap bmp) && bmp != null)
                {
                    imgPath = Path.Combine(Path.GetTempPath(), "OmrXmlBuilder_check.png");
                    bmp.Save(imgPath, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex) { SetStatus("Check OMR প্রস্তুতিতে সমস্যা: " + ex.Message); }

            new CheckOmrForm(xmlPath, imgPath, xmlPath != null && imgPath != null).Show(this);
        }

        private void ShowXmlDialog(string xml)
        {
            var f = new Form { Text = "Generated XML", Icon = Icons.App, Width = 760, Height = 720, StartPosition = FormStartPosition.CenterParent };
            var tb = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f), Text = xml };
            f.Controls.Add(tb);
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            var copy = new Button { Text = "Copy", Left = 10, Top = 8, Width = 100 };
            copy.Click += (s, e) => { Clipboard.SetText(xml); };
            var save = new Button { Text = "Save .xml", Left = 120, Top = 8, Width = 120 };
            save.Click += (s, e) =>
            {
                using (var dlg = new SaveFileDialog { Filter = "XML|*.xml", FileName = _project.DesignFor + "_omr_config.xml" })
                    if (dlg.ShowDialog(f) == DialogResult.OK) File.WriteAllText(dlg.FileName, xml);
            };
            bar.Controls.Add(copy);
            bar.Controls.Add(save);
            f.Controls.Add(bar);
            f.ShowDialog(this);
        }

        private void SetStatus(string s) => _lblStatus.Text = "  " + s;
    }
}
