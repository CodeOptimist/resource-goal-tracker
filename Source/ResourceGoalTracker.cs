using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Harmony;
using HugsLib;
using RimWorld;
using UnityEngine;
using Verse;
//using HugsLib.Settings;

namespace ResourceGoalTracker
{
    class ResourceGoalTracker : ModBase
    {
        static Goal goal;
        public override string ModIdentifier => "COResourceGoalTracker";

        public override void DefsLoaded() {
            //SettingHandle<T> GetSettingHandle<T>(string settingName, T defaultValue) {
            //    return Settings.GetHandle(settingName, $"COSRT_{settingName}Setting_title".Translate(), $"COSRT_{settingName}Setting_description".Translate(), defaultValue);
            //}

            //goal = new Goal(ShipUtility.RequiredParts());
            goal = new Goal(new Dictionary<ThingDef, int> {{ThingDefOf.Ship_Reactor, 1}});
        }

        class Goal
        {
            static readonly Dictionary<ThingDef, List<ThingDefCountClass>> thingDefIngredientCounts = new Dictionary<ThingDef, List<ThingDefCountClass>>();
            public readonly Dictionary<ThingDef, int> parts;
            public Dictionary<ThingDef, int> resourceAmounts;

            public Goal(Dictionary<ThingDef, int> parts) {
                this.parts = parts;
                UpdateThingDefIngredientCounts(parts);
            }

            static void UpdateThingDefIngredientCounts(Dictionary<ThingDef, int> parts) {
                foreach (var part in parts)
                foreach (var cost in part.Key.costList) {
                    // todo use FirstOrDefault, make right clicking product allow changing recipe to create it
                    var recipe = DefDatabase<RecipeDef>.AllDefs.SingleOrDefault(r => r.products.Any(tc => tc.thingDef == cost.thingDef));
                    if (recipe == null) continue;
                    var ingredientCounts = new List<ThingDefCountClass>();
                    thingDefIngredientCounts[cost.thingDef] = ingredientCounts;
                    ingredientCounts.AddRange(
                        from ingredientCount in recipe.ingredients
                        from thingDef in ingredientCount.filter.AllowedThingDefs
                        let count = ingredientCount.CountRequiredOfFor(thingDef, recipe)
                        select new ThingDefCountClass(thingDef, count));
                }
            }

            // todo? live update from trade menu pre-confirm
            public void UpdateResourceAmounts() {
                var countedParts = CountAll(Find.CurrentMap, parts.Keys, false, true);
                var remainingParts = new Dictionary<ThingDef, int>();
                foreach (var part in parts)
                    remainingParts[part.Key] = Math.Max(part.Value - countedParts.TryGetValue(part.Key), 0);

                var costs = new Dictionary<ThingDef, int>();
                foreach (var part in remainingParts)
                foreach (var cost in part.Key.costList)
                    costs[cost.thingDef] = costs.TryGetValue(cost.thingDef) + part.Value * cost.count;

                // base on costs so we have ALL keys
                var deepCosts = new Dictionary<ThingDef, int>(costs);
                foreach (var cost in costs) {
                    if (!thingDefIngredientCounts.ContainsKey(cost.Key)) continue;
                    var ingredientCounts = thingDefIngredientCounts[cost.Key];
                    // deep counts of only remaining things
                    var costCount = Math.Max(cost.Value - Find.CurrentMap.resourceCounter.GetCount(cost.Key), 0);
                    foreach (var ingredientCount in ingredientCounts)
                        deepCosts[ingredientCount.thingDef] = costs.TryGetValue(ingredientCount.thingDef) + costCount * ingredientCount.count;
                }

                var remainingCosts = deepCosts.ToDictionary(cost => cost.Key, cost => Math.Max(cost.Value - Find.CurrentMap.resourceCounter.GetCount(cost.Key), 0));
                resourceAmounts = remainingCosts;
            }

            static Dictionary<ThingDef, int> CountAll(Map map, IEnumerable<ThingDef> thingDefs, bool includeEquipped, bool includeBuildings) {
                var result = new Dictionary<ThingDef, int>();
                foreach (var thingDef in thingDefs) {
                    int asResourceCount = 0, count = 0, minifiedCount = 0, equippedCount = 0, wornCount = 0, directlyHeldCount = 0;
                    if (thingDef.CountAsResource) {
                        asResourceCount = map.resourceCounter.GetCount(thingDef);
                    } else {
                        count = map.listerThings.ThingsOfDef(thingDef).Where(x => includeBuildings || !(x is Building)).Sum(x => x.stackCount);
                        minifiedCount = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).Cast<MinifiedThing>().Where(x => x.InnerThing.def == thingDef)
                            .Select(x => x.stackCount * x.InnerThing.stackCount).Sum();
                    }

                    var carriedCount = map.mapPawns.FreeColonistsSpawned.Where(x => x.carryTracker.CarriedThing?.def == thingDef).Select(x => x.carryTracker.CarriedThing)
                        .Sum(x => x.stackCount * (x is MinifiedThing minifiedThing ? minifiedThing.stackCount : 1));

                    if (includeEquipped) {
                        equippedCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.equipment.AllEquipmentListForReading).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                        wornCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.apparel.WornApparel).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                        directlyHeldCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.inventory.GetDirectlyHeldThings()).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                    }

                    var totalCount = asResourceCount + count + minifiedCount + carriedCount + equippedCount + wornCount + directlyHeldCount;
                    result.Add(thingDef, totalCount);
                }

                return result;
            }
        }

        [HarmonyPatch(typeof(ResourceCounter), nameof(ResourceCounter.ResourceCounterTick))]
        static class ResourceCounter_ResourceCounterTick_Patch
        {
            [HarmonyPostfix]
            static void CounterTick() {
                //goal.parts[ThingDefOf.Ship_CryptosleepCasket] = Find.CurrentMap.mapPawns.FreeColonistsSpawnedCount;
                goal.UpdateResourceAmounts();
            }
        }

        [HarmonyPatch(typeof(ResourceReadout), nameof(ResourceReadout.ResourceReadoutOnGUI))]
        static class ResourceReadout_ResourceReadoutOnGUI_Patch
        {
            static readonly MethodInfo DrawIconMethod = AccessTools.Method(typeof(ResourceReadout), "DrawIcon");

            [HarmonyPostfix]
            static void GoalResourceReadout(ResourceReadout __instance) {
                if (Event.current.type == EventType.layout) return;
                if (Current.ProgramState != ProgramState.Playing) return;
                if (Find.MainTabsRoot.OpenTab == MainButtonDefOf.Menu) return;

                GenUI.DrawTextWinterShadow(new Rect(256f, 512f, -256f, -512f)); // copied from ResourceReadout, not sure exactly
                Text.Font = GameFont.Small;

                const float iconAndLabelWidth = 80f;
                var readoutRect = new Rect(7f + 130f, 7f, iconAndLabelWidth * goal.resourceAmounts.Count, 50f);
                GUI.BeginGroup(readoutRect);
                Text.Anchor = TextAnchor.MiddleLeft;
                var x = 0f;
                foreach (var displayAmount in goal.resourceAmounts.Where(displayAmount => displayAmount.Value > 0)) {
                    const float iconSpace = 34f;
                    const float height = 24f; // what DoReadoutSimple() uses, I'm not sure why
                    var iconRect = new Rect(x, 0f, iconSpace, height);
                    DrawIconMethod.Invoke(__instance, new object[] {iconRect.x, iconRect.y, displayAmount.Key});
                    var labelRect = new Rect(iconRect.x + iconSpace, iconRect.y + 2f, iconAndLabelWidth - iconSpace, height);
                    Widgets.Label(labelRect, displayAmount.Value.ToStringCached());
                    x += iconAndLabelWidth;
                }

                Text.Anchor = TextAnchor.UpperLeft;
                GUI.EndGroup();
            }
        }
    }
}