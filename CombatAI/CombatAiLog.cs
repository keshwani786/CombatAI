using System;
using System.IO;
using System.Text;
using System.Threading;
using TaleWorlds.ModuleManager;

namespace CombatAI
{
    // Dedicated logger for the combat AI override mod.
    //
    // Primary location (user-requested, shared with crbc_bridge.log):
    //   Documents/Mount and Blade II Bannerlord/Configs/ModLogs/combat_ai.log
    // -- resolves into OneDrive\Documents on machines with Known Folder Move.
    // Fallbacks: the module's ModuleData folder, then
    // Documents/Mount and Blade II Bannerlord/Logs/ (same fallbacks CrbcLog
    // uses -- duplicated here deliberately, no project reference to BCCrBridge).
    public static class CombatAiLog
    {
        private static readonly object LockObj = new object();
        private static string _workingPath;
        // Session 59 hard cap: a 243 MB session file proved per-line appends
        // can run away. Track bytes written; at 40 MB rotate to .old once.
        private const long MaxLogBytes = 40L * 1024L * 1024L;
        private static long _bytesWritten = -1L;   // -1 = not initialized yet

        public static string LogFilePath => _workingPath ?? ConfigLogPath;

        public static string ConfigLogPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Configs", "ModLogs");
                return Path.Combine(dir, "combat_ai.log");
            }
        }

        // Legacy location inside the module folder -- now a write fallback.
        public static string PrimaryLogPath => Path.Combine(ModModuleData, "combat_ai.log");

        public static string DocumentsLogPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord",
                    "Logs");
                return Path.Combine(dir, "combat_ai.log");
            }
        }

        private static string ModFolder
        {
            get
            {
                try
                {
                    // CombatAI.dll lives inside the BCCrBridge module folder.
                    var info = ModuleHelper.GetModuleInfo("BCCrBridge");
                    if (info != null && !string.IsNullOrWhiteSpace(info.FolderPath))
                    {
                        string folder = ToAbsolute(info.FolderPath);
                        if (Directory.Exists(folder))
                            return folder;
                    }
                }
                catch { }

                try
                {
                    string dllDir = Path.GetDirectoryName(typeof(CombatAiLog).Assembly.Location);
                    if (!string.IsNullOrWhiteSpace(dllDir))
                    {
                        string folder = Path.GetFullPath(Path.Combine(dllDir, "..", "..", ".."));
                        if (Directory.Exists(folder))
                            return folder;
                    }
                }
                catch { }

                return null;
            }
        }

        private static string ModModuleData => Path.Combine(ModFolder ?? ".", "ModuleData");

        private static string ToAbsolute(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.GetFullPath(Path.Combine(baseDir, path));
            }
            catch
            {
                return Path.GetFullPath(path);
            }
        }

        public static void Log(string message)
        {
            try
            {
                lock (LockObj)
                {
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [thread {Thread.CurrentThread.ManagedThreadId}] {message}";

                    string path = _workingPath ?? ConfigLogPath;

                    if (!TryAppend(path, line))
                    {
                        if (TryAppend(PrimaryLogPath, line))
                            _workingPath = PrimaryLogPath;
                        else
                        {
                            string fallback = DocumentsLogPath;
                            if (TryAppend(fallback, line))
                                _workingPath = fallback;
                        }
                    }
                    else
                    {
                        _workingPath = path;
                    }

                    // Rotation check AFTER a successful write: past 40 MB the
                    // file moves to .old (once) and we start fresh.
                    if (_workingPath != null)
                    {
                        if (_bytesWritten < 0L)
                        {
                            try { _bytesWritten = File.Exists(_workingPath) ? new FileInfo(_workingPath).Length : 0L; }
                            catch { _bytesWritten = 0L; }
                        }
                        _bytesWritten += line.Length + 2;
                        if (_bytesWritten > MaxLogBytes)
                        {
                            try
                            {
                                string oldPath = _workingPath + ".old";
                                if (File.Exists(oldPath))
                                    File.Delete(oldPath);
                                File.Move(_workingPath, oldPath);
                                File.WriteAllText(_workingPath,
                                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === rotated at 40 MB ===" + Environment.NewLine);
                            }
                            catch { }
                            _bytesWritten = 0L;
                        }
                    }
                }
            }
            catch
            {
                // Logging must never crash the game.
            }
        }

        // Truncates the log file to zero bytes (then writes a single header line so
        // the file is never truly empty/confusing). Used by `combatai.clearlog` so each
        // build/test run starts from a clean log. Also resets _workingPath so the
        // primary location is retried even if an earlier failure pushed us to the
        // Documents fallback.
        public static void Clear()
        {
            try
            {
                lock (LockObj)
                {
                    _workingPath = null;
                    _bytesWritten = -1L;
                    string path = ConfigLogPath;

                    string dir = Path.GetDirectoryName(path);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === combat_ai.log cleared ===" + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never crash the game.
            }
        }

        private static bool TryAppend(string path, string line)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    sw.WriteLine(line);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
