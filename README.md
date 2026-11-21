# Fums 1.3 Customs Backport

Customs from version 1.3.0 of the base game (client build 48484), backported to SPT 4.1.6. MapVariants (required) lets you choose this Customs or the SPT one on the map screen.

## Requirements

- SPT 4.1.6 or a later 4.1 release, on every PC that runs the game, including a Fika headless
- MapVariants 1.1.0 or later
- WTT-ContentBackport 2.0.7 or later, and WTT-CommonLib, which it needs

## Install

Download the zip from [Releases](https://github.com/MrFums/FumsCustoms/releases). Copy its `BepInEx` and `SPT_Runtime` folders into your SPT folder.

## Known issue: CustomsClassic

MapVariants can only give a map to one mod at a time. With CustomsClassic installed, this mod turns itself off and Customs offers CustomsClassic's versions instead. Nothing breaks, and every other mod still loads. Remove CustomsClassic to play this Customs. A MapVariants change is planned so both can be offered together.

## Building

Put your SPT folder's path in `spt-path.txt` next to `Directory.Build.props`, then run:

```
dotnet build client -c Release
dotnet build server -c Release
```

The map bundles and the loot data are only in the release zip.

## Credits

- The loot, exit and raid data is generated from the SPT 5.0 team's live data dumps.
- The map is by Battlestate Games.
