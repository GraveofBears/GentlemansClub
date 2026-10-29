using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GentlemansClub
{
    // Club-goers wandering the club: copies of the player's own art (the game's starter female body,
    // in its default bikini) with all gear hidden, each girl always in her own look (Looks below). They walk
    // between points along the room's aisles; two points are linked only when nothing solid lies
    // between them, so they never cut through tables or walls.
    internal class ClubGoers : MonoBehaviour
    {
        internal int Count = 3;

        // Aisle points in the club picture's pixels (see ConceptLayout: origin 527,965; 64 px per unit).
        static readonly Vector2[] NodesPx =
        {
            new Vector2(527, 795), new Vector2(428, 775), new Vector2(635, 780), new Vector2(240, 772),
            new Vector2(760, 785), new Vector2(930, 785), new Vector2(428, 606), new Vector2(635, 606),
            new Vector2(318, 606), new Vector2(300, 440), new Vector2(110, 500), new Vector2(740, 600),
            new Vector2(760, 450), new Vector2(930, 470),
        };
        static Vector3 U(Vector2 px) => new Vector3((px.x - 527f) / 64f, (965f - px.y) / 64f, 0f);

        // Parts of the player's art that are gear, weapons, effects or gameplay, not body.
        static readonly string[] Strip =
        {
            "AttackHitbox", "FarmSelector", "GamepadSelectPivot", "Axe_ShockWave", "Staff", "MagicShot", "MagicBeam", "Bow",
            "FishingIndicator", "Vfxs", "VFXs Weapons", "VfxsBehind", "Fx", "FxBehind", "Extras", "VfxHolder", "Events",
            "Weapons", "Shields", "Image",
        };
        static readonly string[] GearFolders = { "Head", "Chest", "Legs", "Cloak", "CloakB", "FaceHair" };

        static readonly Color[] HairColors =
        {
            new Color(0.12f, 0.09f, 0.08f), new Color(0.35f, 0.2f, 0.1f), new Color(0.6f, 0.38f, 0.18f), new Color(0.92f, 0.8f, 0.5f),
            new Color(0.95f, 0.93f, 0.88f), new Color(0.75f, 0.2f, 0.12f), new Color(0.95f, 0.45f, 0.7f), new Color(0.55f, 0.35f, 0.95f),
            new Color(0.3f, 0.75f, 0.95f),
        };
        static readonly Color[] SuitColors =
        {
            new Color(0.1f, 0.1f, 0.12f), new Color(0.85f, 0.15f, 0.25f), new Color(0.95f, 0.4f, 0.7f), new Color(0.5f, 0.25f, 0.85f),
            new Color(0.2f, 0.7f, 0.9f), new Color(0.95f, 0.85f, 0.4f), new Color(0.95f, 0.95f, 0.95f),
        };

        // Everyone who can be gossiped about: the stage dancers and tonight's club-goers.
        internal static readonly string[] StageNames = { "Ruby", "Violet", "Mocha" };
        internal static readonly List<string> Names = new List<string>(StageNames);
        private readonly List<Wanderer> _crowd = new List<Wanderer>();
        private readonly List<Vector3> _nodes = new List<Vector3>();
        private readonly List<List<int>> _links = new List<List<int>>();

        // The game switches the club's room off while you're elsewhere, so nothing here can wait in a
        // routine. The plugin calls TryBuild every frame until the girls are made (room on or off, so they
        // are already standing there when you walk in); the aisles are traced the first frame the room is on,
        // since its walls only exist for physics then.
        private bool _built, _linked;
        private readonly List<(Wanderer walker, int start)> _waiting = new List<(Wanderer, int)>();

        internal void TryBuild()
        {
            if (_built) return;
            // Wait for normal play: during loads and teleports the game hides the player's art, and a copy
            // made then would stay hidden.
            if (!(Singleton<GameManager>.HasMain && Singleton<GameManager>.main.player != null
                  && Singleton<GameManager>.main.gameState == GameState.Playing)) return;
            _built = true;
            var player = Singleton<GameManager>.main.player;
            foreach (var px in NodesPx) _nodes.Add(transform.TransformPoint(U(px)));

            // Each club-goer is a different girl from the lines file (the stage dancers keep their own names).
            var names = ClubLines.Girls().Where(n => !StageNames.Contains(n)).OrderBy(n => n == "Trixie" ? -1f : Random.value).ToList(); // Trixie's always in
            if (names.Count == 0) names.Add("Candy");
            Names.Clear();
            Names.AddRange(StageNames);
            Names.AddRange(names.Take(Count));
            var starts = Enumerable.Range(0, _nodes.Count).OrderBy(_ => Random.value).ToList();
            for (int i = 0; i < Count; i++)
            {
                string name = names[i % names.Count];
                var goer = MakeFromPlayer(player, transform, "ClubGoer_" + name, look: LookFor(name));
                if (goer == null) return;
                int start = starts[i % starts.Count];
                goer.transform.position = _nodes[start];
                var talker = goer.AddComponent<ClubTalker>();
                talker.Everyone = () => Names;
                talker.CanFollow = true;
                goer.AddComponent<Follower>();
                talker.Setup(name, goer.transform.Find("Graphics").gameObject,
                    triggerOffset: new Vector2(0f, 0.3f), triggerSize: new Vector2(1.3f, 1.2f), headHeight: 1.05f);
                goer.GetComponent<Animator>().SetFloat("DirFactor", -1f);
                _waiting.Add((goer.AddComponent<Wanderer>(), start));
            }
            DressStage(player);
            GentlemansClubPlugin.Log.LogInfo($"Club-goers ready: {string.Join(", ", names.Take(Count))}.");
        }

        private void Update()
        {
            if (!_built || _linked) return;
            _linked = true;
            Physics2D.SyncTransforms();
            int solid = 1 << 10; // BodyCollisions
            for (int i = 0; i < _nodes.Count; i++)
            {
                _links.Add(new List<int>());
                for (int j = 0; j < _nodes.Count; j++)
                {
                    if (i == j) continue;
                    var d = _nodes[j] - _nodes[i];
                    if (d.magnitude < 9f && !Physics2D.CircleCast(_nodes[i], 0.15f, d.normalized, d.magnitude, solid))
                        _links[i].Add(j);
                }
            }
            var linked = Enumerable.Range(0, _nodes.Count).Where(i => _links[i].Count > 0).ToList();
            GentlemansClubPlugin.Log.LogInfo($"Club-goers: {linked.Count}/{_nodes.Count} aisle points linked.");
            if (linked.Count == 0) return; // they just stand where they are
            foreach (var (walker, start) in _waiting)
                if (walker != null) walker.Setup(_nodes, _links, _links[start].Count > 0 ? start : Wanderer.FreeStart(linked, _crowd), _crowd);
            _waiting.Clear();
        }

        // The stage dancers become player-model girls too, with hair to match their names, dancing a routine
        // made from the player's own poses. Their old sprites are hidden; their dialogue and spotlights stay.
        static readonly (string spot, string girl)[] StageSpots =
        {
            ("Dancer_RedheadPole", "Ruby"), ("Dancer_Purple", "Violet"), ("Dancer_Burnett", "Mocha"),
        };

        // The bundle's old pixel-art dancers, switched off the moment the club is placed (before anyone
        // can see them); their spots, lights and dialogue stay.
        internal static void HideOldDancers(Transform interior)
        {
            var stage = interior.Find("== Stage");
            if (stage == null) return;
            foreach (var (spotName, _) in StageSpots)
            {
                var spot = stage.Find(spotName);
                if (spot == null) continue;
                foreach (var r in spot.GetComponentsInChildren<SpriteRenderer>(true)) if (r.transform.parent == spot || r.name == "Art") r.enabled = false;
                var oldAnim = spot.Find("Art")?.GetComponent<Animator>();
                if (oldAnim != null) oldAnim.enabled = false;
            }
        }

        void DressStage(PlayerMaster player)
        {
            var stage = transform.Find("== Stage");
            if (stage == null) return;
            HideOldDancers(transform);
            foreach (var (spotName, name) in StageSpots)
            {
                var spot = stage.Find(spotName);
                if (spot == null) continue;
                var girl = MakeFromPlayer(player, transform, "StageGirl_" + name, look: LookFor(name));
                if (girl == null) return;
                girl.transform.SetParent(spot, false);
                girl.transform.localPosition = Vector3.zero;
                girl.AddComponent<DanceRoutine>();
            }
        }

        // A fixed look: hair style by name, colours, skin tone, beard piece.
        internal class Look
        {
            public string HairStyle;   // part of the HairAsset's name, e.g. "None" (bald) or "Ponytail"
            public Color Hair, Suit = Color.white;
            public string Beard;       // FaceHair piece prefix, e.g. "01_"
            public int Skin = -1;      // 0..8, the game's skin palette
            public int FaceAddon = -1; // FaceAddon piece, -1 for none
        }

        static Color C(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        // Every girl always looks the same, matched to who she is. Hair styles are the game's own
        // (Ponytail, LongLoose, TiedBack, BigBraid, Cohh, MediumLength, Sides, Mic, Clark, PointToupee).
        internal static readonly Dictionary<string, Look> Looks = new Dictionary<string, Look>
        {
            // the headliner: long red hair, red suit
            ["Ruby"] = new Look { HairStyle = "LongLoose", Hair = C(0xC42E1E), Suit = C(0xD81E3C), Skin = 1 },
            // dyed purple, and touchy about it
            ["Violet"] = new Look { HairStyle = "MediumLength", Hair = C(0x9450F2), Suit = C(0x7A3BD9), Skin = 2 },
            // smooth and dark: deep skin, brown braid, gold suit
            ["Mocha"] = new Look { HairStyle = "BigBraid", Hair = C(0x4A2A16), Suit = C(0xE0B040), Skin = 7 },
            // sweet: pink ponytail, pink suit
            ["Candy"] = new Look { HairStyle = "Ponytail", Hair = C(0xF27AB8), Suit = C(0xF266B3), Skin = 0 },
            // fifteen years on the pole: short black hair, black suit
            ["Roxy"] = new Look { HairStyle = "Sides", Hair = C(0x1F1714), Suit = C(0x1A1A1F), Skin = 3 },
            // the stargazer: long silver-blue hair, white suit
            ["Starla"] = new Look { HairStyle = "LongLoose", Hair = C(0xB8D4F5), Suit = C(0xF2F2F7), Skin = 2 },
            // all business: dark hair, red suit
            ["Destiny"] = new Look { HairStyle = "MediumLength", Hair = C(0x2B1A12), Suit = C(0xA8162A), Skin = 6 },
            // wide-eyed: blonde braid, sky-blue suit
            ["Bambi"] = new Look { HairStyle = "BigBraid", Hair = C(0xF0D480), Suit = C(0x7EC8F0), Skin = 0 },
            // ex-dungeon-runner: black hair tied back, green suit
            ["Jade"] = new Look { HairStyle = "TiedBack", Hair = C(0x141414), Suit = C(0x2E9E5E), Skin = 4 },
            // smooth and expensive: wine-red hair, deep plum suit
            ["Velvet"] = new Look { HairStyle = "LongLoose", Hair = C(0x6E1630), Suit = C(0x4A1F5C), Skin = 5 },
            // wired: messy platinum hair, neon cyan suit
            ["Trixie"] = new Look { HairStyle = "Cohh", Hair = C(0xF2EEE0), Suit = C(0x33E0F0), Skin = 1 },
        };

        // A girl from the lines file with no entry above still always gets the same look, picked from her name.
        internal static Look LookFor(string name)
        {
            if (Looks.TryGetValue(name, out var look)) return look;
            int seed = 17;
            foreach (char ch in name) seed = seed * 31 + ch;
            var rng = new System.Random(seed);
            string[] styles = { "Ponytail", "LongLoose", "TiedBack", "BigBraid", "MediumLength", "Sides" };
            return Looks[name] = new Look
            {
                HairStyle = styles[rng.Next(styles.Length)],
                Hair = HairColors[rng.Next(HairColors.Length)],
                Suit = SuitColors[rng.Next(SuitColors.Length)],
                Skin = rng.Next(9),
            };
        }

        // A copy of the player's art: starter body (female unless asked for the male one), no gear, in the
        // given look (random without one). Also used for the bouncer.

        internal static GameObject MakeFromPlayer(PlayerMaster player, Transform parent, string objectName, bool male = false, Look look = null)
        {
            var srcGraphics = player.transform.Find("Graphics");
            if (srcGraphics == null || player.animator == null)
            {
                GentlemansClubPlugin.Log.LogWarning("Club-goers: couldn't find the player's art to copy.");
                return null;
            }
            var root = new GameObject(objectName);
            root.transform.SetParent(parent, false);
            var gfx = Instantiate(srcGraphics.gameObject, root.transform);
            gfx.name = "Graphics"; // the animations address their sprites as Graphics/...
            gfx.transform.localPosition = srcGraphics.localPosition;
            gfx.transform.localRotation = Quaternion.identity;
            var srcShadow = player.transform.Find("Shadow");
            if (srcShadow != null) Instantiate(srcShadow.gameObject, root.transform).name = "Shadow";

            foreach (var name in Strip)
                foreach (var t in gfx.GetComponentsInChildren<Transform>(true).Where(t => t.name == name).ToList())
                    if (t != null) DestroyImmediate(t.gameObject);
            foreach (var c in root.GetComponentsInChildren<Collider2D>(true)) DestroyImmediate(c);
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(mb is ReorderObjects)) DestroyImmediate(mb);

            // The player's art may have been switched off by the game (loading, teleport fades): switch the
            // body back on. Folders stay on; which piece inside each one shows is chosen below.
            gfx.SetActive(true);
            foreach (Transform folder in gfx.transform) folder.gameObject.SetActive(true);
            var character = gfx.transform.Find("Character");
            if (character != null) foreach (Transform part in character) part.gameObject.SetActive(true);
            foreach (var r in root.GetComponentsInChildren<SpriteRenderer>(true)) r.enabled = true;
            foreach (var f in GearFolders) HideChildren(gfx.transform.Find(f));
            ShowOnly(gfx.transform.Find("Clothes"), male ? "00_ClothesMale" : "01_ClothesFemale"); // the starter body: bikini, or shorts
            if (male)
            {
                // Men get dressed: the light armour set reads as a plain shirt and trousers (art layer 2 of
                // the Chest and Legs folders; the game sorts each folder's pieces by name).
                ShowNamed(gfx.transform.Find("Chest"), "02_");
                ShowNamed(gfx.transform.Find("Legs"), "02_");
            }

            // Random hair from the player's own hair list.
            var hairs = player.GetComponent<AddonsHandler>()?.hairAssets;
            var hair = look?.HairStyle != null && hairs != null ? hairs.FirstOrDefault(h => h != null && h.name.Contains(look.HairStyle)) : null;
            if (hair == null && look?.HairStyle == null) hair = hairs != null && hairs.Count > 0 ? hairs[Random.Range(0, hairs.Count)] : null;
            ShowIndex(gfx.transform.Find("HairFistplane"), hair ? hair.overlayHairIndex : -1);
            ShowIndex(gfx.transform.Find("HairFront"), hair ? hair.frontHairIndex : -1);
            ShowIndex(gfx.transform.Find("HairBehind"), hair ? hair.behindHairIndex : -1);
            var face = gfx.transform.Find("FaceAddon");
            ShowIndex(face, look != null ? look.FaceAddon : face != null && Random.value < 0.35f ? Random.Range(0, face.childCount) : -1);

            // Own colours: hair and suit tint, skin from the game's 9-tone palette.
            var hairColor = look != null ? look.Hair : HairColors[Random.Range(0, HairColors.Length)];
            foreach (var f in new[] { "HairFistplane", "HairFront", "HairBehind" }) Tint(gfx.transform.Find(f), "_Color", hairColor);
            Tint(gfx.transform.Find("Clothes"), "_Color", look != null ? look.Suit : SuitColors[Random.Range(0, SuitColors.Length)]);
            int skin = look != null && look.Skin >= 0 ? look.Skin : Random.Range(0, 9);
            if (look?.Beard != null)
            {
                ShowNamed(gfx.transform.Find("FaceHair"), look.Beard);
                Tint(gfx.transform.Find("FaceHair"), "_Color", hairColor);
            }
            foreach (var r in (gfx.transform.Find("Character")?.GetComponentsInChildren<Renderer>(true) ?? new Renderer[0]))
                if (r.sharedMaterial != null && r.sharedMaterial.HasProperty("_Index")) r.material.SetFloat("_Index", skin);

            var anim = root.AddComponent<Animator>();
            anim.runtimeAnimatorController = player.animator.runtimeAnimatorController;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            root.AddComponent<AnimationEventSink>();
            return root;
        }

        static void HideChildren(Transform folder)
        {
            if (folder == null) return;
            foreach (Transform c in folder) c.gameObject.SetActive(false);
        }

        static void ShowOnly(Transform folder, string child)
        {
            if (folder == null) return;
            foreach (Transform c in folder) c.gameObject.SetActive(c.name == child);
        }

        static void ShowNamed(Transform folder, string prefix)
        {
            if (folder == null) return;
            folder.gameObject.SetActive(true);
            foreach (Transform c in folder) c.gameObject.SetActive(c.name.StartsWith(prefix));
        }

        static void ShowIndex(Transform folder, int index)
        {
            if (folder == null) return;
            folder.gameObject.SetActive(true);
            for (int i = 0; i < folder.childCount; i++) folder.GetChild(i).gameObject.SetActive(i == index);
        }

        static void Tint(Transform folder, string prop, Color color)
        {
            if (folder == null) return;
            foreach (var r in folder.GetComponentsInChildren<Renderer>(true))
                if (r.sharedMaterial != null && r.sharedMaterial.HasProperty(prop)) r.material.SetColor(prop, color);
        }
    }

    // Walks node to node with the game's walk animation, pausing and looking around in between.
    internal class Wanderer : MonoBehaviour
    {
        const float WalkSpeed = 1.3f, Personal = 0.6f;
        private List<Vector3> _nodes;
        private List<List<int>> _links;
        private List<Wanderer> _crowd;
        private int _at, _to = -1, _rank;
        private float _waitUntil;
        private Animator _anim;
        private Transform _gfx;
        private ClubTalker _talker;

        internal void Setup(List<Vector3> nodes, List<List<int>> links, int start, List<Wanderer> crowd)
        {
            _nodes = nodes; _links = links; _at = start; _crowd = crowd;
            _rank = crowd.Count;
            crowd.Add(this);
            transform.position = nodes[start];
            _anim = GetComponent<Animator>();
            _gfx = transform.Find("Graphics");
            _talker = GetComponent<ClubTalker>();
            _waitUntil = Time.time + Random.Range(0.5f, 4f);
            Face(new Vector2(Random.Range(-1f, 1f), -1f));
        }

        // A free point to stand on, at the start: nobody else standing there or heading there.
        // Back to wandering from a free aisle point (after following you around).
        internal void ReturnHome()
        {
            var linked = Enumerable.Range(0, _nodes.Count).Where(i => _links[i].Count > 0).ToList();
            _at = FreeStart(linked, _crowd);
            _to = -1;
            transform.position = _nodes[_at];
            _waitUntil = Time.time + Random.Range(1f, 3f);
            enabled = true;
        }

        internal Animator Anim => _anim;

        internal static int FreeStart(List<int> candidates, List<Wanderer> crowd)
        {
            var free = candidates.Where(n => !crowd.Any(w => w._at == n || w._to == n)).ToList();
            var pick = free.Count > 0 ? free : candidates;
            return pick[Random.Range(0, pick.Count)];
        }

        private void OnDestroy() => _crowd?.Remove(this);

        private void Update()
        {
            if (_nodes == null) return;
            if (_talker != null && _talker.Talking)
            {
                // Stop and look at whoever she's talking to.
                _anim.SetFloat("Speed", 0f);
                var player = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
                if (player != null) Face(player.transform.position - transform.position);
                return;
            }
            if (_to < 0)
            {
                _anim.SetFloat("Speed", 0f);
                if (Time.time < _waitUntil) return;
                // Only head for a point nobody else is on or heading to, and never along an aisle
                // someone is walking the other way.
                var options = _links[_at].Where(n => !_crowd.Any(w => w != this &&
                    (w._at == n || w._to == n || (w._at == n && w._to == _at)))).ToList();
                if (options.Count == 0) { _waitUntil = Time.time + 1f; return; }
                _to = options[Random.Range(0, options.Count)];
            }
            var target = _nodes[_to];
            var delta = target - transform.position;
            if (delta.magnitude < 0.05f)
            {
                transform.position = target;
                _at = _to; _to = -1;
                _waitUntil = Time.time + Random.Range(2f, 7f);
                Face(new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 0.4f)));
                return;
            }
            // Someone right ahead: the later-spawned one gives way, so two never wait on each other.
            foreach (var w in _crowd)
            {
                if (w == this) continue;
                var gap = w.transform.position - transform.position;
                if (gap.magnitude < Personal && Vector2.Dot(gap, delta) > 0f && (_rank > w._rank || w._to < 0))
                {
                    _anim.SetFloat("Speed", 0f);
                    return;
                }
            }
            transform.position = Vector3.MoveTowards(transform.position, target, WalkSpeed * Time.deltaTime);
            _anim.SetFloat("Speed", 0.5f); // the Move tree's walk
            Face(delta);
        }

        // Same direction rules as the player: DirFactor 1 up, -1 down, 0 sideways; left is the right pose turned round.
        internal void Face(Vector2 dir)
        {
            bool vertical = Mathf.Abs(dir.y) > Mathf.Abs(dir.x);
            _anim.SetFloat("DirFactor", vertical ? (dir.y > 0 ? 1f : -1f) : 0f);
            if (!vertical && _gfx != null) _gfx.localRotation = Quaternion.Euler(0f, dir.x < 0 ? 180f : 0f, 0f);
        }
    }


    // A club-goer who has come out with you: walks after you, runs to catch up, turns up after doors and
    // teleports, and complains about the date. Sent home, she goes back to wandering the club.
    internal class Follower : MonoBehaviour
    {
        internal bool Following { get; private set; }
        private Transform _home;
        private Wanderer _walker;
        private ClubTalker _talker;
        private float _nextMoan;
        private Vector2 _velocity;
        private bool _moving, _vertical;
        const float Spacing = 1.1f; // gap along your path between you and each girl in line
        static readonly List<Follower> _all = new List<Follower>(); // in the order they joined you

        private void Awake()
        {
            _home = transform.parent;
            _walker = GetComponent<Wanderer>();
            _talker = GetComponent<ClubTalker>();
        }

        private void OnDestroy() => _all.Remove(this);

        // Turns between up/down and sideways only when the new way is clearly stronger, so walking
        // diagonally doesn't flicker her round every frame.
        private void FaceSteady(Vector2 dir)
        {
            float ax = Mathf.Abs(dir.x), ay = Mathf.Abs(dir.y);
            if (_vertical && ax > ay * 1.4f) _vertical = false;
            else if (!_vertical && ay > ax * 1.4f) _vertical = true;
            _walker.Face(_vertical ? new Vector2(0f, dir.y) : new Vector2(dir.x, 0f));
        }

        // The path you've walked, newest first, so the girls follow in your footsteps in a line.
        static class Trail
        {
            static readonly List<Vector2> _points = new List<Vector2>();
            static int _frame = -1;

            internal static void Record(Vector2 pos)
            {
                if (_frame == Time.frameCount) return; // once a frame, however many girls follow
                _frame = Time.frameCount;
                if (_points.Count > 0 && (pos - _points[0]).sqrMagnitude > 12f * 12f) _points.Clear(); // a door or teleport
                if (_points.Count == 0 || (pos - _points[0]).sqrMagnitude > 0.1f * 0.1f) _points.Insert(0, pos);
                else _points[0] = pos;
                if (_points.Count > 200) _points.RemoveAt(_points.Count - 1);
            }

            internal static Vector2 PointBehind(float distance)
            {
                for (int i = 1; i < _points.Count; i++)
                {
                    float step = Vector2.Distance(_points[i - 1], _points[i]);
                    if (step >= distance) return Vector2.Lerp(_points[i - 1], _points[i], distance / step);
                    distance -= step;
                }
                // Not walked that far yet: line up below you.
                return (_points.Count > 0 ? _points[_points.Count - 1] : Vector2.zero) + Vector2.down * distance;
            }
        }

        internal void StartFollowing()
        {
            if (Following) return;
            Following = true;
            if (_walker == null) _walker = GetComponent<Wanderer>();
            if (_walker != null) _walker.enabled = false;
            if (!_all.Contains(this)) _all.Add(this);
            _velocity = Vector2.zero;
            // Out of the club's room, which the game switches off while you're elsewhere.
            transform.SetParent(null, true);
            _nextMoan = Time.time + Random.Range(12f, 25f);
        }

        internal void StopFollowing()
        {
            if (!Following) return;
            if (_walker == null) _walker = GetComponent<Wanderer>();
            Following = false;
            _all.Remove(this);
            if (_home == null) { Destroy(gameObject); return; }
            transform.SetParent(_home, true);
            _walker?.ReturnHome();
        }

        private void Update()
        {
            if (!Following) return;
            if (_home == null) { Destroy(gameObject); return; } // the club was removed
            var player = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
            if (_walker == null) _walker = GetComponent<Wanderer>();
            if (player == null || _walker == null) return;
            var anim = _walker.Anim;
            if (_talker != null && _talker.Talking)
            {
                anim.SetFloat("Speed", 0f);
                _walker.Face(player.transform.position - transform.position);
                return;
            }
            Trail.Record(player.transform.position);
            int place = _all.IndexOf(this);
            var target = Trail.PointBehind((place + 1) * Spacing);
            if (((Vector2)(player.transform.position - transform.position)).sqrMagnitude > 12f * 12f)
            {
                // You went through a door or teleported: she catches up, in her place in the line.
                transform.position = target;
                _velocity = Vector2.zero;
                return;
            }

            // Ease towards her place in the line (no stop-start), and step aside from anyone she's bumping.
            var to = target - (Vector2)transform.position;
            float dist = to.magnitude;
            if (!_moving && dist > 0.5f) _moving = true;
            else if (_moving && dist < 0.12f) _moving = false;
            var want = _moving ? to.normalized * Mathf.Min(dist * 3f, dist > 4f ? 6f : 3.2f) : Vector2.zero;
            foreach (var other in _all)
            {
                if (other == this) continue;
                var away = (Vector2)(transform.position - other.transform.position);
                if (away.sqrMagnitude < 0.6f * 0.6f && away.sqrMagnitude > 0.0001f) want += away.normalized * 1.5f;
            }
            _velocity = Vector2.MoveTowards(_velocity, want, 14f * Time.deltaTime);
            transform.position += (Vector3)(_velocity * Time.deltaTime);

            float speed = _velocity.magnitude;
            anim.SetFloat("Speed", speed < 0.2f ? 0f : speed > 4.5f ? 1f : 0.5f);
            if (speed > 0.3f) FaceSteady(_velocity);
            else if (speed < 0.05f) FaceSteady(player.transform.position - transform.position);

            if (Time.time > _nextMoan && _talker != null)
            {
                _nextMoan = Time.time + Random.Range(18f, 40f);
                _talker.Say(ClubLines.Fill(ClubLines.Pick("girl:" + _talker.SpeakerName, "follow"), _talker.SpeakerName, ClubGoers.Names)
                    .Replace("{PlayerName}", string.IsNullOrEmpty(GameData.playerData?.playerName) ? "Wanderer" : GameData.playerData.playerName), 5.5f);
            }
        }
    }


    // A dance made from the player's own animations: turns, spins, a bounce and a sway on the beat, with
    // an arms-up pose (the spell and shout animations) every few bars.
    internal class DanceRoutine : MonoBehaviour
    {
        const float Beat = 0.47f; // about 128 beats a minute
        static readonly string[] Poses = { "Cast", "DefianceShout" };
        private Animator _anim;
        private Transform _gfx;
        private float _next, _phase;
        private int _beat;
        private Vector3 _rest;

        private void Start()
        {
            _anim = GetComponent<Animator>();
            _gfx = transform.Find("Graphics");
            if (_gfx != null) _rest = _gfx.localPosition;
            _anim.SetFloat("Speed", 0f);
            _phase = Random.value * 10f;
            _next = Time.time + Random.Range(0f, Beat);
            Face(-1f, false);
        }

        private void Update()
        {
            if (_gfx == null) return;
            float t = Time.time + _phase;
            // Bounce on every beat, sway across two.
            _gfx.localPosition = _rest + new Vector3(Mathf.Sin(t * Mathf.PI / Beat / 2f) * 0.05f,
                Mathf.Abs(Mathf.Sin(t * Mathf.PI / Beat)) * 0.06f, 0f);
            if (Time.time < _next) return;
            _next += Beat;
            _beat++;
            int bar = (_beat / 8) % 4, step = _beat % 8;
            switch (bar)
            {
                case 0: // facing the crowd, a shimmy left and right
                    Face(step % 4 < 2 ? 0f : -1f, step % 4 == 1);
                    break;
                case 1: // a full turn on every second beat
                    if (step % 2 == 0) SpinStep(step / 2);
                    break;
                case 2: // back to the crowd, look over each shoulder
                    Face(step < 4 ? 1f : 0f, step >= 6);
                    break;
                case 3: // big finish: quick spin, then strike a pose
                    if (step < 4) SpinStep(step);
                    else if (step == 4) { Face(-1f, false); _anim.SetTrigger(Poses[Random.Range(0, Poses.Length)]); }
                    break;
            }
        }

        // Down, right, up, left: one quarter of a turn.
        void SpinStep(int quarter)
        {
            switch (quarter % 4)
            {
                case 0: Face(-1f, false); break;
                case 1: Face(0f, false); break;
                case 2: Face(1f, false); break;
                default: Face(0f, true); break;
            }
        }

        void Face(float dir, bool left)
        {
            _anim.SetFloat("DirFactor", dir);
            if (_gfx != null) _gfx.localRotation = Quaternion.Euler(0f, left ? 180f : 0f, 0f);
        }
    }

    // The player's animations call these (footsteps, attack and gather hits); club-goers ignore them.
    internal class AnimationEventSink : MonoBehaviour
    {
        public void DoFootstep() { }
        public void PerformAttackHit() { }
        public void GatherActionHit() { }
    }
}
