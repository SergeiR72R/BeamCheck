#if !BRICSCAD
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BeamCheck.Core.Spindles;

namespace BeamCheck.Cad
{
    internal enum ListAction { None, Show, Details, AddParts, RemoveParts, Link, NothingOnTop, Manual, Clear, ToggleOk, UndoRemove, NextProblem, Finish, Close }

    /// <summary>
    /// Main window of SPINDELLOAD: all spindles in a sortable table (problems first), buttons for every change.
    /// The window closes for each drawing action and opens again afterwards (same pattern as BEAMLOAD).
    /// </summary>
    internal sealed class SpindleListForm : Form
    {
        public ListAction Action = ListAction.None;
        public string SelectedKey;

        private static bool s_onlyProblems;
        private static string s_sortColumn = "Fd kN";
        private static ListSortDirection s_sortDir = ListSortDirection.Descending;
        private static string s_lastKey;

        private readonly DataGridView _grid;
        private readonly DataTable _table;
        private readonly CheckBox _onlyProblems;
        private readonly CalcResult _res;

        public SpindleListForm(CalcResult res, SpindleSettings s, Sidecar sc, string header)
        {
            _res = res;
            Text = "BeamCheck " + Plugin.BuildTag + " – SPINDELLOAD – spindle loads";
            Width = 1180; Height = 720;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            _table = new DataTable();
            _table.Columns.Add("Key", typeof(string));
            _table.Columns.Add("No", typeof(int));
            _table.Columns.Add("Type", typeof(string));
            _table.Columns.Add("Fd kN", typeof(double));
            _table.Columns.Add("Gk kN", typeof(double));
            _table.Columns.Add("Qk kN", typeof(double));
            _table.Columns.Add("Status", typeof(string));
            _table.Columns.Add("Your changes", typeof(string));
            _table.Columns.Add("What to check", typeof(string));
            _table.Columns.Add("Z foot", typeof(double));
            _table.Columns.Add("Sort", typeof(int));
            foreach (var sp in res.Spindles)
            {
                var e = sc.Get(sp.Key, false);
                var ch = new List<string>();
                if (e != null)
                {
                    if (e.LinkHandles.Count > 0) ch.Add(e.LinkHandles.Count + " linked");
                    if (e.HeadAnswer == "none") ch.Add("nothing on top");
                    if (e.AddedIds.Count > 0) ch.Add("+" + e.AddedIds.Count + " parts");
                    if (e.Manual.Count > 0) ch.Add(e.Manual.Count + " manual");
                }
                string what = sp.Problems.Count > 0 ? sp.Problems[0] : "";
                int sort = sp.Confirmed ? 3 : (sp.Open ? 0 : (sp.Problems.Count > 0 ? 1 : 2));
                _table.Rows.Add(sp.Key, sp.Index, sp.Type, Math.Round(sp.DesignKn, 2), Math.Round(sp.DeadKn, 2), Math.Round(sp.ServiceKn, 2),
                                sp.Status, string.Join(", ", ch), what, Math.Round(sp.MinZ, 0), sort);
            }

            // ---- header
            var top = new Panel { Dock = DockStyle.Top, Height = 86, Padding = new Padding(8) };
            var lbl = new Label { Text = header, Dock = DockStyle.Fill, AutoSize = false };
            _onlyProblems = new CheckBox { Text = "Only spindles to check", Dock = DockStyle.Bottom, Height = 22, Checked = s_onlyProblems };
            _onlyProblems.CheckedChanged += (o, a) => { s_onlyProblems = _onlyProblems.Checked; ApplyFilter(); };
            top.Controls.Add(lbl); top.Controls.Add(_onlyProblems);

            // ---- grid
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, BackgroundColor = SystemColors.Window
            };
            _grid.DataSource = new DataView(_table);
            _grid.DataBindingComplete += (o, a) =>
            {
                foreach (var c in new[] { "Key", "Sort" }) if (_grid.Columns.Contains(c)) _grid.Columns[c].Visible = false;
                foreach (var c in new[] { "Fd kN", "Gk kN", "Qk kN" }) if (_grid.Columns.Contains(c)) _grid.Columns[c].DefaultCellStyle.Format = "0.00";
                if (_grid.Columns.Contains("Z foot")) _grid.Columns["Z foot"].DefaultCellStyle.Format = "0";
                if (_grid.Columns.Contains("What to check")) _grid.Columns["What to check"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            };
            _grid.CellFormatting += (o, a) =>
            {
                if (a.RowIndex < 0) return;
                var row = ((DataRowView)_grid.Rows[a.RowIndex].DataBoundItem).Row;
                string st = (string)row["Status"];
                if (st.StartsWith("OPEN")) a.CellStyle.BackColor = Color.FromArgb(255, 205, 200);
                else if (st == "CHECK") a.CellStyle.BackColor = Color.FromArgb(255, 245, 190);
                else if (st.StartsWith("OK")) a.CellStyle.BackColor = Color.FromArgb(215, 245, 215);
            };
            _grid.CellDoubleClick += (o, a) => { if (a.RowIndex >= 0) Finish(ListAction.Show); };
            _grid.Sorted += (o, a) =>
            {
                if (_grid.SortedColumn != null) { s_sortColumn = _grid.SortedColumn.Name; s_sortDir = _grid.SortOrder == SortOrder.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending; }
            };
            KeyDown += (o, a) => { if (a.KeyCode == Keys.Enter) { a.Handled = true; Finish(ListAction.Show); } };

            // ---- buttons
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 236, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6), AutoScroll = true };
            AddGroup(right, "Look");
            AddButton(right, "Show in model (work zone)", ListAction.Show, "Isolates the area around the spindle and highlights everything that loads it.");
            AddButton(right, "Calculation (Rechenweg)", ListAction.Details, "BeamCheck's step-by-step calculation for this spindle.");
            AddButton(right, "Next spindle to check  ▶", ListAction.NextProblem, "Selects the next open head / spindle with a warning.");
            AddGroup(right, "Change");
            AddButton(right, "What stands on it? (click)", ListAction.Link, "Head spindle: click near the spindle / vertical standing on it. Its whole load is passed into this spindle and down.");
            AddButton(right, "Nothing stands on it", ListAction.NothingOnTop, "Head spindle really carries nothing more: mark it as answered.");
            AddButton(right, "Add missing parts (window)", ListAction.AddParts, "Drag a window in the work zone: the parts found are added to this spindle's calculation.");
            AddButton(right, "Remove wrong parts (window)", ListAction.RemoveParts, "Drag a window: the parts found are removed from the whole calculation.");
            AddButton(right, "Manual load…", ListAction.Manual, "Type a dead / service load in kN; it also goes into the spindles below.");
            AddButton(right, "Undo my changes on it", ListAction.Clear, "Removes links, added parts, manual loads and answers of this spindle.");
            AddButton(right, "Undo last removal", ListAction.UndoRemove, "Brings back the parts removed last.");
            AddButton(right, "Mark checked / unchecked", ListAction.ToggleOk, "Your review status (green).");
            AddGroup(right, "Finish");
            AddButton(right, "Excel + labels, close", ListAction.Finish, "Writes <drawing>_SPINDLELOAD.xlsx and optional labels.");
            AddButton(right, "Close", ListAction.Close, "Changes are saved with the drawing (.spindleload.json).");

            Controls.Add(_grid);
            Controls.Add(right);
            Controls.Add(top);

            Shown += (o, a) =>
            {
                ApplyFilter();
                try
                {
                    if (_grid.Columns.Contains(s_sortColumn)) _grid.Sort(_grid.Columns[s_sortColumn], s_sortDir);
                }
                catch (Exception) { }
                Select(s_lastKey);
            };
        }

        private void ApplyFilter()
        {
            var view = (DataView)_grid.DataSource;
            view.RowFilter = s_onlyProblems ? "Sort < 2" : "";
            if (_grid.Columns.Contains("Key")) _grid.Columns["Key"].Visible = false;
            if (_grid.Columns.Contains("Sort")) _grid.Columns["Sort"].Visible = false;
        }

        private void Select(string key)
        {
            if (key == null) return;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                var row = ((DataRowView)r.DataBoundItem).Row;
                if ((string)row["Key"] == key)
                {
                    r.Selected = true;
                    try { _grid.CurrentCell = r.Cells["No"]; _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, r.Index - 5); } catch (Exception) { }
                    return;
                }
            }
        }

        private string CurrentKey()
        {
            if (_grid.CurrentRow == null) return null;
            var drv = _grid.CurrentRow.DataBoundItem as DataRowView;
            return drv == null ? null : (string)drv.Row["Key"];
        }

        private void Finish(ListAction a)
        {
            if (a == ListAction.NextProblem)
            {
                // next row (in current sort order) that needs checking
                int start = _grid.CurrentRow != null ? _grid.CurrentRow.Index + 1 : 0;
                for (int k = 0; k < _grid.Rows.Count; k++)
                {
                    var r = _grid.Rows[(start + k) % Math.Max(1, _grid.Rows.Count)];
                    var row = ((DataRowView)r.DataBoundItem).Row;
                    if ((int)row["Sort"] < 2) { Select((string)row["Key"]); return; }
                }
                MessageBox.Show(this, "No spindle left to check.", Text);
                return;
            }
            SelectedKey = CurrentKey();
            bool needsRow = a != ListAction.Close && a != ListAction.Finish && a != ListAction.UndoRemove;
            if (needsRow && SelectedKey == null) { MessageBox.Show(this, "Select a spindle in the list first.", Text); return; }
            s_lastKey = SelectedKey ?? s_lastKey;
            Action = a;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static void AddGroup(FlowLayoutPanel p, string text)
        {
            p.Controls.Add(new Label { Text = text, Width = 214, Height = 22, Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft });
        }

        private void AddButton(FlowLayoutPanel p, string text, ListAction a, string tip)
        {
            var b = new Button { Text = text, Width = 214, Height = 30, TextAlign = ContentAlignment.MiddleLeft };
            b.Click += (o, e) => Finish(a);
            new ToolTip().SetToolTip(b, tip);
            p.Controls.Add(b);
        }
    }

    /// <summary>Simple read-only text window (Rechenweg, lists).</summary>
    internal sealed class SpindleTextForm : Form
    {
        public SpindleTextForm(string title, IEnumerable<string> lines)
        {
            Text = title; Width = 1000; Height = 700; StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; MinimizeBox = false;
            var tb = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f), Text = string.Join(Environment.NewLine, lines)
            };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var close = new Button { Text = "Close", Width = 100, DialogResult = DialogResult.OK };
            var copy = new Button { Text = "Copy", Width = 100 };
            copy.Click += (o, e) => { try { Clipboard.SetText(tb.Text); } catch (Exception) { } };
            bottom.Controls.Add(close); bottom.Controls.Add(copy);
            Controls.Add(tb); Controls.Add(bottom);
            AcceptButton = close; CancelButton = close;
            Shown += (o, e) => { tb.SelectionStart = 0; tb.SelectionLength = 0; };
        }
    }

    /// <summary>
    /// Progress window: phase, bar, count, elapsed and remaining time, Cancel.
    /// Shown modeless; Report() pumps the Windows message loop so the window stays alive while AutoCAD works.
    /// </summary>
    internal sealed class SpindleProgressWindow : System.Windows.Forms.Form
    {
        private readonly System.Windows.Forms.Label _title = new System.Windows.Forms.Label { AutoSize = false, Left = 14, Top = 12, Width = 470, Height = 22, Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold) };
        private readonly System.Windows.Forms.Label _phase = new System.Windows.Forms.Label { AutoSize = false, Left = 14, Top = 40, Width = 470, Height = 20 };
        private readonly System.Windows.Forms.ProgressBar _bar = new System.Windows.Forms.ProgressBar { Left = 14, Top = 64, Width = 470, Height = 22, Minimum = 0, Maximum = 1000 };
        private readonly System.Windows.Forms.Label _count = new System.Windows.Forms.Label { AutoSize = false, Left = 14, Top = 92, Width = 470, Height = 20 };
        private readonly System.Windows.Forms.Label _time = new System.Windows.Forms.Label { AutoSize = false, Left = 14, Top = 114, Width = 470, Height = 20 };
        private readonly System.Windows.Forms.Label _detail = new System.Windows.Forms.Label { AutoSize = false, Left = 14, Top = 136, Width = 470, Height = 20, ForeColor = System.Drawing.Color.DarkBlue };
        private readonly System.Windows.Forms.Label _log = new System.Windows.Forms.Label { AutoSize = false, Left = 14, Top = 158, Width = 370, Height = 54, ForeColor = System.Drawing.Color.DimGray };
        private readonly System.Windows.Forms.Button _cancel = new System.Windows.Forms.Button { Text = "Cancel", Left = 394, Top = 204, Width = 90, Height = 28 };
        private readonly System.Diagnostics.Stopwatch _total = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Diagnostics.Stopwatch _phaseClock = new System.Diagnostics.Stopwatch();
        private readonly System.Collections.Generic.List<string> _done = new System.Collections.Generic.List<string>();
        private string _lastPhase;
        private int _lastPumpTick;

        public bool Cancelled { get; private set; }

        public SpindleProgressWindow(string title)
        {
            Text = "BeamCheck " + Plugin.BuildTag + " – SPINDELLOAD – please wait";
            Font = new System.Drawing.Font("Segoe UI", 9f);
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(498, 240);
            ControlBox = false; ShowInTaskbar = false; TopMost = true; MaximizeBox = false; MinimizeBox = false;
            _title.Text = title;
            _cancel.Click += (o, e) => { Cancelled = true; _cancel.Enabled = false; _cancel.Text = "Stopping…"; };
            Controls.AddRange(new System.Windows.Forms.Control[] { _title, _phase, _bar, _count, _time, _detail, _log, _cancel });
        }

        /// <summary>Update the window (cheap: real redraw at most ~10 times per second unless force).</summary>
        public void Report(string phase, int done, int total, bool force = false, string detail = null)
        {
            int tick = Environment.TickCount;
            if (!force && unchecked(tick - _lastPumpTick) < 100) return;
            _lastPumpTick = tick;
            if (phase != _lastPhase)
            {
                if (_lastPhase != null) _done.Add(_lastPhase + ": " + Fmt(_phaseClock.Elapsed));
                _lastPhase = phase;
                _phaseClock.Restart();
                _log.Text = string.Join("\n", _done.Count > 3 ? _done.GetRange(_done.Count - 3, 3) : _done);
            }
            _phase.Text = phase;
            _detail.Text = string.IsNullOrEmpty(detail) ? "" : "Now: " + detail;
            _bar.Value = total > 0 ? Math.Max(0, Math.Min(1000, (int)(1000.0 * done / total))) : 0;
            _count.Text = total > 0 ? done + " of " + total + "   (" + (100.0 * done / Math.Max(1, total)).ToString("0") + " %)" : "";
            string remaining = "estimating …";
            if (done >= 3 && _phaseClock.Elapsed.TotalSeconds > 1.5 && total > 0)
            {
                double per = _phaseClock.Elapsed.TotalSeconds / done;
                remaining = "about " + Fmt(TimeSpan.FromSeconds(Math.Max(0, per * (total - done))));
            }
            _time.Text = "This step: " + Fmt(_phaseClock.Elapsed) + "   remaining: " + remaining + "   total: " + Fmt(_total.Elapsed);
            System.Windows.Forms.Application.DoEvents();
        }

        public string Summary()
        {
            var all = new System.Collections.Generic.List<string>(_done);
            if (_lastPhase != null) all.Add(_lastPhase + ": " + Fmt(_phaseClock.Elapsed));
            return string.Join(", ", all) + "  (total " + Fmt(_total.Elapsed) + ")";
        }

        private static string Fmt(TimeSpan t)
        {
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                                     : t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }
    }
}
#endif
