using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
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
        public const string ModVersion = "1.0.2";

        internal static ConfigEntry<float> SearchRadius;
        internal static ConfigEntry<bool> IncludeHotbar;
        internal static ConfigEntry<int> MaxDrawersToCheck;
        internal static ConfigEntry<bool> PreferQssRadiusIfAvailable;

        private void Awake()
        {
            SearchRadius = Config.Bind(
                "General",
                "SearchRadius",
                10f,
                "Drawer search radius (meters). Used if PreferQssRadiusIfAvailable=false, or if QSS radius cannot be read."
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

            PreferQssRadiusIfAvailable = Config.Bind(
                "General",
                "PreferQssRadiusIfAvailable",
                true,
                "If true, tries to read QSS nearby-range config and use it instead of SearchRadius."
            );

            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), ModGuid);
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }
    }

    [HarmonyPatch]
    internal static class QssReportPatch
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

                // Если ItemDrawers не стоит - ничего не делаем
                if (AccessTools.TypeByName("kg_ItemDrawers.DrawerComponent") == null) return;

                int added = DrawerQuickStacker.TryQuickStackIntoNearbyDrawers(player);
                if (added > 0) movedCount += added;
            }
            catch (Exception e)
            {
                Debug.LogError($"[QSS-DrawersCompat] Error: {e}");
            }
        }
    }

    internal static class DrawerQuickStacker
    {
        private static readonly string DrawerTypeName = "kg_ItemDrawers.DrawerComponent";

        private static MethodInfo _useItemMethod;
        private static MethodInfo _getInventoryMethod;
        private static FieldInfo _inventoryField;

        public static int TryQuickStackIntoNearbyDrawers(Player player)
        {
            var drawerType = AccessTools.TypeByName(DrawerTypeName);
            if (drawerType == null) return 0;

            _useItemMethod ??= AccessTools.Method(drawerType, "UseItem", new[] { typeof(Humanoid), typeof(ItemDrop.ItemData) });
            if (_useItemMethod == null) return 0;

            float range = GetEffectiveRange();
            if (range <= 0.05f) return 0;

            // Находим drawers
            var all = UnityEngine.Object.FindObjectsByType(drawerType, FindObjectsSortMode.None);
            if (all == null || all.Length == 0) return 0;

            var drawers = all
                .Select(o => o as Component)
                .Where(c => c != null && c.gameObject != null && c.gameObject.activeInHierarchy)
                .Select(c => new { Comp = c, Dist = Vector3.Distance(player.transform.position, c.transform.position) })
                .Where(x => x.Dist <= range)
                .OrderBy(x => x.Dist)
                .Take(Math.Max(1, QuickStackStoreItemDrawersCompatPlugin.MaxDrawersToCheck.Value))
                .Select(x => x.Comp)
                .ToList();

            if (drawers.Count == 0) return 0;

            // Берем inventory игрока без доступа к protected m_inventory
            var inv = GetHumanoidInventory(player);
            if (inv == null) return 0;

            // Кандидаты
            var items = inv.GetAllItems()?.ToList();
            if (items == null || items.Count == 0) return 0;

            bool includeHotbar = QuickStackStoreItemDrawersCompatPlugin.IncludeHotbar.Value;

            var candidates = items
                .Where(i => i != null)
                .Where(i => i.m_shared != null && i.m_shared.m_maxStackSize > 1)
                .Where(i => i.m_stack > 0)
                .Where(i => includeHotbar || !IsHotbarItem(i))
                .ToList();

            if (candidates.Count == 0) return 0;

            int movedStacks = 0;

            foreach (var item in candidates)
            {
                // item мог быть удалён из инвентаря предыдущим UseItem
                if (!inv.ContainsItem(item)) continue;

                foreach (var drawer in drawers)
                {
                    bool ok;
                    try
                    {
                        ok = (bool)_useItemMethod.Invoke(drawer, new object[] { player, item });
                    }
                    catch
                    {
                        ok = false;
                    }

                    if (ok)
                    {
                        movedStacks += 1;
                        break;
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

            float qss = TryGetQssNearbyRangeOrNaN();
            if (!float.IsNaN(qss) && qss > 0.05f) return qss;

            return fallback;
        }

        private static float TryGetQssNearbyRangeOrNaN()
        {
            // QuickStackStore.QSSConfig+QuickStackConfig.QuickStackToNearbyRange.Value
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

                var v = valProp.GetValue(cfgEntry);
                if (v is float f) return f;

                return float.NaN;
            }
            catch
            {
                return float.NaN;
            }
        }

        private static Inventory GetHumanoidInventory(Humanoid h)
        {
            // В разных версиях Valheim это бывает:
            // - protected field m_inventory
            // - публичный метод GetInventory()
            // Поэтому пробуем обе стратегии.

            try
            {
                _getInventoryMethod ??= AccessTools.Method(h.GetType(), "GetInventory", Type.EmptyTypes);
                if (_getInventoryMethod != null)
                {
                    var res = _getInventoryMethod.Invoke(h, Array.Empty<object>());
                    if (res is Inventory invA) return invA;
                }
            }
            catch { }

            try
            {
                _inventoryField ??= AccessTools.Field(h.GetType(), "m_inventory");
                if (_inventoryField != null)
                {
                    var res = _inventoryField.GetValue(h);
                    if (res is Inventory invB) return invB;
                }
            }
            catch { }

            return null;
        }

        private static bool IsHotbarItem(ItemDrop.ItemData item)
        {
            // m_gridPos - Vector2i из assembly_utils
            // Не ссылаемся на тип, читаем через reflection:
            try
            {
                var f = AccessTools.Field(item.GetType(), "m_gridPos");
                if (f == null) return false;
                var gp = f.GetValue(item);
                if (gp == null) return false;

                var yField = gp.GetType().GetField("y", BindingFlags.Instance | BindingFlags.Public);
                if (yField == null) return false;

                var yVal = yField.GetValue(gp);
                if (yVal is int y)
                {
                    // В Valheim хотбар обычно y==0
                    return y == 0;
                }
            }
            catch { }

            return false;
        }
    }
}
