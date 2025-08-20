using HarmonyLib;
using KindredLogistics;
using ProjectM;
using ProjectM.Network;
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

                foreach (Entity entity in entities)
                {
                    if (entity.Equals(Entity.Null)) continue;

                    var fromCharacter = entity.Read<FromCharacter>();
                    var steamId = fromCharacter.User.Read<User>().PlatformId;

                    if (!Core.PlayerSettings.IsSortStashEnabled(steamId)) continue;
                    
                    var found = false;
                    for(int i = 0; i < lastSort.Count; i++)
                    {
                        if (lastSort[i].Item1 != steamId) continue;
                        
                        Core.Stash.StashCharacterInventory(fromCharacter.Character);
                        lastSort.RemoveAt(i);
                        break;
                    }

                    if(!found)
                    {
                        lastSort.Add((steamId, serverTime));
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