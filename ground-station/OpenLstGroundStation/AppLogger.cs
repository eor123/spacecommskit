// AppLogger.cs
// Writes timestamped log entries to daily files in <AppFolder>/Log/
// Thread-safe. UI log callback is separate (MainForm wires that up).

using System;
using System.IO;

namespace OpenLstGroundStation
{
    public static class AppLogger
    {
        private static string _logFolder = "";
        private static readonly object _fileLock = new object();

        public static void Initialize()
        {
            _logFolder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Log");
            Directory.CreateDirectory(_logFolder);
        }

        public static void Write(string message)
        {
            if (string.IsNullOrEmpty(_logFolder)) return;
            string fileName = $"openlst_{DateTime.Now:yyyy-MM-dd}.log";
            string path     = Path.Combine(_logFolder, fileName);
            string entry    = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
            lock (_fileLock)
            {
                try { File.AppendAllText(path, entry + Environment.NewLine); }
                catch { /* never crash the app over a log write */ }
            }
        }

        public static string LogFolder => _logFolder;
    }
}
