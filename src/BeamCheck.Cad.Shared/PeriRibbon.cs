#if !BRICSCAD
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using Autodesk.AutoCAD.ApplicationServices;

namespace BeamCheck.Cad
{
    /// <summary>One button of a ribbon panel: text, the command it starts and a two-letter symbol for the icon.</summary>
    public sealed class RibbonButtonSpec
    {
        public string Text { get; set; }
        public string Command { get; set; }
        public string Tooltip { get; set; }
        public string Glyph { get; set; }
        public Color Color { get; set; } = Color.FromArgb(0x1F, 0x4E, 0x79);
    }

    /// <summary>
    /// The shared ribbon tab "Berechnungssoftware" of the calculation modules. Every module calls <see cref="AddPanel"/> with its own panel:
    /// the tab is created by whichever module loads first, the others add their panel to it, so the order of loading does not matter.
    /// This file is meant to be copied unchanged into every module (the tab id must stay the same).
    /// </summary>
    public static class PeriRibbon
    {
        public const string TabId = "PROMAINTAIN_BERECHNUNGSSOFTWARE";
        public const string TabTitle = "Berechnungssoftware";

        private sealed class PanelSpec
        {
            public string Id;
            public string Title;
            public IList<RibbonButtonSpec> Buttons;
        }

        private static readonly List<PanelSpec> Panels = new List<PanelSpec>();
        private static bool _hooked;
        private static bool _building;

        public static void AddPanel(string panelId, string panelTitle, IList<RibbonButtonSpec> buttons)
        {
            Panels.Add(new PanelSpec { Id = panelId, Title = panelTitle, Buttons = buttons });
            Build();
            if (_hooked)
                return;
            _hooked = true;
            // PERI CAD builds its ribbon after the plugins are loaded (and again when the workspace changes): add the tab whenever it is missing.
            ComponentManager.ItemInitialized += (s, e) => Build();
            Application.Idle += (s, e) => Build();
        }

        private static void Build()
        {
            if (_building)
                return;
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
                return;
            _building = true;
            try
            {
                var tab = ribbon.Tabs.FirstOrDefault(t => t.Id == TabId);
                if (tab == null)
                {
                    tab = new RibbonTab { Title = TabTitle, Id = TabId };
                    ribbon.Tabs.Add(tab);
                }

                foreach (var p in Panels)
                {
                    if (tab.Panels.Any(x => x.Source != null && x.Source.Id == p.Id))
                        continue;
                    var source = new RibbonPanelSource { Title = p.Title, Id = p.Id };
                    foreach (var b in p.Buttons)
                    {
                        source.Items.Add(new RibbonButton
                        {
                            Id = p.Id + "_" + b.Command,
                            Text = b.Text,
                            ShowText = true,
                            ShowImage = true,
                            Size = RibbonItemSize.Large,
                            Orientation = System.Windows.Controls.Orientation.Vertical,
                            LargeImage = Icon(b, 32),
                            Image = Icon(b, 16),
                            ToolTip = b.Tooltip ?? b.Text,
                            CommandHandler = new StartCommand(b.Command),
                        });
                    }

                    tab.Panels.Add(new RibbonPanel { Source = source });
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log("ribbon failed: " + ex.Message);
            }
            finally
            {
                _building = false;
            }
        }

        private sealed class StartCommand : ICommand
        {
            private readonly string _command;
            public StartCommand(string command) { _command = command; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;

            public void Execute(object parameter)
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                    doc.SendStringToExecute(_command + " ", true, false, false);
            }
        }

        /// <summary>A rounded square in the button's colour with its two letters, drawn at run time (no resource files).</summary>
        private static BitmapImage Icon(RibbonButtonSpec b, int size)
        {
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    int r = Math.Max(3, size / 5);
                    using (var path = new GraphicsPath())
                    {
                        var rect = new Rectangle(1, 1, size - 3, size - 3);
                        path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
                        path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
                        path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                        path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                        path.CloseFigure();
                        using (var brush = new SolidBrush(b.Color))
                            g.FillPath(brush, path);
                    }

                    using (var font = new Font("Segoe UI", size * 0.38f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(b.Glyph ?? "", font, Brushes.White, new RectangleF(0, 0, size, size), fmt);
                }

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
        }

        /// <summary>The panel of BeamCheck.</summary>
        internal static void RegisterBeamCheck()
        {
            AddPanel("BEAMCHECK", "BeamCheck – Träger", new List<RibbonButtonSpec>
            {
                new RibbonButtonSpec { Text = "Last auf Träger", Command = "BEAMLOAD", Glyph = "BL", Tooltip = "BEAMLOAD: Lasten aus Ständern auf einen Träger oder Riegel ermitteln" },
                new RibbonButtonSpec { Text = "Alle Träger", Command = "BEAMBATCH", Glyph = "BB", Color = Color.FromArgb(0x2E, 0x7D, 0x32), Tooltip = "BEAMBATCH: alle Träger automatisch berechnen (Excel, Bilder)" },
                new RibbonButtonSpec { Text = "Träger finden", Command = "BEAMFIND", Glyph = "BF", Color = Color.FromArgb(0x6A, 0x1B, 0x9A), Tooltip = "BEAMFIND: Träger aus der Liste im Modell zeigen" },
                new RibbonButtonSpec { Text = "Teile kopieren", Command = "BEAMEXPORT", Glyph = "EX", Color = Color.FromArgb(0xB8, 0x54, 0x10), Tooltip = "BEAMEXPORT: berücksichtigte Teile in eine neue DWG kopieren" },
                new RibbonButtonSpec { Text = "Einstellungen", Command = "BEAMLOADSETTINGS", Glyph = "ES", Color = Color.FromArgb(0x55, 0x55, 0x55), Tooltip = "BEAMLOADSETTINGS: Einstellungen und Gewichte" },
            });
        }

        /// <summary>The panel of SPINDELLOAD (spindle loads with the BeamCheck engine).</summary>
        internal static void RegisterSpindleLoad()
        {
            AddPanel("BEAMCHECK_SPINDLE", "BeamCheck – Spindeln", new List<RibbonButtonSpec>
            {
                new RibbonButtonSpec { Text = "Spindellasten", Command = "SPINDELLOAD", Glyph = "SL", Color = Color.FromArgb(0xC6, 0x28, 0x28), Tooltip = "SPINDELLOAD: maximale Last in jeder Spindel (Fuß-/Kopfspindel, UJB) mit Einzelprüfung" },
                new RibbonButtonSpec { Text = "Spindel-Daten", Command = "SPINDELDUMP", Glyph = "SD", Color = Color.FromArgb(0x00, 0x69, 0x7C), Tooltip = "SPINDELDUMP: was das Programm in einem Teil sieht (wird es als Spindel erkannt?)" },
                new RibbonButtonSpec { Text = "Spindel-Einst.", Command = "SPINDELSETTINGS", Glyph = "SE", Color = Color.FromArgb(0x55, 0x55, 0x55), Tooltip = "SPINDELSETTINGS: Einstellungen von SPINDELLOAD" },
            });
        }
    }
}
#endif
