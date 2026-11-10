using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using MuckinatorGrind;
using UnityEngine;

// Session recorder: measures kill rate, spawn rate, time to kill, FPS and memory while you farm,
// and saves it through StatsStore. Only "active" time is measured: running, menu closed, sword ready.
public partial class MuckinatorGrindPlugin
{
    private const float SampleInterval = 10f;     // seconds of active time per log row
    private const float MinSessionToKeep = 30f;   // shorter sessions are thrown away
    private const float StutterSeconds = 0.1f;    // a frame this slow counts as a stutter
    private const float ConfigSettleSeconds = 2f; // wait this long after a settings change before splitting the session

    private StatsStore statsStore;
    private SessionLog liveLog;

    private float statActive;
    private float winStart;
    private readonly List<float> winFrames = new List<float>(4096);

    private int stKills, stSpawned, stWaves;
    private int winKills0, winSpawned0, winTtkCount0;
    private double stTtkSum, winTtkSum0, stWaveClearSum;
    private int stTtkCount;
    private float stIdle;
    private bool stWaveOpen;
    private float stWaveStart;
    private readonly Dictionary<int, float> stSpawnTimes = new Dictionary<int, float>();

    private string pendingConfigKey;
    private float pendingConfigSince = -1f;
    private float nextConfigCheck;

    private Dictionary<string, string> machineInfo;

    private StatsStore Store
    {
        get
        {
            if (statsStore == null)
            {
                statsStore = new StatsStore(Path.Combine(Paths.BepInExRootPath, "MuckinatorGrind"), msg => Logger.LogWarning(msg));
                statsStore.Load();
            }
            return statsStore;
        }
    }

    private SessionConfig CurrentConfig()
    {
        SessionConfig c = new SessionConfig
        {
            Mob = SelectedMobName(),
            Batch = batchSize,
            MaxAlive = maxActiveMobs,
            Interval = spawnInterval,
            Radius = spawnRadius,
            GodSword = godSwordEnabled,
            AutoAim = godSwordEnabled && autoAimEnabled,
            MoveEvery = cfgMoveEnabled != null && cfgMoveEnabled.Value ? MoveEveryWaves : 0,
            ClearItems = cfgClearItems != null && cfgClearItems.Value,
        };

        foreach (Preset p in Presets)
        {
            if (p.Batch == batchSize && p.Max == maxActiveMobs &&
                Mathf.Approximately(p.Interval, spawnInterval) && Mathf.Approximately(p.Radius, spawnRadius))
            {
                c.Preset = p.Name;
                break;
            }
        }
        return c;
    }

    private Dictionary<string, string> MachineInfo()
    {
        if (machineInfo == null)
        {
            machineInfo = new Dictionary<string, string>();
            try
            {
                machineInfo["cpu"] = SystemInfo.processorType + " (" + SystemInfo.processorCount + " threads)";
                machineInfo["gpu"] = SystemInfo.graphicsDeviceName;
                machineInfo["ram_mb"] = SystemInfo.systemMemorySize.ToString();
                machineInfo["os"] = SystemInfo.operatingSystem;
                machineInfo["plugins"] = Chainloader.PluginInfos.Count.ToString();
            }
            catch (Exception ex)
            {
                Logger.LogDebug("Could not read PC info: " + ex.Message);
            }
        }

        // These can change between sessions.
        Dictionary<string, string> info = new Dictionary<string, string>(machineInfo);
        info["resolution"] = Screen.width + "x" + Screen.height;
        try { info["quality"] = QualitySettings.names[QualitySettings.GetQualityLevel()]; }
        catch { }
        return info;
    }

    // ── Session lifecycle ──

    private void StatsBegin()
    {
        if (liveLog != null)
            StatsEnd();

        statActive = winStart = 0f;
        winFrames.Clear();
        stKills = stSpawned = stWaves = stTtkCount = 0;
        winKills0 = winSpawned0 = winTtkCount0 = 0;
        stTtkSum = winTtkSum0 = stWaveClearSum = 0;
        stIdle = 0f;
        stWaveOpen = false;
        stSpawnTimes.Clear();
        pendingConfigSince = -1f;

        try
        {
            liveLog = Store.Create(CurrentConfig(), MachineInfo());
        }
        catch (Exception ex)
        {
            liveLog = null;
            Logger.LogError("Could not start a session log: " + ex);
        }
    }

    private void StatsEnd()
    {
        if (liveLog == null)
            return;

        SessionLog log = liveLog;
        liveLog = null;
        try
        {
            TakeSample(log);
            bool keep = statActive >= MinSessionToKeep;
            Store.Finish(log, keep);
            if (keep)
                selectedLog = log;
            else if (selectedLog == log)
                selectedLog = null;
        }
        catch (Exception ex)
        {
            Logger.LogError("Could not finish the session log: " + ex);
        }
        stSpawnTimes.Clear();
    }

    private bool StatsRecording()
    {
        return liveLog != null && state == RunState.Running && !showGUI &&
               (!godSwordEnabled || swordReady) && PlayerMovement.Instance != null;
    }

    private void StatsTick()
    {
        if (liveLog == null)
            return;

        CheckConfigChange();
        if (liveLog == null || !StatsRecording())
            return;

        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f || dt > 1f) // >1 s is a load screen or alt-tab, not gameplay
            return;

        statActive += dt;
        winFrames.Add(dt);

        if (spawnQueue == 0 && activeSpawned.Count == 0)
        {
            stIdle += dt;
            if (stWaveOpen)
            {
                stWaveOpen = false;
                stWaves++;
                stWaveClearSum += statActive - stWaveStart;
            }
        }

        if (statActive - winStart >= SampleInterval)
            TakeSample(liveLog);
    }

    // Changing the settings mid-run starts a new session, so each log describes exactly one setup.
    private void CheckConfigChange()
    {
        float now = Time.unscaledTime;
        if (now < nextConfigCheck)
            return;
        nextConfigCheck = now + 0.5f;

        string key = CurrentConfig().Key;
        if (key == liveLog.Config.Key)
        {
            pendingConfigSince = -1f;
            return;
        }

        if (key != pendingConfigKey || pendingConfigSince < 0f)
        {
            pendingConfigKey = key;
            pendingConfigSince = now;
            return;
        }

        if (now - pendingConfigSince >= ConfigSettleSeconds && !Input.GetMouseButton(0) && GUIUtility.keyboardControl == 0)
        {
            StatsBegin();
            Notify("Settings changed — logging a new session.", NoticeLevel.Info);
        }
    }

    private void TakeSample(SessionLog log)
    {
        float window = statActive - winStart;
        if (log == null || window < 0.5f || winFrames.Count == 0)
            return;

        int frames = winFrames.Count;
        double total = 0;
        float worst = 0f;
        int stutters = 0;
        for (int i = 0; i < frames; i++)
        {
            float f = winFrames[i];
            total += f;
            if (f > worst) worst = f;
            if (f >= StutterSeconds) stutters++;
        }

        // 1% low: average FPS of the slowest 1% of frames.
        winFrames.Sort();
        int slowest = Mathf.Max(1, frames / 100);
        double slowSum = 0;
        for (int i = frames - slowest; i < frames; i++)
            slowSum += winFrames[i];

        int ttkCount = stTtkCount - winTtkCount0;
        Sample s = new Sample
        {
            T = statActive,
            Dt = window,
            Kills = stKills,
            Spawned = stSpawned,
            Waves = stWaves,
            Alive = activeSpawned.Count,
            Kpm = (stKills - winKills0) * 60f / window,
            Spm = (stSpawned - winSpawned0) * 60f / window,
            Ttk = ttkCount > 0 ? (float)((stTtkSum - winTtkSum0) / ttkCount) : -1f,
            TtkSum = stTtkSum,
            TtkCount = stTtkCount,
            WaveClearSum = stWaveClearSum,
            IdleSec = stIdle,
            Fps = total > 0 ? (float)(frames / total) : 0f,
            FpsLow = slowSum > 0 ? (float)(slowest / slowSum) : 0f,
            FrameMaxMs = worst * 1000f,
            Stutters = stutters,
            MemMb = MemoryMb(),
        };

        log.Samples.Add(s);
        Store.Append(log, s);
        log.Recompute();

        winStart = statActive;
        winFrames.Clear();
        winKills0 = stKills;
        winSpawned0 = stSpawned;
        winTtkSum0 = stTtkSum;
        winTtkCount0 = stTtkCount;
    }

    private static float MemoryMb()
    {
        try
        {
            using (Process p = Process.GetCurrentProcess())
            {
                long bytes = p.WorkingSet64;
                if (bytes > 0)
                    return bytes / 1048576f;
            }
        }
        catch { }
        return GC.GetTotalMemory(false) / 1048576f;
    }

    // ── Events from the spawner ──

    private void StatsOnWaveStart()
    {
        if (liveLog == null)
            return;
        stWaveOpen = true;
        stWaveStart = statActive;
    }

    private void StatsOnSpawn(int mobId)
    {
        if (liveLog == null)
            return;
        stSpawned++;
        stSpawnTimes[mobId] = statActive;
    }

    private void StatsOnKill(int mobId)
    {
        if (liveLog == null)
            return;
        stKills++;
        float born;
        if (stSpawnTimes.TryGetValue(mobId, out born))
        {
            stSpawnTimes.Remove(mobId);
            stTtkSum += Mathf.Max(0f, statActive - born);
            stTtkCount++;
        }
    }

    private void OnApplicationQuit()
    {
        StatsEnd();
        Log("stop", "Game is quitting.");
    }

    // ═════════════════════════ Logs tab ══════════════════════════════

    private enum LogsView { Live, History, Compare }
    private static readonly string[] LogsViewNames = { "Current session", "History", "Compare setups" };

    private LogsView logsView;
    private SessionLog selectedLog;
    private SessionLog compareLog;
    private int historyShown = 20;
    private float deleteConfirmUntil;
    private SessionLog deleteTarget;

    private void DrawLogsTab()
    {
        StatsStore store = Store;

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        for (int i = 0; i < LogsViewNames.Length; i++)
        {
            bool active = (int)logsView == i;
            if (GUILayout.Button(LogsViewNames[i], active ? ui.TabActive : ui.Button, GUILayout.Height(28)) && !active)
            {
                logsView = (LogsView)i;
                contentScroll = Vector2.zero;
            }
        }
        GUILayout.EndHorizontal();

        switch (logsView)
        {
            case LogsView.Live: DrawLiveView(); break;
            case LogsView.History: DrawHistoryView(store); break;
            case LogsView.Compare: DrawCompareView(store); break;
        }

        Section("FILES");
        GUILayout.Label("Sessions are saved as spreadsheets (CSV) in " + store.Root + ".", ui.Hint);
        if (GUILayout.Button(new GUIContent("Open logs folder", store.Root), ui.Button, GUILayout.Height(28)))
            OpenFolder(store.Root);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Application.OpenURL("file:///" + path.Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            Notify("Could not open folder: " + ex.Message, NoticeLevel.Error);
        }
    }

    // ── Current session ──

    private void DrawLiveView()
    {
        if (liveLog == null)
        {
            EmptyState("No session running",
                "Press START. Your kills and frame rate are recorded while you farm, and the setup gets a grade.");
            return;
        }

        GUILayout.Label("Recording pauses while this menu is open.", ui.Hint);

        SessionSummary s = liveLog.Summary;
        Section(liveLog.Config.Name.ToUpperInvariant());
        DrawRatingCard(s);

        GUILayout.BeginHorizontal();
        StatTile("KILLS", s.Kills.ToString("N0"), "Plugin mobs killed this session");
        StatTile("KILLS/MIN", s.Kpm.ToString("N0"), "Average kills per minute");
        StatTile("FPS", s.FpsAvg.ToString("0"), "Average frames per second");
        StatTile("TIME", FormatClock(s.DurationSec), "Time spent farming (menu closed)");
        GUILayout.EndHorizontal();

        Section("KILLS OVER TIME");
        DrawKillsChart(liveLog, null);
    }

    // ── History ──

    private void DrawHistoryView(StatsStore store)
    {
        List<SessionLog> list = store.Sessions;
        int saved = list.Count - (liveLog != null && list.Contains(liveLog) ? 1 : 0);
        if (saved == 0)
        {
            EmptyState("No saved sessions yet",
                "Sessions longer than " + MinSessionToKeep.ToString("0") + " seconds are saved here when you press STOP.");
            return;
        }

        if (selectedLog == null || !list.Contains(selectedLog) || selectedLog == liveLog)
            selectedLog = LatestSaved(list);
        if (compareLog != null && !list.Contains(compareLog))
            compareLog = null;

        Section("SESSIONS (" + saved + ")");
        GUILayout.Label("Click a session to see it. Press \"vs\" to draw another session on the same chart.", ui.Hint);

        int shown = 0;
        for (int i = list.Count - 1; i >= 0 && shown < historyShown; i--)
        {
            SessionLog log = list[i];
            if (log == liveLog)
                continue;
            shown++;
            DrawSessionRow(log);
        }
        if (saved > historyShown && GUILayout.Button("Show more", ui.ButtonSmall, GUILayout.Height(24)))
            historyShown += 20;

        if (selectedLog == null)
            return;

        SessionSummary s = selectedLog.Summary;
        Section("SESSION " + selectedLog.Started.ToString("ddd d MMM, HH:mm").ToUpperInvariant());
        GUILayout.Label(selectedLog.Config.Name + "  —  " + selectedLog.Config.Details, ui.BoldWrap);
        if (!selectedLog.Finished)
            GUILayout.Label("The game closed before STOP was pressed; this session was recovered.", ui.SmallWrap);
        DrawRatingCard(s);

        GUILayout.BeginHorizontal();
        StatTile("KILLS", s.Kills.ToString("N0"), "Plugin mobs killed");
        StatTile("KILLS/MIN", s.Kpm.ToString("N0"), "Average kills per minute");
        StatTile("FPS", s.FpsAvg.ToString("0"), "Average frames per second (slowest 1%: " + s.FpsLow.ToString("0") + ")");
        StatTile("TIME", FormatClock(s.DurationSec), "Time spent farming (menu closed)");
        GUILayout.EndHorizontal();

        Section("KILLS OVER TIME");
        DrawKillsChart(selectedLog, compareLog != selectedLog ? compareLog : null);

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        GUI.enabled = state == RunState.Stopped;
        if (GUILayout.Button(new GUIContent("Use these settings", "Copy this session's mob and spawn settings into the Spawn tab."),
                ui.Button, GUILayout.Height(28)))
            ApplyConfig(selectedLog.Config);
        GUI.enabled = true;

        bool confirming = deleteTarget == selectedLog && Time.unscaledTime < deleteConfirmUntil;
        if (GUILayout.Button(confirming ? "Click again to delete" : "Delete session", confirming ? ui.ButtonDanger : ui.Button,
                GUILayout.Height(28)))
        {
            if (confirming)
            {
                store.Delete(selectedLog);
                if (compareLog == selectedLog) compareLog = null;
                selectedLog = null;
                deleteTarget = null;
                Notify("Session deleted.", NoticeLevel.Info);
            }
            else
            {
                deleteTarget = selectedLog;
                deleteConfirmUntil = Time.unscaledTime + 3f;
            }
        }
        GUILayout.EndHorizontal();
    }

    private SessionLog LatestSaved(List<SessionLog> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i] != liveLog)
                return list[i];
        return null;
    }

    private void DrawSessionRow(SessionLog log)
    {
        SessionSummary s = log.Summary;
        bool selected = log == selectedLog;
        bool comparing = log == compareLog;

        GUILayout.BeginHorizontal(selected ? ui.CardSelected : ui.Card);
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        GUILayout.Label(log.Started.ToString("ddd d MMM, HH:mm") + "   ·   " + log.Config.Name, ui.BoldWrap);
        GUILayout.Label(FormatClock(s.DurationSec) + "  ·  " + s.Kills.ToString("N0") + " kills  ·  " +
                        s.Kpm.ToString("N0") + " kills/min  ·  " + s.FpsAvg.ToString("0") + " FPS", ui.SmallWrap);
        GUILayout.EndVertical();

        GradeLabel(s.Rating, false);

        if (GUILayout.Button(new GUIContent("vs", "Draw this session on the chart next to the selected one"),
                comparing ? ui.TabActive : ui.ButtonSmall, GUILayout.Width(34), GUILayout.Height(24)))
            compareLog = comparing ? null : log;
        GUILayout.EndHorizontal();

        Rect row = GUILayoutUtility.GetLastRect();
        Event e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && row.Contains(e.mousePosition))
        {
            selectedLog = log;
            e.Use();
        }
    }

    // ── Compare ──

    private void DrawCompareView(StatsStore store)
    {
        List<ConfigGroup> groups = ConfigGroup.Build(store.Sessions);
        if (groups.Count == 0)
        {
            EmptyState("Nothing to compare yet",
                "Farm with a few different presets for at least " + Rating.MinMinutes.ToString("0") +
                " minutes each. Each setup is graded here so you can pick the best one for your PC.");
            return;
        }

        groups.Sort((a, b) => b.Rating.CompareTo(a.Rating));

        GUILayout.Label("Setups ranked from best to worst on this PC. Sessions with the same settings are combined.", ui.Hint);

        for (int i = 0; i < groups.Count; i++)
        {
            ConfigGroup g = groups[i];
            bool best = i == 0;

            GUILayout.BeginHorizontal(best ? ui.CardSelected : ui.Card);
            GradeLabel(g.Rating, best, g.Grade, g.Rating);

            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.Label((best ? "BEST:  " : "") + g.Config.Name, ui.BoldWrap);
            GUILayout.Label(g.Config.Details, ui.SmallWrap);
            GUILayout.Label(g.Kpm.ToString("N0") + " kills/min  ·  " + g.FpsAvg.ToString("0") + " FPS  ·  " +
                            FormatClock(g.TotalSec) + " farmed  ·  confidence " + g.Confidence, ui.SmallWrap);
            if (best && g.LongestSec < Rating.FullMinutes * 60f)
                GUILayout.Label("Run this setup for 60+ minutes in one go to confirm it stays fast.", ui.TextAmber);
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUILayout.Width(64));
            GUILayout.FlexibleSpace();
            GUI.enabled = state == RunState.Stopped;
            if (GUILayout.Button(new GUIContent("Use", "Copy these settings into the Spawn tab."),
                    best ? ui.ButtonPrimary : ui.Button, GUILayout.Height(28), GUILayout.Width(60)))
                ApplyConfig(g.Config);
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
    }

    private void ApplyConfig(SessionConfig c)
    {
        for (int i = 0; i < AllowedMobs.Length; i++)
            if (AllowedMobLabels[i] == c.Mob || AllowedMobs[i] == c.Mob)
                selectedMobName = AllowedMobs[i];
        batchSize = Mathf.Clamp(c.Batch, 1, MaxMobs);
        maxActiveMobs = Mathf.Clamp(c.MaxAlive, 1, MaxMobs);
        spawnInterval = Mathf.Clamp(c.Interval, 0.5f, 120f);
        spawnRadius = Mathf.Clamp(c.Radius, 2f, 100f);
        godSwordEnabled = c.GodSword;
        if (c.GodSword)
            autoAimEnabled = c.AutoAim;
        cfgMoveEnabled.Value = c.MoveEvery > 0;
        if (c.MoveEvery > 0)
            cfgMoveEveryWaves.Value = c.MoveEvery;
        cfgClearItems.Value = c.ClearItems;
        fieldBuffers.Clear();
        Notify("Settings applied: " + c.Name + ".", NoticeLevel.Success);
    }

    // ── Shared pieces ──

    private void DrawRatingCard(SessionSummary s)
    {
        Rating r = s.Rating;
        GUILayout.BeginHorizontal(ui.Card);
        GradeLabel(r, true);

        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        GUILayout.Label(r.Verdict, r.Valid && r.Overall >= 65f ? ui.TextGreen : r.Valid ? ui.TextAmber : ui.Hint);
        if (r.Valid)
        {
            GUILayout.Label("Speed " + r.Speed.ToString("0") + "  ·  Smoothness " + r.Smooth.ToString("0") +
                            "  ·  Stability " + r.Stability.ToString("0") + "  ·  Confidence " + r.Confidence, ui.SmallWrap);
        }
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    private void GradeLabel(Rating r, bool big)
    {
        GradeLabel(r.Overall, big, r.Valid ? r.Grade : "–", r.Valid ? r.Overall : -1f);
    }

    private void GradeLabel(float score, bool big, string grade, float shownScore)
    {
        Color c = grade == "–" ? ui.MutedColor : GradeColor(grade);
        GUIStyle style = big ? ui.GradeBig : ui.GradeSmall;
        Ui.SetTextColor(style, c);

        GUILayout.BeginVertical(GUILayout.Width(big ? 86 : 54));
        GUILayout.Label(grade, style);
        GUILayout.Label(shownScore < 0f ? "no rating" : shownScore.ToString("0") + " / 100", ui.StatCaption);
        GUILayout.EndVertical();
    }

    private Color GradeColor(string grade)
    {
        switch (grade)
        {
            case "S":
            case "A": return ui.GreenColor;
            case "B": return ui.BlueColor;
            case "C": return ui.AmberColor;
            default: return ui.RedColor;
        }
    }

    // ── Kills-over-time line chart ──

    private const int MaxChartPoints = 120;

    private void DrawKillsChart(SessionLog main, SessionLog other)
    {
        List<Vector2> a = KillSeries(main.Samples);
        List<Vector2> b = other != null ? KillSeries(other.Samples) : null;

        if (a.Count < 2)
        {
            GUILayout.Label("The chart appears after " + (SampleInterval * 2).ToString("0") + " seconds of farming.", ui.Hint);
            return;
        }

        LineChart(a, b, 170f);

        GUILayout.BeginHorizontal();
        LegendItem(ui.AccentTex, main == liveLog ? "This session" : main.Started.ToString("d MMM HH:mm") + " · " + main.Config.Name);
        if (b != null)
            LegendItem(ui.BlueTex, other.Started.ToString("d MMM HH:mm") + " · " + other.Config.Name);
        GUILayout.EndHorizontal();
        GUILayout.Label("Total kills (up) against farming time (across). A steeper line means faster farming; " +
                        "a line that bends flatter means the game slowed down.", ui.SmallWrap);
    }

    private void LegendItem(Texture2D swatch, string label)
    {
        Rect r = GUILayoutUtility.GetRect(10, 16, GUILayout.Width(10));
        Ui.DrawRect(new Rect(r.x, r.y + 5, 10, 6), swatch);
        GUILayout.Label(label, ui.HintSmall, GUILayout.ExpandWidth(false));
        GUILayout.Space(10);
    }

    // Cumulative kills against active time, starting at (0, 0), thinned out for long sessions.
    private static List<Vector2> KillSeries(List<Sample> samples)
    {
        List<Vector2> pts = new List<Vector2>(Mathf.Min(samples.Count, MaxChartPoints) + 2) { Vector2.zero };
        if (samples.Count == 0)
            return pts;

        int step = Mathf.Max(1, Mathf.CeilToInt(samples.Count / (float)MaxChartPoints));
        for (int i = step - 1; i < samples.Count; i += step)
            pts.Add(new Vector2(samples[i].T, samples[i].Kills));

        Sample last = samples[samples.Count - 1];
        if (pts[pts.Count - 1].x < last.T)
            pts.Add(new Vector2(last.T, last.Kills));
        return pts;
    }

    private void LineChart(List<Vector2> a, List<Vector2> b, float height)
    {
        Rect r = GUILayoutUtility.GetRect(10, height, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint)
            return;

        float xMax = 60f, yMax = 0f;
        foreach (Vector2 p in a) { xMax = Mathf.Max(xMax, p.x); yMax = Mathf.Max(yMax, p.y); }
        if (b != null)
            foreach (Vector2 p in b) { xMax = Mathf.Max(xMax, p.x); yMax = Mathf.Max(yMax, p.y); }
        yMax = NiceCeiling(yMax);

        Ui.DrawRect(r, ui.ChartBgTex);
        Rect plot = new Rect(r.x + 48, r.y + 10, r.width - 58, r.height - 30);

        for (int i = 0; i <= 4; i++)
        {
            float y = plot.yMax - plot.height * i / 4f;
            Ui.DrawRect(new Rect(plot.x, y, plot.width, 1), i == 0 ? ui.BorderTex : ui.GridTex);
            GUI.Label(new Rect(r.x, y - 8, 44, 16), FormatAxis(yMax * i / 4f), ui.AxisRight);
        }
        for (int i = 0; i <= 4; i++)
        {
            float x = plot.x + plot.width * i / 4f;
            GUI.Label(new Rect(x - 30, plot.yMax + 3, 60, 16), FormatClock(xMax * i / 4f), ui.AxisCenter);
        }

        if (b != null)
            Polyline(b, plot, xMax, yMax, ui.BlueTex);
        Polyline(a, plot, xMax, yMax, ui.AccentTex);
    }

    private static void Polyline(List<Vector2> pts, Rect plot, float xMax, float yMax, Texture2D tex)
    {
        Vector2 prev = Vector2.zero;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector2 p = new Vector2(
                plot.x + plot.width * Mathf.Clamp01(pts[i].x / xMax),
                plot.yMax - plot.height * Mathf.Clamp01(yMax > 0f ? pts[i].y / yMax : 0f));
            if (i > 0)
                DrawLine(prev, p, tex);
            prev = p;
        }
    }

    // A straight line made of small squares. Only axis-aligned rectangles are drawn, so it stays correct
    // at any interface scale and inside scroll views (rotating the GUI matrix does not).
    private static void DrawLine(Vector2 a, Vector2 b, Texture2D tex)
    {
        const float size = 2.5f;
        const float spacing = 1.2f;

        float length = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(length / spacing));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, i / (float)steps);
            GUI.DrawTexture(new Rect(p.x - size / 2f, p.y - size / 2f, size, size), tex);
        }
    }

    private static float NiceCeiling(float v)
    {
        if (v <= 0f)
            return 1f;
        float magnitude = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(v)));
        foreach (float step in new[] { 1f, 2f, 2.5f, 5f, 10f })
            if (v <= step * magnitude)
                return step * magnitude;
        return 10f * magnitude;
    }

    private static string FormatAxis(float v)
    {
        if (v >= 10000f) return (v / 1000f).ToString("0") + "k";
        if (v >= 1000f) return (v / 1000f).ToString("0.#") + "k";
        if (v >= 10f || v == 0f) return v.ToString("0");
        return v.ToString("0.#");
    }

    private static string FormatClock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        TimeSpan t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
        if (t.TotalMinutes >= 1) return t.Minutes + "m " + t.Seconds.ToString("00") + "s";
        return t.Seconds + "s";
    }

    // One line for the HUD while farming.
    private string HudRatingText()
    {
        if (liveLog == null)
            return null;
        SessionSummary s = liveLog.Summary;
        if (!s.Rating.Valid)
            return "after " + Rating.MinMinutes.ToString("0") + " min  (" + FormatClock(statActive) + ")";
        return s.Rating.Grade + " " + s.Rating.Overall.ToString("0") + "  ·  " + s.Kpm.ToString("N0") + "/min  ·  " +
               s.FpsAvg.ToString("0") + " FPS";
    }
}
