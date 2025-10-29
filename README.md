![](logo.png)
# KindredLogistics for V Rising 1.1!

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

This was a collaborative effort from Odjit (Dj) and Zfolmt [(Mitch)](https://www.patreon.com/join/4865914). Feel free to reach out to either of us on the community discord if you have questions or need help with the mod!
Updated for 1.1 as of 1.5.0

[V Rising Modding Discord](https://vrisingmods.com/discord) | [How to Install Mods on BepInEx](https://wiki.vrisingmods.com/user/Mod_Install.html) | [How to Use Mods In-Game](https://wiki.vrisingmods.com/user/Using_Server_Mods.html)


[![logwouldyou](https://github.com/user-attachments/assets/4412fd55-cf6d-488b-9e40-77fba9f83afa)](https://github.com/Odjit/KindredLogistics/wiki)
Check out the details on the WIKI by clicking above!

# Chat Commands Overview

## Player Commands

| Command                          | Description                                                                                   | Shortcut     |
|----------------------------------|-----------------------------------------------------------------------------------------------|--------------|
| .logistics sortstash             | Turns on or off your personal ability to use the sort button in your inventory to stash items. | .l ss        |
| .logistics craftpull             | Turns on or off your personal ability to pull items from chests for crafting in stations.      | .l cr        |
| .logistics dontpulllast          | Turns on or off your personal ability to not pull the last item from a container.              | .l dpl       |
| .logistics autostashmissions     | Turns on or off your servants from autostashing into chests/Mission overflow chests.           | .l asm       |
| .logistics conveyor               | Turns on or off your personal ability to set up linked inventories for crafting materials.    | .l co        |
| .logistics salvage               | Turns on or off your personal ability to set up auto salvaging from a labelled chest.          | .l sal       |
| .logistics unitspawner            | Turns on or off your personal ability to set up feeding unitspawners from a labelled chest.   | .l us        |
| .logistics brazier               | Turns on or off your personal ability to set up feeding braziers from a labelled chest.        | .l bz        |
| .logistics silentstash            | Turns on or off the ability to have stash not report where you stash items.                   | .l ssh       |
| .logistics silentpull             | Turns on or off the ability to have pull not report where you pull items from.                | .l sp        |
| .logistics settings               | Shows the enabled/disabled status of the above systems.                                       | .l s         |

## Stash Commands

| Command                          | Description                                                                                   | Example                |
|----------------------------------|-----------------------------------------------------------------------------------------------|------------------------|
| .stash                           | Sends all items besides the hotbar in your inventory to chests in your territory.           |                        |
| .pull (item) (quantity)          | Takes specified items out of your chests to your inventory.                                 | .pull plank 50         |
| .finditem (item)                 | Searches for an item and returns the names of the chests that contain it.                   | .fi "Blood Essence"    |
| .findchest (name)                 | Searches for a chest by name and returns list of matching chests.	                   | .fc sal    |

## Admin Commands

| Command                          | Description                                                                                   | Shortcut     |
|----------------------------------|-----------------------------------------------------------------------------------------------|--------------|
| .logisticsglobal sortstash       | Turns on or off the ability for players to use the sort button in their inventory.            | .lg ss       |
| .logisticsglobal pull            | Turns on or off the ability for players to pull items from containers.                        | .lg p        |
| .logisticsglobal craftpull       | Turns on or off the ability for players to pull items from chests for crafting.               | .lg cr       |
| .logisticsglobal autostashmissions | Turns on or off the ability for servants to autostash into chests.                          | .lg asm      |
| .logisticsglobal conveyor         | Turns on or off the ability for players to set up linked inventories for crafting materials. | .lg co       |
| .logisticsglobal salvage         | Turns on or off the ability for players to set up auto salvaging from a labelled chest.       | .lg sal      |
| .logisticsglobal unitspawner     | Turns on or off the ability for players to set up feeding unitspawners from a labelled chest. | .lg us       |
| .logisticsglobal brazier         | Turns on or off the ability for players to set up feeding braziers from a labelled chest.     | .lg bz       |
| .logisticsglobal named           | Turns on or off the ability for players to name braziers night/prox to control behaviour.     | .lg sol      |
| .logisticsglobal settings         | Shows the enabled/disabled status of the logistics systems.                                  | .lg s        |
| .adminstash (item) (amount)       | Spawns in items to stash to the current territory                                 |       |
| .emptytrash        | Empties all items in all of your trash containers at once.                                |        |




## **Dependencies & Credits**
- **[HookDOTS API](https://thunderstore.io/c/v-rising/p/cheesasaurus/HookDOTS_API/) by cheesasaurus**: Provides DOTS-compatible hook systems used internally for event handling.  
- **[VCF](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) by Deca**: Powers all chat commands in KindredLogistics
- Special thanks to:
  - **Zfolmt [(Mitch)](https://www.patreon.com/join/4865914)** for co-developing features in KindredLogistics.
  - **[V Rising Modding Community](https://vrisingmods.com)** for ideas, testing and feedback.
- Historical mods that inspired:
  - *QuickStash* and *QuickBrazier* by assorted authors.



 

This mod is licensed under the AGPL-3.0 license.