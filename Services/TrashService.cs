using KindredLogistics;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;
using VampireCommandFramework;

class TrashService
{

    bool CanEmptyTrash(Entity charEntity, out int territoryIndex)
    {
        var userEntity = charEntity.Read<PlayerCharacter>().UserEntity;
        var user = userEntity.Read<User>();

        territoryIndex = Core.TerritoryService.GetTerritoryId(charEntity);
        if (territoryIndex == -1)
        {
            Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to empty trash outside territories!");
            return false;
        }

        var downed = new PrefabGUID(-1992158531);
        if (BuffUtility.TryGetBuff(Core.EntityManager, charEntity, downed, out var buff))
        {
            Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to empty trash while downed!");
            return false;
        }

        var health = charEntity.Read<Health>();
        if (health.IsDead)
        {
            Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Unable to empty trash when dead!");
            return false;
        }

        var castleHeartEntity = Core.TerritoryService.GetCastleHeart(territoryIndex);
        if (castleHeartEntity == Entity.Null)
        {
            Utilities.SendSystemMessageToClient(Core.EntityManager, user, "There is no heart on this territory!");
            return false;
        }

        if (!Core.ServerGameManager.IsAllies(castleHeartEntity, charEntity))
        {
            Utilities.SendSystemMessageToClient(Core.EntityManager, user, "You aren't allies with the heart on this territory!");
            return false;
        }

        var castleHeart = castleHeartEntity.Read<CastleHeart>();
        if (castleHeart.ActiveEvent >= CastleHeartEvent.Attacked)
        {
            Utilities.SendSystemMessageToClient(Core.EntityManager, user, $"Unable to empty trash while castle is {castleHeart.ActiveEvent.ToString()}");
            return false;
        }

        if (BuffUtility.TryGetBuff(Core.Server.EntityManager, charEntity, Const.Buff_InCombat_PvPVampire, out Entity buffEntity))
        {
            Utilities.SendSystemMessageToClient(Core.EntityManager, user, $"Unable to empty trash while in PvP combat.");
            return false;
        }

        return true;
    }

    public void EmptyTrash(Entity charEntity)
    {
        if (!CanEmptyTrash(charEntity, out var territoryIndex)) return;

        var trashCansEmptied = 0;
        foreach (var trashCan in Core.Stash.GetAllTrashStashes(territoryIndex))
        {
            if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, trashCan, out Entity inventory))
                continue;

            var itemBuffer = Core.EntityManager.GetBuffer<InventoryBuffer>(inventory);
            for (int i = 0; i < itemBuffer.Length; i++)
            {
                if (itemBuffer[i].Amount == 0) continue;
                InventoryUtilitiesServer.ClearSlot(Core.EntityManager, inventory, i);
            }

            trashCansEmptied++;
        }

        var userEntity = charEntity.Read<PlayerCharacter>().UserEntity;
        var user = userEntity.Read<User>();
        Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Trash emptied from " + trashCansEmptied.ToString().Color(Color.White) +"x trash containers.");
    }

    public void EmptyTrash(Entity charEntity, Entity trashContainer)
    {
        if (!CanEmptyTrash(charEntity, out var territoryIndex)) return;

        if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, trashContainer, out Entity inventory))
            return;

        var itemBuffer = Core.EntityManager.GetBuffer<InventoryBuffer>(inventory);
        for (int i = 0; i < itemBuffer.Length; i++)
        {
            if (itemBuffer[i].Amount == 0) continue;
            InventoryUtilitiesServer.ClearSlot(Core.EntityManager, inventory, i);
        }

        var userEntity = charEntity.Read<PlayerCharacter>().UserEntity;
        var user = userEntity.Read<User>();
        Utilities.SendSystemMessageToClient(Core.EntityManager, user, "Sunlight, at this hour, in this castle, localized entirely within this trash bin? This trash is ashed.");
    }
}