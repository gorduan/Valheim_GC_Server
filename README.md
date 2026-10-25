# GC Server (Valheim mod)

Gameplay for server and players, one DLL per feature: animal leashes, whistles and an Animal Care skill, more tameable animals with saddles, Valkyrie cart transport, cultivator fields, ladders, portal name lists.

Version 1.8.1 · Author: Gorduan · License: MIT

## Included mods

Each mod is a separate DLL in the package folder. Server owners keep only the ones they want.

| File | Level | Runs on | Feature |
|---|---|---|---|
| GC_Kern.dll | 0 Core | always, on server and clients | GC core |
| GC_Portalnamen.dll | 1 Very vanilla | client, optional server part | Portal names |
| GC_Felder.dll | 3 Vanilla+ | client, optional server part | Fields for the cultivator |
| GC_Futterfolge.dll | 4 GC light | server and all clients | Feeding order |
| GC_Leiter.dll | 4 GC light | server and all clients | Ladder |
| GC_Parade.dll | 4 GC light | server and all clients | Parry |
| GC_Spitzhacke.dll | 4 GC light | server and all clients | Pickaxe as weapon |
| GC_Zuchtgrenze.dll | 4 GC light | server and all clients | Breeding limit |
| GC_Tierarten.dll | 5 GC | server and all clients | Animal species and saddles (requires GC_Tiere) |
| GC_Tiere.dll | 5 GC | server and all clients | Animals: leashes, whistles, animal care |
| GC_WalkuerenTransport.dll | 5 GC | server and all clients | Valkyrie transport (requires GC_Tiere) |

## Rules

- **GC_Kern.dll** must always stay. It ships in every GC package; BepInEx loads it once and reports the other copies as "duplicate". That is intended.
- **Removing DLLs:** mods that run on "server and all clients" must be the same on the server and on every client, otherwise the server refuses the connection. If you remove DLLs, your players must remove the same ones.
- **Dependencies:** GC_Tierarten requires GC_Tiere; GC_WalkuerenTransport requires GC_Tiere.
- **Settings:** GC Option menu (button in the pause and main menu) or `BepInEx/config/gorduan.gc.<id>.cfg`. On first start every mod takes over the values from the old cfg files.

## Credits

The sound files in `Mods/WalkuerenTransport/Assets/Sounds` are not covered by the MIT license of the code. Sources, licenses and all edits are listed in `QUELLEN.md` there (German).

**Music by Kevin MacLeod:** "Strength of the Titans", "Five Armies", "Arcadia". Kevin MacLeod (incompetech.com), licensed under Creative Commons: By Attribution 4.0, http://creativecommons.org/licenses/by/4.0/. Changes: "Five Armies" shortened, "Arcadia" used as an excerpt one octave lower with reverb (choir_doom.ogg), all normalized.

**Ride of the Valkyries:** "Ride of the Valkyries" (Richard Wagner), conducted by Philip Milman (https://pmmusic.pro/), published by Lud and Schlatts Musical Emporium, https://www.youtube.com/watch?v=uNkRW_9pHRQ. License: CC BY 3.0, https://creativecommons.org/licenses/by/3.0/. Changes: compressed, made louder, normalized, faded out.

**Screaming Hawk:** "Screaming Hawk" by PsychoBird, https://soundbible.com/1517-Screaming-Hawk.html. License: CC BY 3.0, https://creativecommons.org/licenses/by/3.0/. Changes: pitched down, doubled, reverb added (valk_screech.ogg).

**Thanks (CC0, no attribution required):** nick121087 (scream, via Wikimedia Commons), Thimras, Joth and bart (OpenGameArt). `quake_rumble.ogg` is our own synthesis.

## Requirements

- Valheim 1.0.20 with BepInExPack Valheim 5.4.2350 or newer
- Jötunn 2.30.2
- Optional: Epic Loot (some mods do more when installed)

## Building

1. Install the .NET SDK 9 or newer.
2. Copy `GamePaths.user.props.example` to `GamePaths.user.props` and set `GamePath` (your Valheim folder with BepInEx) and `JotunnDll`.
3. Run `powershell -ExecutionPolicy Bypass -File build.ps1`. The package folder and a zip for r2modman ("Import local mod") end up in `build-output/`.

`Kern/` is the GC core shared by all GC packages (the GC Option menu, settings, texts, permissions). `Patcher/` holds the preloader that lets GC Option switch mods off, `Mods/` one folder per mod. Code comments and `SDK.md` are in German.

## GC packages

- [Valheim_GC_Server](https://github.com/gorduan/Valheim_GC_Server) (this repository)
- [Valheim_GC_Client](https://github.com/gorduan/Valheim_GC_Client)
- [Valheim_GC_ValheimPlusPlus](https://github.com/gorduan/Valheim_GC_ValheimPlusPlus)

## License

MIT for the code, see [LICENSE](LICENSE).

The sound files in `Mods/WalkuerenTransport/Assets/Sounds` keep their own licenses (CC0, public domain, CC BY 3.0 and 4.0), see Credits above and `QUELLEN.md` in that folder.
