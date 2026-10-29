using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GentlemansClub
{
    // Tips and making it rain: coins showering down, the stage lights flaring, the other dancers cheering.
    internal static class ClubEffects
    {
        internal static Sprite CoinSprite; // the game's own coin, loaded by the plugin

        internal static void Tipped(ClubTalker girl, bool big)
        {
            AkUnitySoundEngine.PostEvent("Play_UI_General_Select_Important", girl.gameObject);
            Shower(girl, big ? 12 : 4, 1.2f, 0.8f);
        }

        internal static void MakeItRain(ClubTalker dancer, bool pour)
        {
            AkUnitySoundEngine.PostEvent("Play_Loot_Rare", dancer.gameObject);
            Shower(dancer, pour ? 90 : 30, pour ? 3.5f : 2.2f, pour ? 2.2f : 1.4f);
            dancer.StartCoroutine(Flare(dancer.transform.root, pour ? 4f : 2.5f));
            // The other dancers notice.
            foreach (var other in Object.FindObjectsByType<ClubTalker>(FindObjectsSortMode.None).Where(t => t.IsDancer && t != dancer))
                dancer.StartCoroutine(CheerLater(other, Random.Range(0.6f, 2.2f), pour));
            if (pour) dancer.StartCoroutine(SecondWave(dancer));
        }

        // A private dance: the room goes dim and pink and the narrator tells you how it went.
        internal static void PrivateDance(ClubTalker dancer) => dancer.StartCoroutine(PrivateDanceRoutine(dancer));

        static IEnumerator PrivateDanceRoutine(ClubTalker dancer)
        {
            var gm = Singleton<GameManager>.main;
            while (gm.gameState == GameState.Dialogue) yield return null; // her goodbye first
            yield return new WaitForSeconds(0.3f);
            var lights = Singleton<GlobalLightManager>.HasMain ? Singleton<GlobalLightManager>.main : null;
            var before = lights != null ? lights.GetLight() : default;
            lights?.SetLight(new LightInfo { intensity = 0.22f, color = new Color(1f, 0.35f, 0.65f, 0f) });
            AkUnitySoundEngine.PostEvent("Play_Loot_Magic", dancer.gameObject);
            Singleton<DialogueManager>.main.PlayDialog(ClubDialogue.PrivateDance(dancer.SpeakerName, ClubTalker.PrivateDancePrice));
            for (float t = 0; t < 2f && gm.gameState != GameState.Dialogue; t += Time.deltaTime) yield return null;
            while (gm.gameState == GameState.Dialogue) yield return null;
            if (lights != null) lights.SetLight(before);
            PlayerTextReaction.ShowText(ClubLines.Pick("player", "after_private", null), 3.5f);
        }

        static IEnumerator SecondWave(ClubTalker dancer)
        {
            yield return new WaitForSeconds(1.2f);
            if (dancer != null) AkUnitySoundEngine.PostEvent("Play_Loot_Rare", dancer.gameObject);
        }

        static IEnumerator CheerLater(ClubTalker girl, float delay, bool pour)
        {
            yield return new WaitForSeconds(delay);
            if (girl != null) girl.Say(ClubLines.Pick("girl:" + girl.SpeakerName, pour ? "pour" : "rain"));
        }

        // Every light in the club burns brighter for a moment.
        static IEnumerator Flare(Transform club, float seconds)
        {
            var lights = club.GetComponentsInChildren<Light2D>().ToList();
            var baseline = lights.Select(l => l.intensity).ToList();
            for (float t = 0; t < seconds; t += Time.deltaTime)
            {
                float k = 1f + 1.2f * Mathf.Abs(Mathf.Sin(t * 9f)) * (1f - t / seconds);
                for (int i = 0; i < lights.Count; i++) if (lights[i] != null) lights[i].intensity = baseline[i] * k;
                yield return null;
            }
            for (int i = 0; i < lights.Count; i++) if (lights[i] != null) lights[i].intensity = baseline[i];
        }

        static void Shower(ClubTalker over, int count, float spread, float height)
        {
            if (CoinSprite == null) return;
            var center = over.transform.position;
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Coin");
                go.transform.position = center + new Vector3(Random.Range(-spread, spread) * 0.5f, height + Random.Range(1.5f, 3.5f), 0f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = CoinSprite;
                sr.sortingLayerName = "Player";
                sr.sortingOrder = 650;
                var fall = go.AddComponent<FallingCoin>();
                fall.LandY = center.y + Random.Range(-0.35f, 0.35f);
                fall.Delay = Random.Range(0f, 0.9f);
                fall.Velocity = new Vector2(Random.Range(-0.6f, 0.6f), -Random.Range(1f, 3f));
            }
        }
    }

    // One coin: drops, bounces once, glints on the floor, fades away.
    internal class FallingCoin : MonoBehaviour
    {
        internal float LandY, Delay;
        internal Vector2 Velocity;
        private SpriteRenderer _sr;
        private bool _bounced, _landed;
        private float _landedAt, _spin;

        private void Start()
        {
            _sr = GetComponent<SpriteRenderer>();
            _sr.enabled = Delay <= 0f;
            _spin = Random.Range(8f, 16f);
        }

        private void Update()
        {
            if (Delay > 0f) { Delay -= Time.deltaTime; if (Delay <= 0f) _sr.enabled = true; return; }
            if (!_landed)
            {
                Velocity.y -= 9f * Time.deltaTime;
                transform.position += (Vector3)(Velocity * Time.deltaTime);
                transform.localScale = new Vector3(Mathf.Cos(Time.time * _spin), 1f, 1f); // spinning edge-on
                if (transform.position.y <= LandY)
                {
                    transform.position = new Vector3(transform.position.x, LandY, 0f);
                    if (!_bounced && Velocity.y < -2f) { Velocity = new Vector2(Velocity.x * 0.4f, -Velocity.y * 0.3f); _bounced = true; }
                    else { _landed = true; _landedAt = Time.time; transform.localScale = Vector3.one; _sr.sortingOrder = 5; }
                }
                return;
            }
            float age = Time.time - _landedAt;
            if (age > 1.4f) _sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(1f - (age - 1.4f) / 0.6f));
            if (age > 2f) Destroy(gameObject);
        }
    }
}
