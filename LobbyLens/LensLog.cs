using System;
using System.IO;
using System.Globalization;

namespace LobbyLens
{
    // Minimal daily-file logger. Logging must never throw or block gameplay paths.
    public static class LensLog
    {
        private static readonly object Gate = new object();
        private static string _folder;
        private static bool _debug;

        public static void Init(string folder)
        {
            _folder = folder;
            try { Directory.CreateDirectory(_folder); }
            catch { _folder = Path.GetTempPath(); }
            Prune();
        }

        public static void SetDebug(bool enabled)
        {
            _debug = enabled;
        }

        public static void Debug(string msg) { if (_debug) { Write("DBG", msg); } }
        public static void Info(string msg) { Write("INF", msg); }
        public static void Warn(string msg) { Write("WRN", msg); }

        public static void Error(string msg, Exception ex = null)
        {
            Write("ERR", ex == null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        private static void Write(string level, string msg)
        {
            if (_folder == null) { return; }
            try
            {
                lock (Gate)
                {
                    string path = Path.Combine(_folder, $"LobbyLens-{DateTime.Today:yyyy-MM-dd}.log");
                    string time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    File.AppendAllText(path, $"{time} {level} {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }

        private static void Prune()
        {
            try
            {
                foreach (string f in Directory.GetFiles(_folder, "LobbyLens-*.log"))
                {
                    string datePart = Path.GetFileNameWithoutExtension(f).Substring("LobbyLens-".Length);
                    if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)
                        && d < DateTime.Today.AddDays(-5))
                    {
                        File.Delete(f);
                    }
                }
            }
            catch { }
        }
    }
}
