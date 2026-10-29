using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.Localization;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace GentlemansClub
{
    // The club's own items (Glimmerdust, the Neon Ember cocktail). Each is a real game item: the game's
    // item loader finds it by key (saves, stores), and its name and tooltip come from here.
    internal static class ClubItems
    {
        internal class Def
        {
            public string Key, Title, Text;
            public string[] Pixels;                  // 16x16 icon, one char per pixel
            public Dictionary<char, Color32> Palette;
            public int Price;
            public Func<ItemData> Create;            // makes the ScriptableObject of the right subclass
            public readonly LocalizedString NameRef = new LocalizedString(), DescRef = new LocalizedString();
            public ItemData Data;
        }

        static readonly Dictionary<string, Def> ByKey = new Dictionary<string, Def>();

        internal static void Add(Def def) => ByKey[def.Key] = def;

        internal static ItemData Get(string key)
        {
            if (!ByKey.TryGetValue(key, out var def)) return null;
            if (def.Data != null) return def.Data;
            var d = def.Create();
            d.hideFlags = HideFlags.HideAndDontSave; // survives scene unloads
            d.name = def.Key;
            d.addressKey = def.Key;
            d.Name = def.NameRef;
            d.Description = def.DescRef;
            d.UnknownName = new LocalizedString();
            d.IsStackable = true;
            d.itemCategory = ItemCategory.Consumable;
            d.itemBehaviour = ItemBehaviour.Consumable;
            d.qualityRange = new Vector2Int(0, 0);
            d.priceRange = new Vector2Int(def.Price, def.Price);
            d.mandatoryModifiers = new List<StatModifier<SecondaryStat>>();
            d.possibleModifiers = new List<StatModifier<SecondaryStat>>();
            d.possibleEffects = new List<DamageEffect>();
            d.itemIconUI = Icon(def, 0.7f);
            d.itemIconWorld = Icon(def, 0.7f * 0.62f); // smaller on the ground, like the game's loot
            return def.Data = d;
        }

        internal static Def Find(ItemData data) => data != null && ByKey.TryGetValue(data.addressKey ?? "", out var d) && d.Data == data ? d : null;

        static Sprite Icon(Def def, float unitsWide)
        {
            var tex = new Texture2D(16, 16, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                tex.SetPixel(x, y, def.Palette.TryGetValue(def.Pixels[15 - y][x], out var c) ? (Color)c : Color.clear);
            tex.Apply();
            var s = Sprite.Create(tex, new Rect(0, 0, 16, 16), new Vector2(0.5f, 0.5f), 16f / unitsWide);
            s.hideFlags = HideFlags.HideAndDontSave;
            return s;
        }

        internal static IEnumerator Register()
        {
            yield return Addressables.InitializeAsync();
            Addressables.ResourceManager.ResourceProviders.Add(new Provider());
            Addressables.AddResourceLocator(new Locator());
            GentlemansClubPlugin.Log.LogInfo("Club items registered: " + string.Join(", ", ByKey.Keys));
        }

        class Provider : ResourceProviderBase
        {
            public const string Id = "OdinPlus.GentlemansClubItems";
            public override string ProviderId => Id;

            public override void Provide(ProvideHandle h)
            {
                var data = Get(h.Location.InternalId);
                if (data != null) h.Complete(data, true, null);
                else h.Complete<ItemData>(null, false, new Exception("Unknown key " + h.Location.InternalId));
            }

            public override Type GetDefaultType(IResourceLocation location) => typeof(ItemData);
        }

        class Locator : IResourceLocator
        {
            public string LocatorId => "OdinPlus.GentlemansClubItemsLocator";
            public IEnumerable<object> Keys => new object[0];
            public IEnumerable<IResourceLocation> AllLocations => new IResourceLocation[0];

            public bool Locate(object key, Type type, out IList<IResourceLocation> locations)
            {
                if (key is string s && ByKey.ContainsKey(s) && (type == null || type.IsAssignableFrom(typeof(ItemData)) || typeof(ItemData).IsAssignableFrom(type)))
                {
                    locations = new List<IResourceLocation> { new ResourceLocationBase(s, s, Provider.Id, typeof(ItemData)) };
                    return true;
                }
                locations = null;
                return false;
            }
        }

        // Our localized strings have no table behind them: answer for them ourselves.
        [HarmonyPatch]
        static class TextPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var m in typeof(LocalizedString).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "GetLocalizedString" && m.ReturnType == typeof(string)) yield return m;
            }

            static bool Prefix(LocalizedString __instance, ref string __result)
            {
                foreach (var def in ByKey.Values)
                {
                    if (ReferenceEquals(__instance, def.NameRef)) { __result = def.Title; return false; }
                    if (ReferenceEquals(__instance, def.DescRef)) { __result = def.Text; return false; }
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(ItemRandomValues), nameof(ItemRandomValues.CurrentName), MethodType.Getter)]
        static class NamePatch
        {
            static bool Prefix(ItemRandomValues __instance, ref string __result)
            {
                var def = Find(__instance.itemData);
                if (def == null) return true;
                __result = def.Title;
                return false;
            }
        }

        [HarmonyPatch(typeof(ItemInfoWindow), nameof(ItemInfoWindow.SetItem))]
        static class TooltipPatch
        {
            static void Postfix(ItemInfoWindow __instance, ItemObject item)
            {
                var def = Find(item?.data);
                if (def == null) return;
                if (__instance.description != null)
                {
                    __instance.description.text = def.Text;
                    if (__instance.descriptionBox != null) __instance.descriptionBox.SetActive(true);
                }
                __instance.UpdateContentSizers();
            }
        }
    }
}
