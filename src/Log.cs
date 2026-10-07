using System;
using System.Diagnostics;
using System.IO;

namespace X2Coop
{
    /// <summary>
    /// Mod logger. Every line goes to ALL of:
    ///   1. UnityEngine.Debug.Log  -> the game's own log pipeline.
    ///   2. The game's ORIGINAL Unity log handler (when detectable) -> the real
    ///      player log / -logFile at INFO level.
    ///   3. An append-only file  x2coop-<pid>.log  in the game's Logs dir
    ///      (Common.Constants.Folders.Logs(), i.e. $PROF\Logs). The PID in the
    ///      name guarantees two game instances never share a file.
    ///
    /// Why channel 2 exists: the game installs Xenonauts.Common.Logging.
    /// UnityLogHandler as Debug.unityLogger.logHandler, which routes Debug.Log
    /// into log4net - whose root level is ERROR (Assets/Configuration/
    /// log4net.xml), so INFO lines never reach -logFile or output.log. The
    /// handler keeps the previous (Unity stock) handler as public property
    /// OriginalLogHandler; writing to it is exactly what the game's own
    /// UnityLogAppender does to echo log4net back into the player log, at
    /// LogType.Log - no analytics, no bugreport.
    ///
    /// Design rules (spec r2 C5):
    ///  - NEVER throws. Every path is guarded; failures degrade silently.
    ///  - NEVER log4net and never Debug.LogError/LogWarning - log4net ERROR
    ///    feeds PostHog via AnalyticsLogAppender. Severity is a plain text tag.
    ///  - Never logs IPs or player names.
    /// </summary>
    public static class Log
    {
        private const string Prefix = "[X2Coop]";

        private static readonly object Gate = new object();

        private static StreamWriter _writer;
        private static bool _writerTried;
        private static string _filePath; // resolved path, or null if file logging is off

        /// <summary>Resolved log file path, or null when file logging is unavailable.</summary>
        public static string FilePath
        {
            get
            {
                lock (Gate)
                {
                    EnsureWriter();
                    return _filePath;
                }
            }
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message) => Write("ERROR", message);

        /// <summary>Log an exception with a short context tag. Never rethrows.</summary>
        public static void Exception(string context, Exception ex)
        {
            try
            {
                Write("ERROR", context + ": " + ex);
            }
            catch
            {
                // Last-resort: Write already swallows, this is paranoia.
            }
        }

        private static void Write(string level, string message)
        {
            string line;
            try
            {
                line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + Prefix + " [" + level + "] " + (message ?? string.Empty);
            }
            catch
            {
                line = Prefix + " [" + level + "] " + (message ?? string.Empty);
            }

            try
            {
                UnityEngine.Debug.Log(line);
            }
            catch
            {
                // Even Unity logging can fail very early in boot; fall through.
            }

            // Echo straight into the real player log (-logFile) at INFO level,
            // bypassing the game's log4net ERROR filter (see class doc).
            try
            {
                if (UnityEngine.Debug.unityLogger.logHandler is Xenonauts.Common.Logging.UnityLogHandler handler
                    && handler.OriginalLogHandler != null)
                {
                    handler.OriginalLogHandler.LogFormat(UnityEngine.LogType.Log, null, "{0}", line);
                }
            }
            catch
            {
            }

            lock (Gate)
            {
                try
                {
                    EnsureWriter();
                    _writer?.WriteLine(line);
                }
                catch
                {
                    // File logging is best-effort; never let it break the caller.
                }
            }
        }

        /// <summary>Lazily opens the per-pid log file. Called under Gate.</summary>
        private static void EnsureWriter()
        {
            if (_writer != null || _writerTried)
            {
                return;
            }
            _writerTried = true;
            try
            {
                string dir = ResolveLogDirectory();
                int pid;
                try
                {
                    pid = Process.GetCurrentProcess().Id;
                }
                catch
                {
                    pid = Environment.TickCount; // still keeps the name unique-ish
                }
                string path = Path.Combine(dir, "x2coop-" + pid + ".log");
                // ReadWrite share: another in-process copy of this assembly (or an
                // external tail tool) may open the same file concurrently.
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs) { AutoFlush = true };
                _filePath = path;
            }
            catch (Exception ex)
            {
                _writer = null;
                _filePath = null;
                try
                {
                    UnityEngine.Debug.Log(Prefix + " [WARN] file logging unavailable: " + ex.Message);
                }
                catch
                {
                }
            }
        }

        /// <summary>Game Logs dir via the game's own resolver; falls back gracefully.</summary>
        private static string ResolveLogDirectory()
        {
            try
            {
                // Ensures the directory exists as a side effect (EnsureExists).
                return global::Common.Constants.Folders.Logs();
            }
            catch
            {
                // Fallback: persistentDataPath/Logs, then OS temp.
                try
                {
                    string dir = Path.Combine(UnityEngine.Application.persistentDataPath, "Logs");
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch
                {
                    return Path.GetTempPath();
                }
            }
        }
    }
}
