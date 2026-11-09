using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

[BepInPlugin("pramodexe.muckinatorgrind", "MuckinatorGrind", Version)]
public class MuckinatorGrindPlugin : BaseUnityPlugin
{
    public const string Version = "1.0.0";
    public const int MaxMobs = 1000;
    private const int SpawnsPerFrame = 40;

    public static MuckinatorGrindPlugin Instance { get; private set; }

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private const uint LEFT_DOWN = 0x0002;
    private const uint LEFT_UP = 0x0004;

    // ───────────────────────────── State ─────────────────────────────

    private enum RunState { Stopped, Running, Paused }
    private enum Tab { Spawn, Combat, Settings }
    private enum HotkeyTarget { None, StartPause, Menu }
    private enum NoticeLevel { Info, Success, Warning, Error }

    private static readonly string[] TabNames = { "Spawn", "Combat", "Settings" };

    // Only these mobs can be picked. Names must match MobType.name in the game (compared ignoring case/spaces).
    private static readonly string[] AllowedMobs = { "lil Dave", "Goblin", "Stone Golem" };
    private static readonly string[] AllowedMobLabels = { "Lil Dave", "Goblin", "Stone Golem" };
    private const string DefaultMob = "Goblin";

    private Harmony harmony;
    private RunState state = RunState.Stopped;

    private bool showGUI;
    private bool compact;
    private Tab activeTab;
    private HotkeyTarget capturing = HotkeyTarget.None;

    // Unique IMGUI window id ("MGRN"); a shared id with another mod's window makes both fail to draw.
    private const int WindowId = 0x4D47524E;
    private const float DefaultWindowWidth = 560f;
    private const float DefaultContentHeight = 340f;
    private const float MinWindowWidth = 480f, MaxWindowWidth = 1200f;
    private const float MinContentHeight = 150f, MaxContentHeight = 1000f;
    private const float GripSize = 16f;
    private float WindowWidth = DefaultWindowWidth;
    private float ContentHeight = DefaultContentHeight;
    private float windowOpacity = 1f;
    private bool resizingWindow;
    private Vector2 resizeStartMouse;
    private Vector2 resizeStartSize;
    private Rect windowRect = new Rect(40, 40, DefaultWindowWidth, 0);
    private Vector2 contentScroll;

    // ─────────────────────────── Settings ────────────────────────────

    private ConfigEntry<string> cfgMob;
    private ConfigEntry<int> cfgBatchSize;
    private ConfigEntry<int> cfgMaxActive;
    private ConfigEntry<float> cfgSpawnInterval;
    private ConfigEntry<float> cfgSpawnRadius;
    private ConfigEntry<bool> cfgUnlimitedHealth;
    private ConfigEntry<bool> cfgUnlimitedStamina;
    private ConfigEntry<bool> cfgAutoClick;
    private ConfigEntry<float> cfgClickInterval;
    private ConfigEntry<bool> cfgLimitEnabled;
    private ConfigEntry<int> cfgKillLimit;
    private ConfigEntry<string> cfgHotkey;
    private ConfigEntry<string> cfgMenuKey;
    private ConfigEntry<bool> cfgCloseOnStart;
    private ConfigEntry<bool> cfgShowHud;
    private ConfigEntry<float> cfgUiScale;
    private ConfigEntry<int> cfgTab;
    private ConfigEntry<float> cfgWindowX;
    private ConfigEntry<float> cfgWindowWidth;
    private ConfigEntry<float> cfgContentHeight;
    private ConfigEntry<float> cfgWindowOpacity;
    private ConfigEntry<float> cfgWindowY;
    private ConfigEntry<bool> cfgGodSword;
    private ConfigEntry<bool> cfgAutoAim;
    private ConfigEntry<bool> cfgTracker;

    private int selectedMobIndex = -1;
    private string selectedMobName = DefaultMob;
    private int batchSize;
    private int maxActiveMobs;
    private float spawnInterval;
    private float spawnRadius;
    private bool unlimitedHealth;
    private bool unlimitedStamina;
    private bool autoClick;
    private float clickInterval;
    private bool limitEnabled;
    private int killLimit;
    private bool closeOnStart;
    private bool showHud;
    private float uiScale;
    private float pendingScale;
    private bool godSwordEnabled;
    private bool autoAimEnabled;
    private bool trackerEnabled;

    // World tracking — sessions must not survive leaving a game.
    private bool wasInWorld;

    // God sword must be in slot 1 before a session starts spawning.
    private bool swordReady;
    private float swordWaitStarted;
    private const float SwordWaitTimeout = 10f;

    // Muckinator 1-4 tracker (Steam stat "Kills"). Goals are read from each achievement's
    // Steam description ("Kill N mobs"); the fallbacks below are used only if that fails.
    private const string KillStat = "Kills";
    private const int MuckinatorTiers = 4;
    private static readonly int[] MuckinatorFallbackGoals = { 100, 1000, 10000, 100000 };
    private readonly int[] muckinatorGoals = (int[])MuckinatorFallbackGoals.Clone();
    private readonly bool[] muckinatorTierUnlocked = new bool[MuckinatorTiers];
    private int steamKills = -1;
    private float nextStatRead;
    private float nextAchievementRead;
    private float killsPerMinute;
    private readonly Queue<KeyValuePair<float, int>> killSamples = new Queue<KeyValuePair<float, int>>();

    // Window dragging (handled manually — more reliable than GUI.DragWindow in Muck).
    private bool draggingWindow;
    private Vector2 dragOffset;

    // Spawning is spread over several frames so big waves don't freeze the game.
    private int spawnQueue;
    private int waveSpawned;

    // God sword / auto-attack
    private float nextSwordCheck;
    private Mob aimTarget;
    private bool aimActive;
    private float aimYaw;
    private float aimPitch;

    private Hotkey startKey;
    private Hotkey menuKey;

    // ─────────────────────────── Runtime ─────────────────────────────

    private float nextSpawnTime;
    private float nextClickTime;
    private float nextSaveCheck;
    private float nextNearbyRefresh;
    private int nearbyCount;

    private int sessionSpawned;
    private int sessionDefeated;
    private int totalSpawned;
    private int totalDefeated;

    private string notice;
    private NoticeLevel noticeLevel;
    private float noticeUntil;
    private float resetConfirmUntil;

    private readonly Dictionary<int, Mob> activeSpawned = new Dictionary<int, Mob>();
    private readonly Dictionary<string, string> fieldBuffers = new Dictionary<string, string>();

    private MobType[] cachedTypes;
    private string[] cachedNames;

    private static KeyCode[] allKeyCodes;
    private Ui ui;

    // ─────────────────────────── Presets ─────────────────────────────

    private struct Preset
    {
        public string Name;
        public string Hint;
        public int Batch;
        public int Max;
        public float Interval;
        public float Radius;
    }

    private static readonly Preset[] Presets =
    {
        new Preset { Name = "Trickle",  Hint = "5 mobs, slow waves",        Batch = 5,   Max = 10,  Interval = 8f,   Radius = 15f },
        new Preset { Name = "Standard", Hint = "25 mobs, default pacing",   Batch = 25,  Max = 50,  Interval = 5f,   Radius = 12f },
        new Preset { Name = "Horde",    Hint = "60 mobs, fast waves",       Batch = 60,  Max = 120, Interval = 3f,   Radius = 20f },
        new Preset { Name = "Swarm",    Hint = "250 mobs, fast waves",      Batch = 250, Max = 400, Interval = 2f,   Radius = 30f },
        new Preset { Name = "Maximum",  Hint = "1000 mobs, no waiting — heavy on performance", Batch = 1000, Max = 1000, Interval = 0.5f, Radius = 40f },
    };

    // ═════════════════════════ Lifecycle ═════════════════════════════

    private void Awake()
    {
        Instance = this;

        cfgMob = Config.Bind("Spawning", "Mob", DefaultMob,
            "Mob to spawn: Lil Dave, Goblin or Stone Golem.");
        cfgBatchSize = Config.Bind("Spawning", "BatchSize", 25);
        cfgMaxActive = Config.Bind("Spawning", "MaxActiveMobs", 50);
        cfgSpawnInterval = Config.Bind("Spawning", "SpawnInterval", 5f);
        cfgSpawnRadius = Config.Bind("Spawning", "SpawnRadius", 12f);

        cfgUnlimitedHealth = Config.Bind("Player", "UnlimitedHealth", true);
        cfgUnlimitedStamina = Config.Bind("Player", "UnlimitedStamina", true,
            "Keep your stamina full so you can sprint forever.");

        cfgAutoClick = Config.Bind("Automation", "AutoClick", false);
        cfgClickInterval = Config.Bind("Automation", "AutoClickInterval", 0.15f);
        cfgLimitEnabled = Config.Bind("Automation", "KillLimitEnabled", false);
        cfgKillLimit = Config.Bind("Automation", "KillLimit", 1000);

        cfgHotkey = Config.Bind("Hotkey", "StartPauseKey", "F8");
        cfgMenuKey = Config.Bind("Hotkey", "MenuKey", "F2");

        cfgCloseOnStart = Config.Bind("Interface", "CloseMenuOnStart", true);
        cfgShowHud = Config.Bind("Interface", "ShowHud", true);
        cfgUiScale = Config.Bind("Interface", "UiScale", 1f);
        cfgTab = Config.Bind("Interface", "LastTab", 0);
        cfgWindowX = Config.Bind("Interface", "WindowX", 40f);
        cfgWindowWidth = Config.Bind("Interface", "WindowWidth", DefaultWindowWidth,
            "Menu width. Drag the bottom-right corner of the menu to change it.");
        cfgContentHeight = Config.Bind("Interface", "WindowContentHeight", DefaultContentHeight,
            "Height of the menu's scrolling area. Drag the bottom-right corner of the menu to change it.");
        cfgWindowOpacity = Config.Bind("Interface", "WindowOpacity", 1f,
            "Menu opacity from 0.3 (very see-through) to 1 (solid).");
        cfgWindowY = Config.Bind("Interface", "WindowY", 40f);
        cfgGodSword = Config.Bind("Combat", "AutoGodSword", true,
            "Give yourself a God Sword in hotbar slot 1 and attack with it while a session runs.");
        cfgTracker = Config.Bind("Interface", "MuckinatorTracker", true,
            "Show live progress toward your next Muckinator achievement (1-4) on the HUD.");
        cfgAutoAim = Config.Bind("Combat", "AutoAim", true,
            "Turn the camera toward the nearest mob while the God Sword is attacking.");

        LoadFromConfig();

        harmony = new Harmony("pramodexe.muckinatorgrind");
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        Logger.LogInfo("MuckinatorGrind " + Version + " loaded.");
    }

    private void LoadFromConfig()
    {
        selectedMobName = AllowedMobName(cfgMob.Value);
        batchSize = Mathf.Clamp(cfgBatchSize.Value, 1, MaxMobs);
        maxActiveMobs = Mathf.Clamp(cfgMaxActive.Value, 1, MaxMobs);
        godSwordEnabled = cfgGodSword.Value;
        autoAimEnabled = cfgAutoAim.Value;
        trackerEnabled = cfgTracker.Value;
        spawnInterval = Mathf.Clamp(cfgSpawnInterval.Value, 0.5f, 120f);
        spawnRadius = Mathf.Clamp(cfgSpawnRadius.Value, 2f, 100f);
        unlimitedHealth = cfgUnlimitedHealth.Value;
        unlimitedStamina = cfgUnlimitedStamina.Value;
        autoClick = cfgAutoClick.Value;
        clickInterval = Mathf.Clamp(cfgClickInterval.Value, 0.05f, 1f);
        limitEnabled = cfgLimitEnabled.Value;
        killLimit = Mathf.Clamp(cfgKillLimit.Value, 1, 1000000);
        closeOnStart = cfgCloseOnStart.Value;
        showHud = cfgShowHud.Value;
        uiScale = Mathf.Clamp(cfgUiScale.Value, 0.75f, 2f);
        pendingScale = uiScale;
        activeTab = (Tab)Mathf.Clamp(cfgTab.Value, 0, TabNames.Length - 1);
        WindowWidth = Mathf.Clamp(cfgWindowWidth.Value, MinWindowWidth, MaxWindowWidth);
        ContentHeight = Mathf.Clamp(cfgContentHeight.Value, MinContentHeight, MaxContentHeight);
        windowOpacity = Mathf.Clamp(cfgWindowOpacity.Value, 0.3f, 1f);
        windowRect.x = cfgWindowX.Value;
        windowRect.y = cfgWindowY.Value;

        startKey = Hotkey.Parse(cfgHotkey.Value, KeyCode.F8);
        menuKey = Hotkey.Parse(cfgMenuKey.Value, KeyCode.F2);
    }

    private void Update()
    {
        if (capturing != HotkeyTarget.None)
        {
            CaptureHotkey();
        }
        else if (!IsTypingInField())
        {
            if (menuKey.IsPressed())
                SetMenuVisible(!showGUI);
            else if (startKey.IsPressed())
                ToggleRun();
        }

        bool inWorld = PlayerMovement.Instance != null;
        if (wasInWorld && !inWorld)
            OnLeftWorld();
        wasInWorld = inWorld;

        PruneSpawnedMobs();

        if (unlimitedStamina && PlayerStatus.Instance != null)
            PlayerStatus.Instance.stamina = PlayerStatus.Instance.maxStamina;

        if (trackerEnabled)
            UpdateMuckinator();

        if (Time.unscaledTime >= nextNearbyRefresh && (showGUI || showHud))
        {
            nearbyCount = CountNearbyMobs();
            nextNearbyRefresh = Time.unscaledTime + 0.5f;
        }

        if (Time.unscaledTime >= nextSaveCheck)
        {
            SaveSettings();
            nextSaveCheck = Time.unscaledTime + 1f;
        }

        // Auto-aim only lives for the frame in which GodSwordAttack() sets it.
        // Anything that skips the attack (stopped, paused, menu open, option off) releases the camera.
        aimActive = false;

        if (state != RunState.Running)
            return;

        if (limitEnabled && sessionDefeated >= killLimit)
        {
            Stop("Kill target of " + killLimit + " reached.", NoticeLevel.Success);
            return;
        }

        if (godSwordEnabled)
        {
            if (Time.unscaledTime >= nextSwordCheck)
            {
                bool ok = EnsureGodSwordEquipped();
                nextSwordCheck = Time.unscaledTime + (swordReady ? 1.5f : 0.5f);

                if (ok && !swordReady)
                {
                    swordReady = true;
                    nextSpawnTime = Time.time;
                    Notify("God Sword ready in slot 1 — spawning.", NoticeLevel.Success);
                }
            }

            // Hold the session until the sword is in slot 1.
            if (!swordReady)
            {
                if (Time.unscaledTime - swordWaitStarted > SwordWaitTimeout)
                {
                    state = RunState.Paused;
                    Notify("Paused: couldn't put a God Sword in slot 1. Check BepInEx/LogOutput.log.", NoticeLevel.Error);
                }
                return;
            }

            if (!showGUI)
                GodSwordAttack();
        }
        else if (autoClick && !showGUI && Time.time >= nextClickTime)
        {
            PerformMouseClick();
            nextClickTime = Time.time + clickInterval;
        }

        if (spawnQueue > 0)
            SpawnStep();
        else if (Time.time >= nextSpawnTime && activeSpawned.Count == 0)
            SpawnBatch();
    }

    private void OnDestroy()
    {
        state = RunState.Stopped;
        SaveSettings();

        if (harmony != null)
            harmony.UnpatchSelf();

        if (ui != null)
            ui.Dispose();
    }

    // ═════════════════════════ Run control ═══════════════════════════

    private void ToggleRun()
    {
        if (state == RunState.Running)
            Pause();
        else
            StartSession();
    }

    private void StartSession()
    {
        if (!IsHost())
        {
            Notify("You must be hosting a game to spawn mobs.", NoticeLevel.Error);
            return;
        }

        if (state == RunState.Stopped)
        {
            sessionSpawned = 0;
            sessionDefeated = 0;
        }

        bool resumed = state == RunState.Paused;
        state = RunState.Running;
        nextSpawnTime = Time.time;
        nextClickTime = Time.time;
        nextSwordCheck = 0f;
        swordReady = !godSwordEnabled;
        swordWaitStarted = Time.unscaledTime;

        if (godSwordEnabled)
        {
            swordReady = EnsureGodSwordEquipped();
            nextSwordCheck = Time.unscaledTime + (swordReady ? 1.5f : 0.5f);
        }

        if (showGUI && closeOnStart)
            SetMenuVisible(false);

        if (!swordReady)
        {
            Notify("Getting God Sword into slot 1 before spawning…", NoticeLevel.Warning);
            return;
        }

        Notify(resumed ? "Resumed." : "Started — " + SelectedMobName() + " waves incoming.",
            NoticeLevel.Success);
    }

    private void Pause()
    {
        if (state != RunState.Running)
            return;

        state = RunState.Paused;
        aimActive = false;
        Notify("Paused. Existing mobs stay alive; session stats kept.", NoticeLevel.Warning);
    }

    private void Stop(string message, NoticeLevel level)
    {
        if (state == RunState.Stopped)
            return;

        state = RunState.Stopped;
        aimActive = false;
        aimTarget = null;
        Notify(message, level);
    }

    private void SetMenuVisible(bool visible)
    {
        showGUI = visible;
        capturing = HotkeyTarget.None;

        // Only grab the cursor back when we're actually in a world.
        bool inWorld = PlayerMovement.Instance != null;

        if (visible)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        else if (inWorld)
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        GUIUtility.keyboardControl = 0;
    }

    private void OnLeftWorld()
    {
        if (state != RunState.Stopped)
        {
            state = RunState.Stopped;
            Notify("Left the world — session stopped.", NoticeLevel.Info);
        }

        activeSpawned.Clear();
        spawnQueue = 0;
        aimActive = false;
        aimTarget = null;
        swordReady = false;
        cachedTypes = null;
        cachedNames = null;
    }

    /// <summary>
    /// Muck keeps one ItemManager alive across scenes. When you return to the menu a duplicate is
    /// created (its Awake overwrites ItemManager.Instance) and then destroyed, so Instance points at a
    /// destroyed object that Unity reports as null — even though its item data is still valid and the
    /// game itself keeps using it. Accept it as long as the data is there.
    /// </summary>
    private static Dictionary<int, InventoryItem> ItemDatabase()
    {
        ItemManager im = ItemManager.Instance;
        if (!ReferenceEquals(im, null) && im.allItems != null && im.allItems.Count > 0)
            return im.allItems;

        ItemManager live = FindObjectOfType<ItemManager>();
        if (live != null && live.allItems != null && live.allItems.Count > 0)
            return live.allItems;

        return null;
    }

    private void UpdateMuckinator()
    {
        float now = Time.unscaledTime;
        if (now < nextStatRead)
            return;
        nextStatRead = now + 0.25f;

        try
        {
            if (!Steamworks.SteamClient.IsValid)
            {
                steamKills = -1;
                return;
            }

            int kills = Steamworks.SteamUserStats.GetStatInt(KillStat);
            steamKills = kills;

            killSamples.Enqueue(new KeyValuePair<float, int>(now, kills));
            while (killSamples.Count > 1 && now - killSamples.Peek().Key > 60f)
                killSamples.Dequeue();

            KeyValuePair<float, int> oldest = killSamples.Peek();
            float span = now - oldest.Key;
            killsPerMinute = span >= 5f ? Mathf.Max(0, kills - oldest.Value) * 60f / span : 0f;

            if (now >= nextAchievementRead)
            {
                nextAchievementRead = now + 10f;
                foreach (Steamworks.Data.Achievement a in Steamworks.SteamUserStats.Achievements)
                {
                    int tier = MuckinatorTierFromName(a.Name);
                    if (tier < 0)
                        continue;

                    muckinatorTierUnlocked[tier] = a.State;
                    int goal = FirstNumber(a.Description);
                    if (goal > 0)
                        muckinatorGoals[tier] = goal;
                }
            }

            for (int i = 0; i < MuckinatorTiers; i++)
                if (kills >= muckinatorGoals[i])
                    muckinatorTierUnlocked[i] = true;
        }
        catch (Exception ex)
        {
            steamKills = -1;
            nextStatRead = now + 5f;
            Logger.LogDebug("Steam stat read failed: " + ex.Message);
        }
    }

    // "Muckinator 3" -> 2, "Muckinator" / "Muckinator 1" / "Muckinator I" -> 0; anything else -> -1.
    private static int MuckinatorTierFromName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return -1;
        string n = name.Trim();
        if (!n.StartsWith("Muckinator", StringComparison.OrdinalIgnoreCase))
            return -1;

        string rest = n.Substring("Muckinator".Length).Trim().ToUpperInvariant();
        switch (rest)
        {
            case "": case "1": case "I": return 0;
            case "2": case "II": return 1;
            case "3": case "III": return 2;
            case "4": case "IV": return 3;
            default: return -1;
        }
    }

    // First whole number in a string, ignoring thousands separators ("Kill 10,000 mobs" -> 10000).
    private static int FirstNumber(string text)
    {
        if (string.IsNullOrEmpty(text))
            return -1;

        long value = 0;
        bool found = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= '0' && c <= '9')
            {
                found = true;
                value = value * 10 + (c - '0');
                if (value > int.MaxValue) return -1;
            }
            else if (found && (c == ',' || c == '.' || c == ' ') && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                // thousands separator inside the number
            }
            else if (found)
            {
                break;
            }
        }
        return found ? (int)value : -1;
    }

    // Index of the next Muckinator to work toward, or -1 when all four are done.
    private int CurrentMuckinatorTier()
    {
        int best = -1;
        for (int i = 0; i < MuckinatorTiers; i++)
        {
            if (muckinatorTierUnlocked[i] || steamKills >= muckinatorGoals[i])
                continue;
            if (best < 0 || muckinatorGoals[i] < muckinatorGoals[best])
                best = i;
        }
        return best;
    }

    private static string FormatDuration(float minutes)
    {
        if (minutes <= 0f || float.IsInfinity(minutes) || float.IsNaN(minutes))
            return "—";

        TimeSpan t = TimeSpan.FromMinutes(minutes);
        if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h";
        if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
        return Mathf.Max(1, t.Minutes) + "m";
    }

    private void DrawMuckinator(bool compactLayout)
    {
        if (!trackerEnabled)
            return;

        GUILayout.Space(compactLayout ? 4 : 6);

        int tier = steamKills < 0 ? -1 : CurrentMuckinatorTier();
        bool allDone = steamKills >= 0 && tier < 0;
        int shown = allDone ? MuckinatorTiers - 1 : Mathf.Max(0, tier);
        int goal = muckinatorGoals[shown];

        int unlockedCount = 0;
        for (int i = 0; i < MuckinatorTiers; i++)
            if (muckinatorTierUnlocked[i]) unlockedCount++;

        GUILayout.BeginHorizontal();
        GUILayout.Label(allDone ? "MUCKINATOR — ALL DONE" : "MUCKINATOR " + (shown + 1), ui.Section);
        GUILayout.FlexibleSpace();
        if (allDone)
            GUILayout.Label("4 / 4 UNLOCKED", ui.TextGreen, GUILayout.ExpandWidth(false));
        else if (steamKills >= 0)
            GUILayout.Label(unlockedCount + " / 4 unlocked", ui.HintSmall, GUILayout.ExpandWidth(false));
        GUILayout.EndHorizontal();

        if (steamKills < 0)
        {
            GUILayout.Label("Steam stats unavailable.", ui.HintSmall);
            return;
        }

        float t = Mathf.Clamp01((float)steamKills / goal);
        ProgressBar(t, steamKills.ToString("N0") + " / " + goal.ToString("N0") +
                       "  (" + (t * 100f).ToString("0.0") + "%)", compactLayout ? 18f : 22f);

        int remaining = Mathf.Max(0, goal - steamKills);
        string rate = killsPerMinute > 0.5f ? killsPerMinute.ToString("N0") + " kills/min" : "no recent kills";
        string eta = remaining == 0 ? "done!" :
            (killsPerMinute > 0.5f ? "ETA " + FormatDuration(remaining / killsPerMinute) : remaining.ToString("N0") + " to go");
        GUILayout.Label(rate + "   ·   " + eta, ui.HintSmall);
    }

    private void Notify(string message, NoticeLevel level)
    {
        notice = message;
        noticeLevel = level;
        noticeUntil = Time.unscaledTime + 4f;

        if (level == NoticeLevel.Error)
            Logger.LogWarning(message);
        else
            Logger.LogInfo(message);
    }

    private void PerformMouseClick()
    {
        try
        {
            mouse_event(LEFT_DOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(LEFT_UP, 0, 0, 0, UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.LogError("Automatic mouse click failed: " + ex);
            autoClick = false;
            Notify("Auto-click isn't supported here and was disabled.", NoticeLevel.Error);
        }
    }

    private bool IsHost()
    {
        return LocalClient.instance != null && LocalClient.serverOwner;
    }

    private bool IsTypingInField()
    {
        return showGUI && GUIUtility.keyboardControl != 0;
    }

    // ═════════════════════════ Hotkeys ═══════════════════════════════

    private void CaptureHotkey()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            capturing = HotkeyTarget.None;
            Notify("Hotkey change cancelled.", NoticeLevel.Info);
            return;
        }

        if (allKeyCodes == null)
            allKeyCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode));

        foreach (KeyCode key in allKeyCodes)
        {
            if (key == KeyCode.None || key == KeyCode.Mouse0 || key == KeyCode.Mouse1 ||
                Hotkey.IsModifier(key) || !Input.GetKeyDown(key))
                continue;

            Hotkey hk = Hotkey.FromCurrentModifiers(key);

            if (capturing == HotkeyTarget.StartPause)
            {
                if (hk.Equals(menuKey)) { Notify(hk + " is already the menu key.", NoticeLevel.Error); return; }
                startKey = hk;
                cfgHotkey.Value = hk.ToString();
            }
            else
            {
                if (hk.Equals(startKey)) { Notify(hk + " is already the start/pause key.", NoticeLevel.Error); return; }
                menuKey = hk;
                cfgMenuKey.Value = hk.ToString();
            }

            Config.Save();
            capturing = HotkeyTarget.None;
            Notify("Hotkey set to " + hk + ".", NoticeLevel.Success);
            return;
        }
    }

    // ═════════════════════════ Persistence ═══════════════════════════

    private void SaveSettings()
    {
        bool changed = false;
        changed |= Set(cfgMob, selectedMobName);
        changed |= Set(cfgBatchSize, batchSize);
        changed |= Set(cfgMaxActive, maxActiveMobs);
        changed |= Set(cfgSpawnInterval, spawnInterval);
        changed |= Set(cfgSpawnRadius, spawnRadius);
        changed |= Set(cfgUnlimitedHealth, unlimitedHealth);
        changed |= Set(cfgUnlimitedStamina, unlimitedStamina);
        changed |= Set(cfgAutoClick, autoClick);
        changed |= Set(cfgClickInterval, clickInterval);
        changed |= Set(cfgLimitEnabled, limitEnabled);
        changed |= Set(cfgKillLimit, killLimit);
        changed |= Set(cfgCloseOnStart, closeOnStart);
        changed |= Set(cfgShowHud, showHud);
        changed |= Set(cfgUiScale, uiScale);
        changed |= Set(cfgTab, (int)activeTab);
        changed |= Set(cfgWindowX, Mathf.Round(windowRect.x));
        if (!resizingWindow)
        {
            changed |= Set(cfgWindowWidth, Mathf.Round(WindowWidth));
            changed |= Set(cfgContentHeight, Mathf.Round(ContentHeight));
        }
        changed |= Set(cfgWindowOpacity, Mathf.Round(windowOpacity * 100f) / 100f);
        changed |= Set(cfgWindowY, Mathf.Round(windowRect.y));
        changed |= Set(cfgGodSword, godSwordEnabled);
        changed |= Set(cfgAutoAim, autoAimEnabled);
        changed |= Set(cfgTracker, trackerEnabled);

        if (changed)
            Config.Save();
    }

    private static bool Set<T>(ConfigEntry<T> entry, T value)
    {
        if (EqualityComparer<T>.Default.Equals(entry.Value, value))
            return false;
        entry.Value = value;
        return true;
    }

    private void ResetToDefaults()
    {
        foreach (ConfigEntryBase entry in new ConfigEntryBase[]
        {
            cfgMob, cfgBatchSize, cfgMaxActive, cfgSpawnInterval, cfgSpawnRadius,
            cfgUnlimitedHealth, cfgUnlimitedStamina, cfgAutoClick, cfgClickInterval, cfgLimitEnabled, cfgKillLimit,
            cfgHotkey, cfgMenuKey, cfgCloseOnStart, cfgShowHud, cfgUiScale, cfgTab,
            cfgWindowX, cfgWindowY, cfgWindowWidth, cfgContentHeight, cfgWindowOpacity, cfgGodSword, cfgAutoAim, cfgTracker,
        })
        {
            entry.BoxedValue = entry.DefaultValue;
        }

        Config.Save();
        LoadFromConfig();
        fieldBuffers.Clear();
        windowRect.height = 0;
        Notify("All settings restored to defaults.", NoticeLevel.Success);
    }

    // ═════════════════════════ Mob data ══════════════════════════════

    private MobType[] GetMobTypes()
    {
        if (MobSpawner.Instance == null)
            return null;

        return MobSpawner.Instance.allMobs;
    }

    private string[] GetMobNames()
    {
        MobType[] types = GetMobTypes();

        if (types == null)
            return null;

        if (!ReferenceEquals(types, cachedTypes) || cachedNames == null || cachedNames.Length != types.Length)
        {
            cachedTypes = types;
            cachedNames = new string[types.Length];

            for (int i = 0; i < types.Length; i++)
                cachedNames[i] = types[i] != null ? types[i].name : "Unknown #" + i;
        }

        return cachedNames;
    }

    private static string NormalizeMobName(string name)
    {
        return (name ?? "").Replace(" ", "").Trim().ToLowerInvariant();
    }

    // Returns the matching allowed mob name, or the default if the value isn't allowed.
    private static string AllowedMobName(string value)
    {
        string key = NormalizeMobName(value);
        foreach (string m in AllowedMobs)
            if (NormalizeMobName(m) == key)
                return m;
        return DefaultMob;
    }

    private static string MobLabel(string name)
    {
        for (int i = 0; i < AllowedMobs.Length; i++)
            if (AllowedMobs[i] == name)
                return AllowedMobLabels[i];
        return name;
    }

    // Spawn index of a mob in the game's mob list, or -1 if it isn't loaded.
    private int FindMobIndex(string name)
    {
        string[] names = GetMobNames();
        if (names == null)
            return -1;

        string key = NormalizeMobName(name);
        for (int i = 0; i < names.Length; i++)
            if (NormalizeMobName(names[i]) == key)
                return i;
        return -1;
    }

    private string SelectedMobName()
    {
        return MobLabel(selectedMobName);
    }

    // ═════════════════════════ Spawning ══════════════════════════════

    private void SpawnBatch()
    {
        if (!IsHost())
        {
            Stop("Stopped — you are no longer the host.", NoticeLevel.Error);
            return;
        }

        MobType[] types = GetMobTypes();
        selectedMobIndex = FindMobIndex(selectedMobName);

        if (types == null || selectedMobIndex < 0 || selectedMobIndex >= types.Length ||
            types[selectedMobIndex] == null)
        {
            Pause();
            Notify("Pick a valid mob in the Spawn tab.", NoticeLevel.Error);
            return;
        }

        if (MobManager.Instance == null || MobSpawner.Instance == null || PlayerMovement.Instance == null)
        {
            nextSpawnTime = Time.time + 2f;
            return;
        }

        int amount = Mathf.Min(batchSize, maxActiveMobs - activeSpawned.Count);

        if (amount <= 0)
        {
            nextSpawnTime = Time.time + spawnInterval;
            return;
        }

        spawnQueue = amount;
        waveSpawned = 0;
        SpawnStep();
    }

    private void SpawnStep()
    {
        if (MobManager.Instance == null || MobSpawner.Instance == null || PlayerMovement.Instance == null || !IsHost())
        {
            spawnQueue = 0;
            return;
        }

        Transform player = PlayerMovement.Instance.transform;
        int thisFrame = Mathf.Min(spawnQueue, SpawnsPerFrame);
        spawnQueue -= thisFrame;

        for (int i = 0; i < thisFrame; i++)
        {
            Vector2 offset = UnityEngine.Random.insideUnitCircle * spawnRadius;
            Vector3 position = player.position + new Vector3(offset.x, 0f, offset.y);

            RaycastHit hit;
            if (Physics.Raycast(position + Vector3.up * 100f, Vector3.down, out hit, 250f))
                position = hit.point + Vector3.up;

            int id = MobManager.Instance.GetNextId();

            try
            {
                MobSpawner.Instance.ServerSpawnNewMob(
                    id, selectedMobIndex, position, 1f, 1f, Mob.BossType.None, -1);

                Mob mob;
                if (MobManager.Instance.mobs.TryGetValue(id, out mob) && mob != null)
                {
                    activeSpawned[mob.GetInstanceID()] = mob;
                    totalSpawned++;
                    sessionSpawned++;
                    waveSpawned++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Mob spawn failed: " + ex);
            }
        }

        if (spawnQueue == 0)
        {
            Logger.LogInfo("Spawned wave of " + waveSpawned + " mobs.");
            nextSpawnTime = Time.time + spawnInterval;
        }
    }

    private readonly List<int> pruneBuffer = new List<int>();

    private void PruneSpawnedMobs()
    {
        pruneBuffer.Clear();

        foreach (KeyValuePair<int, Mob> entry in activeSpawned)
        {
            if (entry.Value == null || !entry.Value.gameObject.activeInHierarchy)
                pruneBuffer.Add(entry.Key);
        }

        foreach (int id in pruneBuffer)
            activeSpawned.Remove(id);
    }

    public void HandleMobDeath(HitableMob hitable)
    {
        if (hitable == null || hitable.mob == null)
            return;

        if (activeSpawned.Remove(hitable.mob.GetInstanceID()))
        {
            totalDefeated++;
            sessionDefeated++;

            if (limitEnabled && sessionDefeated >= killLimit && state == RunState.Running)
                Stop("Kill target of " + killLimit + " reached.", NoticeLevel.Success);
        }
    }

    private int CountNearbyMobs()
    {
        if (PlayerMovement.Instance == null || MobManager.Instance == null || MobManager.Instance.mobs == null)
            return 0;

        int count = 0;
        Vector3 playerPosition = PlayerMovement.Instance.transform.position;
        float rangeSq = spawnRadius * spawnRadius;

        foreach (Mob mob in MobManager.Instance.mobs.Values)
        {
            if (mob != null && (mob.transform.position - playerPosition).sqrMagnitude <= rangeSq)
                count++;
        }

        return count;
    }

    public bool ShouldBlockPlayerDamage()
    {
        return unlimitedHealth;
    }

    public bool ShouldBlockGameClicks()
    {
        return showGUI;
    }

    // ═════════════════════════ God sword ═════════════════════════════

    private const string GodSwordName = "God Sword";
    private const int GodSwordBaseItemId = 142; // same base item Muck God Powers uses

    private static MethodInfo godPowersGetWeapon;
    private static bool godPowersSearched;

    private static bool GodPowersAvailable()
    {
        if (!godPowersSearched)
        {
            godPowersSearched = true;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType("MuckGodPowers2.Abilitys.SuperSword", false);
                if (t != null)
                {
                    godPowersGetWeapon = t.GetMethod("GetWeapon", BindingFlags.Public | BindingFlags.Static);
                    break;
                }
            }
        }

        return godPowersGetWeapon != null;
    }

    private static bool IsGodSword(InventoryItem item)
    {
        return item != null && item.name == GodSwordName;
    }

    private InventoryCell[] HotbarCells()
    {
        InventoryUI inv = InventoryUI.Instance;
        if (inv == null || inv.hotkeysTransform == null)
            return null;
        return inv.hotkeysTransform.GetComponentsInChildren<InventoryCell>(true);
    }

    private InventoryCell FindGodSwordCell()
    {
        InventoryUI inv = InventoryUI.Instance;
        if (inv == null || inv.cells == null)
            return null;

        foreach (InventoryCell cell in inv.cells)
        {
            if (cell != null && IsGodSword(cell.currentItem))
                return cell;
        }
        return null;
    }

    private InventoryItem CreateGodSword()
    {
        Dictionary<int, InventoryItem> items = ItemDatabase();
        if (items == null)
        {
            Logger.LogWarning("God Sword: item database not found.");
            return null;
        }

        // Preferred: let Muck God Powers build its real God Sword (custom model + stats).
        if (GodPowersAvailable())
        {
            try
            {
                godPowersGetWeapon.Invoke(null, null);
                InventoryCell given = FindGodSwordCell();
                if (given != null)
                {
                    InventoryItem item = given.currentItem;
                    given.currentItem = null;
                    given.UpdateCell();
                    return item;
                }

                // Inventory was full, but GetWeapon still upgraded the base item definition.
                InventoryItem baseItem;
                if (items.TryGetValue(GodSwordBaseItemId, out baseItem) && IsGodSword(baseItem))
                {
                    InventoryItem copy = ScriptableObject.CreateInstance<InventoryItem>();
                    copy.Copy(baseItem, 1);
                    return copy;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Muck God Powers GetWeapon failed, using fallback sword: " + ex.Message);
            }
        }

        // Fallback: a copy of the base sword with God Sword stats (looks like the normal sword).
        InventoryItem template;
        if (!items.TryGetValue(GodSwordBaseItemId, out template) || template == null)
        {
            Logger.LogWarning("God Sword: base item " + GodSwordBaseItemId + " missing from item database.");
            return null;
        }

        InventoryItem sword = ScriptableObject.CreateInstance<InventoryItem>();
        sword.Copy(template, 1);
        sword.name = GodSwordName;
        sword.description = "The most powerful sword in Muck.";
        sword.attackDamage = 500;
        sword.attackRange = 15f;
        sword.attackSpeed = 10f;
        return sword;
    }

    /// <summary>Makes sure hotbar slot 1 holds a God Sword and that slot is selected.</summary>
    private bool EnsureGodSwordEquipped()
    {
        InventoryCell[] hotbar = HotbarCells();
        if (hotbar == null || hotbar.Length == 0 || Hotbar.Instance == null || PlayerMovement.Instance == null)
            return false;

        InventoryCell slot1 = hotbar[0];
        bool changed = false;

        if (!IsGodSword(slot1.currentItem))
        {
            InventoryCell existing = FindGodSwordCell();
            InventoryItem sword;

            if (existing != null)
            {
                sword = existing.currentItem;
                existing.currentItem = null;
                existing.UpdateCell();
            }
            else
            {
                sword = CreateGodSword();
            }

            if (sword == null)
            {
                Notify("Couldn't create a God Sword — retrying…", NoticeLevel.Error);
                return false;
            }

            if (slot1.currentItem != null)
            {
                InventoryItem old = slot1.currentItem;
                if (InventoryUI.Instance != null)
                    InventoryUI.Instance.DropItemIntoWorld(old);
                Notify("Dropped " + old.name + " (x" + old.amount + ") to make room for the God Sword.", NoticeLevel.Warning);
            }

            slot1.currentItem = sword;
            slot1.UpdateCell();
            changed = true;
        }

        Traverse active = Traverse.Create(Hotbar.Instance).Field("currentActive");
        if (changed || active.GetValue<int>() != 0)
        {
            active.SetValue(0);
            Hotbar.Instance.UpdateHotbar();
        }

        if (changed)
            Logger.LogInfo("God Sword equipped in slot 1.");

        return IsGodSword(hotbar[0].currentItem);
    }

    private Mob FindNearestMob(Vector3 from, float maxDistance)
    {
        if (MobManager.Instance == null || MobManager.Instance.mobs == null)
            return null;

        Mob best = null;
        float bestSq = maxDistance * maxDistance;

        foreach (Mob mob in MobManager.Instance.mobs.Values)
        {
            if (mob == null || !mob.gameObject.activeInHierarchy)
                continue;

            float sq = (mob.transform.position - from).sqrMagnitude;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = mob;
            }
        }

        return best;
    }

    private void GodSwordAttack()
    {
        aimActive = false;

        if (PlayerMovement.Instance == null || UseInventory.Instance == null || Hotbar.Instance == null ||
            !IsGodSword(Hotbar.Instance.currentItem) || PlayerStatus.Instance == null || PlayerStatus.Instance.IsPlayerDead())
            return;

        Camera cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : PlayerMovement.Instance.transform.position;

        float reach = 1.2f + Hotbar.Instance.currentItem.attackRange + 3f;
        aimTarget = FindNearestMob(eye, Mathf.Max(60f, spawnRadius * 2f));
        if (aimTarget == null)
            return;

        Vector3 targetPos = aimTarget.transform.position + Vector3.up * 1.2f;
        Vector3 dir = targetPos - eye;

        if (autoAimEnabled && dir.sqrMagnitude > 0.01f)
        {
            Quaternion look = Quaternion.LookRotation(dir);
            Vector3 euler = look.eulerAngles;
            aimYaw = euler.y;
            aimPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), -89f, 89f);
            aimActive = true;
        }

        if (dir.magnitude <= reach)
            UseInventory.Instance.Use();
    }

    /// <summary>Called after PlayerInput.Look to steer the camera toward the current target.</summary>
    public void ApplyAim(PlayerInput input)
    {
        if (!aimActive || state != RunState.Running || !godSwordEnabled || showGUI || input == null || Cursor.lockState == CursorLockMode.None || OtherInput.lockCamera)
            return;

        Traverse t = Traverse.Create(input);
        float yaw = t.Field("desiredX").GetValue<float>();
        float pitch = t.Field("xRotation").GetValue<float>();

        float step = 900f * Time.deltaTime;
        yaw = Mathf.MoveTowardsAngle(yaw, aimYaw, step);
        pitch = Mathf.MoveTowards(pitch, aimPitch, step);

        t.Field("desiredX").SetValue(yaw);
        t.Field("xRotation").SetValue(pitch);
        input.cameraRot = new Vector3(pitch, yaw, input.cameraRot.z);

        Transform orientation = t.Field("orientation").GetValue<Transform>();
        if (orientation != null)
            orientation.localRotation = Quaternion.Euler(0f, yaw, 0f);
    }

    private string WaveStatus()
    {
        switch (state)
        {
            case RunState.Stopped:
                return "Idle — press " + startKey + " or Start to begin.";
            case RunState.Paused:
                return "Paused — " + activeSpawned.Count + " spawned mobs still alive.";
        }

        if (!IsHost())
            return "Waiting for host…";

        if (godSwordEnabled && !swordReady)
            return "Equipping God Sword in slot 1…";

        if (spawnQueue > 0)
            return "Spawning wave… " + spawnQueue + " to go.";

        if (activeSpawned.Count > 0)
            return "Wave in progress — " + activeSpawned.Count + " left to defeat.";

        float wait = nextSpawnTime - Time.time;
        return wait > 0.05f ? "Next wave in " + wait.ToString("0.0") + "s" : "Spawning next wave…";
    }

    // ═════════════════════════ GUI root ══════════════════════════════

    private void OnGUI()
    {
        bool hudVisible = !showGUI && showHud &&
            (state != RunState.Stopped || NoticeActive() || (trackerEnabled && PlayerMovement.Instance != null));

        if (!showGUI && !hudVisible)
            return;

        if (ui == null)
            ui = new Ui();
        ui.EnsureBuilt();

        GUISkin previousSkin = GUI.skin;
        Matrix4x4 previousMatrix = GUI.matrix;

        GUI.skin = ui.Skin;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

        if (showGUI)
        {
            if (draggingWindow)
            {
                if (Input.GetMouseButton(0))
                {
                    Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / uiScale;
                    windowRect.x = mouse.x - dragOffset.x;
                    windowRect.y = mouse.y - dragOffset.y;
                }
                else
                {
                    draggingWindow = false;
                }
            }

            if (resizingWindow)
            {
                if (Input.GetMouseButton(0))
                {
                    Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / uiScale;
                    Vector2 delta = mouse - resizeStartMouse;
                    WindowWidth = Mathf.Clamp(Mathf.Round(resizeStartSize.x + delta.x), MinWindowWidth, MaxWindowWidth);
                    ContentHeight = Mathf.Clamp(Mathf.Round(resizeStartSize.y + delta.y), MinContentHeight, MaxContentHeight);
                    windowRect.height = 0;
                }
                else
                {
                    resizingWindow = false;
                }
            }

            Color previousColor = GUI.color;
            GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b, windowOpacity);
            windowRect.width = WindowWidth;
            windowRect = GUILayout.Window(WindowId, windowRect, DrawWindow, GUIContent.none, ui.Window,
                GUILayout.Width(WindowWidth));
            GUI.color = previousColor;

            float screenW = Screen.width / uiScale;
            float screenH = Screen.height / uiScale;
            windowRect.x = Mathf.Clamp(windowRect.x, 0, Mathf.Max(0, screenW - windowRect.width));
            windowRect.y = Mathf.Clamp(windowRect.y, 0, Mathf.Max(0, screenH - 40));
        }
        else
        {
            DrawHud();
        }

        GUI.matrix = previousMatrix;
        GUI.skin = previousSkin;
    }

    private bool NoticeActive()
    {
        return !string.IsNullOrEmpty(notice) && Time.unscaledTime < noticeUntil;
    }

    private void DrawWindow(int id)
    {
        GUI.color = new Color(1f, 1f, 1f, windowOpacity);

        // Enter / Escape commits a text field.
        Event e = Event.current;
        if (e.type == EventType.KeyDown && GUIUtility.keyboardControl != 0 &&
            (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.keyCode == KeyCode.Escape))
        {
            GUI.FocusControl(null);
            e.Use();
        }

        Rect headerRect = DrawHeader();

        GUILayout.Space(10);
        DrawControlBar();

        if (!compact)
        {
            GUILayout.Space(10);
            DrawTabs();
            GUILayout.Space(6);

            contentScroll = GUILayout.BeginScrollView(contentScroll, GUILayout.Height(ContentHeight));
            switch (activeTab)
            {
                case Tab.Spawn: DrawMobsTab(); DrawSpawningTab(); DrawKillTargetSection(); DrawStatsSection(); break;
                case Tab.Combat: DrawCombatTab(); break;
                case Tab.Settings: DrawSettingsTab(); break;
            }
            GUILayout.EndScrollView();
        }

        GUILayout.Space(8);
        DrawFooter();

        // Resize grip in the bottom-right corner.
        Rect grip = new Rect(windowRect.width - GripSize - 2, windowRect.height - GripSize - 2, GripSize, GripSize);
        if (e.type == EventType.Repaint)
        {
            for (int row = 0; row < 3; row++)
                for (int col = 2 - row; col < 3; col++)
                    Ui.DrawRect(new Rect(grip.x + 3 + col * 4, grip.y + 3 + row * 4, 2, 2),
                        resizingWindow || grip.Contains(e.mousePosition) ? ui.AccentTex : ui.BorderTex);
        }
        if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition))
        {
            resizingWindow = true;
            resizeStartMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / uiScale;
            resizeStartSize = new Vector2(WindowWidth, ContentHeight);
            GUI.FocusControl(null);
            e.Use();
        }

        // Start dragging when the title bar is grabbed (buttons in it already consumed their clicks).
        if (e.type == EventType.MouseDown && e.button == 0 && headerRect.Contains(e.mousePosition))
        {
            draggingWindow = true;
            dragOffset = e.mousePosition;
            GUI.FocusControl(null);
            e.Use();
        }

        // Clicking empty space ends text editing.
        if (e.type == EventType.MouseDown)
            GUI.FocusControl(null);
    }

    // ─────────────────────────── Header ──────────────────────────────

    private Rect DrawHeader()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("MUCKINATOR GRIND", ui.Title);
        GUILayout.Label("v" + Version, ui.Muted, GUILayout.ExpandWidth(false));
        GUILayout.FlexibleSpace();
        DrawStatePill();
        GUILayout.Space(8);

        if (GUILayout.Button(new GUIContent(compact ? "+" : "–", compact ? "Expand" : "Collapse to controls only"),
                ui.IconButton, GUILayout.Width(26), GUILayout.Height(24)))
        {
            compact = !compact;
            windowRect.height = 0;
        }

        if (GUILayout.Button(new GUIContent("×", "Close menu (" + menuKey + ")"), ui.IconButtonDanger,
                GUILayout.Width(26), GUILayout.Height(24)))
        {
            SetMenuVisible(false);
        }

        GUILayout.EndHorizontal();

        Rect r = GUILayoutUtility.GetLastRect();
        Ui.DrawRect(new Rect(r.x - 12, r.yMax + 6, r.width + 24, 1), ui.BorderTex);
        return new Rect(0, 0, 10000, r.yMax + 6);
    }

    private void DrawStatePill()
    {
        switch (state)
        {
            case RunState.Running: GUILayout.Label("● RUNNING", ui.PillGreen); break;
            case RunState.Paused: GUILayout.Label("● PAUSED", ui.PillAmber); break;
            default: GUILayout.Label("■ STOPPED", ui.PillGray); break;
        }
    }

    // ───────────────────────── Control bar ───────────────────────────

    private void DrawControlBar()
    {
        if (!IsHost())
            Banner("Not hosting — spawning only works when you are the lobby host.", ui.BannerError);

        GUILayout.BeginHorizontal();

        string primaryText;
        string primaryTip;
        GUIStyle primaryStyle;

        switch (state)
        {
            case RunState.Running:
                primaryText = "PAUSE"; primaryTip = "Pause spawning and auto-click. Mobs and stats are kept.";
                primaryStyle = ui.ButtonWarning; break;
            case RunState.Paused:
                primaryText = "RESUME"; primaryTip = "Continue the current session.";
                primaryStyle = ui.ButtonPrimary; break;
            default:
                primaryText = "START"; primaryTip = "Start a new session (resets session stats).";
                primaryStyle = ui.ButtonPrimary; break;
        }

        GUI.enabled = IsHost() || state == RunState.Running;
        if (GUILayout.Button(new GUIContent(primaryText + "   [" + startKey + "]", primaryTip), primaryStyle,
                GUILayout.Height(38), GUILayout.ExpandWidth(true)))
        {
            ToggleRun();
        }

        GUI.enabled = state != RunState.Stopped;
        if (GUILayout.Button(new GUIContent("STOP", "End the session. Next start resets session stats."),
                ui.ButtonDanger, GUILayout.Height(38), GUILayout.Width(110)))
        {
            Stop("Stopped.", NoticeLevel.Info);
        }
        GUI.enabled = true;

        GUILayout.EndHorizontal();

        GUILayout.Space(8);

        GUILayout.BeginHorizontal();
        StatTile("ALIVE", activeSpawned.Count + " / " + maxActiveMobs, "Plugin-spawned mobs currently alive");
        StatTile("DEFEATED", sessionDefeated.ToString("N0"), "Defeated this session (all-time: " + totalDefeated.ToString("N0") + ")");
        StatTile("SPAWNED", sessionSpawned.ToString("N0"), "Spawned this session (all-time: " + totalSpawned.ToString("N0") + ")");
        StatTile("NEARBY", nearbyCount.ToString(), "All mobs within the spawn radius, including natural ones");
        GUILayout.EndHorizontal();

        if (limitEnabled)
        {
            GUILayout.Space(6);
            float t = killLimit > 0 ? Mathf.Clamp01((float)sessionDefeated / killLimit) : 0f;
            ProgressBar(t, "Kill target  " + sessionDefeated.ToString("N0") + " / " + killLimit.ToString("N0") +
                "   (" + Mathf.Max(0, killLimit - sessionDefeated).ToString("N0") + " to go)");
        }

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label(WaveStatus(), ui.Muted);
        GUILayout.FlexibleSpace();
        GUILayout.Label(SelectedMobName(), ui.Accent, GUILayout.ExpandWidth(false));
        GUILayout.EndHorizontal();

        DrawMuckinator(false);
    }

    private void StatTile(string caption, string value, string tooltip)
    {
        GUILayout.BeginVertical(new GUIContent("", tooltip), ui.Card, GUILayout.ExpandWidth(true));
        GUILayout.Label(value, ui.StatValue);
        GUILayout.Label(caption, ui.StatCaption);
        GUILayout.EndVertical();
    }

    // ─────────────────────────── Tabs ────────────────────────────────

    private void DrawTabs()
    {
        GUILayout.BeginHorizontal();
        for (int i = 0; i < TabNames.Length; i++)
        {
            bool active = (int)activeTab == i;
            if (GUILayout.Button(TabNames[i], active ? ui.TabActive : ui.Tab, GUILayout.Height(30)))
            {
                if (!active)
                {
                    activeTab = (Tab)i;
                    contentScroll = Vector2.zero;
                    GUI.FocusControl(null);
                }
            }
        }
        GUILayout.EndHorizontal();
        Rect r = GUILayoutUtility.GetLastRect();
        Ui.DrawRect(new Rect(r.x, r.yMax, r.width, 2), ui.AccentTex);
    }

    // ── Mobs ──

    private void DrawMobsTab()
    {
        Section("MOB TO SPAWN");
        GUILayout.Label("Every kill counts toward Muckinator.", ui.Hint);
        GUILayout.Space(6);

        bool loaded = GetMobNames() != null;
        GUILayout.BeginHorizontal();
        for (int i = 0; i < AllowedMobs.Length; i++)
        {
            string name = AllowedMobs[i];
            bool selected = name == selectedMobName;
            bool available = !loaded || FindMobIndex(name) >= 0;

            GUI.enabled = available;
            if (GUILayout.Button(new GUIContent(AllowedMobLabels[i], available ? "Spawn " + AllowedMobLabels[i] : "Not found in this game"),
                    selected ? ui.MobSelected : ui.MobButton,
                    GUILayout.Height(40), GUILayout.Width((WindowWidth - 60) / AllowedMobs.Length)))
            {
                selectedMobName = name;
                Notify("Selected " + AllowedMobLabels[i] + ".", NoticeLevel.Info);
            }
            GUI.enabled = true;
        }
        GUILayout.EndHorizontal();

        if (!loaded)
            GUILayout.Label("Load into a world to start spawning.", ui.HintSmall);
    }

    // ── Spawning ──

    private void DrawSpawningTab()
    {
        Section("QUICK PRESETS");
        GUILayout.BeginHorizontal();
        foreach (Preset p in Presets)
        {
            bool matches = p.Batch == batchSize && p.Max == maxActiveMobs &&
                           Mathf.Approximately(p.Interval, spawnInterval) && Mathf.Approximately(p.Radius, spawnRadius);

            if (GUILayout.Button(new GUIContent(p.Name, p.Hint), matches ? ui.TabActive : ui.Button,
                    GUILayout.Height(30)))
            {
                batchSize = p.Batch;
                maxActiveMobs = p.Max;
                spawnInterval = p.Interval;
                spawnRadius = p.Radius;
                fieldBuffers.Clear();
                GUI.FocusControl(null);
                Notify("Applied " + p.Name + " preset.", NoticeLevel.Info);
            }
        }
        GUILayout.EndHorizontal();

        Section("WAVES");
        batchSize = IntSlider("batch", "Wave size", "Mobs spawned per wave. Type an exact number in the box.",
            batchSize, 1, MaxMobs);
        maxActiveMobs = IntSlider("max", "Max alive", "Hard cap on plugin mobs alive at once — limits wave size.",
            maxActiveMobs, 1, MaxMobs);

        if (Mathf.Min(batchSize, maxActiveMobs) > 300)
            Banner("More than 300 mobs at once can lag heavily, especially in multiplayer.", ui.BannerWarning);

        if (batchSize > maxActiveMobs)
            Banner("Wave size is capped at " + maxActiveMobs + " by Max alive.", ui.BannerWarning);

        spawnInterval = FloatSlider("interval", "Wave interval",
            "Minimum time between waves. A new wave only spawns once the last one is cleared.",
            spawnInterval, 0.5f, 120f, 0.5f, "0.0", "s");

        Section("PLACEMENT");
        spawnRadius = FloatSlider("radius", "Spawn radius",
            "Mobs appear at random points within this distance of you.",
            spawnRadius, 2f, 100f, 1f, "0", "m");
    }

    // ── Combat ──

    private void DrawCombatTab()
    {
        Section("PLAYER");
        unlimitedHealth = ToggleRow("Unlimited health", "Blocks all incoming damage to you.", unlimitedHealth);
        unlimitedStamina = ToggleRow("Unlimited stamina", "Keeps your stamina full so you can sprint forever.", unlimitedStamina);

        Section("GOD SWORD");
        godSwordEnabled = ToggleRow("Auto God Sword",
            "While running: puts a God Sword in hotbar slot 1 (whatever was there is dropped on the ground), " +
            "selects it and swings it at nearby mobs.", godSwordEnabled);

        GUI.enabled = godSwordEnabled;
        autoAimEnabled = ToggleRow("Auto-aim",
            "Turns your camera toward the nearest mob so every swing hits. Turn off to aim yourself.",
            autoAimEnabled);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("Equip God Sword now", "Give and equip the sword without starting a session."),
                ui.Button, GUILayout.Height(30)))
        {
            if (EnsureGodSwordEquipped())
                Notify("God Sword is in slot 1.", NoticeLevel.Success);
            else
                Notify("Load into a world first.", NoticeLevel.Error);
        }
        GUILayout.EndHorizontal();
        GUILayout.Label(GodPowersAvailable()
            ? "Using the God Sword from Muck God Powers."
            : "Muck God Powers not found — a stat-boosted sword is used instead.", ui.Hint);
        GUI.enabled = true;

        Section("AUTO-CLICK");
        if (godSwordEnabled)
            Banner("Auto-click is replaced by the God Sword's own attack while Auto God Sword is on.", ui.BannerWarning);

        GUI.enabled = !godSwordEnabled;
        autoClick = ToggleRow("Auto-click", "Presses the left mouse button while running. Paused while this menu is open.",
            autoClick);

        GUI.enabled = autoClick && !godSwordEnabled;
        clickInterval = FloatSlider("click", "Click interval", "Time between clicks — lower is faster.",
            clickInterval, 0.05f, 1f, 0.05f, "0.00", "s");
        GUI.enabled = true;

    }

    private void DrawKillTargetSection()
    {
        Section("KILL TARGET");
        limitEnabled = ToggleRow("Stop at kill target",
            "Automatically stops the session after this many plugin mobs are defeated.", limitEnabled);

        GUI.enabled = limitEnabled;
        GUILayout.BeginVertical(ui.Card);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Target", ui.LabelBold);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("−100", ui.ButtonSmall, GUILayout.Width(48))) killLimit = Mathf.Max(1, killLimit - 100);
        if (GUILayout.Button("−10", ui.ButtonSmall, GUILayout.Width(42))) killLimit = Mathf.Max(1, killLimit - 10);
        killLimit = IntField("killLimit", killLimit, 1, 1000000, 80);
        if (GUILayout.Button("+10", ui.ButtonSmall, GUILayout.Width(42))) killLimit = Mathf.Min(1000000, killLimit + 10);
        if (GUILayout.Button("+100", ui.ButtonSmall, GUILayout.Width(48))) killLimit = Mathf.Min(1000000, killLimit + 100);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        GUI.enabled = true;
    }

    private void DrawStatsSection()
    {
        Section("STATISTICS");
        if (GUILayout.Button(new GUIContent("Reset statistics", "Clears session, all-time and alive counters."), ui.Button,
                GUILayout.Height(30)))
        {
            sessionSpawned = sessionDefeated = totalSpawned = totalDefeated = 0;
            // Stop tracking mobs that are still alive so ALIVE starts at 0. They stay in the
            // world and still count toward Muckinator if killed; the next wave isn't held back by them.
            activeSpawned.Clear();
            Notify("Statistics cleared.", NoticeLevel.Info);
        }
    }

    // ── Settings ──

    private void DrawSettingsTab()
    {
        Section("HOTKEYS");
        HotkeyRow("Start / pause", startKey, HotkeyTarget.StartPause);
        HotkeyRow("Toggle menu", menuKey, HotkeyTarget.Menu);
        GUILayout.Label("Hold Ctrl / Alt / Shift while pressing a key to make a combo. Esc cancels.", ui.Hint);

        Section("INTERFACE");
        closeOnStart = ToggleRow("Close menu on start", "Hide this window as soon as a session starts.", closeOnStart);
        trackerEnabled = ToggleRow("Muckinator tracker",
            "Live Steam kill count toward your next Muckinator (1-4), picked from your current kills, with kill rate and ETA.",
            trackerEnabled);
        showHud = ToggleRow("Show HUD overlay",
            "Small status panel in the top-right while the menu is closed and a session is active.", showHud);

        if (pendingScale <= 0f)
            pendingScale = uiScale;
        pendingScale = FloatSlider("scale", "Interface scale", "Applied when you release the slider.",
            pendingScale, 0.75f, 2f, 0.05f, "0.00", "×");
        if (!Mathf.Approximately(pendingScale, uiScale) && !Input.GetMouseButton(0) &&
            GUI.GetNameOfFocusedControl() != "fld_scale")
        {
            uiScale = pendingScale;
            windowRect.height = 0;
        }

        windowOpacity = FloatSlider("opacity", "Window opacity",
            "Lower values make the menu see-through so you can watch the game behind it.",
            windowOpacity * 100f, 30f, 100f, 5f, "0", "%") / 100f;
        GUILayout.Label("Drag the dots in the menu's bottom-right corner to resize it.", ui.Hint);

        Section("DATA");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("Reset window", "Moves the menu back to the top-left and restores its size."),
                ui.Button, GUILayout.Height(30)))
        {
            windowRect.x = 40;
            windowRect.y = 40;
            WindowWidth = DefaultWindowWidth;
            ContentHeight = DefaultContentHeight;
            windowRect.height = 0;
        }

        bool confirming = Time.unscaledTime < resetConfirmUntil;
        if (GUILayout.Button(new GUIContent(confirming ? "Click again to confirm" : "Restore defaults",
                    "Resets every setting, including hotkeys."),
                confirming ? ui.ButtonDanger : ui.Button, GUILayout.Height(30)))
        {
            if (confirming)
            {
                resetConfirmUntil = 0;
                ResetToDefaults();
            }
            else
            {
                resetConfirmUntil = Time.unscaledTime + 3f;
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.Label("Settings save automatically to BepInEx/config/pramodexe.muckinatorgrind.cfg.", ui.Hint);
    }

    // ─────────────────────────── Footer ──────────────────────────────

    private void DrawFooter()
    {
        Rect line = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
        Ui.DrawRect(new Rect(line.x - 12, line.y, line.width + 24, 1), ui.BorderTex);
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        if (!string.IsNullOrEmpty(GUI.tooltip))
            GUILayout.Label(GUI.tooltip, ui.Label);
        else if (NoticeActive())
            GUILayout.Label(notice, NoticeStyle());
        else
            GUILayout.Label(menuKey + " menu   ·   " + startKey + " start/pause   ·   drag title bar to move", ui.Muted);
        GUILayout.EndHorizontal();
    }

    private GUIStyle NoticeStyle()
    {
        switch (noticeLevel)
        {
            case NoticeLevel.Success: return ui.TextGreen;
            case NoticeLevel.Warning: return ui.TextAmber;
            case NoticeLevel.Error: return ui.TextRed;
            default: return ui.Label;
        }
    }

    // ─────────────────────────── HUD ─────────────────────────────────

    private void DrawHud()
    {
        const float width = 250f;
        float screenW = Screen.width / uiScale;
        Rect area = new Rect(screenW - width - 14, 14, width, 320);

        GUILayout.BeginArea(area);
        GUILayout.BeginVertical(ui.Hud);

        GUILayout.BeginHorizontal();
        GUILayout.Label("MUCKINATOR GRIND", ui.LabelBold);
        GUILayout.FlexibleSpace();
        DrawStatePill();
        GUILayout.EndHorizontal();

        if (state != RunState.Stopped)
        {
            GUILayout.Space(4);
            HudLine("Mob", SelectedMobName());
            HudLine("Alive", activeSpawned.Count.ToString());
            HudLine("Defeated", limitEnabled
                ? sessionDefeated.ToString("N0") + " / " + killLimit.ToString("N0")
                : sessionDefeated.ToString("N0"));

            if (limitEnabled)
                ProgressBar(Mathf.Clamp01((float)sessionDefeated / Mathf.Max(1, killLimit)), null, 6);

            GUILayout.Space(2);
            GUILayout.Label(WaveStatus(), ui.HintSmall);
        }

        DrawMuckinator(true);

        if (NoticeActive())
        {
            GUILayout.Space(2);
            GUILayout.Label(notice, NoticeStyle());
        }

        GUILayout.Label(startKey + " pause/resume  ·  " + menuKey + " menu", ui.HintSmall);

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private void HudLine(string label, string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, ui.Muted, GUILayout.Width(70));
        GUILayout.Label(value, ui.Label);
        GUILayout.EndHorizontal();
    }

    // ═════════════════════════ Widgets ═══════════════════════════════

    private void Section(string title)
    {
        GUILayout.Space(10);
        GUILayout.Label(title, ui.Section);
    }

    private void Banner(string text, GUIStyle style)
    {
        GUILayout.Label(text, style);
        GUILayout.Space(4);
    }

    private void EmptyState(string title, string body)
    {
        GUILayout.Space(30);
        GUILayout.Label(title, ui.EmptyTitle);
        GUILayout.Label(body, ui.EmptyBody);
    }

    private bool ToggleRow(string label, string hint, bool value)
    {
        GUILayout.BeginHorizontal(ui.Card);
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        GUILayout.Label(label, ui.LabelBold);
        GUILayout.Label(hint, ui.Hint);
        GUILayout.EndVertical();
        GUILayout.Space(8);
        GUILayout.BeginVertical(GUILayout.Width(64));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(value ? "ON" : "OFF", value ? ui.SwitchOn : ui.SwitchOff,
                GUILayout.Width(60), GUILayout.Height(26)))
        {
            value = !value;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
        return value;
    }

    private void HotkeyRow(string label, Hotkey current, HotkeyTarget target)
    {
        bool waiting = capturing == target;

        GUILayout.BeginHorizontal(ui.Card);
        GUILayout.Label(label, ui.LabelBold, GUILayout.Width(140));
        GUILayout.Label(current.ToString(), ui.KeyCap, GUILayout.ExpandWidth(false));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(waiting ? "Press a key…  (Esc to cancel)" : "Change",
                waiting ? ui.ButtonWarning : ui.Button, GUILayout.Width(waiting ? 210 : 90), GUILayout.Height(26)))
        {
            capturing = waiting ? HotkeyTarget.None : target;
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();
    }

    private int IntSlider(string id, string label, string hint, int value, int min, int max)
    {
        return Mathf.RoundToInt(FloatSlider(id, label, hint, value, min, max, 1f, "0", null));
    }

    private float FloatSlider(string id, string label, string hint, float value,
        float min, float max, float step, string format, string unit)
    {
        GUILayout.BeginVertical(ui.Card);

        GUILayout.BeginHorizontal();
        GUILayout.Label(label, ui.LabelBold);
        GUILayout.FlexibleSpace();
        value = FloatField(id, value, min, max, format, 64);
        if (!string.IsNullOrEmpty(unit))
            GUILayout.Label(unit, ui.Muted, GUILayout.Width(16));
        GUILayout.EndHorizontal();

        float before = value;
        float after = Slider(value, min, max, step);
        if (!Mathf.Approximately(before, after))
        {
            value = after;
            fieldBuffers.Remove(id);
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label(hint, ui.Hint, GUILayout.ExpandWidth(true));
        GUILayout.Space(8);
        GUILayout.Label(min.ToString(format, CultureInfo.InvariantCulture) + " – " +
                        max.ToString(format, CultureInfo.InvariantCulture), ui.HintSmall, GUILayout.ExpandWidth(false));
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        return value;
    }

    private float Slider(float value, float min, float max, float step)
    {
        Rect r = GUILayoutUtility.GetRect(10, 20, GUILayout.ExpandWidth(true));

        if (Event.current.type == EventType.Repaint)
        {
            float t = Mathf.InverseLerp(min, max, value);
            Rect track = new Rect(r.x, r.y + r.height / 2f - 2f, r.width, 4f);
            float thumb = ui.Thumb.fixedWidth;
            Ui.DrawRect(track, ui.TrackTex);
            Ui.DrawRect(new Rect(track.x, track.y, thumb / 2f + t * (track.width - thumb), 4f),
                GUI.enabled ? ui.AccentTex : ui.TrackFillDisabledTex);
        }

        float v = GUI.HorizontalSlider(r, value, min, max, ui.SliderTrack, ui.Thumb);

        if (step > 0f)
            v = Mathf.Round((v - min) / step) * step + min;

        return Mathf.Clamp(v, min, max);
    }

    private float FloatField(string id, float value, float min, float max, string format, float width)
    {
        string controlName = "fld_" + id;
        bool focused = GUI.GetNameOfFocusedControl() == controlName;

        string buffer;
        if (!focused || !fieldBuffers.TryGetValue(id, out buffer))
            buffer = value.ToString(format, CultureInfo.InvariantCulture);
        if (!focused)
            fieldBuffers.Remove(id);

        GUI.SetNextControlName(controlName);
        string edited = GUILayout.TextField(buffer, 10, ui.InputNumber, GUILayout.Width(width));

        if (edited != buffer)
        {
            fieldBuffers[id] = edited;

            float parsed;
            if (float.TryParse(edited.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                value = Mathf.Clamp(parsed, min, max);
        }

        return value;
    }

    private int IntField(string id, int value, int min, int max, float width)
    {
        return Mathf.RoundToInt(FloatField(id, value, min, max, "0", width));
    }

    private void ProgressBar(float t, string label, float height = 22f)
    {
        Rect r = GUILayoutUtility.GetRect(10, height, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint)
        {
            Ui.DrawRect(r, ui.TrackTex);
            Ui.DrawRect(new Rect(r.x, r.y, r.width * t, r.height), t >= 1f ? ui.GreenTex : ui.AccentTex);
        }
        if (!string.IsNullOrEmpty(label))
            GUI.Label(r, label, ui.ProgressLabel);
    }

    // ═════════════════════════ Hotkey type ═══════════════════════════

    private sealed class Hotkey
    {
        public KeyCode Key;
        public bool Ctrl;
        public bool Alt;
        public bool Shift;

        public static bool IsModifier(KeyCode key)
        {
            return key == KeyCode.LeftControl || key == KeyCode.RightControl ||
                   key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
                   key == KeyCode.LeftShift || key == KeyCode.RightShift ||
                   key == KeyCode.LeftCommand || key == KeyCode.RightCommand;
        }

        private static bool CtrlHeld() { return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl); }
        private static bool AltHeld() { return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt); }
        private static bool ShiftHeld() { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); }

        public static Hotkey FromCurrentModifiers(KeyCode key)
        {
            return new Hotkey { Key = key, Ctrl = CtrlHeld(), Alt = AltHeld(), Shift = ShiftHeld() };
        }

        public static Hotkey Parse(string value, KeyCode fallback)
        {
            value = value ?? "";
            Hotkey hk = new Hotkey
            {
                Ctrl = value.Contains("Ctrl+"),
                Alt = value.Contains("Alt+"),
                Shift = value.Contains("Shift+"),
            };

            string keyName = value.Replace("Ctrl+", "").Replace("Alt+", "").Replace("Shift+", "").Trim();
            KeyCode parsed;
            hk.Key = Enum.TryParse(keyName, true, out parsed) && parsed != KeyCode.None ? parsed : fallback;
            return hk;
        }

        public bool IsPressed()
        {
            return Input.GetKeyDown(Key) && Ctrl == CtrlHeld() && Alt == AltHeld() && Shift == ShiftHeld();
        }

        public bool Equals(Hotkey other)
        {
            return other != null && other.Key == Key && other.Ctrl == Ctrl && other.Alt == Alt && other.Shift == Shift;
        }

        public override string ToString()
        {
            return (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + Key;
        }
    }

    // ═════════════════════════ Theme / styles ════════════════════════

    private sealed class Ui
    {
        private static readonly Color Bg = Hex("#14161B");
        private static readonly Color Surface = Hex("#1C1F26");
        private static readonly Color SurfaceHover = Hex("#252933");
        private static readonly Color Border = Hex("#2C313C");
        private static readonly Color InputBg = Hex("#0F1115");
        private static readonly Color Text = Hex("#E8EAED");
        private static readonly Color MutedText = Hex("#8B93A1");
        private static readonly Color AccentC = Hex("#F2853A");
        private static readonly Color AccentHover = Hex("#FF9A55");
        private static readonly Color AccentDark = Hex("#C7692B");
        private static readonly Color Green = Hex("#3FB950");
        private static readonly Color Amber = Hex("#D29922");
        private static readonly Color Red = Hex("#F85149");

        private readonly List<Texture2D> textures = new List<Texture2D>();

        public GUISkin Skin;
        public GUIStyle Window, Card, Hud;
        public GUIStyle Title, Section, Label, LabelBold, Muted, Accent, Hint, HintSmall, Placeholder;
        public GUIStyle TextGreen, TextAmber, TextRed;
        public GUIStyle StatValue, StatCaption, ProgressLabel, EmptyTitle, EmptyBody, KeyCap;
        public GUIStyle Button, ButtonSmall, ButtonPrimary, ButtonWarning, ButtonDanger, IconButton, IconButtonDanger;
        public GUIStyle Tab, TabActive, MobButton, MobSelected, SwitchOn, SwitchOff;
        public GUIStyle PillGreen, PillAmber, PillGray, BannerError, BannerWarning;
        public GUIStyle TextInput, InputNumber, SliderTrack, Thumb;
        public Texture2D BorderTex, AccentTex, TrackTex, TrackFillDisabledTex, GreenTex;

        public void EnsureBuilt()
        {
            if (Skin != null && BorderTex != null)
                return;

            Dispose();
            Build();
        }

        public void Dispose()
        {
            foreach (Texture2D t in textures)
                if (t != null) UnityEngine.Object.Destroy(t);
            textures.Clear();

            if (Skin != null)
                UnityEngine.Object.Destroy(Skin);
            Skin = null;
        }

        private void Build()
        {
            Skin = UnityEngine.Object.Instantiate(GUI.skin);
            Skin.hideFlags = HideFlags.HideAndDontSave;

            BorderTex = Solid(Border);
            AccentTex = Solid(AccentC);
            TrackTex = Solid(Hex("#2A2E37"));
            TrackFillDisabledTex = Solid(Hex("#555B66"));
            GreenTex = Solid(Green);

            // Scrollbars
            Skin.verticalScrollbar.normal.background = Solid(Bg);
            Skin.verticalScrollbar.fixedWidth = 8;
            Skin.verticalScrollbar.border = new RectOffset(0, 0, 0, 0);
            Skin.verticalScrollbar.margin = new RectOffset(4, 0, 0, 0);
            Skin.verticalScrollbarThumb.normal.background = Solid(Hex("#3A404C"));
            Skin.verticalScrollbarThumb.hover.background = Solid(Hex("#4A5160"));
            Skin.verticalScrollbarThumb.active.background = Solid(AccentC);
            Skin.verticalScrollbarThumb.fixedWidth = 8;
            Skin.verticalScrollbarThumb.border = new RectOffset(0, 0, 0, 0);
            Skin.scrollView.padding = new RectOffset(0, 4, 0, 0);
            Skin.settings.cursorColor = AccentC;
            Skin.settings.selectionColor = new Color(AccentC.r, AccentC.g, AccentC.b, 0.35f);

            Window = new GUIStyle(Skin.window)
            {
                padding = new RectOffset(14, 14, 12, 12),
                border = new RectOffset(1, 1, 1, 1),
                overflow = new RectOffset(0, 0, 0, 0),
                contentOffset = Vector2.zero,
            };
            SetAll(Window, Bordered(Bg, Border), Text);

            Card = Box(Surface, Border, new RectOffset(10, 10, 8, 8), new RectOffset(0, 0, 3, 3));
            Hud = Box(new Color(Bg.r, Bg.g, Bg.b, 0.88f), Border, new RectOffset(10, 10, 8, 8), new RectOffset(0, 0, 0, 0));

            Label = TextStyle(12, Text);
            LabelBold = TextStyle(12, Text, FontStyle.Bold);
            Muted = TextStyle(11, MutedText);
            Accent = TextStyle(12, AccentC, FontStyle.Bold);
            Hint = TextStyle(11, MutedText); Hint.wordWrap = true;
            HintSmall = TextStyle(10, MutedText);
            Placeholder = TextStyle(12, Hex("#5C6370"), FontStyle.Italic);
            Title = TextStyle(15, Text, FontStyle.Bold);
            Section = TextStyle(10, AccentC, FontStyle.Bold);
            Section.margin = new RectOffset(2, 0, 2, 4);
            TextGreen = TextStyle(12, Green, FontStyle.Bold); TextGreen.wordWrap = true;
            TextAmber = TextStyle(12, Amber, FontStyle.Bold); TextAmber.wordWrap = true;
            TextRed = TextStyle(12, Red, FontStyle.Bold); TextRed.wordWrap = true;

            StatValue = TextStyle(17, Text, FontStyle.Bold); StatValue.alignment = TextAnchor.MiddleCenter;
            StatCaption = TextStyle(9, MutedText, FontStyle.Bold); StatCaption.alignment = TextAnchor.MiddleCenter;
            ProgressLabel = TextStyle(11, Color.white, FontStyle.Bold); ProgressLabel.alignment = TextAnchor.MiddleCenter;
            EmptyTitle = TextStyle(14, Text, FontStyle.Bold); EmptyTitle.alignment = TextAnchor.MiddleCenter;
            EmptyBody = TextStyle(11, MutedText); EmptyBody.alignment = TextAnchor.MiddleCenter; EmptyBody.wordWrap = true;

            KeyCap = TextStyle(12, Text, FontStyle.Bold);
            KeyCap.normal.background = Bordered(InputBg, Hex("#454B57"));
            KeyCap.border = new RectOffset(1, 1, 1, 1);
            KeyCap.padding = new RectOffset(10, 10, 4, 4);
            KeyCap.alignment = TextAnchor.MiddleCenter;

            Button = Btn(Surface, SurfaceHover, Hex("#2E333E"), Border, Text);
            ButtonSmall = Btn(Surface, SurfaceHover, Hex("#2E333E"), Border, Text);
            ButtonSmall.fontSize = 11;
            ButtonSmall.padding = new RectOffset(4, 4, 3, 3);
            ButtonPrimary = Btn(AccentC, AccentHover, AccentDark, AccentDark, Hex("#1A1206"));
            ButtonPrimary.fontSize = 14;
            ButtonWarning = Btn(Amber, Hex("#E3AB33"), Hex("#A87A18"), Hex("#A87A18"), Hex("#1A1206"));
            ButtonWarning.fontSize = 13;
            ButtonDanger = Btn(Hex("#3A1D1F"), Hex("#4D2326"), Hex("#2B1517"), Hex("#6E2B2E"), Red);
            ButtonDanger.fontSize = 13;
            IconButton = Btn(Surface, SurfaceHover, Hex("#2E333E"), Border, Text);
            IconButton.fontSize = 14; IconButton.padding = new RectOffset(0, 0, 0, 2);
            IconButtonDanger = Btn(Surface, Hex("#4D2326"), Hex("#2B1517"), Border, Text);
            IconButtonDanger.fontSize = 15; IconButtonDanger.padding = new RectOffset(0, 0, 0, 2);
            IconButtonDanger.hover.textColor = Red;

            Tab = Btn(Bg, Surface, Surface, Bg, MutedText);
            Tab.hover.textColor = Text;
            Tab.margin = new RectOffset(0, 2, 0, 0);
            TabActive = Btn(Hex("#2A2119"), Hex("#33271C"), Hex("#33271C"), AccentDark, AccentC);
            TabActive.margin = new RectOffset(0, 2, 0, 0);

            MobButton = Btn(Surface, SurfaceHover, Hex("#2E333E"), Border, Text);
            MobButton.fontStyle = FontStyle.Normal;
            MobButton.clipping = TextClipping.Clip;
            MobSelected = Btn(Hex("#2A2119"), Hex("#33271C"), Hex("#33271C"), AccentC, AccentC);
            MobSelected.clipping = TextClipping.Clip;

            SwitchOn = Btn(Hex("#1E3A24"), Hex("#244A2C"), Hex("#183020"), Green, Green);
            SwitchOff = Btn(InputBg, SurfaceHover, InputBg, Hex("#3A404C"), MutedText);

            PillGreen = Pill(Hex("#16301D"), Green);
            PillAmber = Pill(Hex("#33280F"), Amber);
            PillGray = Pill(Hex("#262A31"), MutedText);

            BannerError = Box(Hex("#2D1719"), Hex("#6E2B2E"), new RectOffset(10, 10, 6, 6), new RectOffset(0, 0, 0, 2));
            SetTextColor(BannerError, Hex("#FF9B95")); BannerError.fontSize = 12; BannerError.wordWrap = true;
            BannerWarning = Box(Hex("#2D2513"), Hex("#6B5420"), new RectOffset(10, 10, 6, 6), new RectOffset(0, 0, 2, 2));
            SetTextColor(BannerWarning, Hex("#F0C866")); BannerWarning.fontSize = 11; BannerWarning.wordWrap = true;

            TextInput = new GUIStyle(Skin.textField)
            {
                fontSize = 12,
                padding = new RectOffset(8, 8, 5, 5),
                border = new RectOffset(1, 1, 1, 1),
                fixedHeight = 26,
            };
            Texture2D inputTex = Bordered(InputBg, Hex("#353B47"));
            Texture2D inputFocus = Bordered(InputBg, AccentC);
            TextInput.normal.background = inputTex; TextInput.normal.textColor = Text;
            TextInput.hover.background = Bordered(InputBg, Hex("#4A5160")); TextInput.hover.textColor = Text;
            TextInput.focused.background = inputFocus; TextInput.focused.textColor = Text;
            TextInput.active.background = inputFocus; TextInput.active.textColor = Text;
            TextInput.onNormal = TextInput.normal; TextInput.onFocused = TextInput.focused;

            InputNumber = new GUIStyle(TextInput) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };

            SliderTrack = new GUIStyle
            {
                padding = new RectOffset(0, 0, 3, 3),
                fixedHeight = 20,
            };

            Thumb = new GUIStyle
            {
                fixedWidth = 14,
                fixedHeight = 14,
            };
            Thumb.normal.background = Circle(AccentC, Bg, 14);
            Thumb.hover.background = Circle(AccentHover, Bg, 14);
            Thumb.active.background = Circle(Color.white, AccentC, 14);
        }

        // ── Style factories ──

        private GUIStyle TextStyle(int size, Color color, FontStyle style = FontStyle.Normal)
        {
            GUIStyle s = new GUIStyle(Skin.label)
            {
                fontSize = size,
                fontStyle = style,
                richText = false,
                padding = new RectOffset(2, 2, 2, 2),
                margin = new RectOffset(2, 2, 1, 1),
            };
            SetTextColor(s, color);
            return s;
        }

        private GUIStyle Btn(Color normal, Color hover, Color active, Color border, Color text)
        {
            GUIStyle s = new GUIStyle(Skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(10, 10, 6, 6),
                margin = new RectOffset(2, 2, 2, 2),
            };

            Texture2D n = Bordered(normal, border);
            Texture2D h = Bordered(hover, border);
            Texture2D a = Bordered(active, border);

            s.normal.background = n; s.onNormal.background = n;
            s.hover.background = h; s.onHover.background = h;
            s.active.background = a; s.onActive.background = a;
            s.focused.background = n; s.onFocused.background = n;
            SetTextColor(s, text);
            return s;
        }

        private GUIStyle Box(Color fill, Color border, RectOffset padding, RectOffset margin)
        {
            GUIStyle s = new GUIStyle(Skin.box)
            {
                border = new RectOffset(1, 1, 1, 1),
                padding = padding,
                margin = margin,
                alignment = TextAnchor.MiddleLeft,
            };
            SetAll(s, Bordered(fill, border), Text);
            return s;
        }

        private GUIStyle Pill(Color fill, Color text)
        {
            GUIStyle s = TextStyle(10, text, FontStyle.Bold);
            s.normal.background = Bordered(fill, new Color(text.r, text.g, text.b, 0.45f));
            s.border = new RectOffset(1, 1, 1, 1);
            s.padding = new RectOffset(9, 9, 4, 4);
            s.margin = new RectOffset(0, 0, 3, 0);
            s.alignment = TextAnchor.MiddleCenter;
            return s;
        }

        private static void SetAll(GUIStyle s, Texture2D bg, Color text)
        {
            s.normal.background = bg; s.onNormal.background = bg;
            s.hover.background = bg; s.onHover.background = bg;
            s.active.background = bg; s.onActive.background = bg;
            s.focused.background = bg; s.onFocused.background = bg;
            SetTextColor(s, text);
        }

        private static void SetTextColor(GUIStyle s, Color c)
        {
            s.normal.textColor = c; s.onNormal.textColor = c;
            s.hover.textColor = c; s.onHover.textColor = c;
            s.active.textColor = c; s.onActive.textColor = c;
            s.focused.textColor = c; s.onFocused.textColor = c;
        }

        // ── Textures ──

        private Texture2D Keep(Texture2D t)
        {
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Clamp;
            textures.Add(t);
            return t;
        }

        private Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            return Keep(t);
        }

        private Texture2D Bordered(Color fill, Color border)
        {
            Texture2D t = new Texture2D(3, 3, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            for (int x = 0; x < 3; x++)
                for (int y = 0; y < 3; y++)
                    t.SetPixel(x, y, x == 1 && y == 1 ? fill : border);
            t.Apply();
            return Keep(t);
        }

        private Texture2D Circle(Color fill, Color ring, int size)
        {
            Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            float r = size / 2f;
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    float alpha = Mathf.Clamp01(r - d);
                    Color c = d > r - 2.5f ? ring : fill;
                    c.a *= alpha;
                    t.SetPixel(x, y, c);
                }
            }
            t.Apply();
            return Keep(t);
        }

        public static void DrawRect(Rect r, Texture2D tex)
        {
            if (Event.current.type == EventType.Repaint && tex != null)
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill);
        }

        private static Color Hex(string hex)
        {
            Color c;
            return ColorUtility.TryParseHtmlString(hex, out c) ? c : Color.magenta;
        }
    }
}

[HarmonyPatch(typeof(HitableMob), "OnKill")]
internal static class MobDeathPatch
{
    private static void Prefix(HitableMob __instance)
    {
        if (MuckinatorGrindPlugin.Instance != null)
            MuckinatorGrindPlugin.Instance.HandleMobDeath(__instance);
    }
}

[HarmonyPatch(typeof(PlayerStatus), "HandleDamage")]
internal static class PlayerDamagePatch
{
    private static bool Prefix()
    {
        return MuckinatorGrindPlugin.Instance == null ||
               !MuckinatorGrindPlugin.Instance.ShouldBlockPlayerDamage();
    }
}

[HarmonyPatch(typeof(PlayerInput), "Look")]
internal static class PlayerLookPatch
{
    private static void Postfix(PlayerInput __instance)
    {
        if (MuckinatorGrindPlugin.Instance != null)
            MuckinatorGrindPlugin.Instance.ApplyAim(__instance);
    }
}

// Clicking on the mod window shouldn't also swing your weapon in-game.
[HarmonyPatch(typeof(UseInventory), "Use")]
internal static class UseWhileMenuOpenPatch
{
    private static bool Prefix()
    {
        return MuckinatorGrindPlugin.Instance == null || !MuckinatorGrindPlugin.Instance.ShouldBlockGameClicks();
    }
}
