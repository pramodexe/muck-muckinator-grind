using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// Session statistics: data model, rating maths and CSV storage.
// Nothing in here touches the game; the plugin feeds it numbers (see Plugin.Stats.cs).
namespace MuckinatorGrind
{
    /// <summary>The settings a session ran with. Sessions with the same Key are grouped for comparison.</summary>
    internal sealed class SessionConfig
    {
        public string Mob = "";
        public string Preset = "";   // empty = custom values
        public int Batch;
        public int MaxAlive;
        public float Interval;
        public float Radius;
        public bool GodSword;
        public bool AutoAim;
        public int MoveEvery;      // waves per spot, 0 = never moves
        public bool ClearItems;

        public string Key
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3:0.##}|{4:0.##}|{5}|{6}|{7}|{8}",
                    Mob, Batch, MaxAlive, Interval, Radius, GodSword ? 1 : 0, AutoAim ? 1 : 0, MoveEvery, ClearItems ? 1 : 0);
            }
        }

        /// <summary>Short name: "Standard · Goblin" or "Custom · Goblin".</summary>
        public string Name
        {
            get { return (string.IsNullOrEmpty(Preset) ? "Custom" : Preset) + " · " + Mob; }
        }

        /// <summary>Every value: "wave 25 · max 50 · 5s · 12m".</summary>
        public string Details
        {
            get
            {
                string s = string.Format(CultureInfo.InvariantCulture, "wave {0} · max {1} · every {2:0.#}s · radius {3:0}m",
                    Batch, MaxAlive, Interval, Radius);
                if (!GodSword) s += " · no God Sword";
                else if (!AutoAim) s += " · manual aim";
                if (MoveEvery > 0) s += " · move every " + MoveEvery + (MoveEvery == 1 ? " wave" : " waves");
                else if (ClearItems) s += " · clear drops";
                return s;
            }
        }
    }

    /// <summary>
    /// One row of a session log, taken every few seconds of active farming time.
    /// Counters marked "cumulative" are totals since the session started; the rest describe only this window.
    /// </summary>
    internal struct Sample
    {
        public float T;            // active seconds since the session started (end of this window)
        public float Dt;           // length of this window in seconds
        public int Kills;          // cumulative
        public int Spawned;        // cumulative
        public int Waves;          // cumulative, waves fully cleared
        public int Alive;          // spawned mobs alive at the end of the window
        public float Kpm;          // kills per minute in this window
        public float Spm;          // spawns per minute in this window
        public float Ttk;          // average spawn-to-death time of mobs killed in this window, -1 if none
        public double TtkSum;      // cumulative
        public int TtkCount;       // cumulative
        public double WaveClearSum;// cumulative seconds from a wave starting to the last mob dying
        public float IdleSec;      // cumulative seconds with no mobs alive or spawning
        public float Fps;          // average frames per second in this window
        public float FpsLow;       // "1% low": FPS of the slowest 1% of frames in this window
        public float FrameMaxMs;   // slowest single frame in this window
        public int Stutters;       // frames slower than 100 ms in this window
        public float MemMb;        // RAM used by the game at the end of the window
    }

    /// <summary>Rating out of 100 plus a letter grade and a plain-language verdict.</summary>
    internal struct Rating
    {
        public const float MinMinutes = 3f;     // shorter sessions get no rating
        public const float FullMinutes = 60f;   // long-run stability counts fully from here
        public const float KpmReference = 250f; // kills/min that scores 63 on Speed; 3x this scores 95

        public bool Valid;
        public float Overall, Speed, Smooth, Stability, StabilityRaw;
        public float Evidence;     // 0-1: how much of a full-length run we have seen
        public string Grade;
        public string Confidence;
        public string Verdict;

        public static Rating Compute(SessionSummary s)
        {
            Rating r = new Rating { Grade = "–", Confidence = "None" };
            float minutes = (float)(s.DurationSec / 60.0);

            if (minutes < MinMinutes)
            {
                r.Verdict = "Farm for at least " + MinMinutes.ToString("0") + " minutes (menu closed) to get a rating.";
                return r;
            }

            r.Valid = true;

            // Speed: kills per minute on a curve that flattens out, so huge numbers don't dominate.
            r.Speed = 100f * (1f - Mathf.Exp(-s.Kpm / KpmReference));

            // Smoothness: 15 FPS or less scores 0, 60 FPS or more scores 100. Frequent big hitches cost extra.
            float avg = Mathf.Clamp01((s.FpsAvg - 15f) / 45f);
            float low = Mathf.Clamp01((s.FpsLow - 15f) / 45f);
            r.Smooth = Mathf.Clamp(100f * (0.6f * avg + 0.4f * low) - Mathf.Min(30f, 3f * s.StuttersPerMin), 0f, 100f);

            // Long-run stability: how much kill rate and FPS dropped between the start and the end.
            // A 10% drop scores 75, a 20% drop 50, a 40% drop 0.
            float ratio = 1f;
            if (s.EarlyKpm > 0f && s.LateKpm >= 0f) ratio = Mathf.Min(ratio, s.LateKpm / s.EarlyKpm);
            if (s.EarlyFps > 0f && s.LateFps >= 0f) ratio = Mathf.Min(ratio, s.LateFps / s.EarlyFps);
            float drop = Mathf.Max(0f, 1f - ratio);
            r.StabilityRaw = 100f * Mathf.Clamp01(1f - drop * 2.5f);

            // A short run can prove a setup is bad, but not that it stays good for an hour.
            // Good stability results are pulled toward 50 ("unknown") until the run is long enough.
            r.Evidence = Mathf.Clamp01(minutes / FullMinutes);
            r.Stability = r.StabilityRaw < 50f ? r.StabilityRaw : 50f + (r.StabilityRaw - 50f) * r.Evidence;

            r.Overall = 0.45f * r.Speed + 0.30f * r.Smooth + 0.25f * r.Stability;
            r.Grade = GradeFor(r.Overall);
            r.Confidence = ConfidenceFor(minutes);
            r.Verdict = VerdictFor(r, s);
            return r;
        }

        public static string GradeFor(float score)
        {
            if (score >= 90f) return "S";
            if (score >= 80f) return "A";
            if (score >= 65f) return "B";
            if (score >= 50f) return "C";
            if (score >= 35f) return "D";
            return "F";
        }

        public static string ConfidenceFor(float minutes)
        {
            if (minutes < MinMinutes) return "None";
            if (minutes < 10f) return "Provisional";
            if (minutes < 30f) return "Low";
            if (minutes < FullMinutes) return "Medium";
            return "High";
        }

        private static string VerdictFor(Rating r, SessionSummary s)
        {
            string v;
            bool longEnough = r.Evidence >= 0.5f;

            if (r.StabilityRaw < 50f && (longEnough || r.StabilityRaw < 30f))
                v = "Gets slower the longer it runs. Lower Max alive, or restart the game every hour.";
            else if (r.Smooth < 40f)
                v = "The game is lagging. Use fewer mobs (lower Max alive and Wave size).";
            else if (s.IdlePct > 35f)
                v = "No mobs were alive " + s.IdlePct.ToString("0") + "% of the time. Shorten Wave interval or raise Wave size.";
            else if (r.Speed < 45f && r.Smooth >= 70f)
                v = "Smooth but slow. Your PC can probably handle a bigger preset.";
            else if (r.Overall >= 80f)
                v = "Excellent setup for this PC.";
            else if (r.Overall >= 65f)
                v = "Good setup for this PC.";
            else if (r.Smooth < 60f)
                v = "OK, but the frame rate is low. Try slightly fewer mobs.";
            else
                v = "OK. Try another preset and compare.";

            if (r.Evidence < 1f)
                v += " Farm 60+ minutes in one go to confirm long-run stability.";
            return v;
        }
    }

    /// <summary>Totals and averages for one session, worked out from its samples.</summary>
    internal sealed class SessionSummary
    {
        public double DurationSec;
        public int Kills, Spawned, Waves;
        public float Kpm, Spm;
        public float AvgTtk = -1f, AvgWaveClear = -1f, IdlePct;
        public float FpsAvg, FpsLow, StuttersPerMin;
        public float MemStart, MemEnd, MemPeak;
        public float EarlyKpm = -1f, LateKpm = -1f, EarlyFps = -1f, LateFps = -1f;
        public Rating Rating;

        public float SecondsPerKill { get { return Kpm > 0f ? 60f / Kpm : -1f; } }

        public float KpmChangePct { get { return EarlyKpm > 0f && LateKpm >= 0f ? (LateKpm / EarlyKpm - 1f) * 100f : 0f; } }
        public float FpsChangePct { get { return EarlyFps > 0f && LateFps >= 0f ? (LateFps / EarlyFps - 1f) * 100f : 0f; } }

        public float MemGrowthPerHour
        {
            get { return DurationSec > 300 ? (MemEnd - MemStart) / (float)(DurationSec / 3600.0) : 0f; }
        }

        public static SessionSummary From(List<Sample> list)
        {
            SessionSummary s = new SessionSummary();
            if (list == null || list.Count == 0)
            {
                s.Rating = Rating.Compute(s);
                return s;
            }

            Sample last = list[list.Count - 1];
            s.DurationSec = last.T;
            s.Kills = last.Kills;
            s.Spawned = last.Spawned;
            s.Waves = last.Waves;

            float minutes = Mathf.Max(last.T / 60f, 0.001f);
            s.Kpm = s.Kills / minutes;
            s.Spm = s.Spawned / minutes;
            s.AvgTtk = last.TtkCount > 0 ? (float)(last.TtkSum / last.TtkCount) : -1f;
            s.AvgWaveClear = last.Waves > 0 ? (float)(last.WaveClearSum / last.Waves) : -1f;
            s.IdlePct = last.T > 0f ? Mathf.Clamp(100f * last.IdleSec / last.T, 0f, 100f) : 0f;

            double fpsSum = 0, lowSum = 0, weight = 0;
            int stutters = 0;
            s.MemPeak = 0f;
            foreach (Sample x in list)
            {
                fpsSum += x.Fps * x.Dt;
                lowSum += x.FpsLow * x.Dt;
                weight += x.Dt;
                stutters += x.Stutters;
                s.MemPeak = Mathf.Max(s.MemPeak, x.MemMb);
            }
            s.FpsAvg = weight > 0 ? (float)(fpsSum / weight) : 0f;
            s.FpsLow = weight > 0 ? (float)(lowSum / weight) : 0f;
            s.StuttersPerMin = stutters / minutes;
            s.MemStart = list[0].MemMb;
            s.MemEnd = last.MemMb;

            // Early vs late part of the run. The first couple of minutes are skipped as warm-up.
            float duration = last.T;
            float warmUp = Mathf.Min(120f, duration * 0.1f);
            float segment = Mathf.Max(60f, (duration - warmUp) * 0.25f);
            Segment(list, warmUp, warmUp + segment, out s.EarlyKpm, out s.EarlyFps);
            Segment(list, duration - segment, duration, out s.LateKpm, out s.LateFps);

            s.Rating = Rating.Compute(s);
            return s;
        }

        // Kill rate and average FPS over samples whose window ends in (from, to].
        private static void Segment(List<Sample> list, float from, float to, out float kpm, out float fps)
        {
            kpm = -1f;
            fps = -1f;

            int first = -1, lastIdx = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].T <= from || list[i].T > to + 0.01f)
                    continue;
                if (first < 0) first = i;
                lastIdx = i;
            }
            if (first < 0)
                return;

            float t0 = first > 0 ? list[first - 1].T : 0f;
            int k0 = first > 0 ? list[first - 1].Kills : 0;
            float span = list[lastIdx].T - t0;
            if (span <= 0f)
                return;

            kpm = (list[lastIdx].Kills - k0) * 60f / span;

            double sum = 0, w = 0;
            for (int i = first; i <= lastIdx; i++)
            {
                sum += list[i].Fps * list[i].Dt;
                w += list[i].Dt;
            }
            fps = w > 0 ? (float)(sum / w) : -1f;
        }
    }

    /// <summary>One recorded session: its settings, PC info and samples.</summary>
    internal sealed class SessionLog
    {
        public string Id;
        public string FilePath;
        public DateTime Started;
        public bool Finished;
        public SessionConfig Config = new SessionConfig();
        public readonly Dictionary<string, string> Meta = new Dictionary<string, string>();
        public readonly List<Sample> Samples = new List<Sample>();
        public SessionSummary Summary = SessionSummary.From(null);

        public void Recompute()
        {
            Summary = SessionSummary.From(Samples);
        }

        public string MetaValue(string key)
        {
            string v;
            return Meta.TryGetValue(key, out v) ? v : "";
        }
    }

    /// <summary>Several sessions that used the same settings, summed up.</summary>
    internal sealed class ConfigGroup
    {
        public SessionConfig Config;
        public readonly List<SessionLog> Sessions = new List<SessionLog>();
        public double TotalSec, LongestSec;
        public int Kills;
        public float Rating, Speed, Smooth, Stability, Kpm, FpsAvg, FpsLow, Ttk = -1f;
        public SessionLog Best;

        public string Grade { get { return MuckinatorGrind.Rating.GradeFor(Rating); } }
        public string Confidence { get { return MuckinatorGrind.Rating.ConfidenceFor((float)(LongestSec / 60.0)); } }

        /// <summary>Groups rated sessions by settings. Averages are weighted by how long each session ran.</summary>
        public static List<ConfigGroup> Build(List<SessionLog> sessions)
        {
            Dictionary<string, ConfigGroup> map = new Dictionary<string, ConfigGroup>();
            List<ConfigGroup> result = new List<ConfigGroup>();

            foreach (SessionLog log in sessions)
            {
                if (!log.Summary.Rating.Valid)
                    continue;

                ConfigGroup g;
                string key = log.Config.Key;
                if (!map.TryGetValue(key, out g))
                {
                    g = new ConfigGroup { Config = log.Config };
                    map[key] = g;
                    result.Add(g);
                }
                g.Sessions.Add(log);
            }

            foreach (ConfigGroup g in result)
            {
                double w = 0, rating = 0, speed = 0, smooth = 0, stab = 0, fps = 0, low = 0, ttk = 0, ttkW = 0, kills = 0;
                foreach (SessionLog log in g.Sessions)
                {
                    SessionSummary s = log.Summary;
                    double d = s.DurationSec;
                    w += d;
                    rating += s.Rating.Overall * d;
                    speed += s.Rating.Speed * d;
                    smooth += s.Rating.Smooth * d;
                    stab += s.Rating.Stability * d;
                    fps += s.FpsAvg * d;
                    low += s.FpsLow * d;
                    kills += s.Kills;
                    if (s.AvgTtk >= 0f) { ttk += s.AvgTtk * d; ttkW += d; }
                    g.LongestSec = Math.Max(g.LongestSec, d);
                    if (g.Best == null || s.Rating.Overall > g.Best.Summary.Rating.Overall)
                        g.Best = log;
                }

                g.TotalSec = w;
                g.Kills = (int)kills;
                g.Rating = (float)(rating / w);
                g.Speed = (float)(speed / w);
                g.Smooth = (float)(smooth / w);
                g.Stability = (float)(stab / w);
                g.FpsAvg = (float)(fps / w);
                g.FpsLow = (float)(low / w);
                g.Kpm = (float)(kills / (w / 60.0));
                g.Ttk = ttkW > 0 ? (float)(ttk / ttkW) : -1f;
            }

            return result;
        }
    }

    /// <summary>
    /// Saves sessions as CSV files that open in Excel / Google Sheets:
    ///   BepInEx/MuckinatorGrind/sessions/session-YYYYMMDD-HHMMSS.csv  (one row every few seconds)
    ///   BepInEx/MuckinatorGrind/sessions.csv                          (one row per session)
    /// Samples are appended as they are taken, so a game crash loses at most a few seconds.
    /// </summary>
    internal sealed class StatsStore
    {
        public const int FormatVersion = 1;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static readonly string[] Columns =
        {
            "elapsed_s", "window_s", "kills", "spawned", "waves_cleared", "alive",
            "kills_per_min", "spawns_per_min", "avg_time_to_kill_s", "ttk_sum_s", "ttk_count",
            "wave_clear_sum_s", "idle_s", "fps_avg", "fps_1pct_low", "frame_max_ms", "stutters", "memory_mb",
        };

        public readonly string Root;
        public readonly string SessionsDir;
        public readonly string IndexPath;
        public readonly List<SessionLog> Sessions = new List<SessionLog>(); // oldest first

        private readonly Action<string> warn;
        private bool writeFailed;

        public StatsStore(string root, Action<string> warn)
        {
            Root = root;
            SessionsDir = Path.Combine(root, "sessions");
            IndexPath = Path.Combine(root, "sessions.csv");
            this.warn = warn ?? (_ => { });
        }

        // ── Loading ──

        public void Load()
        {
            Sessions.Clear();
            try
            {
                if (!Directory.Exists(SessionsDir))
                    return;

                string[] files = Directory.GetFiles(SessionsDir, "session-*.csv");
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    try
                    {
                        SessionLog log = Parse(file);
                        if (log != null)
                            Sessions.Add(log);
                    }
                    catch (Exception ex)
                    {
                        warn("Could not read " + Path.GetFileName(file) + ": " + ex.Message);
                    }
                }
                Sessions.Sort((a, b) => a.Started.CompareTo(b.Started));
            }
            catch (Exception ex)
            {
                warn("Could not load session logs: " + ex.Message);
            }
        }

        private static SessionLog Parse(string path)
        {
            SessionLog log = new SessionLog { FilePath = path };
            int[] col = null;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                if (line[0] == '#')
                {
                    int eq = line.IndexOf('=');
                    if (eq > 1)
                        log.Meta[line.Substring(1, eq - 1).Trim()] = line.Substring(eq + 1).Trim();
                    continue;
                }

                string[] cells = line.Split(',');
                if (col == null)
                {
                    // Header row: map our known columns to their position so older/newer files still load.
                    col = new int[Columns.Length];
                    for (int i = 0; i < Columns.Length; i++)
                        col[i] = Array.IndexOf(cells, Columns[i]);
                    continue;
                }

                log.Samples.Add(new Sample
                {
                    T = F(cells, col[0]),
                    Dt = F(cells, col[1]),
                    Kills = I(cells, col[2]),
                    Spawned = I(cells, col[3]),
                    Waves = I(cells, col[4]),
                    Alive = I(cells, col[5]),
                    Kpm = F(cells, col[6]),
                    Spm = F(cells, col[7]),
                    Ttk = F(cells, col[8], -1f),
                    TtkSum = F(cells, col[9]),
                    TtkCount = I(cells, col[10]),
                    WaveClearSum = F(cells, col[11]),
                    IdleSec = F(cells, col[12]),
                    Fps = F(cells, col[13]),
                    FpsLow = F(cells, col[14]),
                    FrameMaxMs = F(cells, col[15]),
                    Stutters = I(cells, col[16]),
                    MemMb = F(cells, col[17]),
                });
            }

            log.Id = log.MetaValue("id");
            if (string.IsNullOrEmpty(log.Id))
                log.Id = Path.GetFileNameWithoutExtension(path).Replace("session-", "");

            DateTime started;
            log.Started = DateTime.TryParse(log.MetaValue("started"), Inv, DateTimeStyles.RoundtripKind, out started)
                ? started : File.GetCreationTime(path);
            log.Finished = log.Meta.ContainsKey("ended");

            SessionConfig c = log.Config;
            c.Mob = log.MetaValue("mob");
            c.Preset = log.MetaValue("preset");
            int.TryParse(log.MetaValue("batch"), NumberStyles.Integer, Inv, out c.Batch);
            int.TryParse(log.MetaValue("max_alive"), NumberStyles.Integer, Inv, out c.MaxAlive);
            float.TryParse(log.MetaValue("interval"), NumberStyles.Float, Inv, out c.Interval);
            float.TryParse(log.MetaValue("radius"), NumberStyles.Float, Inv, out c.Radius);
            c.GodSword = log.MetaValue("god_sword") == "true";
            c.AutoAim = log.MetaValue("auto_aim") == "true";
            int.TryParse(log.MetaValue("move_every_waves"), NumberStyles.Integer, Inv, out c.MoveEvery);
            c.ClearItems = log.MetaValue("clear_items") == "true";

            log.Recompute();
            return log;
        }

        private static float F(string[] cells, int i, float fallback = 0f)
        {
            float v;
            return i >= 0 && i < cells.Length && float.TryParse(cells[i], NumberStyles.Float, Inv, out v) ? v : fallback;
        }

        private static int I(string[] cells, int i)
        {
            int v;
            return i >= 0 && i < cells.Length && int.TryParse(cells[i], NumberStyles.Integer, Inv, out v) ? v : 0;
        }

        // ── Writing ──

        public SessionLog Create(SessionConfig config, Dictionary<string, string> machine)
        {
            DateTime now = DateTime.Now;
            string id = now.ToString("yyyyMMdd-HHmmss", Inv);
            string path = Path.Combine(SessionsDir, "session-" + id + ".csv");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(SessionsDir, "session-" + id + "-" + n + ".csv");

            SessionLog log = new SessionLog { Id = id, FilePath = path, Started = now, Config = config };
            log.Meta["format"] = FormatVersion.ToString(Inv);
            log.Meta["id"] = id;
            log.Meta["started"] = now.ToString("o", Inv);
            log.Meta["mob"] = config.Mob;
            log.Meta["preset"] = config.Preset;
            log.Meta["batch"] = config.Batch.ToString(Inv);
            log.Meta["max_alive"] = config.MaxAlive.ToString(Inv);
            log.Meta["interval"] = config.Interval.ToString("0.##", Inv);
            log.Meta["radius"] = config.Radius.ToString("0.##", Inv);
            log.Meta["god_sword"] = config.GodSword ? "true" : "false";
            log.Meta["auto_aim"] = config.AutoAim ? "true" : "false";
            log.Meta["move_every_waves"] = config.MoveEvery.ToString(Inv);
            log.Meta["clear_items"] = config.ClearItems ? "true" : "false";
            if (machine != null)
                foreach (KeyValuePair<string, string> kv in machine)
                    log.Meta[kv.Key] = kv.Value;

            StringBuilder sb = new StringBuilder();
            sb.Append("# MuckinatorGrind session log. Times are active farming seconds (menu closed, not paused).\n");
            foreach (KeyValuePair<string, string> kv in log.Meta)
                sb.Append("# ").Append(kv.Key).Append('=').Append(OneLine(kv.Value)).Append('\n');
            sb.Append(string.Join(",", Columns)).Append('\n');

            Write(path, sb.ToString(), false);
            Sessions.Add(log);
            return log;
        }

        public void Append(SessionLog log, Sample s)
        {
            string line = string.Join(",", new[]
            {
                s.T.ToString("0.0", Inv), s.Dt.ToString("0.00", Inv),
                s.Kills.ToString(Inv), s.Spawned.ToString(Inv), s.Waves.ToString(Inv), s.Alive.ToString(Inv),
                s.Kpm.ToString("0.0", Inv), s.Spm.ToString("0.0", Inv), s.Ttk.ToString("0.00", Inv),
                s.TtkSum.ToString("0.0", Inv), s.TtkCount.ToString(Inv), s.WaveClearSum.ToString("0.0", Inv),
                s.IdleSec.ToString("0.0", Inv), s.Fps.ToString("0.0", Inv), s.FpsLow.ToString("0.0", Inv),
                s.FrameMaxMs.ToString("0.0", Inv), s.Stutters.ToString(Inv), s.MemMb.ToString("0", Inv),
            });
            Write(log.FilePath, line + "\n", true);
        }

        /// <summary>Closes a session. Sessions too short to be useful are deleted instead of kept.</summary>
        public void Finish(SessionLog log, bool keep)
        {
            if (!keep)
            {
                Sessions.Remove(log);
                TryDelete(log.FilePath);
                return;
            }

            log.Finished = true;
            log.Meta["ended"] = DateTime.Now.ToString("o", Inv);
            Write(log.FilePath, "# ended=" + log.Meta["ended"] + "\n", true);
            WriteIndex();
        }

        public void Delete(SessionLog log)
        {
            Sessions.Remove(log);
            TryDelete(log.FilePath);
            WriteIndex();
        }

        /// <summary>Rewrites sessions.csv: one row per session, handy for spreadsheets.</summary>
        public void WriteIndex()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("session,started,minutes,preset,mob,wave_size,max_alive,wave_interval_s,spawn_radius_m,god_sword,auto_aim,move_every_waves,clear_items," +
                      "kills,spawned,kills_per_min,spawns_per_min,avg_time_to_kill_s,avg_wave_clear_s,idle_pct," +
                      "fps_avg,fps_1pct_low,stutters_per_min,kills_per_min_change_pct,fps_change_pct,memory_start_mb,memory_end_mb," +
                      "speed_score,smooth_score,stability_score,rating,grade,confidence,file\n");

            foreach (SessionLog log in Sessions)
            {
                SessionSummary s = log.Summary;
                SessionConfig c = log.Config;
                Rating r = s.Rating;
                sb.Append(string.Join(",", new[]
                {
                    Csv(log.Id), Csv(log.Started.ToString("yyyy-MM-dd HH:mm:ss", Inv)), (s.DurationSec / 60.0).ToString("0.0", Inv),
                    Csv(c.Preset), Csv(c.Mob), c.Batch.ToString(Inv), c.MaxAlive.ToString(Inv),
                    c.Interval.ToString("0.##", Inv), c.Radius.ToString("0.##", Inv), c.GodSword ? "yes" : "no", c.AutoAim ? "yes" : "no",
                    c.MoveEvery.ToString(Inv), c.ClearItems ? "yes" : "no",
                    s.Kills.ToString(Inv), s.Spawned.ToString(Inv), s.Kpm.ToString("0.0", Inv), s.Spm.ToString("0.0", Inv),
                    s.AvgTtk.ToString("0.00", Inv), s.AvgWaveClear.ToString("0.0", Inv), s.IdlePct.ToString("0.0", Inv),
                    s.FpsAvg.ToString("0.0", Inv), s.FpsLow.ToString("0.0", Inv), s.StuttersPerMin.ToString("0.00", Inv),
                    s.KpmChangePct.ToString("0.0", Inv), s.FpsChangePct.ToString("0.0", Inv),
                    s.MemStart.ToString("0", Inv), s.MemEnd.ToString("0", Inv),
                    r.Valid ? r.Speed.ToString("0", Inv) : "", r.Valid ? r.Smooth.ToString("0", Inv) : "",
                    r.Valid ? r.Stability.ToString("0", Inv) : "", r.Valid ? r.Overall.ToString("0", Inv) : "",
                    r.Valid ? r.Grade : "", r.Confidence, Csv(Path.GetFileName(log.FilePath)),
                })).Append('\n');
            }

            Write(IndexPath, sb.ToString(), false);
        }

        private void Write(string path, string text, bool append)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (append)
                    File.AppendAllText(path, text, Encoding.UTF8);
                else
                    File.WriteAllText(path, text, Encoding.UTF8);
                writeFailed = false;
            }
            catch (Exception ex)
            {
                if (!writeFailed)
                    warn("Could not save session log to " + path + ": " + ex.Message);
                writeFailed = true;
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                warn("Could not delete " + path + ": " + ex.Message);
            }
        }

        private static string OneLine(string s)
        {
            return (s ?? "").Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string Csv(string s)
        {
            s = OneLine(s);
            return s.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
