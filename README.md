# Fums 1.3 Customs Backport

Customs from version 1.3.0 of the base game (client build 48484), backported to SPT 4.1.6. With MapVariants installed, you choose this Customs or the SPT one on the map screen.

## Requirements

- SPT 4.1.6 or a later 4.1 release, on every PC that runs the game, including a Fika headless
- WTT-ContentBackport 2.0.7 or later, and WTT-CommonLib, which it needs
- Optional: MapVariants 1.1.0 or later, to choose Fums or Vanilla Customs on the map screen. Without it, Customs is always Fums.

## Install

Download the zip from [Releases](https://github.com/MrFums/FumsCustoms/releases). Copy its `BepInEx` and `SPT_Runtime` folders into your SPT folder.

## CustomsClassic

With MapVariants 1.2.0 or later and CustomsClassic 1.1.0 or later, the map screen offers Vanilla, Fums and Classic Customs.

With an older version of either, MapVariants can only give a map to one mod. Then, with CustomsClassic installed, this mod turns itself off and Customs offers CustomsClassic's versions instead. Nothing breaks, and every other mod still loads.

## Building

Put your SPT folder's path in `spt-path.txt` next to `Directory.Build.props`, then run:

```
dotnet build client -c Release
dotnet build server -c Release
```

The map bundles and the loot data are only in the release zip.

## Credits

- The loot, exit and raid data is generated from the SPT 5.0 team's live data dumps.
- Manimal, whose map backports (Interchange, Lighthouse, Terminal, Labs and Icebreaker) showed how a backport is built.
- The map is by Battlestate Games.
