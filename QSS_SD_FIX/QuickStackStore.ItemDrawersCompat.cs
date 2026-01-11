using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace QuickStackStoreItemDrawersCompat
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency("goldenrevolver.quick_stack_store", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("kg.ItemDrawers", BepInDependency.DependencyFlags.SoftDependency)]
    public class QuickStackStoreItemDrawersCompatPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "pavel.quickstackstore.itemdrawers_compat";
        public const string ModName = "QuickStackStore - ItemDrawers Compatibility";
        public const string ModVersion = "1.0.4";

        internal static ConfigEntry<float> SearchRadius;
        internal static ConfigEntry<bool> PreferQssRadiusIfAvailable;
        internal static ConfigEntry<bool> IncludeHotbar;
        internal static ConfigEntry<int> MaxDrawersToCheck;
        internal static ConfigEntry<bool> FillEmptyDrawers;

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;

            SearchRadius = Config.Bind(
                "General",
                "SearchRadius",
                10f,
                "Drawer search radius (meters). Used if PreferQssRadiusIfAvailable=false, or if QSS radius cannot be read."
            );

            PreferQssRadiusIfAvailable = Config.Bind(
                "General",
                "PreferQssRadiusIfAvailable",
                true,
                "If true, tries to read QuickStackStore nearby-range config and use it instead of SearchRadius."
            );

            IncludeHotbar = Config.Bind(
                "General",
                "IncludeHotbar",
                false,
                "If true, quickstack will also move items from hotbar."
            );

            MaxDrawersToCheck = Config.Bind(
                "General",
                "MaxDrawersToCheck",
                150,
                "Safety limit: max number of drawers to evaluate (nearest first)."
            );

            FillEmptyDrawers = Config.Bind(
                "General",
                "FillEmptyDrawers",
                false,
                "If true, when no matching drawer exists, the mod may use ONE empty drawer per item type (prefab+quality). If false - items stay in inventory."
            );

            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), ModGuid);
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }
    }

    [HarmonyPatch]
    internal static class QssHookPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            // QuickStackStore.QuickStackModule.ReportQuickStackResult(Player player, int movedCount)
            var type = AccessTools.TypeByName("QuickStackStore.QuickStackModule");
            return type == null ? null : AccessTools.Method(type, "ReportQuickStackResult", new[] { typeof(Player), typeof(int) });
        }

        [HarmonyPrefix]
        private static void Prefix(Player player, ref int movedCount)
        {
            try
            {
                if (player == null) return;
                if (!DrawerInterop.IsItemDrawersInstalled()) return;

                int added = DrawerQuickStacker.TryQuickStackIntoNearbyDrawers(player);
                if (added > 0) movedCount += added;
            }
            catch (Exception e)
            {
                QuickStackStoreItemDrawersCompatPlugin.Log?.LogError($"[QSS-ItemDrawersCompat] Hook error: {e}");
            }
        }
    }

    internal static class DrawerQuickStacker
    {
        public static int TryQuickStackIntoNearbyDrawers(Player player)
        {
            if (player == null) return 0;

            float range = GetEffectiveRange();
            if (range <= 0.05f) return 0;

            var inv = GetPlayerInventory(player);
            if (inv == null) return 0;

            var allItems = inv.GetAllItems();
            if (allItems == null || allItems.Count == 0) return 0;

            bool includeHotbar = QuickStackStoreItemDrawersCompatPlugin.IncludeHotbar.Value;

            var candidates = allItems
                .Where(i => i != null)
                .Where(i => i.m_stack > 0)
                .Where(i => i.m_dropPrefab != null)
                .Where(i => i.m_customData == null || i.m_customData.Count == 0)
                .Where(i => includeHotbar || !InventoryHelpers.IsHotbarItem(i))
                .ToList();

            if (candidates.Count == 0) return 0;

            var drawers = DrawerInterop.GetDrawersInRange(player.transform.position, range);
            if (drawers.Count == 0) return 0;

            int safetyLimit = Math.Max(1, QuickStackStoreItemDrawersCompatPlugin.MaxDrawersToCheck.Value);
            drawers = drawers
                .OrderBy(d => d.Distance)
                .Take(safetyLimit)
                .ToList();

            var byKey = candidates
                .GroupBy(i => new ItemKey(i.m_dropPrefab.name, i.m_quality))
                .ToList();

            bool allowEmpty = QuickStackStoreItemDrawersCompatPlugin.FillEmptyDrawers.Value;
            int movedStacks = 0;

            var assignedEmpty = new Dictionary<ItemKey, DrawerHandle>(byKey.Count);

            foreach (var group in byKey)
            {
                ItemKey key = group.Key;

                List<DrawerHandle> matching = drawers
                    .Where(d => d.IsValid)
                    .Where(d => d.CurrentPrefab == key.Prefab && d.Quality == key.Quality)
                    .OrderBy(d => d.Distance)
                    .ToList();

                DrawerHandle forcedDrawer = null;
                if (allowEmpty && assignedEmpty.TryGetValue(key, out var assigned) && assigned != null && assigned.IsValid)
                {
                    forcedDrawer = assigned;
                }

                foreach (var item in group)
                {
                    if (item == null) continue;
                    if (item.m_stack <= 0) continue;
                    if (!inv.ContainsItem(item)) continue;

                    bool movedThisItem = false;

                    if (matching.Count > 0)
                    {
                        foreach (var d in matching)
                        {
                            if (!d.IsValid) continue;
                            if (d.TryUseItem(player, item))
                            {
                                movedThisItem = true;
                                movedStacks += 1;
                                break;
                            }
                        }
                    }

                    if (movedThisItem) continue;

                    if (!allowEmpty) continue;

                    if (forcedDrawer != null && forcedDrawer.IsValid)
                    {
                        if (forcedDrawer.TryUseItem(player, item))
                        {
                            movedStacks += 1;
                            if (forcedDrawer.CurrentPrefab == key.Prefab && forcedDrawer.Quality == key.Quality)
                                matching = new List<DrawerHandle> { forcedDrawer };
                            continue;
                        }
                    }

                    DrawerHandle empty = drawers
                        .Where(d => d.IsValid)
                        .Where(d => string.IsNullOrEmpty(d.CurrentPrefab))
                        .OrderBy(d => d.Distance)
                        .FirstOrDefault();

                    if (empty == null) continue;

                    if (empty.TryUseItem(player, item))
                    {
                        movedStacks += 1;
                        assignedEmpty[key] = empty;
                        forcedDrawer = empty;

                        if (empty.CurrentPrefab == key.Prefab && empty.Quality == key.Quality)
                            matching = new List<DrawerHandle> { empty };
                    }
                }
            }

            return movedStacks;
        }

        private static float GetEffectiveRange()
        {
            float fallback = Math.Max(0f, QuickStackStoreItemDrawersCompatPlugin.SearchRadius.Value);

            if (!QuickStackStoreItemDrawersCompatPlugin.PreferQssRadiusIfAvailable.Value)
                return fallback;

            float qss = QssConfigInterop.TryGetQssNearbyRangeOrNaN();
            if (!float.IsNaN(qss) && qss > 0.05f) return qss;

            return fallback;
        }

        private static Inventory GetPlayerInventory(Player p)
        {
            // 1) Нормальный путь (в Valheim обычно есть public Inventory GetInventory())
            try
            {
                return p.GetInventory();
            }
            catch
            {
                // 2) Fallback: reflection GetInventory()
                try
                {
                    var mi = AccessTools.Method(p.GetType(), "GetInventory", Type.EmptyTypes);
                    if (mi != null)
                    {
                        var res = mi.Invoke(p, Array.Empty<object>());
                        return res as Inventory;
                    }
                }
                catch { }

                // 3) Fallback: поле m_inventory (protected) через reflection
                try
                {
                    var fi = AccessTools.Field(p.GetType(), "m_inventory");
                    if (fi != null)
                    {
                        var res = fi.GetValue(p);
                        return res as Inventory;
                    }
                }
                catch { }

                return null;
            }
        }


        private readonly struct ItemKey : IEquatable<ItemKey>
        {
            public readonly string Prefab;
            public readonly int Quality;

            public ItemKey(string prefab, int quality)
            {
                Prefab = prefab ?? "";
                Quality = quality;
            }

            public bool Equals(ItemKey other) => Prefab == other.Prefab && Quality == other.Quality;
            public override bool Equals(object obj) => obj is ItemKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + Prefab.GetHashCode();
                    h = h * 31 + Quality.GetHashCode();
                    return h;
                }
            }
        }
    }

    internal static class DrawerInterop
    {
        private const string DrawerTypeName = "kg_ItemDrawers.DrawerComponent";

        private static Type _drawerType;
        private static FieldInfo _allDrawersField;

        private static PropertyInfo _currentPrefabProp;
        private static PropertyInfo _qualityProp;

        private static MethodInfo _useItemMi;

        public static bool IsItemDrawersInstalled()
        {
            _drawerType ??= AccessTools.TypeByName(DrawerTypeName);
            return _drawerType != null;
        }

        public static List<DrawerHandle> GetDrawersInRange(Vector3 playerPos, float range)
        {
            var res = new List<DrawerHandle>(128);

            _drawerType ??= AccessTools.TypeByName(DrawerTypeName);
            if (_drawerType == null) return res;

            _allDrawersField ??= AccessTools.Field(_drawerType, "AllDrawers");
            if (_allDrawersField == null) return res;

            _useItemMi ??= AccessTools.Method(_drawerType, "UseItem", new[] { typeof(Humanoid), typeof(ItemDrop.ItemData) });
            if (_useItemMi == null) return res;

            _currentPrefabProp ??= AccessTools.Property(_drawerType, "CurrentPrefab");
            _qualityProp ??= AccessTools.Property(_drawerType, "Quality");
            if (_currentPrefabProp == null || _qualityProp == null) return res;

            object listObj;
            try { listObj = _allDrawersField.GetValue(null); }
            catch { return res; }

            if (listObj is not System.Collections.IEnumerable enumerable) return res;

            foreach (var it in enumerable)
            {
                if (it == null) continue;
                if (it is not Component comp) continue;
                if (!comp) continue;

                float dist = Vector3.Distance(playerPos, comp.transform.position);
                if (dist > range) continue;

                res.Add(new DrawerHandle(comp, _useItemMi, _currentPrefabProp, _qualityProp, dist));
            }

            return res;
        }
    }

    internal sealed class DrawerHandle
    {
        private readonly Component _comp;
        private readonly MethodInfo _useItemMi;
        private readonly PropertyInfo _currentPrefabProp;
        private readonly PropertyInfo _qualityProp;

        public float Distance { get; }
        public bool IsValid => _comp != null && _comp;

        public DrawerHandle(Component comp, MethodInfo useItemMi, PropertyInfo currentPrefabProp, PropertyInfo qualityProp, float distance)
        {
            _comp = comp;
            _useItemMi = useItemMi;
            _currentPrefabProp = currentPrefabProp;
            _qualityProp = qualityProp;
            Distance = distance;
        }

        public string CurrentPrefab
        {
            get
            {
                if (!IsValid) return "";
                try { return _currentPrefabProp.GetValue(_comp, null) as string ?? ""; }
                catch { return ""; }
            }
        }

        public int Quality
        {
            get
            {
                if (!IsValid) return 1;
                try { return _qualityProp.GetValue(_comp, null) is int i ? i : 1; }
                catch { return 1; }
            }
        }

        public bool TryUseItem(Humanoid user, ItemDrop.ItemData item)
        {
            if (!IsValid) return false;
            if (user == null || item == null) return false;

            try { return _useItemMi.Invoke(_comp, new object[] { user, item }) is bool b && b; }
            catch { return false; }
        }
    }

    internal static class InventoryHelpers
    {
        public static bool IsHotbarItem(ItemDrop.ItemData item)
        {
            try
            {
                var f = AccessTools.Field(item.GetType(), "m_gridPos");
                if (f == null) return false;

                var gp = f.GetValue(item);
                if (gp == null) return false;

                var yField = gp.GetType().GetField("y", BindingFlags.Instance | BindingFlags.Public);
                if (yField == null) return false;

                return yField.GetValue(gp) is int y && y == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static class QssConfigInterop
    {
        public static float TryGetQssNearbyRangeOrNaN()
        {
            try
            {
                var cfgType = AccessTools.TypeByName("QuickStackStore.QSSConfig+QuickStackConfig");
                if (cfgType == null) return float.NaN;

                var field = AccessTools.Field(cfgType, "QuickStackToNearbyRange");
                if (field == null) return float.NaN;

                var cfgEntry = field.GetValue(null);
                if (cfgEntry == null) return float.NaN;

                var valProp = cfgEntry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                if (valProp == null) return float.NaN;

                return valProp.GetValue(cfgEntry) is float f ? f : float.NaN;
            }
            catch
            {
                return float.NaN;
            }
        }
    }
}
