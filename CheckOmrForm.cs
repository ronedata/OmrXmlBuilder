using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OmrXmlBuilder
{
    /// <summary>
    /// "Check OMR" — reads a filled sheet with a generated config XML and shows
    /// the result like the commercial OMR Checker: magenta reading areas, green
    /// detected marks, plus a member=value list. For verifying a mapping without
    /// leaving the builder; OslOmr.Core stays the production authority.
    /// </summary>
    public class CheckOmrForm : Form
    {
        private TextBox _txtXml, _txtImg;
        private CheckBox _chkReading, _chkGrid, _chkRotate, _chkSkew;
        private float _skewApplied;
        private ListView _lstResults;
        private TextBox _txtDetail;   // selected row-এর পুরো value (wrap করে) দেখায়
        private Label _lblInfo;
        private ToolStripStatusLabel _lblStatus;
        private Panel _panel;
        private PictureBox _pic;

        private Bitmap _source;      // the opened scan (possibly auto-rotated by the reader)
        private Bitmap _annotated;   // overlay render shown/saved
        private OmrProject _proj;
        private CheckResult _result;
        private float _zoom = 1f;
        private bool _panning; private Point _panMouse, _panLoc;

        public CheckOmrForm(string configPath = null, string imagePath = null, bool autoRun = false)
        {
            Text = "Check OMR — generated XML দিয়ে filled sheet পড়ে দেখুন";
            Icon = Icons.App;
            Width = 1250; Height = 850;
            StartPosition = FormStartPosition.CenterParent;
            BuildUi();
            if (!string.IsNullOrEmpty(configPath)) _txtXml.Text = configPath;
            if (!string.IsNullOrEmpty(imagePath)) _txtImg.Text = imagePath;
            if (autoRun)
                Shown += (s, e) =>
                {
                    RunCheck();
                    Status("XmlBuilder-এর বর্তমান image + XML দিয়ে check হলো — অন্য sheet দেখতে double-click করুন.");
                };
        }

        private void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 66 };
            Controls.Add(top);

            top.Controls.Add(new Label { Text = "Config XML", Left = 8, Top = 9, Width = 70 });
            _txtXml = new TextBox { Left = 80, Top = 6, Width = 430, ReadOnly = true };
            top.Controls.Add(_txtXml);
            var bXml = new Button { Text = "Browse…", Left = 515, Top = 4, Width = 75 };
            bXml.Click += (s, e) => Browse(_txtXml, "OMR config|*.xml");
            top.Controls.Add(bXml);

            top.Controls.Add(new Label { Text = "Image", Left = 8, Top = 39, Width = 70 });
            _txtImg = new TextBox { Left = 80, Top = 36, Width = 430, ReadOnly = true };
            top.Controls.Add(_txtImg);
            var bImg = new Button { Text = "Browse…", Left = 515, Top = 34, Width = 75 };
            bImg.Click += (s, e) => Browse(_txtImg, "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff");
            top.Controls.Add(bImg);

            _chkReading = new CheckBox { Text = "Draw Reading Area", Left = 605, Top = 6, Width = 140, Checked = true, ForeColor = Color.Magenta };
            _chkGrid = new CheckBox { Text = "Draw Grid", Left = 750, Top = 6, Width = 90 };
            _chkRotate = new CheckBox { Text = "Auto Rotate 180", Left = 605, Top = 36, Width = 140, Checked = true };
            _chkSkew = new CheckBox { Text = "Auto Skew Correct", Left = 750, Top = 36, Width = 140, Checked = true };
            _chkReading.CheckedChanged += (s, e) => Rerender();
            _chkGrid.CheckedChanged += (s, e) => Rerender();
            top.Controls.Add(_chkReading); top.Controls.Add(_chkGrid); top.Controls.Add(_chkRotate); top.Controls.Add(_chkSkew);

            var bCheck = new Button { Text = "▶  Check", Left = 850, Top = 4, Width = 110, Height = 27, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            bCheck.Click += (s, e) => RunCheck();
            top.Controls.Add(bCheck);
            var bSave = new Button { Text = "Save Image…", Left = 850, Top = 34, Width = 110 };
            bSave.Click += (s, e) => SaveAnnotated();
            top.Controls.Add(bSave);

            // right: results
            var right = new Panel { Dock = DockStyle.Right, Width = 430 };
            Controls.Add(right);
            _lblInfo = new Label { Dock = DockStyle.Top, Height = 46, Padding = new Padding(6, 4, 4, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            right.Controls.Add(_lblInfo);
            _lstResults = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = true };
            _lstResults.Columns.Add("member", 100);
            _lstResults.Columns.Add("value (পড়া হয়েছে)", 150);
            _lstResults.Columns.Add("black %", 62);
            _lstResults.Columns.Add("দরকার", 55);
            right.Controls.Add(_lstResults);

            // selected row-এর পুরো value নিচে wrap করে — লম্বা answer string পুরোটা পড়া যায়
            _txtDetail = new TextBox
            {
                Dock = DockStyle.Bottom, Height = 96, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9.5f)
            };
            right.Controls.Add(_txtDetail);
            _lstResults.BringToFront();
            _lstResults.SelectedIndexChanged += (s, e) =>
            {
                if (_lstResults.SelectedItems.Count == 0) { _txtDetail.Text = ""; return; }
                var it = _lstResults.SelectedItems[0];
                string extra = it.SubItems.Count > 3 && it.SubItems[2].Text != ""
                    ? $"{Environment.NewLine}{Environment.NewLine}black: {it.SubItems[2].Text}    দরকার: {it.SubItems[3].Text}"
                    : "";
                _txtDetail.Text = $"{it.Text} = {it.SubItems[1].Text}{extra}";
            };

            // value copy: Ctrl+C = selected rows, double-click = ঐ row-এর value
            _lstResults.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) { CopyResults(false); e.Handled = true; }
                if (e.Control && e.KeyCode == Keys.A)
                {
                    foreach (ListViewItem it in _lstResults.Items) it.Selected = true;
                    e.Handled = true;
                }
            };
            _lstResults.MouseDoubleClick += (s, e) =>
            {
                if (_lstResults.SelectedItems.Count == 0) return;
                string v = _lstResults.SelectedItems[0].SubItems[1].Text;
                if (v.Length > 0) { Clipboard.SetText(v); Status($"Copied: {v}"); }
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Copy value", null, (s, e) => CopyResults(false));
            menu.Items.Add("Copy সব (member = value)", null, (s, e) => CopyResults(true));
            _lstResults.ContextMenuStrip = menu;
            _lstResults.Resize += (s, e) => FitResultColumns();

            // canvas — wheel zoom, drag pan (same feel as the designer)
            _panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(60, 60, 65) };
            Controls.Add(_panel);
            _panel.BringToFront();
            _pic = new PictureBox { SizeMode = PictureBoxSizeMode.StretchImage, Location = new Point(0, 0) };
            _panel.Controls.Add(_pic);
            _pic.MouseWheel += (s, e) => Zoom(e.Delta > 0 ? 1.15f : 1f / 1.15f);
            _panel.MouseWheel += (s, e) => Zoom(e.Delta > 0 ? 1.15f : 1f / 1.15f);
            _pic.MouseDown += (s, e) =>
            {
                ActiveControl = null;   // wheel-zoom কাজ করার জন্য canvas-এ click-ই যথেষ্ট
                _panning = true; _panMouse = Cursor.Position; _panLoc = _pic.Location; _pic.Cursor = Cursors.Hand;
            };
            _pic.MouseMove += (s, e) =>
            {
                if (!_panning) return;
                var now = Cursor.Position;
                _pic.Location = new Point(_panLoc.X + now.X - _panMouse.X, _panLoc.Y + now.Y - _panMouse.Y);
            };
            _pic.MouseUp += (s, e) => { _panning = false; _pic.Cursor = Cursors.Default; };
            // canvas-এ double-click → image open (config সেট থাকলে সাথে সাথে check)
            _pic.MouseDoubleClick += (s, e) => OpenImageAndCheck();
            _panel.MouseDoubleClick += (s, e) => OpenImageAndCheck();
            // wheel zoom even when a button/list had focus — route by cursor position
            MouseWheel += (s, e) =>
            {
                var pt = _panel.PointToClient(Cursor.Position);
                if (_panel.ClientRectangle.Contains(pt)) Zoom(e.Delta > 0 ? 1.15f : 1f / 1.15f);
            };

            var status = new StatusStrip();
            _lblStatus = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            status.Items.Add(_lblStatus);
            Controls.Add(status);
            Status("Config XML আর filled image বেছে নিয়ে Check চাপুন.");
        }

        private void Browse(TextBox target, string filter)
        {
            using (var dlg = new OpenFileDialog { Filter = filter })
                if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
        }

        /// <summary>Double-click on the canvas: pick a filled image; if a config is
        /// already selected the check runs immediately.</summary>
        private void OpenImageAndCheck()
        {
            using (var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _txtImg.Text = dlg.FileName;
                if (File.Exists(_txtXml.Text)) RunCheck();
                else Status("Image নেওয়া হলো — এবার config XML বেছে নিয়ে Check চাপুন.");
            }
        }

        private void RunCheck()
        {
            if (!File.Exists(_txtXml.Text)) { Status("আগে config XML বেছে নিন."); return; }
            if (!File.Exists(_txtImg.Text)) { Status("আগে filled image বেছে নিন."); return; }
            try
            {
                _proj = ConfigReader.Load(_txtXml.Text);
                _source?.Dispose();
                _source = (Bitmap)Image.FromStream(new MemoryStream(File.ReadAllBytes(_txtImg.Text)));

                // straighten first — a tilted sheet throws the whole grid off
                _skewApplied = 0f;
                if (_chkSkew.Checked)
                {
                    try
                    {
                        var straight = GridDetector.Deskew(_source, out float sa);
                        if (straight != null) { _source.Dispose(); _source = straight; _skewApplied = sa; }
                    }
                    catch { }
                }

                _result = OmrReader.Check(_source, _proj, _chkRotate.Checked);
                if (!_result.GridOk)
                {
                    Status("Grid detection FAILED — timing mark/corner marker দেখা যাচ্ছে না.");
                    MessageBox.Show(this, _result.Log, "Grid detection failed");
                    return;
                }

                ShowResults();
                Rerender();
                FitZoom();
                Status($"পড়া শেষ — omrNo={_result.OmrNo}" +
                       (_skewApplied != 0f ? $"  (skew {_skewApplied:F2}° ঠিক করা হয়েছে)" : "") +
                       (_result.Rotated ? "  (image auto-rotated 180°)" : "") +
                       $"   gridX={_result.Grid.GridX}, gridY={_result.Grid.GridY}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Check OMR error");
            }
        }

        private void ShowResults()
        {
            _lblInfo.Text = $"omrNo detected: {_result.OmrNo}" +
                            (_result.Rotated ? "   ⟳ rotated 180°" : "") +
                            $"\npage read: OMR Page {_result.Page?.OmrNo.ToString() ?? "?"}";
            _lstResults.BeginUpdate();
            _lstResults.Items.Clear();
            foreach (var rr in _result.Regions)
            {
                var item = new ListViewItem(rr.El.MemberName);
                item.SubItems.Add(rr.Value);
                bool measured = rr.El.Kind != ElementKind.Virtual && (rr.IsArea || rr.Bubbles.Count > 0);
                item.SubItems.Add(measured ? $"{rr.MaxPct:0.#}%" : "");   // পাওয়া গেছে (সেরা cell)
                // imagePart-এ threshold-check হয় না — "দরকার" ফাঁকা থাকে
                item.SubItems.Add(measured && rr.El.Kind != ElementKind.ImagePart ? $"{rr.Threshold:0.##}%" : "");
                if (rr.Value.Contains("*")) item.ForeColor = Color.Red;     // double-fill
                else if (rr.El.Kind == ElementKind.Virtual) item.ForeColor = Color.Gray;
                else if (measured && !rr.IsArea && rr.Value == "" && rr.MaxPct < rr.Threshold)
                    item.ForeColor = Color.DarkOrange;   // কিছুই threshold পেরোয়নি — ফাঁকা বা mapping সরে আছে
                _lstResults.Items.Add(item);
            }

            // Row Data — roll | reg | সব answer মিলিয়ে এক লাইনে (checker-এর output row-এর মতো)
            string roll = _result.Regions.FirstOrDefault(r => r.El.MemberName == "rollNo")?.Value ?? "";
            string reg = _result.Regions.FirstOrDefault(r => r.El.MemberName == "registrationNo")?.Value ?? "";
            var ansParts = _result.Regions
                .Where(r => !r.IsArea && r.Bubbles.Count > 0 && r.El.HasLineStart)
                .OrderBy(r => r.El.LineStart)
                .Select(r => r.Value)
                .ToList();
            if (roll != "" || reg != "" || ansParts.Count > 0)
            {
                var rowData = new ListViewItem("Row Data")
                {
                    Font = new Font(_lstResults.Font, FontStyle.Bold),
                    ForeColor = Color.Navy
                };
                rowData.SubItems.Add($"{roll} | {reg} | {string.Join(" ", ansParts)}");
                rowData.SubItems.Add("");
                rowData.SubItems.Add("");
                _lstResults.Items.Add(rowData);
            }

            _lstResults.EndUpdate();
            FitResultColumns();   // value column fits the panel — no horizontal scroll
            _txtDetail.Text = "";
        }

        private void Rerender()
        {
            if (_source == null || _result == null || !_result.GridOk) return;
            // dispose the OLD bitmap only after the PictureBox points at the new one,
            // otherwise a paint in between hits a disposed image
            var old = _annotated;
            _annotated = OmrReader.Render(_source, _result, _chkReading.Checked, _chkGrid.Checked);
            _pic.Image = _annotated;
            old?.Dispose();
            ApplyZoom();
        }

        private void Zoom(float factor)
        {
            if (_annotated == null) return;
            // anchor: keep the image point under the cursor in place
            var cp = _panel.PointToClient(Cursor.Position);
            float ix = (cp.X - _pic.Left) / _zoom, iy = (cp.Y - _pic.Top) / _zoom;
            _zoom = Math.Max(0.05f, Math.Min(8f, _zoom * factor));
            ApplyZoom();
            _pic.Location = new Point((int)(cp.X - ix * _zoom), (int)(cp.Y - iy * _zoom));
        }

        private void ApplyZoom()
        {
            if (_annotated == null) return;
            _pic.Size = new Size((int)(_annotated.Width * _zoom), (int)(_annotated.Height * _zoom));
        }

        private void FitZoom()
        {
            if (_annotated == null) return;
            _zoom = Math.Min((float)_panel.ClientSize.Width / _annotated.Width,
                             (float)_panel.ClientSize.Height / _annotated.Height);
            if (_zoom <= 0) _zoom = 1f;
            ApplyZoom();
            _pic.Location = new Point(Math.Max(0, (_panel.ClientSize.Width - _pic.Width) / 2),
                                      Math.Max(0, (_panel.ClientSize.Height - _pic.Height) / 2));
        }

        private void SaveAnnotated()
        {
            if (_annotated == null) { Status("আগে Check চালান."); return; }
            using (var dlg = new SaveFileDialog
            {
                Filter = "PNG|*.png|JPEG|*.jpg",
                FileName = Path.GetFileNameWithoutExtension(_txtImg.Text) + "_checked.png"
            })
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _annotated.Save(dlg.FileName);
                    Status("Saved: " + dlg.FileName);
                }
        }

        /// <summary>Size the value column to the space left by the other three —
        /// everything stays visible without horizontal scrolling; long values are
        /// read in full from the detail box below.</summary>
        private void FitResultColumns()
        {
            if (_lstResults.Columns.Count < 4) return;
            int w = _lstResults.ClientSize.Width
                    - _lstResults.Columns[0].Width - _lstResults.Columns[2].Width - _lstResults.Columns[3].Width - 4;
            _lstResults.Columns[1].Width = Math.Max(80, w);
        }

        /// <summary>Copy results to the clipboard — selected rows (or all when none
        /// selected). withMember=true copies "member = value" lines, else values only.</summary>
        private void CopyResults(bool withMember)
        {
            var rows = _lstResults.SelectedItems.Count > 0
                ? _lstResults.SelectedItems.Cast<ListViewItem>()
                : _lstResults.Items.Cast<ListViewItem>();
            var lines = rows
                .Select(it => withMember ? $"{it.Text} = {it.SubItems[1].Text}" : it.SubItems[1].Text)
                .Where(t => t.Length > 0)
                .ToList();
            if (lines.Count == 0) { Status("Copy — কিছু নেই."); return; }
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            Status($"{lines.Count}টা value copy হলো.");
        }

        private void Status(string s) => _lblStatus.Text = "  " + s;

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _source?.Dispose(); _annotated?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
