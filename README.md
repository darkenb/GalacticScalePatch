# BUG_GalacticScalePatch

Fixes for Galactic Scale 2.80.7, applied while the game runs. Galactic Scale's own files are not changed: removing this mod gives you plain Galactic Scale back.

If the installed Galactic Scale is not the version these fixes were written for, nothing is patched and the BepInEx log says so. Layout fixes for Galactic Scale's screens are in [BUG_UIFix](https://thunderstore.io/c/dyson-sphere-program/p/BUG/BUG_UIFix/).

This mod only applies fixes; it adds no features of its own. It needs Galactic Scale 2.80.7 and does nothing without it.

## Memory

- Generating a new galaxy no longer keeps the textures, materials and sounds of every galaxy made before it. On the new game screen each change of a setting made a whole new set; they stayed in memory until the game closed. Now each set is freed when the next galaxy is made. The built-in themes and themes from other mods are never freed.
- Theme copies share the sound and sky reflection they came from instead of copying them, and a tinted sky reflection is made once, not every time a theme is applied.
- When planet scanning runs out of memory, the remaining scans of that galaxy are dropped instead of retried one after another. The next galaxy starts fresh.

## Planet scanning

- A planet whose scan fails no longer keeps half-made scan data, and is scanned again later.
- Scanning a planet that is already loaded keeps its data instead of replacing it with a fresh copy.
- Leaving to the main menu during a scan no longer marks the planet as scanned with empty data.

## New game screen

- Changing a setting while a galaxy is being generated waits for that generation to finish, instead of replacing the settings under it.
- Dragging a slider (for example UniverseGenTweaks' four sliders) makes one galaxy when you let go, not one per step of the drag. The preview is no longer made after the screen is closed.
- Pressing a slider, toggle or button of the new game settings no longer also clicks the star behind it. Before, the star's system view could open under the settings of other mods.

## Saves

- If a save's Galactic Scale settings file (`.gs2`) cannot be read, loading stops with an error. Before, the save was loaded with the settings of the previous galaxy, which could break it.

## Error log

- Galactic Scale's error log has the full error with its cause and where it happened, not only the message.

## Licence

MIT. Galactic Scale is released under CC0 1.0; this mod contains none of its files and patches it at runtime. Not made or endorsed by the Galactic Scale authors.

## Building from source

You need a current .NET SDK and the .NET Framework 4.7.2 targeting pack. The game, Unity and BepInEx assemblies are referenced from your own installation and are not part of this repository.

```text
dotnet build -c Release "-p:DSPGamePath=D:\Games\Dyson Sphere Program" "-p:BepInExCorePath=C:\path\to\BepInEx\core"
```

The output is `bin/Release/net472/BUG_GalacticScalePatch.dll`. Copy it to `BepInEx/plugins/BUG_GalacticScalePatch/`.
