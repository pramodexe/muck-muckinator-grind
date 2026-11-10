using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MuckinatorGrind
{
    /// <summary>
    /// A plain-text troubleshooting log: BepInEx/MuckinatorGrind/debug.log.
    /// The previous game's log is kept as debug-previous.log, so it survives restarting after a freeze.
    /// A background thread notices when the game stops updating (a real freeze) and writes that down too,
    /// because the game itself can't log anything while it is stuck.
    /// </summary>
    internal sealed class DiagnosticLog : IDisposable
    {
        private const long MaxBytes = 8L * 1024 * 1024;
        private const int HangMs = 5000;

        public readonly string Folder;
        public readonly string CurrentPath;
        public readonly string PreviousPath;

        public bool Enabled = true;

        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private StreamWriter writer;
        private Thread watchdog;
        private volatile bool stopping;
        private long lastTickMs;
        private volatile string lastState = "starting";
        private volatile bool watching;
        private bool reportedHang;

        public DiagnosticLog(string folder)
        {
            Folder = folder;
            CurrentPath = Path.Combine(folder, "debug.log");
            PreviousPath = Path.Combine(folder, "debug-previous.log");
        }

        public void Open()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(CurrentPath))
                {
                    if (File.Exists(PreviousPath))
                        File.Delete(PreviousPath);
                    File.Move(CurrentPath, PreviousPath);
                }
                OpenWriter(FileMode.Create);
            }
            catch (Exception ex)
            {
                writer = null;
                UnityEngine.Debug.LogWarning("[MuckinatorGrind] Could not open debug.log: " + ex.Message);
            }

            lastTickMs = clock.ElapsedMilliseconds;
            watchdog = new Thread(WatchLoop) { IsBackground = true, Name = "MuckinatorGrind watchdog" };
            watchdog.Start();
        }

        private void OpenWriter(FileMode mode)
        {
            FileStream fs = new FileStream(CurrentPath, mode, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public long SizeBytes
        {
            get
            {
                lock (gate)
                {
                    try { return writer != null ? writer.BaseStream.Length : 0; }
                    catch { return 0; }
                }
            }
        }

        public void Write(string category, string message)
        {
            if (!Enabled)
                return;
            WriteAlways(category, message);
        }

        /// <summary>Writes even when the log is switched off (used for the on/off line itself).</summary>
        public void WriteAlways(string category, string message)
        {
            lock (gate)
            {
                if (writer == null)
                    return;
                try
                {
                    if (writer.BaseStream.Length > MaxBytes)
                    {
                        // Keep the file small enough to upload: start over, noting why.
                        writer.Dispose();
                        OpenWriter(FileMode.Create);
                        writer.WriteLine(Stamp() + " [log] debug.log reached " + (MaxBytes / 1048576) + " MB and was restarted.");
                    }
                    writer.WriteLine(Stamp() + " [" + category + "] " + message);
                }
                catch
                {
                    // Never let logging break the game.
                }
            }
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        }

        /// <summary>Call every frame from Update. <paramref name="state"/> is written if the game later freezes.</summary>
        public void Tick(string state)
        {
            Interlocked.Exchange(ref lastTickMs, clock.ElapsedMilliseconds);
            if (state != null)
                lastState = state;
        }

        /// <summary>Only watch for freezes while in a world; menus and loading screens are allowed to be slow.</summary>
        public void SetWatching(bool on)
        {
            if (on && !watching)
                Interlocked.Exchange(ref lastTickMs, clock.ElapsedMilliseconds);
            watching = on;
        }

        private void WatchLoop()
        {
            while (!stopping)
            {
                Thread.Sleep(1000);
                long gap = clock.ElapsedMilliseconds - Interlocked.Read(ref lastTickMs);

                if (watching && gap >= HangMs && !reportedHang)
                {
                    reportedHang = true;
                    Write("FREEZE", "The game has not updated for " + (gap / 1000) + " s. Last known state: " + lastState);
                }
                else if (reportedHang && gap < HangMs)
                {
                    reportedHang = false;
                    Write("FREEZE", "The game is updating again.");
                }
            }
        }

        public void Dispose()
        {
            stopping = true;
            lock (gate)
            {
                try { if (writer != null) writer.Dispose(); }
                catch { }
                writer = null;
            }
        }
    }
}
