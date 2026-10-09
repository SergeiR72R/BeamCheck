using System;
using System.IO;
using BeamCheck.Core.Settings;
#if BRICSCAD
using Teigha.Runtime;
using CadApp = Bricscad.ApplicationServices.Application;
#else
using Autodesk.AutoCAD.Runtime;
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

[assembly: ExtensionApplication(typeof(BeamCheck.Cad.Plugin))]
[assembly: CommandClass(typeof(BeamCheck.Cad.Commands))]

namespace BeamCheck.Cad
{
    public sealed class Plugin : IExtensionApplication
    {
        public const string LoadedMessage =
            "\nBeamCheck загружен. Команды: BEAMLOAD — нагрузки от стоек на балку, PERIDUMP — данные объектов, BEAMLOADSETTINGS — настройки.\n";

        public void Initialize()
        {
            Log("loaded " + typeof(Plugin).Assembly.Location + " into " + Environment.Version + " in " + System.Diagnostics.Process.GetCurrentProcess().ProcessName);

            // At startup (autoload) there is no drawing yet: announce on the first idle moment instead.
            CadApp.Idle += AnnounceOnce;
        }

        private static void AnnounceOnce(object sender, EventArgs e)
        {
            var doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;
            CadApp.Idle -= AnnounceOnce;
            try
            {
                doc.Editor.WriteMessage(LoadedMessage);
            }
            catch (System.Exception)
            {
            }
        }

        /// <summary>Appends a line to %APPDATA%\BeamCheck\BeamCheck.log (load diagnostics).</summary>
        public static void Log(string line)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeamCheck");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "BeamCheck.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch (System.Exception)
            {
                // Logging must never break loading.
            }
        }

        public void Terminate()
        {
        }

        /// <summary>User settings file (%APPDATA%\BeamCheck) takes precedence over the one shipped next to the DLL.</summary>
        public static string UserSettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeamCheck", SettingsStore.FileName);

        public static string BundledSettingsPath =>
            Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", SettingsStore.FileName);

        public static string UserWeightsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeamCheck", CatalogCsv.FileName);

        public static string BundledWeightsPath =>
            Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", CatalogCsv.FileName);

        public static BeamCheckSettings LoadSettings(out string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath));
            CopyIfMissing(BundledSettingsPath, UserSettingsPath);
            CopyIfMissing(BundledWeightsPath, UserWeightsPath);

            var settings = SettingsStore.LoadOrCreate(out path, UserSettingsPath, BundledSettingsPath);
            // The editable weight table in %APPDATA% wins over the shipped one.
            CatalogCsv.Merge(settings, BundledWeightsPath);
            CatalogCsv.Merge(settings, UserWeightsPath);
            return settings;
        }

        private static void CopyIfMissing(string from, string to)
        {
            try
            {
                if (!File.Exists(to) && File.Exists(from))
                    File.Copy(from, to);
            }
            catch (System.Exception ex)
            {
                Log("copy " + from + " → " + to + " failed: " + ex.Message);
            }
        }
    }
}
