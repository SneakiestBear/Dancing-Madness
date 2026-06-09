## Dancing Madness

Dancing Madness is a Dalamud plugin for Final Fantasy XIV raiding, focused on adding support for the **Dancing Mad (Ultimate)** encounter — most notably an automarker for the **Forsaken** tower/AoE sequence (configurable tower order, priority, and per-role markers).

It is a fork of [Lemegeton](https://github.com/paissaheavyindustries/Lemegeton) and inherits its broad feature set (automarkers, overlays, and helpers for a wide variety of content), with the Dancing Mad support added on top.

## Dancing Mad (Forsaken) automarker

- Detects the Forsaken cast and follows the tower sequence
- Configurable tower order (e.g. `AAABBBBA`, `ABBAABBA`, `AAAABBBB`)
- Marks only the currently-active group; persistent per-player categories (Stack / Cone / AoE)
- Configurable markers and role priority
- Built-in debug simulator to drive the whole sequence with fake packets (no boss pull needed)

## Installing

This plugin runs under [Dalamud](https://github.com/goatcorp/FFXIVQuickLauncher). As a source fork it is built from this repository (Visual Studio / the .NET SDK) and added to Dalamud as a dev plugin; it is not currently published to a Dalamud repository.

## Credits

Based on **Lemegeton** by **Paissa Heavy Industries**, used under the MIT License (see [LICENSE](LICENSE)). All credit for the underlying plugin and its features goes to the original authors:

https://github.com/paissaheavyindustries/Lemegeton
