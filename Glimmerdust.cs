using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.Localization;
using UnityEngine.Rendering.Universal;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using Random = UnityEngine.Random;

namespace GentlemansClub
{
    // Glimmerdust: ground Kymia crystal from Trixie's "allergy medicine" pouch. Use it for a minute of speed
    // and fast swings with your character babbling, then a minute of comedown, then the odd craving.
    internal class GlimmerdustData : CollectableItem
    {
        public override bool Execute(CharacterStats owner, ItemObject item)
        {
            if (!owner.TryGetComponent<PlayerMaster>(out _)) return false;
            Singleton<Inventory>.main.ConsumeItem(item, 1);
            GlimmerHigh.Take();
            return true;
        }
    }

    internal static class Glimmerdust
    {
        public const string Key = "OdinPlus_Glimmerdust";
        internal static int SellPrice = 15;
        internal static ItemData Data => ClubItems.Get(Key);

        // A tiny ziplock baggie of sparkly pink powder.
        internal static void Define() => ClubItems.Add(new ClubItems.Def
        {
            Key = Key,
            Title = "Glimmerdust",
            Text = "Finely ground Kymia crystal from Trixie's \"allergy medicine\" pouch. A minute of going very fast and hitting very fast, then a minute of regretting it. Not for sale. Except by Trixie.",
            Price = SellPrice,
            Create = () => ScriptableObject.CreateInstance<GlimmerdustData>(),
            Palette = new Dictionary<char, Color32>
            {
                { 'k', new Color32(45, 30, 55, 255) }, { 'w', new Color32(225, 232, 245, 200) }, { 'r', new Color32(205, 60, 85, 255) },
                { 'p', new Color32(245, 165, 220, 255) }, { 'P', new Color32(255, 255, 255, 255) }, { 's', new Color32(255, 255, 255, 255) },
            },
            Pixels = new[]
            {
                "................",
                "................",
                "....kkkkkkkk....",
                "....kwwwwwwk....",
                "....krrrrrrk....",
                "....kwwwwwwk....",
                "...kwwwwwwwwk...",
                "...kwwwswwwwk...",
                "...kwwwwwwwwk...",
                "...kppppppppk...",
                "...kpPpppPppk...",
                "...kpppPpppPk...",
                "...kPpppppppk...",
                "...kppPppPppk...",
                "....kkkkkkkk....",
                "................",
            },
        });
    }

    // The high, the comedown and the cravings. Lives on the plugin object, so it carries over scene loads
    // and puts its stat changes back on the new player each time.
    internal class GlimmerHigh : MonoBehaviour
    {
        internal static GlimmerHigh Instance;
        internal static float HighSeconds = 60f, ComedownSeconds = 60f;
        internal static float HighSpeed = 35f, HighAttack = 30f, LowSpeed = -25f, LowAttack = -20f; // percent

        enum Phase { Sober, High, Comedown, Craving }
        Phase _phase;
        float _phaseEnds, _nextLine;
        int _cravings;
        CharacterStats _stats;
        StatModifier<SecondaryStat> _speed, _attack;
        Light2D _glow;

        internal static void Take()
        {
            if (Instance == null) return;
            Instance.Begin();
        }

        void Begin()
        {
            bool again = _phase == Phase.High;
            Set(Phase.High, HighSeconds);
            AkUnitySoundEngine.PostEvent("Play_Loot_Magic", Player()?.gameObject);
            Babble(again ? "again" : "high", 3.5f);
        }

        void Set(Phase phase, float seconds)
        {
            ClearMods();
            _phase = phase;
            _phaseEnds = Time.time + seconds;
            _nextLine = Time.time + Random.Range(5f, 9f);
            if (phase == Phase.High) ApplyMods(HighSpeed, HighAttack);
            if (phase == Phase.Comedown) ApplyMods(LowSpeed, LowAttack);
            if (phase == Phase.Craving) _cravings = 3;
        }

        void ApplyMods(float speed, float attack)
        {
            var player = Player();
            if (player == null) return;
            _stats = player.GetComponent<CharacterStats>();
            if (_stats == null) return;
            _speed = new StatModifier<SecondaryStat> { stat = SecondaryStat.MoveSpeed, modifier = new Modifier(speed, ModifierType.Multiplier) };
            _attack = new StatModifier<SecondaryStat> { stat = SecondaryStat.AttackRate, modifier = new Modifier(attack, ModifierType.Multiplier) };
            _stats.AddModifier(_speed);
            _stats.AddModifier(_attack);
        }

        void ClearMods()
        {
            if (_stats != null)
            {
                if (_speed != null) _stats.RemoveModifier(_speed);
                if (_attack != null) _stats.RemoveModifier(_attack);
            }
            _stats = null; _speed = null; _attack = null;
        }

        static PlayerMaster Player() => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

        void Update()
        {
            if (_phase == Phase.Sober) { SetGlow(null, 0f); return; }
            var player = Player();
            // New scene, new player: move the stat changes over.
            if (player != null && (_stats == null || _stats.gameObject != player.gameObject) && (_phase == Phase.High || _phase == Phase.Comedown))
            {
                _stats = null; _speed = null; _attack = null; // the old player went with its scene
                ApplyMods(_phase == Phase.High ? HighSpeed : LowSpeed, _phase == Phase.High ? HighAttack : LowAttack);
            }

            if (_phase == Phase.High)
            {
                // Sparkly: a glow around you that runs through the colours.
                SetGlow(player, 1.1f, Color.HSVToRGB(Mathf.Repeat(Time.time * 0.35f, 1f), 0.55f, 1f));
                Sparkle.Emit(player, 14f, true);
                if (Time.time > _nextLine) { Babble("high"); _nextLine = Time.time + Random.Range(6f, 11f); }
                if (Time.time > _phaseEnds) { Set(Phase.Comedown, ComedownSeconds); Babble("comedown"); }
            }
            else if (_phase == Phase.Comedown)
            {
                SetGlow(player, 0.5f, new Color(0.45f, 0.55f, 0.8f));
                Sparkle.Emit(player, 3f, false); // the last few, dull and slow
                if (Time.time > _nextLine) { Babble("comedown"); _nextLine = Time.time + Random.Range(10f, 16f); }
                if (Time.time > _phaseEnds) { Set(Phase.Craving, 0f); _nextLine = Time.time + Random.Range(25f, 45f); }
            }
            else if (_phase == Phase.Craving)
            {
                SetGlow(null, 0f);
                if (Time.time > _nextLine)
                {
                    Babble("craving");
                    _nextLine = Time.time + Random.Range(40f, 80f);
                    if (--_cravings <= 0) _phase = Phase.Sober;
                }
            }
        }

        void SetGlow(PlayerMaster player, float intensity, Color color = default)
        {
            if (player == null || intensity <= 0f)
            {
                if (_glow != null) _glow.enabled = false;
                return;
            }
            if (_glow == null || _glow.transform.parent != player.transform)
            {
                if (_glow != null) Destroy(_glow.gameObject);
                var go = new GameObject("GlimmerGlow");
                go.transform.SetParent(player.transform, false);
                go.transform.localPosition = new Vector3(0f, 0.5f, 0f);
                _glow = go.AddComponent<Light2D>();
                _glow.lightType = Light2D.LightType.Point;
                // A light made from code only lights the Default layer; you are on "Player".
                typeof(Light2D).GetField("m_ApplyToSortingLayers", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(_glow, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(SortingLayer.layers, l => l.id)));
                _glow.pointLightOuterRadius = 3f;
                _glow.pointLightInnerRadius = 0.3f;
            }
            _glow.enabled = true;
            _glow.color = color;
            _glow.intensity = intensity * (0.8f + 0.2f * Mathf.Sin(Time.time * 7f));
        }

        static void Babble(string key, float seconds = 3f)
        {
            var line = ClubLines.Pick("player", key, null).Replace("$PLAYER", "you");
            PlayerTextReaction.ShowText(line, seconds);
        }

        void OnDestroy() => ClearMods();
    }

    // A little four-point star that pops up around you on Glimmerdust, twinkles, drifts up and fades.
    internal class Sparkle : MonoBehaviour
    {
        static Sprite _sprite;
        static float _owed;
        private SpriteRenderer _sr;
        private float _born, _life, _hue, _twinkle;
        private bool _bright;
        private Vector3 _drift;

        // Spawns about `perSecond` sparkles a second around the player.
        internal static void Emit(PlayerMaster player, float perSecond, bool bright)
        {
            if (player == null) return;
            _owed += perSecond * Time.deltaTime;
            for (; _owed >= 1f; _owed -= 1f)
            {
                var go = new GameObject("GlimmerSparkle");
                go.transform.position = player.transform.position +
                    new Vector3(Random.Range(-0.45f, 0.45f), Random.Range(0.05f, 1.3f), 0f);
                var s = go.AddComponent<Sparkle>();
                s._bright = bright;
            }
        }

        static Sprite Star()
        {
            if (_sprite != null) return _sprite;
            // 7x7: a bright centre, four short arms, soft corners.
            var tex = new Texture2D(7, 7, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var px = new Color32[49];
            for (int y = 0; y < 7; y++)
            for (int x = 0; x < 7; x++)
            {
                int dx = Mathf.Abs(x - 3), dy = Mathf.Abs(y - 3);
                byte a = dx == 0 && dy == 0 ? (byte)255 : (dx == 0 || dy == 0) ? (byte)(255 - 70 * (dx + dy)) : dx == 1 && dy == 1 ? (byte)110 : (byte)0;
                px[y * 7 + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply();
            _sprite = Sprite.Create(tex, new Rect(0, 0, 7, 7), new Vector2(0.5f, 0.5f), 32f);
            return _sprite;
        }

        private void Start()
        {
            _sr = gameObject.AddComponent<SpriteRenderer>();
            _sr.sprite = Star();
            _sr.sortingLayerName = "Player";
            _sr.sortingOrder = 640; // over you, under the talk bubbles
            _born = Time.time;
            _life = _bright ? Random.Range(0.5f, 0.9f) : Random.Range(0.8f, 1.2f);
            _hue = Random.value;
            _twinkle = Random.Range(14f, 22f);
            _drift = new Vector3(Random.Range(-0.15f, 0.15f), _bright ? Random.Range(0.4f, 0.8f) : 0.15f, 0f);
            Update();
        }

        private void Update()
        {
            float t = (Time.time - _born) / _life;
            if (t >= 1f) { Destroy(gameObject); return; }
            transform.position += _drift * Time.deltaTime;
            // Pops in, twinkles, shrinks away.
            float size = Mathf.Sin(t * Mathf.PI) * (0.75f + 0.25f * Mathf.Sin(Time.time * _twinkle));
            transform.localScale = Vector3.one * size * (_bright ? 1f : 0.6f);
            var c = _bright ? Color.HSVToRGB(Mathf.Repeat(_hue + Time.time * 0.6f, 1f), 0.35f, 1f) : new Color(0.6f, 0.7f, 0.95f);
            c.a = 1f - t * t;
            _sr.color = c;
        }
    }
}
