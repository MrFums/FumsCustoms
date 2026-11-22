# Changelog

## 1.0.1

- MapVariants is now optional. Without it, Customs is always Fums Customs.
- With MapVariants 1.2.0+ and CustomsClassic 1.1.0+ installed, Customs offers Vanilla, Fums and Classic. With older versions, this mod still turns itself off while CustomsClassic is installed.
- Fixed: with CustomsClassic on the server only (or no FumsCustoms on the server), clients still loaded this Customs over the server's other Customs data. Most containers then had no loot and opened like doors. Each client now asks the server, and loads vanilla Customs unless the server serves this one. Install the same version on the server and every client, Fika headless included.
- The startup line about 13 Customs loot items that will not spawn now says it is expected and not an error. These items exist only in the 1.x game.

## 1.0.0

First release.

- Customs from version 1.3.0 of the base game: the 1.3 map, doors and keys, lights, water, sounds, sky and extracts.
- 1.x loose loot, containers, static loot and raid settings. Other mods' Customs loot is kept.
- A bot navigation mesh baked from the 1.3 map, covering the new areas.
- MapVariants lets you choose this Customs or the SPT one on the map screen.
- Reshala's bunkhouse key drops at live rates and can be sold on the flea.
- With CustomsClassic installed, this mod turns itself off and Customs offers CustomsClassic's versions.
