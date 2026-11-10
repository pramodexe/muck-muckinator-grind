<p align="center"><img src="art/logo.png" alt="MuckinatorGrind logo" width="256"></p>

# MuckinatorGrind

### The easiest way to get every Muckinator achievement in Muck.

Press one key and walk away. The mod spawns mobs around you, kills them for you, and keeps going for hours without lagging, while you do something else.

> **Version:** 2.0.0 · **Game:** Muck (Steam) · **Needs:** BepInEx 5

---

## What it's for

Muck has four kill achievements, and they take a *lot* of kills:

| Achievement | Kills needed |
|-------------|-------------:|
| **Muckinator** | 100 |
| **Muckinator 2** | 1,000 |
| **Muckinator 3** | 10,000 |
| **Muckinator 4** | 100,000 |

MuckinatorGrind farms those kills for you. It reads your real kill count from Steam and always shows the next Muckinator you haven't unlocked yet, with a progress bar and an estimate of how long is left.

---

## How to install

- **With a mod manager:** search for **MuckinatorGrind** and download it. That's it.
- **Without a mod manager:** download `MuckinatorGrind.dll` and put it in your `BepInEx/plugins` folder.

---

## How to use

1. **Host a game on your own.** Start a new game as the host and don't invite friends. Only the host can spawn mobs, and a solo game keeps things smooth.
2. **Wait until you spawn** in the world.
3. **Press `F2`** to open the menu. The default settings already work well (see below). Change them if you like, or leave them as they are.
4. **Press `F8`** (or **START** in the menu) and close the menu.
5. **That's it.** Leave the game running and use your computer for something else. Check back now and then to watch the Muckinator bar go up.

| Key | What it does |
|-----|--------------|
| **F2** | Open or close the menu |
| **F8** | Start / pause farming |
| **Esc** | Close the menu |

You can change both keys in the **Settings** tab.

### Default settings

These are the recommended settings. They're already set when you install, and the **Default** preset puts them back.

| Setting | Default |
|---------|---------|
| Mob | Goblin |
| Mobs per wave | 100 |
| Most mobs alive at once | 150 |
| Time between waves | 2 seconds |
| Spawn radius | 12 m |
| Move to a new spot | every 3 waves |

---

## What it does while you're away

- **Spawns mobs** around you in waves: Lil Dave, Goblin or Stone Golem.
- **Kills them for you.** It puts a God Sword in hotbar slot 1, aims at the nearest mob and swings.
- **Keeps you alive** with unlimited health and stamina.
- **Moves around the map.** Every 3 waves it clears the area (all mobs, creatures and dropped items), moves you to a new spot on the island and restores your health. This stops long sessions from getting laggy.
- **Cleans up loot** like coins after every wave so it doesn't pile up. Power-ups are kept.
- **Never gets stuck.** If a mob ends up somewhere the sword can't reach, the mod gives up on it and starts the next wave.
- **Tracks your Muckinator progress** with your live Steam kill count, kills per minute and time left.
- **Grades your setup** in the **Logs** tab, so you can find the settings that run best on your PC.
- **Small HUD** in the corner shows what's happening while the menu is closed.

---

## The menu

| Tab | What's in it |
|-----|--------------|
| **Spawn** | Mob, presets, wave size, how often to move, kill target |
| **Combat** | Unlimited health and stamina, God Sword, auto-aim |
| **Logs** | Your grade, a kills-over-time chart, past sessions, and which setup is best |
| **Settings** | Hotkeys, HUD, menu size and see-through amount, troubleshooting log |

Settings save automatically.

---

## More details

### Grades (Logs tab)

Every session gets a grade from **S** (best) to **F**, plus a short tip such as *"Smooth but slow. Your PC can probably handle a bigger preset."* The grade combines:

- **Speed:** kills per minute.
- **Smoothness:** your frame rate (FPS).
- **Stability:** whether it stays fast for the whole run or slows down over time.

Long runs often get slower, so a short run can't earn a top grade. Run a setup for **60 minutes or more** to get a fully confident grade. Runs under 3 minutes aren't graded.

**Compare setups** ranks every setup you've tried, best first. Press **Use** to switch to one.

Time with the menu open isn't counted, since you can't attack while it's open.

### Moving around the map

Change this in the **Spawn** tab under **Move around the map**: turn it on or off, choose how many waves to stay in one spot, clear loot every wave, keep power-ups, or press **Move now**. Traders (Woodmen) are never removed, and your own items are never dropped by the God Sword. Whatever was in slot 1 moves to a free inventory slot.

### Files

Everything is saved in the `BepInEx/MuckinatorGrind/` folder:

| File | What it is |
|------|------------|
| `sessions.csv` | One row per session. Opens in Excel or Google Sheets. |
| `sessions/` | A detailed file for each session. |
| `debug.log` | The troubleshooting log for this game. |
| `debug-previous.log` | The troubleshooting log from the game before. |

### Something went wrong?

The mod keeps a **troubleshooting log** of everything it does, including freezes. If something goes wrong, send `debug.log` with your report, or `debug-previous.log` if you've restarted the game since. In **Settings → Troubleshooting log** you can open the folder, or press **Mark a problem now** at the moment something looks wrong so it's easy to find in the log.

### Common questions

- **The START button is grey.** You're not the host. Host your own game.
- **The game is laggy.** Try a smaller preset or lower *Most mobs alive at once*. The **Logs → Compare setups** page shows what runs best on your PC.
- **The tracker says "Steam stats unavailable".** Make sure Steam is open and you're online. Kills only count toward achievements when Steam is working.
- **Do kills from this mod count?** Yes. Muck counts them the same as any other kill.

---

## Disclaimer

This is a fan-made mod. Using mods to unlock achievements is your choice, so use it at your own risk.
