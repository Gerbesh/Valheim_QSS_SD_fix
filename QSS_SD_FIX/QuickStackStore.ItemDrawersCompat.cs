#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace QuickStackStore.ItemDrawersCompat
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    // ВАЖНО: правильный GUID QSS (у тебя в логе ConfigSync именно он)
    [BepInDependency("goldenrevolver.quick_stack_store", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class QssItemDrawersCompatPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "gerbesh.QuickStackStore.ItemDrawersCompat";
        public const string ModName = "QuickStackStore ItemDrawers Compat";
        public const string ModVersion = "1.1.2"; // fixed: correct QSS guid + correct hook + logs + suppress QSS NRE

        private static ManualLogSource? Log;

        private static ConfigEntry<float>? CfgSearchRadius;
        private static ConfigEntry<bool>? CfgFillEmptyDrawers;
        private static ConfigEntry<bool>? CfgDebug;
        private static ConfigEntry<float>? CfgPlacementCheckDelay;

        // ItemDrawers API
        private static bool _drawersInstalled;
        private static MethodInfo? _miAllDrawers; // List<ZNetView>

        // QSS hooks
        private static MethodInfo? _miReportQuickStackResult; // QuickStackStore.QuickStackModule.ReportQuickStackResult(Player,int)

        // Safe clone (MemberwiseClone) for ItemDrop.ItemData
        private static readonly MethodInfo? MemberwiseCloneMethod =
            AccessTools.DeclaredMethod(typeof(object), "MemberwiseClone");

        private void Awake()
        {
            Log = Logger;

            CfgSearchRadius = Config.Bind(
                "General",
                "SearchRadius",
                15f,
                "Radius (meters) to search ItemDrawers around the player.");

            CfgFillEmptyDrawers = Config.Bind(
                "General",
                "FillEmptyDrawers",
                false,
                "If true, when no matching drawer exists, the mod may put items into ONE empty drawer (per item prefab + quality). If false, only drawers that already contain the item are used.");

            CfgDebug = Config.Bind(
                "General",
                "DebugLogging",
                false,
                "Enable verbose logs for troubleshooting.");

            CfgPlacementCheckDelay = Config.Bind(
                "General",
                "PlacementCheckDelay",
                0.5f,
                "Delay (seconds) before checking if the item was successfully placed in the drawer (to account for network latency).");

            TryInitItemDrawersApi();

            var h = new Harmony(ModGuid);
            TryPatchQss(h);

            // Доп-патч: гасим NRE в QSS SortModule.SortContainer(Container)
            TryPatchQssSortNre(h);

            h.PatchAll();

            // Лог всегда, чтобы ты видел что мод реально загрузился
            Log?.LogInfo($"[{ModName}] Loaded v{ModVersion}. DrawersInstalled={_drawersInstalled}. Hook={( _miReportQuickStackResult != null ? "OK" : "MISSING" )}");
        }

        private static void TryInitItemDrawersApi()
        {
            try
            {
                var drawersApiType = Type.GetType("API.ClientSideV2, kg_ItemDrawers");
                if (drawersApiType == null)
                {
                    _drawersInstalled = false;
                    Log?.LogWarning("[ItemDrawersCompat] ItemDrawers API.ClientSideV2 not found. ItemDrawers not installed?");
                    return;
                }

                _miAllDrawers = drawersApiType.GetMethod("AllDrawers", BindingFlags.Public | BindingFlags.Static);
                if (_miAllDrawers == null)
                {
                    _drawersInstalled = false;
                    Log?.LogWarning("[ItemDrawersCompat] ItemDrawers API.ClientSideV2.AllDrawers not found.");
                    return;
                }

                _drawersInstalled = true;
                Dbg("ItemDrawers API initialized.");
            }
            catch (Exception ex)
            {
                _drawersInstalled = false;
                Log?.LogError($"[ItemDrawersCompat] Failed to init ItemDrawers API: {ex}");
            }
        }

        private static void TryPatchQss(Harmony h)
        {
            try
            {
                // В дампе QSS метод тут:
                // namespace QuickStackStore
                // public class QuickStackModule { public static void ReportQuickStackResult(Player player, int movedCount) }
                var qssType = AccessTools.TypeByName("QuickStackStore.QuickStackModule");
                if (qssType == null)
                {
                    Log?.LogError("[ItemDrawersCompat] QuickStackStore.QuickStackModule type not found. Is QSS installed / correct version?");
                    return;
                }

                _miReportQuickStackResult = AccessTools.Method(qssType, "ReportQuickStackResult", new[] { typeof(Player), typeof(int) });
                if (_miReportQuickStackResult == null)
                {
                    Log?.LogError("[ItemDrawersCompat] QuickStackModule.ReportQuickStackResult(Player,int) not found. QSS version mismatch.");
                    return;
                }

                h.Patch(
                    original: _miReportQuickStackResult,
                    postfix: new HarmonyMethod(typeof(QssItemDrawersCompatPlugin), nameof(ReportQuickStackResult_Postfix))
                );

                Log?.LogInfo("[ItemDrawersCompat] Patched QuickStackStore.QuickStackModule.ReportQuickStackResult(Player,int)");
            }
            catch (Exception ex)
            {
                Log?.LogError($"[ItemDrawersCompat] Failed to patch QSS: {ex}");
            }
        }

        private static void TryPatchQssSortNre(Harmony h)
        {
            try
            {
                var sortType = AccessTools.TypeByName("QuickStackStore.SortModule");
                if (sortType == null) return;

                var miSortContainer = AccessTools.Method(sortType, "SortContainer", new[] { typeof(Container) });
                if (miSortContainer == null) return;

                h.Patch(
                    original: miSortContainer,
                    prefix: new HarmonyMethod(typeof(QssItemDrawersCompatPlugin), nameof(SortContainer_Prefix))
                );

                Log?.LogInfo("[ItemDrawersCompat] Patched QuickStackStore.SortModule.SortContainer(Container) to prevent NRE");
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[ItemDrawersCompat] Sort NRE patch failed (non-fatal): {ex.Message}");
            }
        }

        // Гасим твой краш:
        // NullReferenceException: QuickStackStore.SortModule.SortContainer(Container container)
        public static bool SortContainer_Prefix(Container container)
        {
            try
            {
                if (container == null) return false;
                var inv = container.GetInventory();
                if (inv == null) return false;
                return true; // продолжаем оригинальный метод
            }
            catch
            {
                return false;
            }
        }

        // Сигнатура должна совпадать с ReportQuickStackResult(Player,int)
        public static void ReportQuickStackResult_Postfix(Player __0, int __1)
        {
            try
            {
                if (!_drawersInstalled || _miAllDrawers == null)
                    return;

                var player = __0 ?? Player.m_localPlayer;
                if (player == null)
                    return;

                // movedCount <= 0 - смысла гонять логику нет
                if (__1 <= 0)
                {
                    Dbg("QSS movedCount=0, skip drawers pass.");
                    return;
                }

                var inv = player.GetInventory();
                if (inv == null)
                    return;

                float radius = Mathf.Max(0f, CfgSearchRadius?.Value ?? 0f);
                bool fillEmpty = CfgFillEmptyDrawers?.Value ?? false;

                var drawers = GetDrawersInRange(player.transform.position, radius);
                if (drawers.Count == 0)
                {
                    Dbg($"No drawers found in radius {radius}.");
                    return;
                }

                // Snapshot drawer states from ZDO. Key: prefab|quality
                var filledByKey = new Dictionary<string, List<DrawerState>>(StringComparer.Ordinal);
                var empties = new List<DrawerState>();

                foreach (var d in drawers)
                {
                    if (d == null || !d.IsValid())
                        continue;

                    var zdo = d.GetZDO();
                    if (zdo == null)
                        continue;

                    string prefab = zdo.GetString("Prefab") ?? string.Empty;
                    int quality = zdo.GetInt("Quality", 1);

                    var st = new DrawerState(d, prefab, quality);

                    if (string.IsNullOrEmpty(st.Prefab))
                    {
                        empties.Add(st);
                        continue;
                    }

                    string k = MakeKey(st.Prefab, st.Quality);
                    if (!filledByKey.TryGetValue(k, out var list))
                    {
                        list = new List<DrawerState>();
                        filledByKey[k] = list;
                    }
                    list.Add(st);
                }

                var reservedEmpty = new Dictionary<string, DrawerState>(StringComparer.Ordinal);

                int movedStacks = 0;
                int movedItemsTotal = 0;

                var items = inv.GetAllItems();
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var item = items[i];
                    if (item == null) continue;
                    if (item.m_stack <= 0) continue;

                    if (item.m_customData != null && item.m_customData.Count > 0)
                    {
                        Dbg($"Skip customData item: {item.m_shared?.m_name} ({item.m_dropPrefab?.name}) customData={item.m_customData.Count}");
                        continue;
                    }

                    string? dropPrefabMaybe = item.m_dropPrefab?.name;
                    if (string.IsNullOrEmpty(dropPrefabMaybe))
                    {
                        Dbg($"Item without valid dropPrefab skipped: {item.m_shared?.m_name}");
                        continue;
                    }

                    string dropPrefab = dropPrefabMaybe;
                    int q = item.m_quality;
                    string key = MakeKey(dropPrefab, q);

                    DrawerState? target = null;

                    if (filledByKey.TryGetValue(key, out var candidates) && candidates.Count > 0)
                    {
                        target = SelectBestDrawer(candidates, player.transform.position);
                    }

                    if (target == null && fillEmpty)
                    {
                        if (!reservedEmpty.TryGetValue(key, out var reserved))
                        {
                            if (empties.Count == 0) continue;

                            reserved = SelectBestDrawer(empties, player.transform.position) ?? empties[0];
                            empties.Remove(reserved);
                            reservedEmpty[key] = reserved;

                            if (!filledByKey.TryGetValue(key, out var list))
                            {
                                list = new List<DrawerState>();
                                filledByKey[key] = list;
                            }
                            list.Add(reserved);
                        }

                        target = reserved;
                    }

                    if (target == null) continue;

                    int amount = item.m_stack;
                    int prevAmount = target.NView.GetZDO()?.GetInt("Amount", 0) ?? 0;

                    var backupItem = CloneItemData(item);
                    if (backupItem == null)
                    {
                        Dbg($"Clone failed, skipping item: {dropPrefab} x{amount}");
                        continue;
                    }

                    bool deposited = TryDepositToDrawer(player, inv, item, target.NView, dropPrefab, q, amount);
                    if (!deposited) continue;

                    movedStacks++;
                    movedItemsTotal += amount;

                    player.StartCoroutine(CheckPlacementCoroutine(
                        drawerNView: target.NView,
                        prefab: dropPrefab,
                        quality: q,
                        expectedAmount: amount,
                        prevAmount: prevAmount,
                        backupItem: backupItem,
                        inv: inv,
                        player: player));
                }

                if (movedStacks > 0)
                {
                    Dbg($"Deposited stacks={movedStacks} items={movedItemsTotal} (fillEmpty={fillEmpty}) radius={radius} drawers={drawers.Count}");
                }
            }
            catch (Exception ex)
            {
                Log?.LogError($"[ItemDrawersCompat] Postfix failed: {ex}");
            }
        }

        private static IEnumerator CheckPlacementCoroutine(
            ZNetView drawerNView,
            string prefab,
            int quality,
            int expectedAmount,
            int prevAmount,
            ItemDrop.ItemData backupItem,
            Inventory inv,
            Player player)
        {
            float delay = Mathf.Max(0f, CfgPlacementCheckDelay?.Value ?? 0.5f);
            yield return new WaitForSeconds(delay);

            try
            {
                if (drawerNView == null || !drawerNView.IsValid())
                    yield break;

                var zdo = drawerNView.GetZDO();
                if (zdo == null)
                    yield break;

                string currentPrefab = zdo.GetString("Prefab") ?? string.Empty;
                int currentQuality = zdo.GetInt("Quality", 1);
                int currentAmount = zdo.GetInt("Amount", 0);

                bool sameType = currentPrefab == prefab && currentQuality == quality;
                int delta = currentAmount - prevAmount;

                if (!sameType || delta <= 0)
                {
                    Dbg($"Placement failed for {prefab} x{expectedAmount} (delta={delta}, sameType={sameType}). Recovering full.");
                    RecoverItem(player, inv, backupItem, expectedAmount);
                    yield break;
                }

                if (delta < expectedAmount)
                {
                    int remaining = expectedAmount - delta;
                    Dbg($"Placement partial for {prefab}: expected={expectedAmount}, delta={delta}. Recovering remaining={remaining}.");
                    RecoverItem(player, inv, backupItem, remaining);
                    yield break;
                }

                Dbg($"Placement OK for {prefab} x{expectedAmount} (delta={delta}).");
            }
            catch (Exception ex)
            {
                Log?.LogError($"[ItemDrawersCompat] Placement check failed: {ex}");
                RecoverItem(player, inv, backupItem, expectedAmount);
            }
        }

        private static void RecoverItem(Player player, Inventory inv, ItemDrop.ItemData templateItem, int amount)
        {
            if (amount <= 0) return;

            try
            {
                var recovered = CloneItemData(templateItem);
                if (recovered == null) return;

                recovered.m_stack = amount;

                if (!inv.AddItem(recovered))
                {
                    Vector3 dropPos = player.transform.position + player.transform.forward * 1.5f + Vector3.up * 0.5f;
                    Quaternion dropRot = Quaternion.LookRotation(player.transform.forward);

                    ItemDrop dropped = ItemDrop.DropItem(recovered, amount, dropPos, dropRot);
                    if (dropped != null) dropped.OnPlayerDrop();

                    player.Message(MessageHud.MessageType.Center, "Inventory full: dropped recovered item.");
                }
            }
            catch (Exception ex)
            {
                Log?.LogError($"[ItemDrawersCompat] RecoverItem failed: {ex}");
            }
        }

        private static List<ZNetView> GetDrawersInRange(Vector3 pos, float range)
        {
            var result = new List<ZNetView>();

            try
            {
                if (_miAllDrawers == null)
                    return result;

                if (_miAllDrawers.Invoke(null, null) is not List<ZNetView> raw)
                    return result;

                if (range <= 0f)
                    return raw.Where(z => z != null && z.IsValid()).ToList();

                float r2 = range * range;
                foreach (var znv in raw)
                {
                    if (znv == null || !znv.IsValid())
                        continue;

                    var p = znv.transform.position;
                    if ((p - pos).sqrMagnitude <= r2)
                        result.Add(znv);
                }
            }
            catch (Exception ex)
            {
                Log?.LogError($"[ItemDrawersCompat] GetDrawersInRange failed: {ex}");
            }

            return result;
        }

        private static bool TryDepositToDrawer(
            Player player,
            Inventory inv,
            ItemDrop.ItemData item,
            ZNetView drawerNView,
            string prefab,
            int quality,
            int amount)
        {
            try
            {
                if (drawerNView == null || !drawerNView.IsValid())
                    return false;

                if (string.IsNullOrEmpty(prefab))
                    return false;

                if (amount <= 0)
                    return false;

                // MP: часто без владельца сервер отклоняет RPC
                drawerNView.ClaimOwnership();

                inv.RemoveItem(item);
                drawerNView.InvokeRPC("AddItem_Request", prefab, amount, quality);

                return true;
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[ItemDrawersCompat] Deposit failed for {prefab} x{amount} q{quality}: {ex}");

                // Best-effort immediate recovery using same instance
                try
                {
                    item.m_stack = amount;
                    if (!inv.AddItem(item))
                    {
                        Vector3 dropPos = player.transform.position + player.transform.forward * 1.5f + Vector3.up * 0.5f;
                        Quaternion dropRot = Quaternion.LookRotation(player.transform.forward);

                        ItemDrop dropped = ItemDrop.DropItem(item, amount, dropPos, dropRot);
                        if (dropped != null) dropped.OnPlayerDrop();
                    }
                }
                catch (Exception rex)
                {
                    Log?.LogError($"[ItemDrawersCompat] Immediate recovery failed: {rex}");
                }

                return false;
            }
        }

        private static DrawerState? SelectBestDrawer(List<DrawerState> candidates, Vector3 playerPos)
        {
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            return candidates
                .OrderBy(d => (d.NView.transform.position - playerPos).sqrMagnitude)
                .FirstOrDefault();
        }

        private static string MakeKey(string prefab, int quality) => prefab + "|" + quality;

        private static void Dbg(string msg)
        {
            if (CfgDebug?.Value != true) return;
            Log?.LogInfo("[ItemDrawersCompat][DBG] " + msg);
        }

        private static ItemDrop.ItemData? CloneItemData(ItemDrop.ItemData src)
        {
            try
            {
                if (MemberwiseCloneMethod == null)
                    return null;

                return (ItemDrop.ItemData)MemberwiseCloneMethod.Invoke(src, Array.Empty<object>());
            }
            catch (Exception ex)
            {
                Log?.LogError($"[ItemDrawersCompat] CloneItemData failed: {ex}");
                return null;
            }
        }

        private sealed class DrawerState
        {
            public ZNetView NView { get; }
            public string Prefab { get; }
            public int Quality { get; }

            public DrawerState(ZNetView nview, string prefab, int quality)
            {
                NView = nview;
                Prefab = prefab;
                Quality = quality;
            }
        }
    }
}
