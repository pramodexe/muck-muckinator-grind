using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using MuckinatorGrind;
using UnityEngine;

// Diagnostic log (debug.log) plus the two safety nets for stuck waves:
//  - the stuck-wave watchdog: if no spawned mob dies for a while, the leftovers are finished off / skipped
//    so the next wave can spawn, instead of the session silently waiting forever;
//  - auto-aim gives up on a mob it cannot reach, so it stops holding the camera.
public partial class MuckinatorGrindPlugin
{
    private const float HeartbeatSeconds = 30f;
    private const float SlowFrameSeconds = 0.5f;
    private const float StuckWaveSeconds = 20f;     // no plugin kill for this long while a wave is alive = stuck
    private const float AimOutOfReachSeconds = 8f;  // target out of reach and not getting closer for this long
    private const float AimIgnoreSeconds = 20f;     // how long a given-up target is left alone
    private const float AimSameTargetSeconds = 30f; // target in reach but would not die

    private DiagnosticLog diag;
    private ConfigEntry<bool> cfgDebugLog;

    private float nextHeartbeat;
    private float nextStateSnapshot;
    private float lastSlowFrameLog;
    private int hbFrames;
    private double hbTime;
    private float hbWorst;
    private readonly Dictionary<string, string> settingsSnapshot = new Dictionary<string, string>();

    private readonly object exceptionGate = new object();
    private float exceptionWindowStart;
    private int exceptionsInWindow;
    private string lastExceptionText;

    // Stuck-wave watchdog
    private float noKillTimer;
    private int stragglersTotal;
    private readonly HashSet<int> stragglers = new HashSet<int>();

    // Auto-aim give-up
    private readonly Dictionary<int, float> aimIgnored = new Dictionary<int, float>(); // mob id -> ignore until
    private int aimTargetId;
    private float aimClosest;
    private float aimSameTimer;
    private float aimOutOfReachTimer;
    private float aimTargetDistance;
    private float aimReach;

    // ── Setup ──

    private void DiagAwake()
    {
        cfgDebugLog = Config.Bind("Interface", "DiagnosticLog", true,
            "Write BepInEx/MuckinatorGrind/debug.log with events, setting changes and freezes, for troubleshooting.");

        diag = new DiagnosticLog(Path.Combine(Paths.BepInExRootPath, "MuckinatorGrind"));
        diag.Enabled = cfgDebugLog.Value;
        diag.Open();
        WriteLogHeader();
        SnapshotSettings(false);

        Application.logMessageReceivedThreaded += OnUnityLog;
    }

    private void WriteLogHeader()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("MuckinatorGrind ").Append(Version).Append(" diagnostic log").Append(diag.Enabled ? "" : " (switched off)");
        diag.WriteAlways("start", sb.ToString());

        try
        {
            diag.Write("pc", "OS " + SystemInfo.operatingSystem + " | CPU " + SystemInfo.processorType + " x" + SystemInfo.processorCount +
                             " | RAM " + SystemInfo.systemMemorySize + " MB | GPU " + SystemInfo.graphicsDeviceName +
                             " (" + SystemInfo.graphicsMemorySize + " MB) | Unity " + Application.unityVersion);
            diag.Write("pc", "Screen " + Screen.width + "x" + Screen.height + " | quality " +
                             QualitySettings.names[QualitySettings.GetQualityLevel()] + " | vSync " + QualitySettings.vSyncCount +
                             " | target FPS " + Application.targetFrameRate);
        }
        catch (Exception ex)
        {
            diag.Write("pc", "Could not read PC info: " + ex.Message);
        }

        try
        {
            List<string> mods = new List<string>();
            foreach (BepInEx.PluginInfo p in Chainloader.PluginInfos.Values)
                mods.Add(p.Metadata.Name + " " + p.Metadata.Version);
            mods.Sort(StringComparer.OrdinalIgnoreCase);
            diag.Write("mods", mods.Count + " loaded: " + string.Join(", ", mods.ToArray()));
        }
        catch (Exception ex)
        {
            diag.Write("mods", "Could not list mods: " + ex.Message);
        }
    }

    private void DiagShutdown(string reason)
    {
        if (diag == null)
            return;
        Application.logMessageReceivedThreaded -= OnUnityLog;
        diag.Write("stop", reason);
        diag.Dispose();
        diag = null;
    }

    private void Log(string category, string message)
    {
        if (diag != null)
            diag.Write(category, message);
    }

    // ── Every frame ──

    private void DiagTick()
    {
        if (diag == null)
            return;

        bool inWorld = PlayerMovement.Instance != null;
        diag.SetWatching(inWorld);

        float now = Time.unscaledTime;
        float dt = Time.unscaledDeltaTime;
        string snapshot = null;
        if (now >= nextStateSnapshot)
        {
            nextStateSnapshot = now + 1f;
            snapshot = StateLine();
            SnapshotSettings(true);
        }
        diag.Tick(snapshot);

        if (!inWorld)
            return;

        hbFrames++;
        hbTime += dt;
        if (dt > hbWorst)
            hbWorst = dt;

        if (dt >= SlowFrameSeconds && now - lastSlowFrameLog > 2f)
        {
            lastSlowFrameLog = now;
            Log("slow-frame", "One frame took " + dt.ToString("0.00") + " s. " + StateLine());
        }

        if (now >= nextHeartbeat)
        {
            nextHeartbeat = now + HeartbeatSeconds;
            if (hbFrames > 0)
            {
                Log("status", StateLine() + " | fps " + (hbFrames / hbTime).ToString("0") +
                              " worst " + (hbWorst * 1000f).ToString("0") + "ms | mem " + MemoryMb().ToString("0") + "MB");
            }
            hbFrames = 0;
            hbTime = 0;
            hbWorst = 0f;
        }
    }

    /// <summary>Everything worth knowing about "why is it doing nothing" on one line.</summary>
    private string StateLine()
    {
        StringBuilder sb = new StringBuilder(256);
        sb.Append(state);
        if (state != RunState.Stopped && godSwordEnabled)
            sb.Append(swordReady ? " sword=ready" : " sword=waiting");
        sb.Append(" menu=").Append(showGUI ? "open" : "closed");
        sb.Append(" cursor=").Append(Cursor.lockState);
        sb.Append(" host=").Append(IsHost() ? "yes" : "no");
        sb.Append(" | alive ").Append(activeSpawned.Count).Append('/').Append(maxActiveMobs);
        sb.Append(" queue ").Append(spawnQueue);
        if (state == RunState.Running && spawnQueue == 0 && activeSpawned.Count == 0)
            sb.Append(" next-wave ").Append(Mathf.Max(0f, nextSpawnTime - Time.time).ToString("0.0")).Append('s');
        sb.Append(" | no-kill ").Append(noKillTimer.ToString("0.0")).Append('s');
        sb.Append(" | session kills ").Append(sessionDefeated).Append(" spawned ").Append(sessionSpawned);
        if (stragglersTotal > 0)
            sb.Append(" stuck-skipped ").Append(stragglersTotal);
        if (cfgMoveEnabled != null && cfgMoveEnabled.Value)
            sb.Append(" | waves here ").Append(wavesSinceMove).Append('/').Append(MoveEveryWaves).Append(" moves ").Append(movesThisSession);
        if (pendingCleanupAt >= 0f)
            sb.Append(" cleanup-pending");
        if (godSwordEnabled && autoAimEnabled)
        {
            sb.Append(" | aim ");
            if (aimTarget != null)
                sb.Append(aimTarget.name).Append(" at ").Append(aimTargetDistance.ToString("0.0"))
                  .Append("m (reach ").Append(aimReach.ToString("0.0")).Append("m)");
            else
                sb.Append("none");
            if (aimIgnored.Count > 0)
                sb.Append(" ignored ").Append(aimIgnored.Count);
        }
        if (steamKills >= 0)
            sb.Append(" | steam kills ").Append(steamKills);
        return sb.ToString();
    }

    // Logs every setting that changed since the last check, as "Name: old -> new".
    private void SnapshotSettings(bool logChanges)
    {
        Dictionary<string, string> now = new Dictionary<string, string>
        {
            { "Mob", selectedMobName }, { "Wave size", batchSize.ToString() }, { "Max alive", maxActiveMobs.ToString() },
            { "Wave interval", spawnInterval.ToString("0.##") }, { "Spawn radius", spawnRadius.ToString("0.##") },
            { "Unlimited health", unlimitedHealth.ToString() }, { "Unlimited stamina", unlimitedStamina.ToString() },
            { "God Sword", godSwordEnabled.ToString() }, { "Auto-aim", autoAimEnabled.ToString() },
            { "Auto-click", autoClick.ToString() }, { "Click interval", clickInterval.ToString("0.##") },
            { "Kill target on", limitEnabled.ToString() }, { "Kill target", killLimit.ToString() },
            { "Start key", startKey != null ? startKey.ToString() : "" }, { "Menu key", menuKey != null ? menuKey.ToString() : "" },
            { "Move to new spot", cfgMoveEnabled != null ? cfgMoveEnabled.Value.ToString() : "" },
            { "Waves before moving", cfgMoveEveryWaves != null ? cfgMoveEveryWaves.Value.ToString() : "" },
            { "Clear dropped items", cfgClearItems != null ? cfgClearItems.Value.ToString() : "" },
            { "Keep power-ups", cfgKeepPowerups != null ? cfgKeepPowerups.Value.ToString() : "" },
            { "HUD", showHud.ToString() }, { "Tracker", trackerEnabled.ToString() }, { "UI scale", uiScale.ToString("0.##") },
        };

        if (logChanges && !Input.GetMouseButton(0))
        {
            List<string> changes = new List<string>();
            foreach (KeyValuePair<string, string> kv in now)
            {
                string old;
                if (settingsSnapshot.TryGetValue(kv.Key, out old) && old != kv.Value)
                    changes.Add(kv.Key + ": " + old + " -> " + kv.Value);
            }
            if (changes.Count > 0)
                Log("setting", string.Join(", ", changes.ToArray()));
        }

        if (!logChanges || !Input.GetMouseButton(0))
        {
            settingsSnapshot.Clear();
            foreach (KeyValuePair<string, string> kv in now)
                settingsSnapshot[kv.Key] = kv.Value;
        }
    }

    // Exceptions from any mod end up here; the game's own error spam (plain errors) is ignored.
    private void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        if (diag == null || type != LogType.Exception)
            return;

        lock (exceptionGate)
        {
            float now = (Environment.TickCount & int.MaxValue) / 1000f;
            if (now - exceptionWindowStart > 60f)
            {
                if (exceptionsInWindow > 20)
                    diag.Write("exception", "(" + (exceptionsInWindow - 20) + " more exceptions in the last minute were not logged)");
                exceptionWindowStart = now;
                exceptionsInWindow = 0;
            }
            exceptionsInWindow++;
            if (exceptionsInWindow > 20 || condition == lastExceptionText)
                return;
            lastExceptionText = condition;
        }

        string[] lines = (stackTrace ?? "").Split('\n');
        string top = string.Join(" <- ", lines, 0, Math.Min(4, lines.Length)).Replace("\r", "").Trim();
        diag.Write("exception", condition + " | at " + top);
    }

    // ── Stuck-wave watchdog ──

    private void ResetStuckWatch()
    {
        noKillTimer = 0f;
    }

    /// <summary>Called every frame while running with the sword ready.</summary>
    private void CheckStuckWave()
    {
        // Only for automatic farming; when you fight by hand, you decide when a wave is done.
        if (!godSwordEnabled)
            return;

        if (showGUI || spawnQueue > 0 || activeSpawned.Count == 0)
        {
            if (spawnQueue > 0 || activeSpawned.Count == 0)
                noKillTimer = 0f;
            return;
        }

        float dt = Time.unscaledDeltaTime;
        if (dt > 1f)
            return;

        noKillTimer += dt;
        if (noKillTimer >= StuckWaveSeconds)
            ReleaseStragglers();
    }

    private void ReleaseStragglers()
    {
        Vector3 me = PlayerMovement.Instance != null ? PlayerMovement.Instance.transform.position : Vector3.zero;
        int count = activeSpawned.Count;
        int finished = 0;

        StringBuilder where = new StringBuilder();
        foreach (KeyValuePair<int, Mob> kv in activeSpawned)
        {
            Mob mob = kv.Value;
            if (mob == null)
                continue;

            float dist = Vector3.Distance(me, mob.transform.position);
            Hitable h = mob.hitable;
            if (where.Length < 1500)
                where.Append(mob.name).Append(" hp ").Append(h != null ? h.hp : -1).Append(" at ")
                     .Append(mob.transform.position.ToString("F0")).Append(" (").Append(dist.ToString("0")).Append("m); ");

            stragglers.Add(kv.Key);
            aimIgnored[kv.Key] = float.MaxValue;

            // Finish it off the same way a sword hit would, so it still counts as a kill.
            try
            {
                if (h != null && h.hp > 0)
                {
                    h.Hit(h.hp * 10 + 10000, 1f, 0, mob.transform.position, 0);
                    finished++;
                }
            }
            catch (Exception ex)
            {
                Log("stuck-wave", "Could not finish off " + mob.name + ": " + ex.Message);
            }
        }

        stragglersTotal += count;
        activeSpawned.Clear();
        noKillTimer = 0f;
        aimTarget = null;
        aimActive = false;

        Log("stuck-wave", "No spawned mob died for " + StuckWaveSeconds.ToString("0") + " s with " + count +
                          " still alive. Player at " + me.ToString("F0") + ". Tried to finish off " + finished +
                          " and moved on to the next wave. Mobs: " + where);
        Notify(count + (count == 1 ? " mob was" : " mobs were") + " stuck out of reach — skipped to the next wave.",
            NoticeLevel.Warning);
    }

    /// <summary>Kills of mobs the watchdog already skipped still count.</summary>
    private bool TakeStraggler(int mobId)
    {
        aimIgnored.Remove(mobId);
        return stragglers.Remove(mobId);
    }

    private void ClearStuckState()
    {
        stragglers.Clear();
        aimIgnored.Clear();
        noKillTimer = 0f;
        aimTargetId = 0;
    }

    // ── Auto-aim give-up ──

    private bool AimIgnored(Mob mob)
    {
        float until;
        if (aimIgnored.Count == 0 || !aimIgnored.TryGetValue(mob.GetInstanceID(), out until))
            return false;
        if (Time.unscaledTime < until)
            return true;
        aimIgnored.Remove(mob.GetInstanceID());
        return false;
    }

    /// <summary>
    /// Returns false (and stops aiming at it) when the current target looks unkillable:
    /// never in reach for a while, or in reach but not dying.
    /// </summary>
    private bool AimTargetStillWorthIt(Mob target, float distance, float reach)
    {
        aimTargetDistance = distance;
        aimReach = reach;

        int id = target.GetInstanceID();
        if (id != aimTargetId)
        {
            aimTargetId = id;
            aimSameTimer = 0f;
            aimOutOfReachTimer = 0f;
            aimClosest = distance;
        }

        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
        aimSameTimer += dt;

        // Out of reach is fine while the mob is still walking toward us; only count time it isn't.
        if (distance <= reach || distance < aimClosest - 1f)
        {
            aimClosest = Mathf.Min(aimClosest, distance);
            aimOutOfReachTimer = 0f;
        }
        else
        {
            aimOutOfReachTimer += dt;
        }

        string why = null;
        if (aimOutOfReachTimer >= AimOutOfReachSeconds)
            why = "it stayed out of sword reach without coming closer for " + AimOutOfReachSeconds.ToString("0") + " s";
        else if (aimSameTimer >= AimSameTargetSeconds)
            why = "it did not die after " + AimSameTargetSeconds.ToString("0") + " s";

        if (why == null)
            return true;

        aimIgnored[id] = Time.unscaledTime + AimIgnoreSeconds;
        aimTargetId = 0;
        Log("aim", "Gave up on " + target.name + " at " + distance.ToString("0.0") + "m (reach " + reach.ToString("0.0") +
                   "m) because " + why + ". Camera released; ignoring it for " + AimIgnoreSeconds.ToString("0") + " s.");
        return false;
    }

    // ── Settings tab section ──

    private void DrawDiagnosticsSection()
    {
        Section("TROUBLESHOOTING LOG");
        bool before = cfgDebugLog.Value;
        bool after = ToggleRow("Diagnostic log",
            "Writes what the mod is doing to debug.log: starts and stops, waves, setting changes, slow frames, freezes " +
            "and errors. If something goes wrong, send debug.log (or debug-previous.log if you restarted the game since).",
            before);
        if (after != before)
        {
            cfgDebugLog.Value = after;
            Config.Save();
            if (diag != null)
            {
                diag.Enabled = after;
                diag.WriteAlways("log", after ? "Diagnostic log switched on." : "Diagnostic log switched off.");
            }
        }

        if (diag == null)
            return;

        GUILayout.Label(diag.CurrentPath + "   (" + (diag.SizeBytes / 1024f).ToString("N0") + " KB)", ui.SmallWrap);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("Open log folder", diag.Folder), ui.Button, GUILayout.Height(28)))
            OpenFolder(diag.Folder);

        GUI.enabled = diag.Enabled;
        if (GUILayout.Button(new GUIContent("Mark a problem now",
                    "Adds a line with the current state to the log, so it is easy to find what happened at this moment."),
                ui.Button, GUILayout.Height(28)))
        {
            Log("USER MARK", "Problem marked by the player. " + StateLine());
            Notify("Marked in debug.log.", NoticeLevel.Success);
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();
    }
}
