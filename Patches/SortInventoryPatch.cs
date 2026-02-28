using HarmonyLib;
using KindredLogistics;
using ProjectM;
using ProjectM.Behaviours;
using ProjectM.Network;
using Steamworks;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace KindredLogistics.Patches
{
    [HarmonyPatch]
    public class SortSingleInventorySystemPatch
    {
        // Would be better as a circular buffer but in general this will be one element so doesn't really matter
        static List<(ulong, double)> lastSort = [];
        static List<(ulong, double)> lastTrashSort = [];

        [HarmonyPatch(typeof(SortSingleInventorySystem), nameof(SortSingleInventorySystem.OnUpdate))]
        [HarmonyPrefix]
        static void Prefix(SortSingleInventorySystem __instance)
        {
            var entities = __instance._EventQuery.ToEntityArray(Allocator.Temp);
            try
            {
                // Do a first pass of removing old entries from the lastSort list
                var serverTime = Core.ServerTime;
                for (int i = lastSort.Count - 1; i >= 0; i--)
                {
                    var lastSortTime = lastSort[i].Item2;
                    if ((serverTime - lastSortTime) >= 1)
                        lastSort.RemoveAt(i);
                }

                for (int i = lastTrashSort.Count - 1; i >= 0; i--)
                {
                    var lastSortTime = lastTrashSort[i].Item2;
                    if ((serverTime - lastSortTime) >= 1)
                        lastTrashSort.RemoveAt(i);
                }

                foreach (Entity entity in entities)
                {
                    if (entity.Equals(Entity.Null)) continue;

                    var fromCharacter = entity.Read<FromCharacter>();

                    var sort = entity.Read<SortSingleInventoryEvent>();
                    
                    var playerInventoryNetworkId = fromCharacter.Character.Read<NetworkId>();
                    var steamId = fromCharacter.User.Read<User>().PlatformId;

                    if (sort.Inventory == playerInventoryNetworkId)
                    {
                        if (!Core.PlayerSettings.IsSortStashEnabled(steamId)) continue;

                        var found = false;
                        for (int i = lastSort.Count - 1; i >= 0; i--)
                        {
                            if (lastSort[i].Item1 != steamId) continue;

                            Core.Stash.StashCharacterInventory(fromCharacter.Character);
                            lastSort.RemoveAt(i);
                            found = true;
                            break;
                        }

                        if (!found)
                        {
                            lastSort.Add((steamId, serverTime));
                        }
                    }
                    else
                    {
                        // Maybe trash
                        var found = false;
                        for (int i = lastTrashSort.Count - 1; i >= 0; i--)
                        {
                            if (lastTrashSort[i].Item1 != steamId) continue;

                            lastTrashSort.RemoveAt(i);
                            found = true;

                            // Check if its a trash container
                            var territoryIndex = Core.TerritoryService.GetTerritoryId(fromCharacter.Character);
                            foreach (var trashContainer in Core.Stash.GetAllTrashStashes(territoryIndex))
                            {
                                if (trashContainer.Read<NetworkId>() != sort.Inventory) continue;
                                Core.Trash.EmptyTrash(fromCharacter.Character, trashContainer);
                                break;
                            }
                            break;
                        }

                        if (!found)
                        {
                            lastTrashSort.Add((steamId, serverTime));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Log.LogError(ex);
            }
            finally
            {
                entities.Dispose();
            }
        }       
    }
}