![](logo.png)
# KindredLogistics for V Rising 2.0

> **Notice**
> - Due to the *vanilla* bug of the advanced furnace having a hidden recipe for bottles, Logistics reveals it on the furnace so it can be toggled off.
> - **Dependency Added:** KindredLogistics now includes [**HookDOTS API**](https://thunderstore.io/c/v-rising/p/cheesasaurus/HookDOTS_API/) as a required dependency as of v1.6 and above.

KindredLogistics is a server modification for V Rising that adds expansive features like stashing, crafting, pulling, searching for items, conveyor system for chain crafting, and auto stashing of servant inventories.

- It is entirely server side, and you can double tap R with your inventory open to stash, or double click the sort button to stash. (Legacy .stash is also available) Contained within a territory!
- You can pull items from your chests no matter where they are on territory for crafting by right clicking the recipe in the crafting station!
- Servants will autostash their inventories into chests or mission overflow chests (Label them "spoils").
- Auto Salvage: Place items in a chest and label it "salvage" to have it automatically send them to a devourer!
- Unit Spawner Refills: Place items in a chest and label it "spawner" to have it automatically send them to a tomb, vermin nest or stygian spawner!
- Brazier Refills: Place items in a chest and label it "brazier" to have it automatically send them to a brazier!
  - Night: on all of the time, including night for decorative purposes.
  - Prox: high efficiency! Only on when an allied player is nearby during the day
- Never lose where your stuff is again! Use .finditem to find where your items are stored!
- Tired of running around from station to station to make something? No worries! Use the conveyor system to link chests and refining inventories for chain crafting!
- Repair your gear without fetching materials yourself!
- Trash containers to dump unwanted items into and empty them with ease!
- Overflow chests for stashing to keep your conveyor system flowing!

[V Rising Modding Discord](https://vrisingmods.com/discord) | [How to Install Mods on BepInEx](https://wiki.vrisingmods.com/user/Mod_Install.html) | [How to Use Mods In-Game](https://wiki.vrisingmods.com/user/Using_Server_Mods.html)

[![logwouldyou](https://github.com/user-attachments/assets/4412fd55-cf6d-488b-9e40-77fba9f83afa)](https://github.com/Odjit/KindredLogistics/wiki)
Check out the details on the WIKI by clicking above!

# Chat Commands Overview

## Player Commands

| Command | Shortcut | Description |
|---|---|---|
| .logistics sortstash | .l ss | Toggle autostashing via the sort button in inventory. |
| .logistics craftpull | .l cr | Toggle pulling missing ingredients from chests when right-clicking a recipe. |
| .logistics dontpulllast | .l dpl | Toggle keeping the last item in a container (prevents pulling the last stack). |
| .logistics autostashmissions | .l asm | Toggle servants autostashing loot into chests/mission overflow chests. |
| .logistics conveyor | .l co | Toggle the conveyor system (sender/receiver linked inventories). |
| .logistics salvage | .l sal | Toggle auto salvaging from chests named "salvage". |
| .logistics unitspawner | .l us | Toggle auto-filling unit spawners from chests named "spawner". |
| .logistics brazier | .l bz | Toggle auto-filling braziers from chests named "brazier". |
| .logistics silentstash | .l ssh | Toggle hiding stash destination messages. |
| .logistics silentpull | .l sp | Toggle hiding pull source messages. |
| .logistics stashblacklist | .l sbl | Toggle per-player stash blacklist (keeps blacklisted items in inventory during .stash). |
| .logistics settings | .l s | Show the current status of all settings. |

## Stash Blacklist Commands

| Command | Shortcut | Description |
|---|---|---|
| .logistics blacklist | .l bl | List all blacklisted items. |
| .logistics blacklist [item] [count] | .l bl [item] [count] | Add/update a blacklist entry (0 to remove). Item is searched by name (partial match). |
| .logistics blacklistclear | .l blclear | Clear your entire blacklist. |

## Keep/Only Stack Commands

The K (Keep) and O (Only) systems use named tags on chests to control item flow:
- **K** (Keep) = floor on pulls. A chest tagged `K3` will always keep at least 3x max stack of each item.
- **O** (Only) = cap on deposits. A chest tagged `O2` will never receive more than 2x max stack of each item.
- K and O can coexist on the same chest (e.g. `K1 O5`).
- Both share the same multiplier table (K1 = O1 = same multiplier), configurable per template ID (0-9).

| Command | Shortcut | Description |
|---|---|---|
| .logistics keepstack | .l ks | Show all K/O stack multipliers (0-9). |
| .logistics keepstack [id] [multiplier] | .l ks [id] [mult] | Set the multiplier for a K/O template ID (0-9). |

## Stash & Search Commands

| Command | Shortcut | Description |
|---|---|---|
| .stash | | Send all items (except hotbar) to chests in your territory. |
| .pull [item] [quantity] | | Pull specified items from your chests to your inventory. |
| .finditem [item] | .fi | Search for an item and show which chests contain it. |
| .findchest [name] | .fc | Search for a chest by name. |

## Conveyor System

Name your chests and stations with S (sender) and R (receiver) group tags to create item flow chains:
- `s5` on a chest = sends items to group 5
- `r5` on a station = receives items from group 5
- A station can have multiple groups: `s5r30r50` sends to group 5, receives from groups 30 and 50
- An overflow chest (named "overflow") catches items that don't fit in their destination

## Admin Commands

| Command | Shortcut | Description |
|---|---|---|
| .logisticsglobal sortstash | .lg ss | Toggle sort-to-stash globally. |
| .logisticsglobal pull | .lg p | Toggle pulling from containers globally. |
| .logisticsglobal craftpull | .lg cr | Toggle craft pull globally. |
| .logisticsglobal autostashmissions | .lg asm | Toggle servant autostash globally. |
| .logisticsglobal conveyor | .lg co | Toggle the conveyor system globally. |
| .logisticsglobal salvage | .lg sal | Toggle auto salvage globally. |
| .logisticsglobal unitspawner | .lg us | Toggle unit spawner refills globally. |
| .logisticsglobal brazier | .lg bz | Toggle brazier refills globally. |
| .logisticsglobal named | .lg nam | Toggle night/proximity controlled braziers globally. |
| .logisticsglobal trash | .lg trash | Toggle trash containers globally. |
| .logisticsglobal settings | .lg s | Show global settings status. |
| .adminstash [item] [amount] | | Spawn items and stash them to the current territory. |
| .emptytrash | | Empty all items from all trash containers at once. |

## Dependencies & Credits
- **Originally created by [Odjit](https://github.com/Odjit/KindredLogistics)** (Dj) and **Zfolmt [(Mitch)](https://www.patreon.com/join/4865914)** - this is a fork of their work.
- **[HookDOTS API](https://thunderstore.io/c/v-rising/p/cheesasaurus/HookDOTS_API/) by cheesasaurus**: Provides DOTS-compatible hook systems used internally for event handling.
- **[VCF](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) by Deca**: Powers all chat commands in KindredLogistics.
- **[V Rising Modding Community](https://vrisingmods.com)** for ideas, testing and feedback.
- Historical mods that inspired: *QuickStash* and *QuickBrazier* by assorted authors.

This mod is licensed under the AGPL-3.0 license.
