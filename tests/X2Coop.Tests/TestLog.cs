using System;
using System.Collections.Generic;

namespace X2Coop
{
    /// <summary>
    /// Test double for the mod's logger (src/Log.cs writes to UnityEngine.Debug
    /// + a file, which cannot run in a test host). The codec only calls
    /// Info/Warn/Error/Exception - the stub captures every line so tests can
    /// assert drop logging ("codec drop: ...", "codec ok n/n").
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Captured = new List<string>();

        /// <summary>All captured lines, newest last (snapshot).</summary>
        public static string[] Lines
        {
            get { lock (Gate) { return Captured.ToArray(); } }
        }

        public static void Clear() { lock (Gate) { Captured.Clear(); } }

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message) => Write("ERROR", message);

        public static void Exception(string context, Exception ex) =>
            Write("ERROR", context + ": " + ex);

        private static void Write(string level, string message)
        {
            string line = "[X2Coop] [" + level + "] " + (message ?? string.Empty);
            lock (Gate) { Captured.Add(line); }
            Console.WriteLine(line); // visible in `dotnet test` detailed output
        }
    }
}
