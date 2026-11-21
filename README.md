# ErrorPatch

SMAPI mod for Stardew Valley 1.6 (Steam). It checks every installed mod at game launch, downloads missing required mods, and restarts the game so SMAPI loads them on the next launch.

## Install

1. Install SMAPI 4.x for Stardew Valley 1.6.
2. Copy `ErrorPatch.dll` and `manifest.json` into `Stardew Valley/Mods/ErrorPatch/`.
3. Create `Stardew Valley/Mods/downloads.json` with direct ZIP URLs for the mods you want auto-installed:

```json
{
  "SpaceCore": "https://example.com/mods/SpaceCore.zip",
  "ContentPatcher": "https://example.com/mods/ContentPatcher.zip"
}
```

Press Play in Steam. ErrorPatch will check dependencies, download what is missing, write `Mods/errorpatch-report.txt`, and restart the game if anything was installed.

## Notes

- Dependencies are read from each mod's `manifest.json` (`Dependencies`, `ContentPackFor`, `MinimumGameVersion`).
- Optional dependencies (`IsRequired: false`) are skipped.
- SMAPI can only load mods that exist at startup, so a restart is required after downloading.
- Only direct `http`/`https` links are supported; Nexus/other APIs need an API key and are not used.
