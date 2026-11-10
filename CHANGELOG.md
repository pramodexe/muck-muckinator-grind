# Changelog

## 2.0.0

A big update: smoother, faster and no more getting stuck.

### New
- **Move around the map.** Every 3 waves (you can change this), all mobs, creatures and dropped items are removed, you are moved to a new safe spot on the island, your health is restored, and farming carries on there. This keeps long sessions from getting laggy. There's also a **Move now** button.
- **Clear dropped items every wave.** Coins and other loot are removed after each wave so they don't pile up. Power-ups are kept (you can turn that off).
- **Logs tab.** Every session is recorded and given a grade from S to F, with a simple tip on how to improve it. Shows a kills-over-time chart, your past sessions, and a ranking of every setup you've tried so you can see which one works best on your PC. Sessions are also saved as spreadsheet (CSV) files.
- **Troubleshooting log.** `BepInEx/MuckinatorGrind/debug.log` records what the mod is doing, including freezes, so problems can be found and fixed. The previous game's log is kept as `debug-previous.log`. Found in the Settings tab, with a **Mark a problem now** button.
- The HUD shows your live grade, kills per minute and FPS.
- New **Default** preset.

### Changed
- New default settings: **100 mobs per wave, 150 alive at most, a wave every 2 seconds, 12 m radius, move every 3 waves.**
- The God Sword no longer drops your slot-1 item on the ground. It moves to a free inventory slot instead (it's only dropped if your inventory is full).

### Fixed
- **Sessions getting stuck and the camera locking up.** If a mob got stuck somewhere the sword couldn't reach, the next wave never came, and auto-aim kept the camera pointed at that mob so the mouse seemed to stop working. Now auto-aim gives up on mobs it can't reach, and if nothing dies for 20 seconds the leftover mobs are cleared and the next wave starts.

## 1.0.0

- First release.
- Spawns Lil Dave, Goblin (default) or Stone Golem in waves around you, with 5 quick presets (Trickle to Maximum).
- Auto God Sword, auto-aim, auto-click, unlimited health and unlimited stamina.
- Muckinator tracker: reads your Steam kill count and shows your next Muckinator (1, 2, 3 or 4) with progress, kills per minute and time left.
- Kill target that stops the session by itself.
- Small HUD and changeable hotkeys (F2 menu, F8 start/pause).
- Resizable menu (drag the bottom-right corner) with adjustable opacity.
