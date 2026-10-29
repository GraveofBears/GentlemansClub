using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GentlemansClub
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class GentlemansClubPlugin : BaseUnityPlugin
    {
        internal const string ModName = "EmbervilleAfterDark";
        internal const string ModVersion = "1.0.2";
        internal const string ModGUID = "OdinPlus.EmbervilleAfterDark";
        const string OldGUID = "OdinPlus.GentlemansClub"; // the mod's name before 1.0.0

        const string BundleFile = "gentlemansclub";
        const string ExteriorAsset = "GentlemansClub_Exterior";
        const string InteriorAsset = "GentlemansClub_Interior";
        // Building root sits up and to the right of you, so its door ends up just in front of you.
        static readonly Vector3 SpawnOffset = new Vector3(1.39f, 0.6f, 0f);
        // The interior is a separate room far from the playable map, like the game's extra dungeon rooms.
        static readonly Vector3 InteriorOffset = new Vector3(0f, 500f, 0f);

        internal static ManualLogSource Log;

        private static ConfigEntry<string> SpawnKey, RemoveKey, BarStock, MusicFile, GoldKey;
        private static ConfigEntry<int> GoldAmount;
        private static ConfigEntry<bool> DebugMode;
        private Hotkey _goldKey;
        private static ConfigEntry<float> MusicVolume, RestockHours;
        private static ConfigEntry<int> ClubGoerCount;
        private readonly Harmony _harmony = new Harmony(ModGUID);
        // Dialogue passage after which the barkeep opens the drinks store.
        const string MenuPassage = "Menu";
        internal const string SaveIdPrefix = "GentlemansClub";
        private Hotkey _spawnKey, _removeKey;

        private AssetBundle _bundle;
        private GameObject _exteriorPrefab, _interiorPrefab;
        private HashSet<Material> _bundleMaterials;
        private GameObject _exterior, _interior;
        private GameArea _interiorArea;
        private Teleporter _outsideDoor, _insideDoor;
        internal static GentlemansClubPlugin Instance;
        // Where the club stands in each scene, saved next to the config so it survives restarts.
        private readonly Dictionary<string, Vector3> _placed = new Dictionary<string, Vector3>();
        // Scenes the club was taken out of with the remove key; only honoured in debug mode, so in normal
        // play the club is always back at its home spot.
        private readonly HashSet<string> _removed = new HashSet<string>();
        static readonly Dictionary<string, Vector3> DefaultPlacements = new Dictionary<string, Vector3>
        {
            ["TutorialArea"] = new Vector3(-17.7021f, 82.81997f, 0f),
        };
        private string PlacementsFile => Path.Combine(Paths.ConfigPath, ModGUID + ".placements.txt");

        private void Awake()
        {
            Log = Logger;
            CarryOverOldSettings();
            DebugMode = Config.Bind("Debug", "DebugMode", false,
                "Turns on the keys below: placing, moving and removing the club, and the coin cheat. Off, the keys do " +
                "nothing and the club is always there: in the starting area, or wherever you moved it in debug mode.");
            SpawnKey = Config.Bind("Debug", "SpawnKey", "Ctrl+T",
                "Places the club in front of you (press again to move it). A UnityEngine.InputSystem.Key name, " +
                "optionally with Ctrl+, Shift+ or Alt+ in front, or None to disable.");
            RemoveKey = Config.Bind("Debug", "RemoveKey", "Ctrl+Shift+T", "Removes the club from the current scene.");
            BarStock = Config.Bind("Bar", "Stock",
                "item_csmb_smlHlngPtn:4-8, item_csmb_bgHlngPtn:1-3, item_csmb_smlKymPtn:4-8, item_csmb_bgKymPtn:1-3, item_food_rstdMeat:4-8, item_food_rstdTuber:4-8",
                "What the barkeep sells: the game's item keys (addressKey) with a min-max amount, comma separated. " +
                "Defaults: small/big healing tonic, small/big Kymia tonic, roasted meat, roasted tuber.");
            RestockHours = Config.Bind("Bar", "RestockHours", 24f,
                "In-game hours between restocks of the bar (24 = once a game day). What you buy stays sold out until then. " +
                "0 restocks every time the club is placed (each load of the area).");
            MusicVolume = Config.Bind("Music", "Volume", 0.6f, new ConfigDescription(
                "Volume of the club's music (plays only while you are inside the club). The game's Main and Music volume sliders apply on top of this.", new AcceptableValueRange<float>(0f, 1f)));
            MusicFile = Config.Bind("Music", "File", "*",
                "What plays inside the club: * for every MP3 in this mod's audio folder as a playlist (drop more songs " +
                "in there), or one file name from that folder to loop just that song. Empty to turn the music off.");
            if (MusicFile.Value == "club_music.mp3") MusicFile.Value = "*"; // the old one-song default becomes the playlist
            var shuffle = Config.Bind("Music", "Shuffle", true, "Playlist order: shuffled (true) or by file name (false).");
            if (!string.IsNullOrWhiteSpace(MusicFile.Value))
            {
                var music = gameObject.AddComponent<ClubMusic>();
                music.Folder = Path.Combine(Path.GetDirectoryName(Info.Location), "audio");
                music.Song = MusicFile.Value.Trim() == "*" ? null : MusicFile.Value.Trim();
                music.Shuffle = shuffle.Value;
                music.Volume = MusicVolume.Value;
                music.IsInside = () => _interiorArea != null && Singleton<GameManager>.HasMain && Singleton<GameManager>.main.CurrentArea == _interiorArea;
                MusicVolume.SettingChanged += (_, __) => music.Volume = MusicVolume.Value;
            }
            ClubTalker.TipSmall = Config.Bind("Club", "TipSmall", 10, "Coins for a small tip.").Value;
            ClubTalker.TipBig = Config.Bind("Club", "TipBig", 50, "Coins for a big tip.").Value;
            ClubTalker.RainPrice = Config.Bind("Club", "MakeItRain", 50, "Coins thrown when you make it rain on a dancer.").Value;
            ClubTalker.PourPrice = Config.Bind("Club", "MakeItPour", 200, "Coins thrown when you make it pour.").Value;
            ClubTalker.DustPrice = Config.Bind("Glimmerdust", "Price", 30, "Coins Trixie asks for a pinch (she also takes a small blue Kymia tonic).").Value;
            GlimmerHigh.HighSeconds = Config.Bind("Glimmerdust", "HighSeconds", 60f, "How long the high lasts.").Value;
            GlimmerHigh.ComedownSeconds = Config.Bind("Glimmerdust", "ComedownSeconds", 60f, "How long the comedown lasts.").Value;
            GlimmerHigh.HighSpeed = Config.Bind("Glimmerdust", "HighMoveSpeed", 35f, "Move speed while high, in percent (35 = 35% faster).").Value;
            GlimmerHigh.HighAttack = Config.Bind("Glimmerdust", "HighAttackSpeed", 30f, "Attack speed while high, in percent.").Value;
            GlimmerHigh.LowSpeed = Config.Bind("Glimmerdust", "ComedownMoveSpeed", -25f, "Move speed during the comedown, in percent (negative is slower).").Value;
            GlimmerHigh.LowAttack = Config.Bind("Glimmerdust", "ComedownAttackSpeed", -20f, "Attack speed during the comedown, in percent.").Value;
            Instance = this;
            GlimmerHigh.Instance = gameObject.AddComponent<GlimmerHigh>();
            Drunk.SecondsPerLevel = Config.Bind("Drinks", "SecondsPerDrink", 75f, "How long each Neon Ember keeps you drunk.").Value;
            Drunk.Instance = gameObject.AddComponent<Drunk>();
            Glimmerdust.Define();
            NeonEmber.Define();
            StartCoroutine(ClubItems.Register());
            Bouncer.Enabled = Config.Bind("Club", "Bouncer", true, "A bouncer in the VIP room's doorway, who wants paying (or a Glimmerdust bribe) before he steps aside. Off: the VIP room is open.").Value;
            Bouncer.Price = Config.Bind("Club", "VipPass", 100, "Coins the bouncer asks for VIP entry (for the visit).").Value;
            ClubTalker.PrivateDancePrice = Config.Bind("Club", "PrivateDance", 100, "Coins for a private dance with Ruby in the VIP room.").Value;
            ClubGoerCount = Config.Bind("Club", "ClubGoers", 3, new ConfigDescription(
                "How many club-goers wander the club (picked from the girls in the lines file; each always has her own look).", new AcceptableValueRange<int>(0, 10)));
            GoldKey = Config.Bind("Debug", "GoldKey", "Ctrl+Shift+G", "A cheat key that gives you coins, for trying the club out.");
            GoldAmount = Config.Bind("Debug", "GoldAmount", 500, "Coins the GoldKey gives.");
            _goldKey = Hotkey.Parse(GoldKey.Value, "GoldKey");
            _spawnKey = Hotkey.Parse(SpawnKey.Value, "SpawnKey");
            _removeKey = Hotkey.Parse(RemoveKey.Value, "RemoveKey");
            LoadPlacements();
            ClubLines.FilePath = Path.Combine(Path.GetDirectoryName(Info.Location), "text", "club_lines.txt");
            _harmony.PatchAll();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Log.LogInfo(ModName + " " + ModVersion + " loaded." + (DebugMode.Value ? " Debug mode: spawn " + SpawnKey.Value + ", remove " + RemoveKey.Value + ", coins " + GoldKey.Value + "." : ""));
        }

        private void Update()
        {
            if (_goers != null) _goers.TryBuild(); // the girls are made while you're still outside, so they're there when you walk in
            if (Time.frameCount % 60 == 0) RestockBar();
            var kb = Keyboard.current;
            if (kb == null || !DebugMode.Value) return;
            if (_goldKey.Pressed(kb) && Singleton<Inventory>.HasMain)
            {
                Singleton<Inventory>.main.AddGold(GoldAmount.Value);
                PlayerTextReaction.ShowText("+" + GoldAmount.Value + " coins. Don't spend it all at the club. Spend it all at the club.", 3f);
                return;
            }
            bool spawn = _spawnKey.Pressed(kb), remove = _removeKey.Pressed(kb);
            if (!spawn && !remove) return;
            if (!Singleton<GameManager>.HasMain || !Singleton<GameManager>.main.gameStarted || Singleton<GameManager>.main.player == null) return;
            if (_interiorArea != null && Singleton<GameManager>.main.CurrentArea == _interiorArea)
            {
                Log.LogInfo("You are inside the club; step outside to move or remove it.");
                return;
            }
            var scene = SceneManager.GetActiveScene().name;
            if (remove)
            {
                Despawn();
                _placed.Remove(scene);
                _removed.Add(scene);
                SavePlacements();
                Log.LogInfo("Club removed from " + scene + ".");
                return;
            }
            var pos = Singleton<GameManager>.main.player.transform.position + SpawnOffset;
            pos.z = 0f;
            _placed[scene] = pos;
            _removed.Remove(scene);
            SavePlacements();
            Spawn(pos);
            LinkOutsideDoor();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Spawned before the GameManager looks for its areas, so a save made inside the club
            // (which remembers the area by name) puts you back inside it.
            if (mode == LoadSceneMode.Single && TryGetPlacement(scene.name, out var pos))
            {
                Spawn(pos);
                StartCoroutine(LinkWhenStarted());
            }
        }

        private System.Collections.IEnumerator LinkWhenStarted()
        {
            while (_outsideDoor != null && !(Singleton<GameManager>.HasMain && Singleton<GameManager>.main.gameStarted))
                yield return null;
            LinkOutsideDoor();
        }

        private void Spawn(Vector3 pos)
        {
            if (!LoadBundle()) return;
            Despawn();
            _exterior = Instantiate(_exteriorPrefab, pos, Quaternion.identity);
            _exterior.name = ExteriorAsset;
            _interior = Instantiate(_interiorPrefab, pos + InteriorOffset, Quaternion.identity);
            _interior.name = InteriorAsset; // the save remembers the area by this name
            _interiorArea = _interior.GetComponent<GameArea>();
            _interiorArea.Setup();

            foreach (var go in new[] { _exterior, _interior })
            {
                UseGameMaterials(go);
                foreach (var light in go.GetComponentsInChildren<Light2D>(true))
                {
                    if (light.name.StartsWith("Flicker")) light.gameObject.AddComponent<LightFlicker>();
                    if (light.name.StartsWith("Sweep")) light.gameObject.AddComponent<LightSweep>();
                }
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("Sfx_")) t.gameObject.AddComponent<WwiseEmitter>().EventName = t.name.Substring(4);
            }
            // Start each dancer at a different point of her routine.
            foreach (var anim in _interior.transform.Find("== Stage").GetComponentsInChildren<Animator>(true))
                anim.Play(0, 0, UnityEngine.Random.value);

            ClubLines.Reload(); // pick up edits to the lines file without restarting
            if (ClubEffects.CoinSprite == null) StartCoroutine(LoadCoinSprite());
            if (ClubTalker.KymiaTonic == null) StartCoroutine(LoadTonic());
            _vipPass = !Bouncer.Enabled;
            SetupBar(_interior);
            SetupDancers(_interior);
            ClubGoers.HideOldDancers(_interior.transform); // the player-model girls take their place
            _goers = _interior.AddComponent<ClubGoers>(); // also dresses the stage dancers
            _goers.Count = ClubGoerCount.Value;

            _outsideDoor = _exterior.transform.Find("Door").GetComponent<Teleporter>();
            // Coming out, the game drops you on the door's MovePoint before you step out. The bundle has it up
            // in the doorway against the wall behind, which left you stuck in the wall: use the threshold.
            _outsideDoor.transform.Find("MovePoint").localPosition = new Vector3(0f, -0.35f, 0f);
            var insideDoor = _insideDoor = _interior.transform.Find("Exit").GetComponent<Teleporter>();
            _outsideDoor.destination = insideDoor;
            insideDoor.destination = _outsideDoor;
            _bouncer = null;
            if (Bouncer.Enabled) StartCoroutine(Bouncer.Create(_interior, b =>
            {
                _bouncer = b;
                if (b == null) _vipPass = true; // nobody to stop you
                else b.OnLetIn = () => _vipPass = true;
            }));
            Log.LogInfo("Club placed at " + pos + " in " + SceneManager.GetActiveScene().name + ".");
        }

        // The barkeep talks; choosing "What are you pouring?" opens the drinks store once the dialogue closes.
        private void SetupBar(GameObject interior)
        {
            var store = interior.transform.Find("== Bar/DrinksMenu").GetComponent<StoreComponent>();
            var keeper = interior.transform.Find("== Bar/Barkeep").GetComponent<DialogueTrigger>();
            StartCoroutine(StockStore(store));

            // A fresh conversation every time; ending on "Menu" (LastTrigger holds the last passage shown)
            // means you asked for drinks.
            TextAsset BarkeepTalk() => ClubDialogue.Barkeep(ClubGoers.Names, Bouncer.Price, _vipPass,
                Singleton<Inventory>.HasMain ? Singleton<Inventory>.main.TotalGold.CurrentValue : 0);
            RefreshBarkeep = () => keeper.dialogueFile = BarkeepTalk();
            keeper.dialogueFile = BarkeepTalk();
            keeper.onEndDialogueEvent.AddListener(() =>
            {
                if (Singleton<DialogueManager>.main.LastTrigger == MenuPassage) StartCoroutine(OpenNextFrame(store));
                keeper.dialogueFile = BarkeepTalk();
            });
            ClubTalker.BubbleTemplate = interior.transform.Find("== Bar/Barkeep/Bubble")?.GetComponent<NPCTalkBubble>();
        }

        private static bool _vipPassStatic;
        private bool _vipPass { get => _vipPassStatic; set => _vipPassStatic = value; }
        private ClubGoers _goers;
        private Bouncer _bouncer;
        internal static System.Action RefreshBarkeep;

        // The three stage dancers can be talked to from the front of their platforms.
        private static void SetupDancers(GameObject interior)
        {
            var names = new Dictionary<string, string> { { "Dancer_RedheadPole", "Ruby" }, { "Dancer_Purple", "Violet" }, { "Dancer_Burnett", "Mocha" } };
            var stage = interior.transform.Find("== Stage");
            foreach (var kv in names)
            {
                var dancer = stage.Find(kv.Key);
                if (dancer == null) continue;
                var talker = dancer.gameObject.AddComponent<ClubTalker>();
                talker.IsDancer = true;
                if (kv.Value == "Ruby") talker.CanPrivateDance = () => _vipPassStatic; // she's in the orange VIP room
                talker.Everyone = () => ClubGoers.Names;
                talker.Setup(kv.Value, dancer.Find("Art").gameObject, triggerOffset: new Vector2(0f, -0.8f),
                    triggerSize: new Vector2(1.8f, 1.0f), headHeight: 1.45f);
            }
        }

        private static System.Collections.IEnumerator OpenNextFrame(StoreComponent store)
        {
            yield return null; // let the dialogue UI close first
            if (store != null && store.gameObject.activeInHierarchy) store.OpenStore();
        }

        // Stocks the store with the game's own items (loaded the way the game loads them, by address key),
        // so what you buy stacks with and saves like any other item.
        private System.Collections.IEnumerator StockStore(StoreComponent store)
        {
            var items = new List<LootItem>();
            foreach (var entry in BarStock.Value.Split(',').Select(e => e.Trim()).Where(e => e.Length > 0))
            {
                var parts = entry.Split(':');
                var key = parts[0].Trim();
                var range = new Vector2Int(1, 1);
                if (parts.Length > 1)
                {
                    var mm = parts[1].Split('-');
                    if (int.TryParse(mm[0], out var min)) range = new Vector2Int(min, min);
                    if (mm.Length > 1 && int.TryParse(mm[1], out var max)) range.y = Math.Max(range.x, max);
                }
                var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<ItemData>(key);
                yield return handle;
                if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && handle.Result != null)
                    items.Add(new LootItem { item = handle.Result, chance = 100, amountRange = range });
                else
                    Log.LogWarning("Bar stock: no item with key '" + key + "'.");
            }
            if (store == null) yield break;
            items.Add(new LootItem { item = NeonEmber.Data, chance = 100, amountRange = new Vector2Int(6, 12) }); // the house cocktail
            store.storeItems = items;
            // One store for the bar, so what you buy stays bought when the club is placed again. The game
            // keeps a store's stock by its ID; dropping it makes the store fill its shelves afresh.
            store.storeID = BarStoreId;
            if (RestockDue()) { Singleton<StoreManager>.main.allStores.Remove(BarStoreId); _lastRestock = GameHours(); }
            _barStore = store;
            store.gameObject.SetActive(true);
            Log.LogInfo("Bar stocked with " + items.Count + " items.");
        }

        const string BarStoreId = "OdinPlus_ClubBar";
        private static double _lastRestock = double.NaN;
        private StoreComponent _barStore;

        // In-game hours since the start of the world, or real minutes if there's no clock.
        private static double GameHours() =>
            Singleton<TimeManager>.HasMain ? Singleton<TimeManager>.main.CurrentDay * 24.0 + Singleton<TimeManager>.main.CurrentTime
                                           : Time.realtimeSinceStartup / 60.0;

        private static bool RestockDue()
        {
            if (RestockHours.Value <= 0f || double.IsNaN(_lastRestock)) return true;
            double now = GameHours();
            return now < _lastRestock || now - _lastRestock >= RestockHours.Value; // an older save: time went back
        }

        // The time comes round while the club is up: the barkeep fills the shelves (not while you're shopping).
        private void RestockBar()
        {
            if (_barStore == null || RestockHours.Value <= 0f || _barStore.isOpened || !_barStore.gameObject.activeInHierarchy) return;
            if (!Singleton<StoreManager>.HasMain || Singleton<StoreManager>.main.isLoading || !RestockDue()) return;
            Singleton<StoreManager>.main.allStores.Remove(BarStoreId);
            _barStore.CreateStoreItems();
            _lastRestock = GameHours();
            Log.LogInfo("The barkeep restocked the bar.");
        }

        // The game's own coin, for tips and making it rain.
        private static System.Collections.IEnumerator LoadCoinSprite()
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<ItemData>("item_gld");
            yield return handle;
            if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && handle.Result != null)
                ClubEffects.CoinSprite = handle.Result.itemIconWorld;
        }

        private static System.Collections.IEnumerator LoadTonic()
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<ItemData>("item_csmb_smlKymPtn");
            yield return handle;
            if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) ClubTalker.KymiaTonic = handle.Result;
        }

        private void Despawn()
        {
            if (_exterior != null) Destroy(_exterior);
            if (_interior != null) Destroy(_interior);
            _exterior = _interior = null;
            _interiorArea = null;
            _outsideDoor = _insideDoor = null;
        }

        // Coming back out, the game switches the camera to the outside door's area, so it needs the
        // area the club stands in. Without one, the door stays shut rather than trap the camera.
        private void LinkOutsideDoor()
        {
            if (_outsideDoor == null) return;
            var pos = _outsideDoor.transform.position;
            var areas = FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(a => a != _interiorArea && a.CameraConfiner != null).ToList();
            var current = Singleton<GameManager>.main.CurrentArea;
            // Areas you aren't in are switched off, and a switched-off collider never overlaps anything,
            // so test the confiner's shape. Otherwise a new world (which starts you indoors) linked the
            // door to that building, and coming out of the club put you inside its walls.
            var area = areas.FirstOrDefault(a => ConfinerContains(a.CameraConfiner, pos))
                       ??(current != _interiorArea ? current : null)
                       ?? areas.FirstOrDefault();
            _outsideDoor.gameArea = area;
            // Shut without a camera area to come back to, and until the bouncer lets you in.
            _outsideDoor.isDisabled = area == null;
            if (area == null) Log.LogWarning("No camera area found around the club here; its door stays shut in this scene.");
        }

        // Walking in through the front door, the area you're in is the one the door brings you back to.
        internal void DoorUsed(Teleporter door)
        {
            if (!Singleton<GameManager>.HasMain || door == null) return;
            var gm = Singleton<GameManager>.main;
            if (door == _outsideDoor)
            {
                if (gm.CurrentArea != null && gm.CurrentArea != _interiorArea) _outsideDoor.gameArea = gm.CurrentArea;
                Log.LogInfo("Into the club from " + Describe(gm.CurrentArea) + ", you at " + gm.player.transform.position +
                            "; coming out returns you to " + Describe(_outsideDoor.gameArea) + ".");
            }
            else if (door == _insideDoor)
                Log.LogInfo("Out of the club to " + Describe(_outsideDoor.gameArea) + " at " + _outsideDoor.DestPosition + ".");
        }

        private static string Describe(GameArea area)
        {
            if (area == null) return "no area";
            var c = area.CameraConfiner;
            if (c == null) return area.name + " (no camera bounds)";
            Bounds b;
            if (c.enabled && c.gameObject.activeInHierarchy) b = c.bounds;
            else if (c is BoxCollider2D box) b = new Bounds(c.transform.TransformPoint(box.offset), Vector3.Scale(box.size, c.transform.lossyScale));
            else return area.name + " (camera bounds switched off)";
            return area.name + " (camera x " + b.min.x.ToString("0.0") + ".." + b.max.x.ToString("0.0") +
                   ", y " + b.min.y.ToString("0.0") + ".." + b.max.y.ToString("0.0") + ")";
        }

        // Whether a point lies inside a collider's shape, whether or not the collider is switched on.
        private static bool ConfinerContains(Collider2D c, Vector2 point)
        {
            if (c.enabled && c.gameObject.activeInHierarchy) return c.OverlapPoint(point);
            var local = (Vector2)c.transform.InverseTransformPoint(point) - c.offset;
            if (c is BoxCollider2D box)
                return Mathf.Abs(local.x) <= box.size.x / 2f && Mathf.Abs(local.y) <= box.size.y / 2f;
            if (c is PolygonCollider2D poly)
            {
                for (int p = 0; p < poly.pathCount; p++)
                    if (InPolygon(poly.GetPath(p), local)) return true;
                return false;
            }
            return false;
        }

        private static bool InPolygon(Vector2[] pts, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
                if ((pts[i].y > p.y) != (pts[j].y > p.y) &&
                    p.x < (pts[j].x - pts[i].x) * (p.y - pts[i].y) / (pts[j].y - pts[i].y) + pts[i].x)
                    inside = !inside;
            return inside;
        }

        private bool LoadBundle()
        {
            if (_exteriorPrefab != null && _interiorPrefab != null) return true;
            var path = Path.Combine(Path.GetDirectoryName(Info.Location), BundleFile);
            _bundle = _bundle ?? AssetBundle.LoadFromFile(path);
            if (_bundle == null) { Log.LogError("Could not load " + path); return false; }
            _exteriorPrefab = _bundle.LoadAsset<GameObject>(ExteriorAsset);
            _interiorPrefab = _bundle.LoadAsset<GameObject>(InteriorAsset);
            if (_exteriorPrefab == null || _interiorPrefab == null) { Log.LogError("Club prefabs missing from " + path); return false; }
            _bundleMaterials = new HashSet<Material>(new[] { _exteriorPrefab, _interiorPrefab }
                .SelectMany(p => p.GetComponentsInChildren<Renderer>(true)).SelectMany(r => r.sharedMaterials).Where(m => m != null));
            return true;
        }

        // The bundle carries its own copies of the game's materials (and stand-in copies of its custom
        // shaders); use the game's own wherever it has them loaded, so everything matches its URP build.
        private void UseGameMaterials(GameObject go)
        {
            var game = Resources.FindObjectsOfTypeAll<Material>()
                .Where(m => !_bundleMaterials.Contains(m) && m.shader != null)
                .GroupBy(m => m.name).ToDictionary(g => g.Key, g => g.First());
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    if (game.TryGetValue(mats[i].name, out var m)) mats[i] = m;
                    else if (_bundleMaterials.Contains(mats[i]))
                    {
                        var real = Shader.Find(mats[i].shader.name);
                        if (real != null && real != mats[i].shader) mats[i].shader = real;
                    }
                }
                r.sharedMaterials = mats;
            }
        }

        // The mod used to be called GentlemansClub: take over its config and the club's saved spots once.
        private void CarryOverOldSettings()
        {
            try
            {
                var oldCfg = Path.Combine(Paths.ConfigPath, OldGUID + ".cfg");
                if (!File.Exists(Config.ConfigFilePath) && File.Exists(oldCfg))
                {
                    File.Copy(oldCfg, Config.ConfigFilePath);
                    Config.Reload();
                    Log.LogInfo("Settings carried over from " + oldCfg + ".");
                }
                var oldPlaced = Path.Combine(Paths.ConfigPath, OldGUID + ".placements.txt");
                if (!File.Exists(PlacementsFile) && File.Exists(oldPlaced)) File.Copy(oldPlaced, PlacementsFile);
            }
            catch (Exception e) { Log.LogWarning("Could not carry over the old GentlemansClub settings: " + e.Message); }
        }

        // Where the club stands in a scene: where you put it, else its home spot (starting area).
        private bool TryGetPlacement(string scene, out Vector3 pos)
        {
            pos = default;
            if (DebugMode.Value && _removed.Contains(scene)) return false;
            return _placed.TryGetValue(scene, out pos) || DefaultPlacements.TryGetValue(scene, out pos);
        }

        // One line per scene: "Scene|x|y" for a club you moved, "Scene|removed" for one you took away.
        private void LoadPlacements()
        {
            try
            {
                if (!File.Exists(PlacementsFile)) return;
                foreach (var line in File.ReadAllLines(PlacementsFile))
                {
                    var p = line.Split('|');
                    if (p.Length == 2 && p[1] == "removed") _removed.Add(p[0]);
                    else if (p.Length == 3 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                        && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                        _placed[p[0]] = new Vector3(x, y, 0f);
                }
            }
            catch (Exception e) { Log.LogWarning("Could not read " + PlacementsFile + ": " + e.Message); }
        }

        private void SavePlacements()
        {
            try
            {
                File.WriteAllLines(PlacementsFile, _placed.Select(kv =>
                    kv.Key + "|" + kv.Value.x.ToString(CultureInfo.InvariantCulture) + "|" + kv.Value.y.ToString(CultureInfo.InvariantCulture))
                    .Concat(_removed.Select(r => r + "|removed")));
            }
            catch (Exception e) { Log.LogWarning("Could not write " + PlacementsFile + ": " + e.Message); }
        }
    }

    // A key with optional Ctrl/Shift/Alt, e.g. "Ctrl+T".
    internal struct Hotkey
    {
        private Key _key;
        private bool _ctrl, _shift, _alt;

        internal static Hotkey Parse(string text, string what)
        {
            var h = new Hotkey();
            var parts = (text ?? "").Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            h._ctrl = parts.RemoveAll(p => p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) > 0;
            h._shift = parts.RemoveAll(p => p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) > 0;
            h._alt = parts.RemoveAll(p => p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) > 0;
            if (parts.Count != 1 || !Enum.TryParse(parts[0], true, out h._key) || !Enum.IsDefined(typeof(Key), h._key))
            {
                if (!(parts.Count == 1 && parts[0].Equals("None", StringComparison.OrdinalIgnoreCase)))
                    GentlemansClubPlugin.Log.LogWarning("Unknown " + what + " '" + text + "', that hotkey is off.");
                h._key = Key.None;
            }
            return h;
        }

        internal bool Pressed(Keyboard kb) =>
            _key != Key.None && kb[_key].wasPressedThisFrame &&
            _ctrl == kb.ctrlKey.isPressed && _shift == kb.shiftKey.isPressed && _alt == kb.altKey.isPressed;
    }

    // Club conversations are rebuilt the moment you open them, so they offer what you can afford now.
    [HarmonyPatch(typeof(DialogueTrigger), nameof(DialogueTrigger.TriggerDialogue), new Type[0])]
    internal static class RefreshClubDialogue
    {
        private static void Prefix(DialogueTrigger __instance)
        {
            if (__instance.TryGetComponent<ClubTalker>(out var talker)) talker.Refresh();
            else if (__instance.uniqueID == GentlemansClubPlugin.SaveIdPrefix + "_Barkeep") GentlemansClubPlugin.RefreshBarkeep?.Invoke();
        }
    }

    // Tells the club which door you went through (before the fade), so it can bring you back where you came from.
    [HarmonyPatch(typeof(Teleporter), nameof(Teleporter.Teleport))]
    internal static class ClubDoorUsed
    {
        private static void Prefix(Teleporter __instance)
        {
            if (!__instance.isTeleporting && GentlemansClubPlugin.Instance != null) GentlemansClubPlugin.Instance.DoorUsed(__instance);
        }
    }

    // The club's NPCs keep nothing between visits, so their dialogue never writes into the world save.
    [HarmonyPatch(typeof(DialogueTrigger), nameof(DialogueTrigger.OnSave))]
    internal static class SkipClubDialogueSave
    {
        private static bool Prefix(DialogueTrigger __instance) =>
            __instance.uniqueID == null || !__instance.uniqueID.StartsWith(GentlemansClubPlugin.SaveIdPrefix);
    }

    // Gentle random flicker around the light's authored intensity, like neon and candle bulbs.
    internal class LightFlicker : MonoBehaviour
    {
        private Light2D _light;
        private float _base, _seed;

        private void Awake()
        {
            _light = GetComponent<Light2D>();
            _base = _light.intensity;
            _seed = UnityEngine.Random.value * 100f;
        }

        private void Update()
        {
            float n = Mathf.PerlinNoise(_seed, Time.time * 3f);
            // Neon now and then drops out for a moment.
            float dip = Mathf.PerlinNoise(_seed + 50f, Time.time * 0.7f) > 0.82f && Mathf.PerlinNoise(_seed, Time.time * 25f) > 0.5f ? 0.35f : 1f;
            _light.intensity = _base * (0.85f + 0.3f * n) * dip;
        }
    }

    // Club spotlight: swings its beam slowly back and forth around the way it was aimed.
    internal class LightSweep : MonoBehaviour
    {
        private float _baseAngle, _seed;

        private void Awake()
        {
            _baseAngle = transform.localEulerAngles.z;
            _seed = UnityEngine.Random.value * 10f;
        }

        private void Update()
        {
            float swing = Mathf.Sin(Time.time * 0.6f + _seed) * 12f + Mathf.Sin(Time.time * 1.7f + _seed * 2f) * 3f;
            transform.localRotation = Quaternion.Euler(0f, 0f, _baseAngle + swing);
        }
    }

    // Plays one of the game's Wwise events on this object while it exists ("Play_X", stopped with "Stop_X").
    internal class WwiseEmitter : MonoBehaviour
    {
        internal string EventName;

        private void Start()
        {
            if (AkUnitySoundEngine.IsInitialized()) AkUnitySoundEngine.PostEvent(EventName, gameObject);
        }

        private void OnDestroy()
        {
            if (EventName != null && EventName.StartsWith("Play_") && AkUnitySoundEngine.IsInitialized())
                AkUnitySoundEngine.PostEvent("Stop_" + EventName.Substring(5), gameObject);
        }
    }
}
