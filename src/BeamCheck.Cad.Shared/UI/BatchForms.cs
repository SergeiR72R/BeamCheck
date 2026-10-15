using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.Serialization;
using System.Windows.Forms;
using BeamCheck.Core.Settings;

namespace BeamCheck.Cad.UI
{
    /// <summary>One line of the beam list: kept in the index file next to the drawing so beams can be found again later.</summary>
    [DataContract]
    internal sealed class BeamRow
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Handle { get; set; }
        [DataMember(Order = 3)] public string Article { get; set; }
        [DataMember(Order = 4)] public string Description { get; set; }
        [DataMember(Order = 5)] public double Length { get; set; }
        [DataMember(Order = 6)] public int Supports { get; set; }
        [DataMember(Order = 7)] public int Loads { get; set; }
        [DataMember(Order = 8)] public double MaxLoad { get; set; }
        [DataMember(Order = 9)] public double MaxReaction { get; set; }
        [DataMember(Order = 10)] public string Notes { get; set; }
    }

    [DataContract]
    internal sealed class BeamIndex
    {
        [DataMember(Order = 1)] public string Drawing { get; set; }
        [DataMember(Order = 2)] public string Created { get; set; }
        [DataMember(Order = 3)] public List<BeamRow> Beams { get; set; } = new List<BeamRow>();
    }

    /// <summary>Progress of a long run with the estimated remaining time; Cancel stops it at the next beam.</summary>
    internal sealed class ProgressForm : Form
    {
        private readonly Label _phase = new Label { AutoSize = false, Width = 440, Height = 22, Left = 12, Top = 12 };
        private readonly Label _current = new Label { AutoSize = false, Width = 440, Height = 22, Left = 12, Top = 70 };
        private readonly Label _eta = new Label { AutoSize = false, Width = 440, Height = 22, Left = 12, Top = 94 };
        private readonly ProgressBar _bar = new ProgressBar { Width = 440, Height = 22, Left = 12, Top = 40, Minimum = 0, Maximum = 1000 };
        private readonly Stopwatch _clock = new Stopwatch();
        private string _lastPhase;

        public ProgressForm(string title)
        {
            Text = title;
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(466, 168);
            ControlBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            var cancel = new Button { Text = "Abbrechen", Left = 366, Top = 128, Width = 88 };
            cancel.Click += (s, e) => Cancelled = true;
            Controls.AddRange(new Control[] { _phase, _bar, _current, _eta, cancel });
        }

        public bool Cancelled { get; private set; }

        /// <summary>Updates the bar; the remaining time is extrapolated from the time per item of the current phase.</summary>
        public void Report(string phase, int done, int total, string current)
        {
            if (phase != _lastPhase)
            {
                _lastPhase = phase;
                _clock.Restart();
            }

            _phase.Text = phase + " — " + done + " / " + total;
            _bar.Value = total <= 0 ? 0 : Math.Min(1000, (int)(1000.0 * done / total));
            _current.Text = string.IsNullOrEmpty(current) ? "" : "Aktuell: " + current;
            if (done >= 2 && _clock.Elapsed.TotalSeconds > 1)
            {
                double perItem = _clock.Elapsed.TotalSeconds / done;
                var left = TimeSpan.FromSeconds(Math.Max(0, perItem * (total - done)));
                _eta.Text = "Verbleibende Zeit: ca. " + (left.TotalHours >= 1 ? (int)left.TotalHours + ":" : "") + left.Minutes.ToString("00") + ":" + left.Seconds.ToString("00")
                            + "   (vergangen " + _clock.Elapsed.Minutes.ToString("00") + ":" + _clock.Elapsed.Seconds.ToString("00") + ")";
            }
            else
            {
                _eta.Text = "Verbleibende Zeit: wird berechnet …";
            }

            Application.DoEvents();
        }
    }

    internal sealed class BatchOptionsForm : Form
    {
        private readonly ComboBox _class = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly CheckBox _excel = new CheckBox { Text = "Excel-Tabelle mit Abbildungen erstellen", Checked = true, AutoSize = true };
        private readonly CheckBox _labels = new CheckBox { Text = "Balkennamen im Modell beschriften (Layer BEAMCHECK_NAMEN)", Checked = true, AutoSize = true };
        private readonly CheckBox _navis = new CheckBox { Text = "Navisworks-Ansichtspunkte (XML): Träger in NWF/NWC per Name anspringen", Checked = true, AutoSize = true };
        private readonly CheckBox _pictures = new CheckBox { Text = "Abbildungen in die Zeichnung einfügen", Checked = false, AutoSize = true };
        private readonly List<int> _classes = new List<int>();

        public BatchOptionsForm(BeamCheckSettings settings, int count)
        {
            Text = "BeamCheck — alle Träger berechnen";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(500, 262);

            var info = new Label { Left = 12, Top = 12, Width = 476, Height = 36, Text = count + " Objekte gewählt. Alle Träger werden gesucht, benannt (B-001, B-002 …) und einzeln berechnet: Auflager und Lasten von links nach rechts." };
            var cl = new Label { Left = 12, Top = 60, AutoSize = true, Text = "Lastklasse:" };
            _class.Left = 100;
            _class.Top = 56;
            foreach (var c in settings.LoadClasses)
            {
                _classes.Add(c.Class);
                _class.Items.Add("Klasse " + c.Class + " — " + c.ServiceLoadKnPerM2.ToString("0.00") + " kN/m²");
            }

            _class.SelectedIndex = Math.Max(0, _classes.IndexOf(settings.DefaultLoadClass));
            _excel.Left = 12; _excel.Top = 96;
            _labels.Left = 12; _labels.Top = 122;
            _pictures.Left = 12; _pictures.Top = 148;
            _navis.Left = 12; _navis.Top = 174;
            var ok = new Button { Text = "Start", Left = 320, Top = 218, Width = 80, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Abbrechen", Left = 408, Top = 218, Width = 80, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { info, cl, _class, _excel, _labels, _pictures, _navis, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public int LoadClass => _classes.Count == 0 ? 0 : _classes[Math.Max(0, _class.SelectedIndex)];
        public bool MakeExcel => _excel.Checked;
        public bool MakeLabels => _labels.Checked;
        public bool MakePictures => _pictures.Checked;
        public bool MakeNavisworks => _navis.Checked;
    }

    internal enum BatchAction
    {
        Close,
        Show,
        Illustrate,
        IllustrateAll,
        OpenExcel,
    }

    /// <summary>List of all beams with a search box: pick one to find it in the model.</summary>
    internal sealed class BatchResultForm : Form
    {
        private readonly List<BeamRow> _rows;
        private readonly DataGridView _grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = SystemColors.Window,
        };
        private readonly TextBox _search = new TextBox { Width = 220 };

        public BatchResultForm(List<BeamRow> rows, bool fullActions, string excelPath)
        {
            _rows = rows;
            Text = "BeamCheck — Träger (" + rows.Count + ")";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(980, 560);
            MinimumSize = new Size(760, 380);
            ShowInTaskbar = false;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4) };
            top.Controls.Add(new Label { Text = "Suche (Name, Artikel):", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
            top.Controls.Add(_search);
            top.Controls.Add(new Label { Text = "Doppelklick oder „Im Modell zeigen“ springt zum Träger.", AutoSize = true, Margin = new Padding(12, 6, 3, 3) });

            foreach (var h in new[] { "Träger", "Handle", "Artikel", "Bezeichnung", "Länge, mm", "Auflager", "Lasten", "max. Last Fk, kN", "max. Auflagerkraft Fk, kN", "Hinweise" })
                _grid.Columns.Add(h, h);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var close = Btn("Schließen", BatchAction.Close);
            buttons.Controls.Add(close);
            if (fullActions)
            {
                var open = Btn("Excel öffnen", BatchAction.OpenExcel);
                open.Enabled = !string.IsNullOrEmpty(excelPath);
                buttons.Controls.Add(open);
                buttons.Controls.Add(Btn("Alle Abbildungen einfügen…", BatchAction.IllustrateAll));
                buttons.Controls.Add(Btn("Abbildung einfügen…", BatchAction.Illustrate));
            }

            buttons.Controls.Add(Btn("Im Modell zeigen", BatchAction.Show));
            Controls.Add(_grid);
            Controls.Add(top);
            Controls.Add(buttons);
            CancelButton = close;

            _search.TextChanged += (s, e) => Fill();
            _grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0)
                {
                    Action = BatchAction.Show;
                    DialogResult = DialogResult.OK;
                }
            };
            Fill();
        }

        public BatchAction Action { get; private set; }

        /// <summary>Index into the row list of the highlighted line (-1 = none).</summary>
        public int SelectedIndex
        {
            get
            {
                if (_grid.CurrentRow == null)
                    return -1;
                return _grid.CurrentRow.Tag is int i ? i : -1;
            }
        }

        public string SearchText
        {
            get => _search.Text;
            set => _search.Text = value ?? "";
        }

        private Button Btn(string text, BatchAction action)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (s, e) =>
            {
                Action = action;
                DialogResult = DialogResult.OK;
            };
            return b;
        }

        private void Fill()
        {
            string q = (_search.Text ?? "").Trim();
            _grid.Rows.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (q.Length > 0 && (r.Name + " " + r.Article + " " + r.Description + " " + r.Handle).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                int idx = _grid.Rows.Add(r.Name, r.Handle, r.Article, r.Description, r.Length.ToString("0"), r.Supports, r.Loads,
                    r.MaxLoad.ToString("0.00"), r.MaxReaction.ToString("0.00"), r.Notes);
                _grid.Rows[idx].Tag = i;
            }
        }
    }
}
