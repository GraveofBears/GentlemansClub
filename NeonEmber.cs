using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;

namespace GentlemansClub
{
    // The Neon Ember: the club's own cocktail, sold at the bar. A little health, a lot of courage.
    internal class NeonEmberData : CollectableItem
    {
        public override bool Execute(CharacterStats owner, ItemObject item)
        {
            if (!owner.TryGetComponent<PlayerMaster>(out var player)) return false;
            Singleton<Inventory>.main.ConsumeItem(item, 1);
            player.RestoreHealth(5, displayHealthFullNotification: false);
            Drunk.Instance?.Drink();
            return true;
        }
    }

    internal static class NeonEmber
    {
        public const string Key = "OdinPlus_NeonEmber";
        internal static ItemData Data => ClubItems.Get(Key);

        internal static void Define() => ClubItems.Add(new ClubItems.Def
        {
            Key = Key,
            Title = "Neon Ember",
            Text = "The club's own cocktail: glows pink, tastes like a bad decision, restores a little health. Each one makes you a little drunker. Four and you'll wake up somewhere with lighter pockets.",
            Price = 25,
            Create = () => ScriptableObject.CreateInstance<NeonEmberData>(),
            Palette = new Dictionary<char, Color32>
            {
                { 'k', new Color32(40, 28, 50, 255) }, { 'p', new Color32(255, 70, 180, 255) }, { 'w', new Color32(255, 200, 235, 255) },
                { 'g', new Color32(200, 215, 230, 255) }, { 'c', new Color32(210, 30, 50, 255) }, { 's', new Color32(60, 140, 60, 255) },
            },
            Pixels = new[]
            {
                "................",
                "...........s....",
                "..kkkkkkkkkcck..",
                "..kpwppppppcck..",
                "...kpwpppppk....",
                "....kppppppk....",
                ".....kppppk.....",
                "......kppk......",
                ".......gg.......",
                ".......gg.......",
                ".......gg.......",
                ".......gg.......",
                ".......gg.......",
                ".....kggggk.....",
                ".....kkkkkk.....",
                "................",
            },
        });
    }

    // How drunk you are: each drink adds a level that wears off after a while. You sway, the screen swims,
    // your character slurs; at four you pass out and wake up sober, a few coins lighter.
    internal class Drunk : MonoBehaviour
    {
        internal static Drunk Instance;
        internal static float SecondsPerLevel = 75f;
        const int PassOut = 4;

        int _level;
        float _soberAt, _nextLine;
        Volume _volume;
        LensDistortion _lens;
        ChromaticAberration _chroma;
        bool _postWasOn, _postForced, _passingOut;

        internal void Drink()
        {
            if (_passingOut) return;
            _level++;
            _soberAt = Time.time + SecondsPerLevel;
            if (_level >= PassOut) { StartCoroutine(PassOutRoutine()); return; }
            Say(_level == 1 ? "tipsy" : _level == 2 ? "drunk" : "wasted");
            _nextLine = Time.time + Random.Range(8f, 14f);
        }

        void Update()
        {
            if (_level <= 0) { SetScreen(0f); return; }
            if (Time.time > _soberAt)
            {
                _level--;
                _soberAt = Time.time + SecondsPerLevel;
                if (_level == 0) Say("sober");
            }
            float k = _level / 3f;
            SetScreen(_level >= 2 ? k : 0f);
            if (Time.time > _nextLine && _level > 0)
            {
                Say(_level == 1 ? "tipsy" : _level == 2 ? "drunk" : "wasted");
                _nextLine = Time.time + Random.Range(10f, 18f) / Mathf.Max(1, _level);
            }
        }

        // Sway after the game has posed the character this frame.
        void LateUpdate()
        {
            var player = Player();
            var gfx = player != null ? player.transform.Find("Graphics") : null;
            if (gfx == null) return;
            var e = gfx.localEulerAngles;
            float sway = _level <= 0 ? 0f : Mathf.Sin(Time.time * (1.4f + _level * 0.4f)) * (3f + _level * 4f);
            gfx.localEulerAngles = new Vector3(e.x, e.y, sway);
        }

        System.Collections.IEnumerator PassOutRoutine()
        {
            _passingOut = true;
            Say("passout");
            yield return new WaitForSeconds(1.2f);
            bool dark = false;
            Bus<FadeEvent>.Invoke(new FadeEvent(FadeType.FadeOut, 1.2f, () => dark = true));
            for (float t = 0; t < 4f && !dark; t += Time.unscaledDeltaTime) yield return null;
            // While you're out, someone helps themselves.
            var inv = Singleton<Inventory>.HasMain ? Singleton<Inventory>.main : null;
            int lost = 0;
            if (inv != null)
            {
                lost = Mathf.Min(inv.TotalGold.CurrentValue, Mathf.Max(5, inv.TotalGold.CurrentValue / 10));
                if (lost > 0) inv.AddGold(-lost);
            }
            _level = 0;
            SetScreen(0f);
            yield return new WaitForSeconds(1.5f);
            Bus<FadeEvent>.Invoke(new FadeEvent(FadeType.FadeIn, 1.2f, () => { }));
            yield return new WaitForSeconds(1.4f);
            PlayerTextReaction.ShowText(lost > 0 ? ClubLines.Pick("player", "wake", null).Replace("$COINS", lost.ToString()) : "Ugh. What happened?", 4f);
            _passingOut = false;
        }

        // The screen swims: lens distortion and colour fringing through a post-processing volume of our own.
        void SetScreen(float amount)
        {
            var cam = Camera.main;
            if (amount <= 0f)
            {
                if (_volume != null) _volume.weight = 0f;
                if (_postForced && cam != null) { cam.GetUniversalAdditionalCameraData().renderPostProcessing = _postWasOn; _postForced = false; }
                return;
            }
            if (cam == null) return;
            if (_volume == null)
            {
                var go = new GameObject("GentlemansClub_Drunk");
                DontDestroyOnLoad(go);
                _volume = go.AddComponent<Volume>();
                _volume.isGlobal = true;
                _volume.priority = 100f;
                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                _lens = profile.Add<LensDistortion>(true);
                _chroma = profile.Add<ChromaticAberration>(true);
                _volume.profile = profile;
            }
            var data = cam.GetUniversalAdditionalCameraData();
            if (!_postForced) { _postWasOn = data.renderPostProcessing; data.renderPostProcessing = true; _postForced = true; }
            _volume.weight = 1f;
            float t = Time.time;
            _lens.intensity.Override(Mathf.Sin(t * 1.3f) * 0.35f * amount);
            _lens.xMultiplier.Override(0.6f + 0.4f * Mathf.Sin(t * 0.7f));
            _lens.center.Override(new Vector2(0.5f + Mathf.Sin(t * 0.9f) * 0.05f * amount, 0.5f + Mathf.Cos(t * 1.1f) * 0.05f * amount));
            _chroma.intensity.Override(0.35f + 0.5f * amount);
        }

        static PlayerMaster Player() => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

        static void Say(string key) => PlayerTextReaction.ShowText(ClubLines.Pick("player", key, null), 3.5f);

        void OnDestroy() => SetScreen(0f);
    }
}
