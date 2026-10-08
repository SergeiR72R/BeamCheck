using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace BeamCheck.Core.Settings
{
    public static class SettingsStore
    {
        public const string FileName = "BeamCheck.settings.json";

        /// <summary>
        /// Loads settings from the first existing file of <paramref name="candidatePaths"/>.
        /// If none exists, writes defaults to the first path and returns them.
        /// </summary>
        public static BeamCheckSettings LoadOrCreate(out string usedPath, params string[] candidatePaths)
        {
            foreach (var path in candidatePaths)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    usedPath = path;
                    return Load(path);
                }
            }

            var settings = new BeamCheckSettings();
            usedPath = null;
            foreach (var path in candidatePaths)
            {
                if (string.IsNullOrEmpty(path))
                    continue;
                try
                {
                    Save(settings, path);
                    usedPath = path;
                    break;
                }
                catch (Exception)
                {
                    // Folder may be read-only (e.g. Program Files); try the next candidate.
                }
            }

            return settings;
        }

        public static BeamCheckSettings Load(string path)
        {
            using (var stream = File.OpenRead(path))
                return Deserialize(stream);
        }

        public static BeamCheckSettings Deserialize(Stream stream)
        {
            var serializer = new DataContractJsonSerializer(typeof(BeamCheckSettings));
            return (BeamCheckSettings)serializer.ReadObject(stream);
        }

        public static void Save(BeamCheckSettings settings, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, Serialize(settings), new UTF8Encoding(false));
        }

        public static string Serialize(BeamCheckSettings settings)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    new DataContractJsonSerializer(typeof(BeamCheckSettings)).WriteObject(writer, settings);
                    writer.Flush();
                }

                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
