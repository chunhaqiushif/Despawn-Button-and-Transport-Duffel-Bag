using System;
using System.Collections.Generic;
using System.Linq;

using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics;
using Microsoft.Xna.Framework;

namespace RemoveCorpses
{
  /// <summary>
  /// 遗物袋逻辑（Despawn Button 扩展）
  ///
  /// 打包本身完全复用原版：Character.Despawn() 会给「有 despawncontainer 的生物」生成容器
  /// （人类走 Tags.DespawnContainer，怪物走 Params.DespawnContainer），并把尸体内的物品搬进去。
  /// 这里只做三件事：
  ///   1) 怪物没配 Params.DespawnContainer 时，临时补一个 duffelbag，让它走原版的打包分支；
  ///   2) 抓住原版生成的那个遗物袋，登记 10 秒传送计时；
  ///   3) 10 秒后传送到主潜艇（己方袋子优先复生点，否则 Cargo 出生点；只执行一次；被拾取则取消）。
  /// 传送特效与传送时序参照 Protagonist Never Die (C#)：先播特效 → 200ms 后移动 → 再播一次。
  /// 只在服务端（单机 / 主机 / 专用服）执行。
  /// </summary>
  public partial class Mod
  {
    private const string RelicBagCoroutine = "DespawnButton.RelicBagTick";
    private const float RelicBagTeleportDelay = 10f;  // 生成后多久传送（秒）
    private const int TeleportFxDelayMs = 200;        // 传送前播特效的提前量（与 PND 一致）
    private const string RelicBagPrefabId = "duffelbag";
    private static readonly Identifier RespawnPointId = "respawn_point"; // Protagonist Never Die 的复生点

    private static readonly Color OwnSubLightColor = new Color(0f, 1f, 0f, 1f);       // 己方：绿 0,1,0,1
    private static readonly Color FriendlyNpcLightColor = new Color(1f, 1f, 0f, 1f);   // 友方 NPC：黄 1,1,0,1

    private sealed class PendingBag
    {
      public Item Bag;
      public double Due;
      public bool OwnSub;     // 袋内有己方潜艇阵营（OwnSub）的 ID 卡
    }

    private sealed class DelayedAction
    {
      public double Due;
      public Action Action;
    }

    private static readonly List<PendingBag> pendingBags = new List<PendingBag>();
    private static readonly List<DelayedAction> delayedActions = new List<DelayedAction>();
    private static bool captureNextContainer;
    private static int relicBagErrorCount;
    private static bool fxMissingLogged;

    // ---------------------------------------------------------------------
    // 驱动
    // ---------------------------------------------------------------------
    public static void StartRelicBagTicker()
    {
      try { CoroutineManager.StopCoroutines(RelicBagCoroutine); } catch { }
      try
      {
        CoroutineManager.StartCoroutine(RelicBagTickLoop(), RelicBagCoroutine);
      }
      catch (Exception e) { LogError(e); }
    }

    public static void StopRelicBagTicker()
    {
      try { CoroutineManager.StopCoroutines(RelicBagCoroutine); } catch { }
      pendingBags.Clear();
      delayedActions.Clear();
    }

    private static IEnumerable<CoroutineStatus> RelicBagTickLoop()
    {
      while (true)
      {
        try { TickRelicBags(); }
        catch (Exception e) { LogError(e); }
        yield return CoroutineStatus.Running;
      }
    }

    private static bool IsAuthority()
    {
      NetworkMember net = GameMain.NetworkMember;
      return net == null || net.IsServer;
    }

    private static void TickRelicBags()
    {
      if (!IsAuthority()) return;

      double now = Timing.TotalTimeUnpaused;

      // 延迟动作（传送前 0.2s 的提前量）
      for (int i = delayedActions.Count - 1; i >= 0; i--)
      {
        if (delayedActions[i].Due > now) continue;
        Action action = delayedActions[i].Action;
        delayedActions.RemoveAt(i);
        try { action?.Invoke(); }
        catch (Exception e) { LogError(e); }
      }

      for (int i = pendingBags.Count - 1; i >= 0; i--)
      {
        PendingBag pending = pendingBags[i];
        Item bag = pending.Bag;

        // 被拾取 / 进了容器 / 被销毁 → 取消传送与计时
        if (bag == null || bag.Removed || bag.ParentInventory != null)
        {
          pendingBags.RemoveAt(i);
          continue;
        }

        if (now < pending.Due) continue;

        pendingBags.RemoveAt(i); // 只执行一次
        TeleportBag(pending);
      }
    }

    // ---------------------------------------------------------------------
    // 清除尸体：复用原版 Despawn 的打包逻辑
    // ---------------------------------------------------------------------
    /// <summary>
    /// 按钮 / 继电器触发的清除（只改怪物这一侧）：
    /// 有掉落物且没配 despawncontainer → 临时补一个 duffelbag 让原版打包；
    /// 身上一个道具都没有 → 临时清空 despawncontainer，不生成空袋子。
    /// </summary>
    public static void DespawnWithRelicBag(Character character)
    {
      if (character == null || character.Removed) return;

      bool forcedContainer = false;
      Identifier originalContainer = default;

      try
      {
        if (!character.IsHuman)
        {
          if (HasAnyItem(character))
          {
            //有掉落物但没配 despawncontainer：临时补一个 duffelbag，走原版的打包分支
            if (character.Params.DespawnContainer.IsEmpty)
            {
              originalContainer = character.Params.DespawnContainer;
              character.Params.DespawnContainer = RelicBagPrefabId.ToIdentifier();
              forcedContainer = true;
            }
          }
          else if (!character.Params.DespawnContainer.IsEmpty)
          {
            //身上没有任何道具：临时清空 despawncontainer，不生成空袋子
            originalContainer = character.Params.DespawnContainer;
            character.Params.DespawnContainer = Identifier.Empty;
            forcedContainer = true;
          }
        }

        DespawnAndRegisterBag(character);
      }
      catch (Exception e) { LogError(e); }
      finally
      {
        if (forcedContainer) character.Params.DespawnContainer = originalContainer;
      }
    }

    /// <summary>
    /// 走原版 Despawn，并把它生成的遗物袋登记传送。
    /// 按钮清除和「尸体到时间自然清除」都走这里，两种来源的袋子行为一致。
    /// </summary>
    public static void DespawnAndRegisterBag(Character character)
    {
      if (character == null || character.Removed) return;

      captureNextContainer = true;
      try { character.Despawn(); }
      catch (Exception e) { LogError(e); }
      finally { captureNextContainer = false; }
    }

    private static bool HasAnyItem(Character character)
    {
      return character.Inventory != null && character.Inventory.AllItemsMod.Any();
    }

    /// <summary>原版 Despawn 生成遗物袋时会调用这个方法，顺手把袋子接过来登记传送。</summary>
    public static void Spawner_AddItemToSpawnQueue_Prefix(ref Action<Item> onSpawned)
    {
      if (!captureNextContainer) return;
      captureNextContainer = false;

      Action<Item> original = onSpawned;
      onSpawned = item =>
      {
        original?.Invoke(item);
        RegisterBag(item);
      };
    }

    private static void RegisterBag(Item bag)
    {
      if (bag == null || bag.Removed) return;

      // 按袋内 ID 卡的阵营决定灯色：己方潜艇阵营=绿，友方 NPC=黄，其余保持 XML 的红灯
      CharacterTeamType bagTeam = GetBagTeam(bag);
      bool ownSub = bagTeam == GetOwnSubTeam();

      if (ownSub) SetBagLight(bag, OwnSubLightColor);
      else if (bagTeam == CharacterTeamType.FriendlyNPC) SetBagLight(bag, FriendlyNpcLightColor);

      // 无论生成在哪里（含已经在主潜艇上）都登记传送
      pendingBags.Add(new PendingBag
      {
        Bag = bag,
        Due = Timing.TotalTimeUnpaused + RelicBagTeleportDelay,
        OwnSub = ownSub,
      });
    }

    /// <summary>己方潜艇阵营（OwnSub）：主潜艇所属阵营，拿不到时退回 Team1。</summary>
    private static CharacterTeamType GetOwnSubTeam()
    {
      Submarine mainSub = Submarine.MainSub;
      if (mainSub != null && mainSub.TeamID != CharacterTeamType.None) return mainSub.TeamID;
      return CharacterTeamType.Team1;
    }

    /// <summary>袋内 ID 卡的阵营：己方潜艇阵营 &gt; 友方 NPC &gt; 其它（敌方 / 无卡）。</summary>
    private static CharacterTeamType GetBagTeam(Item bag)
    {
      ItemContainer container = bag.GetComponent<ItemContainer>();
      if (container == null) return CharacterTeamType.None;

      CharacterTeamType ownTeam = GetOwnSubTeam();
      bool hasFriendlyNpc = false;

      // recursive=true：ID 卡可能被塞在袋子里的工具包 / 箱子里
      foreach (Item item in container.Inventory.FindAllItems(i => i.GetComponent<IdCard>() != null, true))
      {
        IdCard card = item?.GetComponent<IdCard>();
        if (card == null) continue;

        if (card.TeamID == ownTeam) return ownTeam;
        if (card.TeamID == CharacterTeamType.FriendlyNPC) hasFriendlyNpc = true;
      }

      return hasFriendlyNpc ? CharacterTeamType.FriendlyNPC : CharacterTeamType.None;
    }

    /// <summary>改袋子上的灯色（0.0~1.0）。</summary>
    private static void SetBagLight(Item bag, Color color)
    {
      LightComponent light = bag.GetComponent<LightComponent>();
      if (light == null) return;

      light.LightColor = color;

      // 颜色是序列化属性，服务端改了不会自动同步，手动发一次属性变更事件
      NetworkMember net = GameMain.NetworkMember;
      if (net != null && net.IsServer
          && light.SerializableProperties != null
          && light.SerializableProperties.TryGetValue("LightColor".ToIdentifier(), out SerializableProperty prop))
      {
        net.CreateEntityEvent(bag, new Item.ChangePropertyEventData(prop, light));
      }
    }

    /// <summary>主潜艇上任意一个复生点（Protagonist Never Die 的 respawn_point）。</summary>
    private static Item FindRespawnPointOnMainSub(Submarine mainSub)
    {
      List<Item> allItems = Item.ItemList;
      if (allItems == null) return null;

      List<Item> candidates = new List<Item>();
      for (int i = 0; i < allItems.Count; i++)
      {
        Item item = allItems[i];
        if (item == null || item.Removed || item.Prefab == null) continue;
        if (item.Prefab.Identifier != RespawnPointId) continue;

        // 静态摆放的复生点有时 Submarine 还没赋值，用所在舱室兜底
        Submarine itemSub = item.Submarine ?? item.CurrentHull?.Submarine;
        if (itemSub == null || itemSub.ID != mainSub.ID) continue;

        candidates.Add(item);
      }

      if (candidates.Count == 0) return null;
      return candidates[Rand.Int(candidates.Count)];
    }

    // ---------------------------------------------------------------------
    // 传送
    // ---------------------------------------------------------------------
    private static void TeleportBag(PendingBag pending)
    {
      try
      {
        Item bag = pending?.Bag;
        if (bag == null || bag.Removed) return;

        Submarine mainSub = Submarine.MainSub;
        if (mainSub == null) return;

        Vector2? destination = null;

        // 己方潜艇阵营的遗物袋优先送回复生点
        if (pending.OwnSub)
        {
          Item respawnPoint = FindRespawnPointOnMainSub(mainSub);
          if (respawnPoint != null) destination = respawnPoint.WorldPosition;
        }

        // 没有复生点（或不是己方袋子）：退回 Cargo 出生点
        if (destination == null)
        {
          WayPoint waypoint = WayPoint.GetRandom(SpawnType.Cargo, null, mainSub);
          if (waypoint == null)
          {
            Log("[Despawn Button] 主潜艇没有 Cargo 出生点，遗物袋不传送");
            return;
          }
          destination = waypoint.WorldPosition;
        }

        Vector2 target = destination.Value;

        PlayTeleportFx(bag);
        delayedActions.Add(new DelayedAction
        {
          Due = Timing.TotalTimeUnpaused + TeleportFxDelayMs / 1000.0,
          Action = () =>
          {
            if (bag == null || bag.Removed || bag.ParentInventory != null) return;
            MoveItem(bag, target);
            PlayTeleportFx(bag);
          }
        });
      }
      catch (Exception e) { LogError(e); }
    }

    private static void MoveItem(Item item, Vector2 worldPosition)
    {
      // 与 Character.TeleportTo 同样的做法：先脱离潜艇坐标系，再按世界坐标落位并重新找舱室
      item.Submarine = null;
      item.SetTransform(ConvertUnits.ToSimUnits(worldPosition), item.Rotation, findNewHull: true);
      item.FindHull();
    }

    private static void PlayTeleportFx(Item item)
    {
      if (item == null || item.Removed) return;
      try
      {
        // 诊断：袋子上没有 OnUse 状态效果（多半是 relicbag.xml 被别的模组覆盖了）
        if (item.hasStatusEffectsOfType != null && !item.hasStatusEffectsOfType[(int)ActionType.OnUse])
        {
          if (!fxMissingLogged)
          {
            fxMissingLogged = true;
            Log("[Despawn Button] 遗物袋上没有 OnUse 特效，检查 relicbag.xml 是否被其他模组覆盖");
          }
          return;
        }

        // 袋子 Holdable 上的 OnUse 状态效果：粒子 relicbag_teleporter + 三个 teleport 音效随机其一
        // deltaTime 用 1f（与原生 ApplyStatusEffects 一致），0f 时粒子发射器不会生成粒子
        item.ApplyStatusEffects(ActionType.OnUse, 1f, null, null, null, true, item.WorldPosition);

        // 专用服务器本身没有客户端，上面的调用只跑逻辑、不出画面；这里按原版 ApplyTreatment 的做法
        // 把状态效果广播给客户端，客户端收到后自己播放粒子与音效
        NetworkMember net = GameMain.NetworkMember;
        if (net != null && net.IsServer)
        {
          net.CreateEntityEvent(item, new Item.ApplyStatusEffectEventData(ActionType.OnUse, null, null, null, null, item.WorldPosition));
        }
      }
      catch (Exception e) { LogError(e); }
    }

    private static void LogError(Exception e)
    {
      if (relicBagErrorCount >= 5) return;
      relicBagErrorCount++;
      LuaCsLogger.Log("[Despawn Button] 遗物袋逻辑出错：" + e);
    }
  }
}
