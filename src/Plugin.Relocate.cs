using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

// Moving around the map while farming:
//  - after every wave, dropped items (coins etc.) are cleared so they don't pile up;
//  - every N waves, every mob and creature is removed (no loot), dropped items are cleared,
//    the player is moved to a fresh spot on the island and healed, and farming carries on there.
// Everything here uses the same calls the game itself uses (item despawn, out-of-map teleport,
// server mob death), so other players in the lobby see the same thing.
public partial class MuckinatorGrindPlugin
{
    private const float CleanupDelay = 1f;      // wait for the last kills' loot to land before clearing
    private const float AfterMoveDelay = 3f;    // let the new area settle before the next wave
    private const float MinMoveDistance = 250f; // a new spot is at least this far from the old one
    private const float IslandRadius = 1100f;   // GameManager.mapRadius
    private const string Woodman = "Woodman";   // traders: never removed

    private ConfigEntry<bool> cfgMoveEnabled;
    private ConfigEntry<int> cfgMoveEveryWaves;
    private ConfigEntry<bool> cfgClearItems;
    private ConfigEntry<bool> cfgKeepPowerups;

    private bool waveInProgress;
    private int wavesSinceMove;
    private float pendingCleanupAt = -1f;
    private int movesThisSession;

    private bool MoveEnabled { get { return cfgMoveEnabled.Value; } }
    private int MoveEveryWaves { get { return Mathf.Clamp(cfgMoveEveryWaves.Value, 1, 100); } }
    private bool ClearItemsEnabled { get { return cfgClearItems.Value; } }

    private void RelocateAwake()
    {
        cfgMoveEnabled = Config.Bind("Moving", "MoveToNewSpot", true,
            "Every few waves: remove all mobs and dropped items, move to a new spot on the map and heal.");
        cfgMoveEveryWaves = Config.Bind("Moving", "MoveEveryWaves", 3,
            "How many waves to farm in one spot before moving (1-100).");
        cfgClearItems = Config.Bind("Moving", "ClearDroppedItems", true,
            "Remove dropped items (coins and other loot) after every wave.");
        cfgKeepPowerups = Config.Bind("Moving", "KeepPowerups", true,
            "When clearing dropped items, leave power-ups on the ground.");
    }

    private void ResetRelocateState()
    {
        waveInProgress = false;
        wavesSinceMove = 0;
        pendingCleanupAt = -1f;
        movesThisSession = 0;
    }

    // ── Wave tracking (called every frame while running) ──

    private void OnWaveStarted()
    {
        waveInProgress = true;
    }

    /// <summary>Returns true while a cleanup or move is pending; spawning waits for it.</summary>
    private bool UpdateWaveCleanup()
    {
        if (waveInProgress && spawnQueue == 0 && activeSpawned.Count == 0)
        {
            waveInProgress = false;
            wavesSinceMove++;
            if (ClearItemsEnabled || (MoveEnabled && wavesSinceMove >= MoveEveryWaves))
                pendingCleanupAt = Time.unscaledTime + CleanupDelay;
        }

        if (pendingCleanupAt < 0f)
            return false;
        if (Time.unscaledTime < pendingCleanupAt)
            return true;

        pendingCleanupAt = -1f;
        if (MoveEnabled && wavesSinceMove >= MoveEveryWaves)
            MoveToNewSpot("after " + wavesSinceMove + " waves");
        else if (ClearItemsEnabled)
        {
            int items = ClearDroppedItems();
            if (items > 0)
                Log("cleanup", "Wave done: removed " + items + " dropped items.");
        }
        return false;
    }

    // ── The move ──

    private bool MoveToNewSpot(string reason)
    {
        if (!IsHost() || PlayerMovement.Instance == null)
        {
            Notify("Moving needs you to be the host and in a world.", NoticeLevel.Error);
            return false;
        }

        wavesSinceMove = 0;
        Vector3 from = PlayerMovement.Instance.transform.position;

        Vector3 spot;
        if (!FindNewSpot(from, out spot))
        {
            Log("move", "No safe spot found " + reason + "; staying at " + from.ToString("F0") + ".");
            Notify("Couldn't find a safe spot to move to — staying here.", NoticeLevel.Warning);
            return false;
        }

        int mobs = DespawnAllMobs();
        int items = ClearDroppedItems();

        PlayerMovement.Instance.transform.position = spot + Vector3.up * 2f;
        Rigidbody rb = PlayerMovement.Instance.GetRb();
        if (rb != null)
            rb.velocity = Vector3.zero;

        RestorePlayer();

        // Fresh start in the new area.
        spawnQueue = 0;
        waveInProgress = false;
        aimTarget = null;
        aimActive = false;
        aimIgnored.Clear();
        stragglers.Clear();
        noKillTimer = 0f;
        nextSpawnTime = Time.time + AfterMoveDelay;
        movesThisSession++;

        float distance = Vector3.Distance(from, spot);
        Log("move", "Moved " + reason + " from " + from.ToString("F0") + " to " + spot.ToString("F0") + " (" + distance.ToString("0") +
                    " m). Removed " + mobs + " mobs and " + items + " dropped items. Move #" + movesThisSession + " this session.");
        Notify("Moved to a new spot (" + distance.ToString("0") + " m away). Removed " + mobs + " mobs and " + items + " items.",
            NoticeLevel.Success);
        return true;
    }

    // Same test the game uses for player spawn points: solid ground that isn't water,
    // plus a flatness check and a check that we're not inside a rock or tree.
    private bool FindNewSpot(Vector3 from, out Vector3 spot)
    {
        spot = Vector3.zero;
        if (GameManager.instance == null)
            return false;

        LayerMask ground = GameManager.instance.whatIsGround;
        LayerMask solid = GameManager.instance.whatIsGroundAndObject;

        for (int attempt = 0; attempt < 120; attempt++)
        {
            Vector2 p = UnityEngine.Random.insideUnitCircle * IslandRadius * 0.8f;
            RaycastHit hit;
            if (!Physics.Raycast(new Vector3(p.x, 200f, p.y), Vector3.down, out hit, 500f, ground))
                continue;
            if (WorldUtility.WorldHeightToBiome(hit.point.y) == TextureData.TerrainType.Water)
                continue;
            if (hit.normal.y < 0.8f)
                continue;
            if (Physics.CheckSphere(hit.point + Vector3.up * 2f, 1f, solid))
                continue;

            // Prefer somewhere properly far away; accept closer spots only if the island is cramped.
            float dist = Vector3.Distance(new Vector3(from.x, 0f, from.z), new Vector3(hit.point.x, 0f, hit.point.z));
            if (dist < (attempt < 90 ? MinMoveDistance : MinMoveDistance / 3f))
                continue;

            spot = hit.point;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Removes every mob and creature except traders, without loot and without counting kills.
    /// Mirrors what the server does when a mob dies, minus the loot drop.
    /// </summary>
    private int DespawnAllMobs()
    {
        if (MobManager.Instance == null || MobManager.Instance.mobs == null || LocalClient.instance == null)
            return 0;

        int me = LocalClient.instance.myId;
        int removed = 0;
        foreach (Mob mob in new List<Mob>(MobManager.Instance.mobs.Values))
        {
            if (mob == null || (mob.mobType != null && mob.mobType.name == Woodman))
                continue;

            try
            {
                int id = mob.GetId();
                int instanceId = mob.GetInstanceID();
                Vector3 pos = mob.transform.position;

                if (mob.hitable != null)
                    mob.hitable.hp = 0;
                MobManager.Instance.RemoveMob(id);
                activeSpawned.Remove(instanceId);
                stSpawnTimes.Remove(instanceId);
                UnityEngine.Object.Destroy(mob.gameObject);

                // Other players: the same message a killing blow sends (hp 0), so the mob disappears for them too.
                ServerSend.PlayerHitMob(me, id, 0, 0, pos, 0);
                removed++;
            }
            catch (Exception ex)
            {
                Log("error", "Could not remove " + mob.name + ": " + ex.Message);
            }
        }

        // Clean up ids of mobs that are already gone.
        List<int> gone = new List<int>();
        foreach (KeyValuePair<int, Mob> kv in MobManager.Instance.mobs)
            if (kv.Value == null)
                gone.Add(kv.Key);
        foreach (int id in gone)
            MobManager.Instance.RemoveMob(id);

        return removed;
    }

    /// <summary>
    /// Removes dropped items exactly like the game's own 5-minute despawn does: "important" items are kept.
    /// Power-ups are kept too unless that option is off.
    /// </summary>
    private int ClearDroppedItems()
    {
        ItemManager im = ItemManager.Instance;
        if (ReferenceEquals(im, null) || im.list == null)
            im = FindObjectOfType<ItemManager>();
        if (ReferenceEquals(im, null) || im.list == null || !IsHost())
            return 0;

        bool keepPowerups = cfgKeepPowerups.Value;
        int removed = 0;
        foreach (int id in new List<int>(im.list.Keys))
        {
            GameObject go;
            if (!im.list.TryGetValue(id, out go))
                continue;
            if (go == null)
            {
                im.list.Remove(id);
                continue;
            }

            Item drop = go.GetComponent<Item>();
            if (drop == null)
                continue;
            if (drop.item != null && drop.item.important)
                continue;
            if (keepPowerups && drop.powerup != null)
                continue;

            try
            {
                im.PickupItem(id);
                ServerSend.PickupItem(-1, id);
                removed++;
            }
            catch (Exception ex)
            {
                Log("error", "Could not remove dropped item " + id + ": " + ex.Message);
            }
        }
        return removed;
    }

    private void RestorePlayer()
    {
        PlayerStatus ps = PlayerStatus.Instance;
        if (ps == null || ps.IsPlayerDead())
            return;
        ps.hp = ps.maxHp;
        ps.shield = ps.maxShield;
        ps.hunger = ps.maxHunger;
        ps.stamina = ps.maxStamina;
    }

    // ── Spawn tab section ──

    private void DrawMoveSection()
    {
        Section("MOVE AROUND THE MAP");
        GUILayout.Label("Farming in one spot for a long time gets laggy. Moving clears everything around you and starts fresh.",
            ui.Hint);

        bool move = ToggleRow("Move to a new spot",
            "After the set number of waves: removes all mobs and creatures (no loot), clears dropped items, " +
            "moves you somewhere else on the island and restores your health.", MoveEnabled);
        if (move != MoveEnabled)
            cfgMoveEnabled.Value = move;

        GUI.enabled = move;
        int every = IntSlider("moveEvery", "Waves before moving", "How many waves to farm in one spot.",
            MoveEveryWaves, 1, 20);
        if (every != cfgMoveEveryWaves.Value)
            cfgMoveEveryWaves.Value = every;
        if (move && state != RunState.Stopped)
            GUILayout.Label("Next move after " + Mathf.Max(0, MoveEveryWaves - wavesSinceMove) + " more wave(s).", ui.HintSmall);
        GUI.enabled = true;

        bool clear = ToggleRow("Clear dropped items every wave",
            "Removes coins and other loot after each wave, like the game's own 5-minute cleanup but sooner.", ClearItemsEnabled);
        if (clear != ClearItemsEnabled)
            cfgClearItems.Value = clear;

        GUI.enabled = clear || move;
        bool keep = ToggleRow("Keep power-ups", "Leave power-ups on the ground when clearing items.", cfgKeepPowerups.Value);
        if (keep != cfgKeepPowerups.Value)
            cfgKeepPowerups.Value = keep;
        GUI.enabled = true;

        GUI.enabled = IsHost() && PlayerMovement.Instance != null;
        if (GUILayout.Button(new GUIContent("Move now", "Clear the area and move to a new spot right away."), ui.Button,
                GUILayout.Height(30)))
            MoveToNewSpot("by the Move now button");
        GUI.enabled = true;
    }
}
