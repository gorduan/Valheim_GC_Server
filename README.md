# GC Server (Valheim mod)

Wardrobe outfits, leashes, whistles, saddles, Animal Care, more tameable animals, fields, ladders, Valkyrie carts. With EpicLoot (magic leashes), WAP, PlantEverything. More on GitHub. Gorduan Community (GC). Free to use, modify, re-upload (MIT).

Version 1.9.0 · Author: Gorduan · License: MIT

## Included mods

All mods of this package are in GC_Server.dll, next to GC_Kern.dll (the shared core).

| Mod | Level | Runs on | Feature |
|---|---|---|---|
| GC_Kern | 0 Core | always, on server and clients | **GC core**: GC Option menu for all GC mods (pause and main menu), switch single mods off, server level, mod check on joining, optional boss progression. Explanations for every feature in DeepTip windows. |
| GC_Outfit | 1 Very vanilla | server and all clients | **Outfit**: A wardrobe (workbench, 10 fine wood, 10 bronze nails) holds your outfit: put gear in and your character shows its look, while stats stay those of the worn pieces. The outfit is kept on death, armor pieces can be hidden. |
| GC_Portalnamen | 1 Very vanilla | client, optional server part | **Portal names**: List of portal names when renaming a portal: your own list and, if the server has the mod, the server list. |
| GC_Felder | 3 Vanilla+ | client, optional server part | **Fields for the cultivator**: Draw fields with the cultivator and plant them automatically. The server stores the fields and sets the rules. |
| GC_Blitzspule | 4 GC light | server and all clients | **Lightning coils**: Up to three lightning coils at the Obliterator divert its lightning and return 25, 55 or 85 % of the crafting materials, EpicLoot items also 50-80 % of their sacrifice products. |
| GC_Futterfolge | 4 GC light | server and all clients | **Feeding order**: Tamed animals see food twice as far when a path leads there. The highest level eats first, pregnant animals last. |
| GC_Leiter | 4 GC light | server and all clients | **Ladder**: Buildable ladder pieces (1 m) with a fixed front. Stack them to any height, E takes you to the top or bottom. |
| GC_Parade | 4 GC light | server and all clients | **Parry**: A perfect parry throws arrows and projectiles back. |
| GC_Spitzhacke | 4 GC light | server and all clients | **Pickaxe as weapon**: Pickaxes strike like a mace with the middle mouse button and work as a weapon. |
| GC_Zuchtgrenze | 4 GC light | server and all clients | **Breeding limit**: More animals per area before breeding stops. With GC_Tiere, leashed animals count half. |
| GC_Tierarten | 5 GC | server and all clients | **Animal species and saddles**: Higher max levels per species (lox 300, wolf and bear 200), stars at birth, more loot, more tameable animals (bear, deer, hare, seal, Neck), bear cub and Unbjorn, bear and Unbjorn saddles, juveniles that fight. (requires GC_Tiere) |
| GC_Tiere | 5 GC | server and all clients | **Animals: leashes, whistles, animal care**: Minecraft-like leashes (one per biome) with posts and portal travel, four whistles to command your animals, the Animal Care skill, animal experience and breeding levels. With EpicLoot: enchantable leashes, whistles and saddles, including unique items. |
| GC_WalkuerenTransport | 5 GC | server and all clients | **Valkyrie transport**: The Valkyrie whistle calls a valkyrie that picks up your cart for 20 lox meat and drops it near you later, followed by a raid. Includes an easter egg. (requires GC_Tiere) |

## Compatibility

Works together with these mods and extends some of them. All of them are optional, unless the table above says a mod requires one.

- **[EpicLoot](https://thunderstore.io/c/valheim/p/RandyKnapp/EpicLoot/)**
  - GC_Kern: Optional boss progression counts EpicLoot as at least +10 %.
  - GC_Blitzspule: Enchanted items also return 50-80 % of their sacrifice products.
  - GC_Parade: Deflecting also works with EpicLoot's parry window effect.
  - GC_Tiere: Leashes, whistles and all saddles (also those of GC_Tierarten) can be enchanted with their own effects, including unique legendary and mythic items.
  - GC_WalkuerenTransport: Carried items keep their enchantments.
- **[ValheimPlus](https://thunderstore.io/c/valheim/p/Grantapher/ValheimPlus_Grantapher_Temporary/)**
  - GC_Kern: Shows the ValheimPlus settings in the GC Option menu; change them in the main menu.
  - GC_Blitzspule: Coils still need an Obliterator, even if ValheimPlus allows free placement.
- **[World Advancement Progression](https://thunderstore.io/c/valheim/p/VentureValheim/World_Advancement_Progression/)**
  - GC_Kern: Boss progression counts the bosses each player has defeated (private keys).
  - GC_Tiere: Legendary and mythic leashes and whistles check the bosses each player has defeated.
- **[PlantEverything](https://thunderstore.io/c/valheim/p/Advize/PlantEverything/)**
  - GC_Kern: Its texts come back after changing the language in game.
  - GC_Felder: Fields use their own spacing for its berry bushes and other plants without growth rules.
- **[PlantEasily](https://thunderstore.io/c/valheim/p/Advize/PlantEasily/)**
  - GC_Felder: Fields stop planting automatically while PlantEasily is installed (it plants whole grids itself).
- **[Mod Menu](https://thunderstore.io/c/valheim/p/Dismonder/ModMenu/)**
  - GC_Kern: The GC Option menu replaces it: switch any mod on or off, with profiles and dependencies. Its list and profiles are taken over once.
- **All other mods**
  - GC_Kern: The GC Option menu shows and edits the settings of every installed mod, server rules (Jotunn, ServerSync) only for admins. On joining, the mod check lists every mod that differs from the server.

## Rules

- **Requires [DeepTip](https://thunderstore.io/c/valheim/p/Gorduan/DeepTip/)** (since 1.9.0): every GC feature explains itself in DeepTip windows. Rest the mouse on a function name in the GC Option menu or on a highlighted term in a tooltip.
- **GC_Kern.dll** must always stay. It ships in every GC package so that each package works on its own; BepInEx loads it once and reports the other copies as "duplicate". That is intended.
- **Switching mods off:** mods that run on "server and all clients" must be the same on the server and on every client, otherwise the server refuses the connection. If you switch such a mod off on the server, your players must switch it off too.
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

`Kern/` is the GC core shared by all GC packages (the GC Option menu, settings, texts, permissions). `Patcher/` holds the preloader that lets GC Option switch mods off, `Mods/` one folder per mod, `Pakete/` the project that merges the mods into one DLL (ILRepack). Code comments and `SDK.md` are in German.

## GC packages

- [Valheim_GC_Server](https://github.com/gorduan/Valheim_GC_Server) (this repository)
- [Valheim_GC_Client](https://github.com/gorduan/Valheim_GC_Client)
- [Valheim_GC_ValheimPlusPlus](https://github.com/gorduan/Valheim_GC_ValheimPlusPlus)

## License

MIT for the code, see [LICENSE](LICENSE).

The sound files in `Mods/WalkuerenTransport/Assets/Sounds` keep their own licenses (CC0, public domain, CC BY 3.0 and 4.0), see Credits above and `QUELLEN.md` in that folder.
