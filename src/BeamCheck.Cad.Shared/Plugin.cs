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
        public void Initialize()
        {
            try
            {
                CadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                    "\nBeamCheck загружен. Команды: BEAMLOAD — нагрузки от стоек на балку, PERIDUMP — данные объектов, BEAMLOADSETTINGS — настройки.\n");
            }
            catch (System.Exception)
            {
                // No document at startup is fine.
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

        public static BeamCheckSettings LoadSettings(out string path)
        {
            if (!File.Exists(UserSettingsPath) && File.Exists(BundledSettingsPath))
            {
                // First run: copy shipped settings to a writable place the user can edit.
                Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath));
                File.Copy(BundledSettingsPath, UserSettingsPath);
            }

            return SettingsStore.LoadOrCreate(out path, UserSettingsPath, BundledSettingsPath);
        }
    }
}
