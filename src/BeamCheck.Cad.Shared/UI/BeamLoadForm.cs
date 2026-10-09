using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Report;
using BeamCheck.Core.Settings;

namespace BeamCheck.Cad.UI
{
    internal enum FormAction
    {
        Cancel,
        Insert,
        Highlight,
        Exclude,
        Add,
    }

    /// <summary>
    /// Load parameters and live results. Built in code (no designer/XAML) so the same file
    /// compiles for AutoCAD (.NET Framework 4.8) and BricsCAD (.NET Framework 4.8 / .NET 8).
    /// </summary>
    internal sealed class BeamLoadForm : Form
    {
        private readonly BeamLoadSession _session;
        private readonly NumberFormat _nf;
        private bool _updating;

        private ComboBox _loadClass;
        private ComboBox _levelMode;
        private NumericUpDown _gammaG;
        private NumericUpDown _gammaQ;
        private CheckBox _showDesign;
        private CheckBox _flip;
        private DataGridView _levels;
        private DataGridView _results;
        private TextBox _warnings;
        private Label _info;
        private GroupBox _resultsBox;
        private CheckBox _snap;
        private NumericUpDown _snapMm;

        public BeamLoadForm(BeamLoadSession session)
        {
            _session = session;
            _nf = new NumberFormat(session.Settings.DecimalSeparator);
            BuildUi();
            LoadOptions();
            RefreshAll();
        }

        public FormAction Action { get; private set; } = FormAction.Cancel;

        private void BuildUi()
        {
            Text = "BeamCheck — нагрузки от стоек на балку";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            Size = new Size(900, 680);
            MinimumSize = new Size(760, 560);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            _info = new Label { AutoSize = true, Margin = new Padding(3, 3, 3, 8) };
            root.Controls.Add(_info);

            var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            _loadClass = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
            foreach (var c in _session.Settings.LoadClasses)
                _loadClass.Items.Add(new ClassItem(c, _nf));
            _levelMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            _levelMode.Items.AddRange(new object[] { "Live load на одном ярусе — самом нагруженном", "Live load на всех ярусах 100%", "Один ярус 100% + один соседний 50% (EN 12811-1)" });
            _gammaG = Num(1.35m);
            _gammaQ = Num(1.5m);
            _showDesign = new CheckBox { Text = "На схеме — расчётные Fd", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
            _snap = new CheckBox { Text = "Деки не на леджере — привязать к ближайшему, мм:", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
            _snapMm = new NumericUpDown { Minimum = 50, Maximum = 2000, Increment = 50, Value = 300, Width = 70 };
            _flip = new CheckBox { Text = "Отсчёт от другого конца балки", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
            options.Controls.AddRange(new Control[]
            {
                Caption("Класс нагрузки:"), _loadClass,
                Caption("Ярусы:"), _levelMode,
                Caption("γG:"), _gammaG, Caption("γQ:"), _gammaQ,
                _showDesign, _flip, _snap, _snapMm,
            });
            root.Controls.Add(options);

            _levels = Grid();
            _levels.Columns.Add("level", "Ярус");
            _levels.Columns.Add("z", "Отметка, мм");
            _levels.Columns.Add("area", "Площадь деков на стойки, м²");
            _levels.Columns.Add("factor", "Полезная нагрузка, % q");
            foreach (DataGridViewColumn c in _levels.Columns)
                c.ReadOnly = c.Name != "factor";
            _levels.CellEndEdit += OnLevelFactorEdited;
            root.Controls.Add(Group("Ярусы (коэффициент можно редактировать)", _levels));

            _results = Grid();
            foreach (var h in new[] { "Стойка", "X, мм", "Площадь, м²", "Live load, кН", "Self weight, кН", "Total load, кН", "Fd, кН", "С верх. балок, кН", "Высота до Z, мм", "Секций", "По ярусам" })
                _results.Columns.Add(h, h);
            _results.Columns[10].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            foreach (DataGridViewColumn c in _results.Columns)
                c.ReadOnly = true;
            _resultsBox = Group("Нагрузки на балку", _results);
            root.Controls.Add(_resultsBox);

            _warnings = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, ForeColor = Color.DarkRed };
            root.Controls.Add(_warnings);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var insert = MakeButton("Вставить в чертёж", FormAction.Insert);
            var cancel = MakeButton("Отмена", FormAction.Cancel);
            var export = new Button { Text = "Экспорт CSV…", AutoSize = true };
            export.Click += (s, e) => ExportCsv();
            buttons.Controls.AddRange(new Control[]
            {
                cancel, insert, export,
                MakeButton("Добавить объекты…", FormAction.Add),
                MakeButton("Исключить объекты…", FormAction.Exclude),
                MakeButton("Показать в модели", FormAction.Highlight),
            });
            root.Controls.Add(buttons);
            AcceptButton = insert;
            CancelButton = cancel;

            _loadClass.SelectedIndexChanged += (s, e) => OnOptionChanged();
            _gammaG.ValueChanged += (s, e) => OnOptionChanged();
            _gammaQ.ValueChanged += (s, e) => OnOptionChanged();
            _showDesign.CheckedChanged += (s, e) => OnOptionChanged();
            _flip.CheckedChanged += (s, e) => OnOptionChanged();
            EventHandler snapChanged = (s, e) =>
            {
                if (_updating)
                    return;
                _session.Settings.DeckSnapEnabled = _snap.Checked;
                _session.Settings.DeckSnapDistance = (double)_snapMm.Value;
                _session.Settings.DeckSnapHeight = Math.Max(_session.Settings.DeckSnapHeight, (double)_snapMm.Value);
                _session.Reanalyze();
                _session.StatusMessage = _snap.Checked
                    ? "Деки, лежащие не на леджере, привязываются к ближайшему (до " + _snapMm.Value + " мм) — расчёт обновлён."
                    : "Привязка к ближайшему леджеру выключена: строгий допуск — расчёт обновлён.";
                RefreshAll();
            };
            _snap.CheckedChanged += snapChanged;
            _snapMm.ValueChanged += snapChanged;
            _levelMode.SelectedIndexChanged += (s, e) =>
            {
                if (_updating)
                    return;
                _session.Settings.LevelMode = _levelMode.SelectedIndex == 1 ? LevelModes.AllLevels : _levelMode.SelectedIndex == 2 ? LevelModes.WorstLevelPlusAdjacent : LevelModes.SingleWorstLevel;
                _session.ResetLevelFactors();
                RefreshAll();
            };
        }

        private void LoadOptions()
        {
            _updating = true;
            var o = _session.Options;
            _loadClass.SelectedIndex = Math.Max(0, _session.Settings.LoadClasses.FindIndex(c => c.Class == o.LoadClass));
            _levelMode.SelectedIndex = string.Equals(_session.Settings.LevelMode, LevelModes.AllLevels, StringComparison.OrdinalIgnoreCase) ? 1
                : string.Equals(_session.Settings.LevelMode, LevelModes.WorstLevelPlusAdjacent, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            _gammaG.Value = (decimal)o.GammaG;
            _gammaQ.Value = (decimal)o.GammaQ;
            _showDesign.Checked = o.ShowDesignValues;
            _flip.Checked = o.FlipBeam;
            _snap.Checked = _session.Settings.DeckSnapEnabled;
            _snapMm.Value = (decimal)Math.Max(50, Math.Min(2000, _session.Settings.DeckSnapDistance));
            _updating = false;
        }

        private void OnOptionChanged()
        {
            if (_updating)
                return;
            var o = _session.Options;
            if (_loadClass.SelectedItem is ClassItem ci)
                o.LoadClass = ci.Definition.Class;
            o.GammaG = (double)_gammaG.Value;
            o.GammaQ = (double)_gammaQ.Value;
            o.ShowDesignValues = _showDesign.Checked;
            o.FlipBeam = _flip.Checked;
            _session.Recalculate();
            RefreshResults();
        }

        private void OnLevelFactorEdited(object sender, DataGridViewCellEventArgs e)
        {
            if (_updating || e.RowIndex < 0)
                return;
            var row = _levels.Rows[e.RowIndex];
            int index = (int)row.Tag;
            string text = Convert.ToString(row.Cells["factor"].Value);
            if (ElementClassifierNumber(text, out double percent) && percent >= 0)
                _session.SetLevelFactor(index, percent / 100.0);
            BeginInvoke(new MethodInvoker(RefreshAll));
        }

        private static bool ElementClassifierNumber(string text, out double value) =>
            BeamCheck.Core.Recognition.ElementClassifier.TryParseNumber(text, out value);

        private void RefreshAll()
        {
            _updating = true;
            _levels.Rows.Clear();
            foreach (var level in _session.Topology.Levels)
            {
                _session.Options.LevelFactors.TryGetValue(level.Index, out double f);
                int i = _levels.Rows.Add(level.Index, _nf.Mm(level.Z), _nf.Num(_session.LevelArea(level), 2), _nf.Num(f * 100, 0));
                _levels.Rows[i].Tag = level.Index;
            }

            _updating = false;
            RefreshResults();
        }

        private void RefreshResults()
        {
            var r = _session.Result;
            var topo = _session.Topology;
            _info.Text = (string.IsNullOrEmpty(_session.StatusMessage) ? "" : "▶ " + _session.StatusMessage + Environment.NewLine) + string.Format("Балка: {0}   L = {1} мм   стоек: {2}   ярусов: {3}   верхних балок: {4}   учтено объектов: {5}",
                string.IsNullOrEmpty(r.BeamName) ? "—" : r.BeamName, _nf.Mm(r.BeamLength), topo.Columns.Count, topo.Levels.Count, topo.UpperBeams.Count, topo.UsedElementIds.Count);

            _results.Rows.Clear();
            foreach (var p in r.Points)
            {
                string perLevel = string.Join("; ", p.Levels.Select(l =>
                    $"{l.Level.Index}: {_nf.Num(l.AreaM2, 2)} м² × {_nf.Num(l.Factor * 100, 0)}% → Q {_nf.Num(l.Qk, 2)}, G дек {_nf.Num(l.DeckGk, 2)}"));
                _results.Rows.Add(p.Name, _nf.Mm(p.Position), _nf.Num(p.AreaM2, 2), _nf.Num(p.Qk, 2), _nf.Num(p.Gk, 2),
                    _nf.Num(p.Fk, 2), _nf.Num(p.Fd, 2), _nf.Num(p.TransferFk, 2), _nf.Mm(p.ZTop), p.SectionCount, perLevel);
            }

            _resultsBox.Text = string.Format("Summary:  площадь (все ярусы до верха) {0} м²   |   live load {1} кН   |   self weight load {2} кН   |   total load {3} кН",
                _nf.Num(r.Points.Sum(p => p.AreaM2), 2), _nf.Num(r.Points.Sum(p => p.Qk), 2),
                _nf.Num(r.Points.Sum(p => p.Gk), 2), _nf.Num(r.Points.Sum(p => p.Fk), 2));

            _warnings.Text = r.Warnings.Count == 0 ? "Замечаний нет." : string.Join(Environment.NewLine, r.Warnings.Distinct());
        }

        private void ExportCsv()
        {
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "BeamLoad.csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                var table = ResultTable.Build(_session.Result, _session.Settings.DecimalSeparator);
                File.WriteAllText(dlg.FileName, table.ToCsv(), new UTF8Encoding(true));
            }
        }

        private Button MakeButton(string text, FormAction action)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (s, e) =>
            {
                Action = action;
                DialogResult = action == FormAction.Cancel ? DialogResult.Cancel : DialogResult.OK;
                Close();
            };
            return b;
        }

        private static Label Caption(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(8, 7, 3, 3) };

        private static NumericUpDown Num(decimal value) => new NumericUpDown
        {
            DecimalPlaces = 2, Increment = 0.05m, Minimum = 0, Maximum = 5, Value = value, Width = 60,
        };

        private static DataGridView Grid() => new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            BackgroundColor = SystemColors.Window,
        };

        private static GroupBox Group(string title, Control content)
        {
            var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(6) };
            g.Controls.Add(content);
            return g;
        }

        private sealed class ClassItem
        {
            private readonly NumberFormat _nf;

            public ClassItem(LoadClassDefinition d, NumberFormat nf)
            {
                Definition = d;
                _nf = nf;
            }

            public LoadClassDefinition Definition { get; }

            public override string ToString() => $"Класс {Definition.Class} — {_nf.Num(Definition.ServiceLoadKnPerM2, 2)} кН/м²";
        }
    }
}
