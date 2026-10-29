using System.Collections;
using UnityEngine;

namespace GentlemansClub
{
    // The bouncer at the VIP room: he stands square in its doorway until you pay (or bribe him with
    // Glimmerdust), then steps aside for the rest of the visit.
    internal class Bouncer : MonoBehaviour
    {
        internal static bool Enabled = true;
        internal static int Price = 100;
        internal bool Paid { get; private set; }
        internal System.Action OnLetIn;
        private ClubTalker _talker;
        private GameObject _block;
        private Animator _anim;
        private float _nextBlockLine;

        // In the club picture's pixels (see ConceptLayout): the orange room's doorway (x 640..720, opening
        // onto the hall at y 345), and where he stands once he's let you through.
        static readonly Vector2 DoorPx = new Vector2(680, 352), AsidePx = new Vector2(765, 368);
        const float DoorWidth = 80f / 64f;
        static Vector3 U(Vector2 px) => new Vector3((px.x - 527f) / 64f, (965f - px.y) / 64f, 0f);

        internal static IEnumerator Create(GameObject interior, System.Action<Bouncer> ready)
        {
            while (interior != null && !(Singleton<GameManager>.HasMain && Singleton<GameManager>.main.player != null
                                         && Singleton<GameManager>.main.gameState == GameState.Playing))
                yield return null;
            if (interior == null) yield break;
            var player = Singleton<GameManager>.main.player;
            // Always the same man: bald, bearded, shirt and trousers, a bit bigger than you.
            var body = ClubGoers.MakeFromPlayer(player, interior.transform, "Bouncer", male: true,
                look: new ClubGoers.Look { HairStyle = "None", Hair = new Color(0.12f, 0.09f, 0.08f), Beard = "01_", Skin = 5 });
            if (body == null) { ready(null); yield break; }
            body.transform.localPosition = U(DoorPx);
            body.transform.localScale = new Vector3(1.15f, 1.15f, 1f);
            var bouncer = body.AddComponent<Bouncer>();
            bouncer._anim = body.GetComponent<Animator>();
            bouncer._anim.SetFloat("DirFactor", -1f);

            // His shoulders fill the doorway.
            bouncer._block = new GameObject("DoorBlock") { layer = 10 }; // BodyCollisions
            bouncer._block.transform.SetParent(interior.transform, false);
            bouncer._block.transform.localPosition = U(new Vector2(680, 345)) + new Vector3(0f, 0.2f, 0f);
            bouncer._block.AddComponent<BoxCollider2D>().size = new Vector2(DoorWidth + 0.2f, 0.4f);

            var talker = body.AddComponent<ClubTalker>();
            talker.CustomDialogue = () =>
            {
                var inv = Singleton<Inventory>.main;
                return ClubDialogue.Bouncer(bouncer.Paid, Price, inv.TotalGold.CurrentValue, inv.ContainsItem(Glimmerdust.Data, out _));
            };
            talker.CustomEnd = bouncer.OnTalkEnd;
            talker.BarkSection = "bouncer";
            talker.Everyone = () => ClubGoers.Names;
            talker.Setup("Bouncer", body.transform.Find("Graphics").gameObject, triggerOffset: new Vector2(0f, -0.45f),
                triggerSize: new Vector2(1.8f, 1.1f), headHeight: 1.3f);
            bouncer._talker = talker;
            ready(bouncer);
        }

        private void OnTalkEnd(string end)
        {
            var inv = Singleton<Inventory>.main;
            if (end == ClubDialogue.CoverEnd && inv.HasEnoughGold(Price)) { inv.AddGold(-Price); LetIn(); }
            else if (end == ClubDialogue.BribeEnd && inv.ConsumeItemTypeOf(Glimmerdust.Data, 1) == 0) LetIn();
        }

        private void LetIn()
        {
            Paid = true;
            if (_block != null) _block.SetActive(false);
            AkUnitySoundEngine.PostEvent("Play_UI_General_Select_Important", gameObject);
            OnLetIn?.Invoke();
            StartCoroutine(StepAside());
        }

        // A few steps to the right, then back to facing the room.
        private IEnumerator StepAside()
        {
            var target = transform.parent.TransformPoint(U(AsidePx));
            var gfx = transform.Find("Graphics");
            while ((transform.position - target).sqrMagnitude > 0.0025f)
            {
                var d = target - transform.position;
                _anim.SetFloat("Speed", 0.5f);
                _anim.SetFloat("DirFactor", 0f);
                if (gfx != null) gfx.localRotation = Quaternion.Euler(0f, d.x < 0 ? 180f : 0f, 0f);
                transform.position = Vector3.MoveTowards(transform.position, target, 1.6f * Time.deltaTime);
                yield return null;
            }
            transform.position = target;
            Stand();
        }

        void Stand()
        {
            _anim.SetFloat("Speed", 0f);
            _anim.SetFloat("DirFactor", -1f);
            var gfx = transform.Find("Graphics");
            if (gfx != null) gfx.localRotation = Quaternion.identity;
        }

        private void OnEnable()
        {
            // The club's room was switched off mid-step (you left): finish the step at once.
            if (Paid && _anim != null && transform.parent != null)
            {
                transform.position = transform.parent.TransformPoint(U(AsidePx));
                Stand();
            }
        }

        private void Update()
        {
            if (Paid || _talker == null || _talker.Talking) return;
            var player = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
            if (player == null) return;
            // Walking up to him unpaid gets you a word.
            if ((player.transform.position - transform.position).sqrMagnitude < 1.4f * 1.4f && Time.time > _nextBlockLine)
            {
                _nextBlockLine = Time.time + 6f;
                _talker.Say(ClubLines.Fill(ClubLines.Pick("bouncer", "block", null), "Bouncer", ClubGoers.Names).Replace("$PRICE", Price.ToString()));
            }
        }
    }
}
