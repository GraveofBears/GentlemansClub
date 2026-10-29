using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization;
using Random = UnityEngine.Random;

namespace GentlemansClub
{
    // Everything the club's people say, read from text/club_lines.txt next to the mod.
    internal static class ClubLines
    {
        static Dictionary<string, Dictionary<string, List<string>>> _sections;
        internal static string FilePath;
        internal static void Reload() => _sections = null;

        static Dictionary<string, Dictionary<string, List<string>>> Sections
        {
            get
            {
                if (_sections != null) return _sections;
                _sections = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
                if (FilePath == null || !File.Exists(FilePath))
                {
                    GentlemansClubPlugin.Log.LogWarning("Club lines not found: " + FilePath);
                    return _sections;
                }
                Dictionary<string, List<string>> section = null;
                List<string> list = null;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        var name = line.Substring(1, line.Length - 2).Trim();
                        if (!_sections.TryGetValue(name, out section))
                            _sections[name] = section = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        list = null;
                    }
                    else if (line.EndsWith(":") && !line.StartsWith("-") && section != null)
                    {
                        var key = line.Substring(0, line.Length - 1).Trim();
                        if (!section.TryGetValue(key, out list)) section[key] = list = new List<string>();
                    }
                    else if (line.StartsWith("-") && list != null)
                        list.Add(Clean(line.Substring(1).Trim()));
                }
                return _sections;
            }
        }

        // Strip what the game's dialogue format would read as markup.
        static string Clean(string s) => s.Replace("::", ":").Replace("[[", "[").Replace("]]", "]").Replace("{", "(").Replace("}", ")").Replace("//", "/");

        internal static List<string> Girls() =>
            Sections.Keys.Where(k => k.StartsWith("girl:", StringComparison.OrdinalIgnoreCase)).Select(k => k.Substring(5)).ToList();

        // A random line for this speaker and topic; girls fall back to the shared [common] pool.
        internal static string Pick(string section, string key, string fallbackSection = "common")
        {
            var pool = new List<string>();
            if (Sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var own)) pool.AddRange(own);
            // Mix in the shared lines, but favour a girl's own; a girl with plenty of her own (the stage
            // dancers) keeps to her own voice.
            if (fallbackSection != null && pool.Count < 8 && Sections.TryGetValue(fallbackSection, out var c) && c.TryGetValue(key, out var shared))
                pool.AddRange(pool.Count > 0 ? shared.Where(_ => Random.value < 0.5f) : shared);
            return pool.Count == 0 ? "..." : pool[Random.Range(0, pool.Count)];
        }

        internal static List<string> PickSome(string section, string key, int count, string fallbackSection = "common")
        {
            var picked = new List<string>();
            for (int tries = 0; picked.Count < count && tries < count * 6; tries++)
            {
                var line = Pick(section, key, fallbackSection);
                if (!picked.Contains(line)) picked.Add(line);
            }
            return picked;
        }

        internal static string Fill(string line, string speaker, IList<string> others)
        {
            var other = others.Where(o => o != speaker).ToList();
            return line.Replace("$PLAYER", "{PlayerName}").Replace("$NAME", speaker)
                       .Replace("$GIRL", other.Count > 0 ? other[Random.Range(0, other.Count)] : "the new girl");
        }
    }

    // Builds a fresh conversation in the game's dialogue format (Twine/Twee) each time you talk.
    internal static class ClubDialogue
    {
        const string Head = ":: StoryTitle\nGentlemansClub\n\n:: StoryData\n{\n  \"format\": \"Harlowe\",\n  \"start\": \"DialogStart\"\n}\n\n";

        // What a girl's conversation can offer right now.
        internal struct GirlOptions
        {
            public int Gold;                 // the player's coins when the conversation opens
            public int TipSmall, TipBig;     // tip prices (0 = no tipping)
            public int Rain, Pour;           // make-it-rain prices (0 = not a dancer)
            public bool CanFollow, Following;
            public bool Dealer, HasTonic;    // Trixie sells Glimmerdust for coins or a small Kymia tonic
            public int DustPrice;
            public int PrivatePrice;         // > 0: she offers a private dance (VIP pass holders)
        }

        // Ending passages the mod acts on when the conversation closes (see ClubTalker.OnDialogueEnd).
        internal const string TipSmallEnd = "TipSmall", TipBigEnd = "TipBig", RainEnd = "Rain", PourEnd = "Pour",
                              FollowEnd = "Follow", HomeEnd = "Home", DustGoldEnd = "BuyDustGold", DustTonicEnd = "BuyDustTonic", PrivateEnd = "PrivateDance";

        internal static TextAsset Girl(string name, IList<string> everyone, GirlOptions o)
        {
            var sb = new StringBuilder(Head);
            string sec = "girl:" + name;
            string Say(string line) => $"<b>{name}:</b> {ClubLines.Fill(line, name, everyone)}";
            var menu = new List<string>
            {
                "[[How's the night going?|Work]]", "[[So what's your story?|Life]]",
                o.Dealer ? "[[Got anything... special?|DustOffer]]"
                    : o.PrivatePrice > 0 && o.Gold >= o.PrivatePrice ? $"[[How about a private dance? ({o.PrivatePrice} coins)|{PrivateEnd}]]"
                    : "[[Heard any good gossip?|Gossip]]",
            };
            // The game shows at most six choices.
            if (o.Rain > 0)
            {
                if (o.Gold >= o.Pour) menu.Add($"[[Make it pour! ({o.Pour} coins)|{PourEnd}]]");
                else if (o.Gold >= o.Rain) menu.Add($"[[Make it rain! ({o.Rain} coins)|{RainEnd}]]");
            }
            if (o.TipSmall > 0)
            {
                if (o.Gold >= o.TipBig) menu.Add($"[[Here, a big tip. ({o.TipBig} coins)|{TipBigEnd}]]");
                else if (o.Gold >= o.TipSmall) menu.Add($"[[Here's a tip. ({o.TipSmall} coins)|{TipSmallEnd}]]");
                else menu.Add("[[I'd tip, but I'm broke.|Broke]]");
            }
            if (o.Following) menu.Add($"[[You can head back to the club now.|{HomeEnd}]]");
            else if (o.CanFollow) menu.Add($"[[Want to get out of here?|{FollowEnd}]]");
            menu.Add("[[See you around.|Bye]]");

            var choices = menu.ToArray();
            Passage(sb, "DialogStart", Say(ClubLines.Pick(sec, o.Following ? "follow" : "greet")), choices);
            Chain(sb, "Work", ClubLines.PickSome(sec, "work", 2).Select(Say).ToList(), "[[Something else...|More]]");
            Chain(sb, "Life", ClubLines.PickSome(sec, "life", 2).Select(Say).ToList(), "[[Something else...|More]]");
            Chain(sb, "Gossip", ClubLines.PickSome(sec, "gossip", 1).Select(Say).ToList(), "[[Something else...|More]]");
            Passage(sb, "More", Say(Random.value < 0.5f ? "Anything else, sugar?" : "What else do you want to know?"), choices);
            Passage(sb, TipSmallEnd, Say(ClubLines.Pick(sec, "tip_small")));
            Passage(sb, TipBigEnd, Say(ClubLines.Pick(sec, "tip_big")));
            Passage(sb, "Broke", Say(ClubLines.Pick(sec, "broke")), "[[Something else...|More]]");
            Passage(sb, RainEnd, Say(ClubLines.Pick(sec, "rain")));
            Passage(sb, PourEnd, Say(ClubLines.Pick(sec, "pour")));
            Passage(sb, FollowEnd, Say(ClubLines.Pick(sec, "follow_start")));
            Passage(sb, HomeEnd, Say(ClubLines.Pick(sec, "follow_end")));
            Passage(sb, "Bye", Say(ClubLines.Pick(sec, "bye")));
            Passage(sb, PrivateEnd, Say(ClubLines.Pick(sec, "private_yes")));
            if (o.Dealer)
            {
                var buy = new List<string>();
                if (o.Gold >= o.DustPrice) buy.Add($"[[Here, {o.DustPrice} coins.|{DustGoldEnd}]]");
                if (o.HasTonic) buy.Add($"[[Swap you a blue tonic for it.|{DustTonicEnd}]]");
                buy.Add("[[Not today.|More]]");
                Passage(sb, "DustOffer", Say(ClubLines.Pick(sec, buy.Count > 1 ? "dust_offer" : "dust_broke")), buy.ToArray());
                Passage(sb, DustGoldEnd, Say(ClubLines.Pick(sec, "dust_sold")));
                Passage(sb, DustTonicEnd, Say(ClubLines.Pick(sec, "dust_sold")));
            }
            return new TextAsset(sb.ToString());
        }

        // Ending on the "Menu" passage opens the drinks store (the mod checks the last passage shown).
        internal const string VipEnd = "BuyVip";

        internal static TextAsset Barkeep(IList<string> girls, int vipPrice = 0, bool hasVip = true, int gold = 0)
        {
            var sb = new StringBuilder(Head);
            string Say(string line) => $"<b>Barkeep:</b> {ClubLines.Fill(line, "Barkeep", girls)}";
            var list = new List<string>
            {
                "[[What are you pouring?|Menu]]", "[[Any house rules?|Rules]]", "[[Tell me about the girls.|Girls]]",
                "[[Heard anything interesting?|Gossip]]",
            };
            if (!hasVip && vipPrice > 0)
                list.Add("[[What's behind the big guy?|VipInfo]]");
            list.Add("[[Just looking around.|Bye]]");
            var menu = list.ToArray();
            Passage(sb, VipEnd, Say(ClubLines.Pick("barkeep", "vip_sold", null)));
            Passage(sb, "VipInfo", Say(ClubLines.Pick("barkeep", "vip_info", null).Replace("$PRICE", vipPrice.ToString())), "[[Something else.|More]]");
            Passage(sb, "DialogStart", Say(ClubLines.Pick("barkeep", "greet", null)), menu);
            Passage(sb, "Menu", Say(ClubLines.Pick("barkeep", "prices", null)));
            Chain(sb, "Rules", ClubLines.PickSome("barkeep", "rules", 3, null).Select(Say).ToList(), "[[Something else.|More]]");
            Chain(sb, "Girls", ClubLines.PickSome("barkeep", "girls", 2, null).Select(Say).ToList(), "[[Something else.|More]]");
            Chain(sb, "Gossip", ClubLines.PickSome("barkeep", "gossip", 2, null).Select(Say).ToList(), "[[Something else.|More]]");
            Passage(sb, "More", Say("Anything else? I've got all night. Well, until closing."), menu);
            Passage(sb, "Bye", Say(ClubLines.Pick("barkeep", "bye", null)));
            return new TextAsset(sb.ToString());
        }

        // The narrator's account of a private dance: a few lines in a row, then the bill.
        internal static TextAsset PrivateDance(string dancer, int price)
        {
            var sb = new StringBuilder(Head);
            string Narrate(string line) => "<i>" + ClubLines.Fill(line, dancer, ClubGoers.Names).Replace("$PRICE", price.ToString()) + "</i>";
            var lines = ClubLines.PickSome("private", "scene", 4, null).Select(Narrate).ToList();
            lines.Add(Narrate(ClubLines.Pick("private", "ending", null)));
            for (int i = 0; i < lines.Count; i++)
            {
                string id = i == 0 ? "DialogStart" : "Scene" + i;
                if (i + 1 < lines.Count) Passage(sb, id, lines[i], $"[[Continue|Scene{i + 1}]]");
                else Passage(sb, id, lines[i]);
            }
            return new TextAsset(sb.ToString());
        }

        internal const string CoverEnd = "PayCover", BribeEnd = "BribeDust";

        // The bouncer at the club door: cover charge, a Glimmerdust bribe, or a lecture.
        internal static TextAsset Bouncer(bool paid, int cover, int gold, bool hasDust)
        {
            var sb = new StringBuilder(Head);
            string Say(string line) => $"<b>Bouncer:</b> {ClubLines.Fill(line, "Bouncer", ClubGoers.Names).Replace("$PRICE", cover.ToString())}";
            var list = new List<string>();
            if (!paid)
            {
                if (gold >= cover) list.Add($"[[Here's the cover. ({cover} coins)|{CoverEnd}]]");
                if (hasDust) list.Add($"[[How about a little Glimmerdust instead?|{BribeEnd}]]");
                list.Add("[[Do you know who I am?|WhoAmI]]");
                list.Add("[[What's the dress code?|Dress]]");
            }
            else list.Add("[[How's the night?|Chat]]");
            list.Add("[[Never mind.|Bye]]");
            var menu = list.ToArray();
            Passage(sb, "DialogStart", Say(ClubLines.Pick("bouncer", paid ? "greet_paid" : "greet", null)), menu);
            Passage(sb, CoverEnd, Say(ClubLines.Pick("bouncer", "cover_paid", null)));
            Passage(sb, BribeEnd, Say(ClubLines.Pick("bouncer", "bribe", null)));
            Passage(sb, "WhoAmI", Say(ClubLines.Pick("bouncer", "whoami", null)), "[[Fine...|More]]");
            Passage(sb, "Dress", Say(ClubLines.Pick("bouncer", "dress", null)), "[[Right.|More]]");
            Chain(sb, "Chat", ClubLines.PickSome("bouncer", "chat", 2, null).Select(Say).ToList(), "[[Later.|Bye]]");
            Passage(sb, "More", Say("Anything else? I don't have all night. I do, actually. I'm here all night."), menu);
            Passage(sb, "Bye", Say(ClubLines.Pick("bouncer", "bye", null)));
            return new TextAsset(sb.ToString());
        }

        static void Passage(StringBuilder sb, string id, string text, params string[] choices)
        {
            sb.Append(":: ").Append(id).Append(" [NPC:GC_Speaker]\n").Append(text).Append("\n\n");
            foreach (var c in choices) sb.Append(c).Append('\n');
            sb.Append("\n\n");
        }

        // Several lines in a row, each with a Continue, then the closing choice.
        static void Chain(StringBuilder sb, string id, List<string> lines, string end)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string pid = i == 0 ? id : id + "_" + i;
                string next = i + 1 < lines.Count ? $"[[Continue|{id}_{i + 1}]]" : end;
                Passage(sb, pid, lines[i], next);
            }
        }
    }

    // Makes someone in the club talkable (the game's own DialogueTrigger, built from code) and has them
    // call out a line over their head now and then.
    internal class ClubTalker : MonoBehaviour
    {
        internal string SpeakerName;
        internal Func<IList<string>> Everyone = () => new string[0];
        internal DialogueTrigger Dialogue;
        private TextMeshPro _bark;
        private float _nextBark, _barkHideAt;

        internal static NPCTalkBubble BubbleTemplate;

        internal void Setup(string name, GameObject art, Vector2 triggerOffset, Vector2 triggerSize, float headHeight)
        {
            SpeakerName = name;
            var trig = new GameObject("TalkTrigger") { layer = 14 }; // Interactables
            trig.transform.SetParent(transform, false);
            trig.transform.localPosition = triggerOffset;
            var box = trig.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = triggerSize;
            var forward = trig.AddComponent<TriggerForward>();

            NPCTalkBubble bubble = null;
            if (BubbleTemplate != null)
            {
                var b = Instantiate(BubbleTemplate.gameObject, transform);
                b.name = "Bubble";
                b.transform.localPosition = new Vector3(0.4f, headHeight, 0f);
                bubble = b.GetComponent<NPCTalkBubble>();
            }

            // A DialogueTrigger made from code has none of the lists and events the editor fills in.
            Dialogue = gameObject.AddComponent<DialogueTrigger>();
            Dialogue.npcName = name;
            Dialogue.art = art;
            Dialogue.triggerForward = forward;
            Dialogue.selectArrow = bubble;
            Dialogue.uniqueID = GentlemansClubPlugin.SaveIdPrefix + "_" + name;
            Dialogue.dialogueFileLocalized = new LocalizedAsset<TextAsset>();
            Dialogue.triggers = new List<DialogueTriggerReceiver>();
            Dialogue.onStartDialogue = new CustomEventList { events = new List<CustomEvent>() };
            Dialogue.onEndDialogue = new CustomEventList { events = new List<CustomEvent>() };
            Dialogue.onStartDialogueEvent = new UnityEvent();
            Dialogue.onEndDialogueEvent = new UnityEvent();
            Refresh();
            Dialogue.onEndDialogueEvent.AddListener(OnDialogueEnd);

            _bark = MakeBarkText(transform, headHeight + 0.45f);
            _nextBark = Time.time + Random.Range(4f, 20f);
        }

        internal bool CanFollow, IsDancer;
        // Someone who isn't one of the girls (the bouncer) brings their own conversation and endings.
        internal Func<TextAsset> CustomDialogue;
        internal string BarkSection; // own remarks (the bouncer's "chat"), instead of the girls' pool
        internal Action<string> CustomEnd;
        internal Func<bool> CanPrivateDance = () => false;
        internal static int PrivateDancePrice = 100;
        internal static int TipSmall = 10, TipBig = 50, RainPrice = 50, PourPrice = 200, DustPrice = 30;
        internal static ItemData KymiaTonic; // the small blue tonic Trixie takes in trade, loaded by the plugin
        internal bool Dealer => SpeakerName == "Trixie";

        internal bool Talking => Dialogue != null && Dialogue.isOpened;
        Follower FollowerComp => GetComponent<Follower>();

        // A new conversation, built for your purse and her situation; called just before it opens.
        internal void Refresh()
        {
            if (CustomDialogue != null) { Dialogue.dialogueFile = CustomDialogue(); return; }
            var inv = Singleton<Inventory>.HasMain ? Singleton<Inventory>.main : null;
            var f = FollowerComp;
            Dialogue.dialogueFile = ClubDialogue.Girl(SpeakerName, Everyone(), new ClubDialogue.GirlOptions
            {
                Gold = inv != null ? inv.TotalGold.CurrentValue : 0,
                TipSmall = TipSmall, TipBig = TipBig,
                Rain = IsDancer ? RainPrice : 0, Pour = IsDancer ? PourPrice : 0,
                CanFollow = CanFollow, Following = f != null && f.Following,
                Dealer = Dealer, DustPrice = DustPrice,
                PrivatePrice = CanPrivateDance() ? PrivateDancePrice : 0,
                HasTonic = inv != null && KymiaTonic != null && inv.ContainsItem(KymiaTonic, out _),
            });
        }

        // The last passage shown says what you chose.
        private void OnDialogueEnd()
        {
            string end = Singleton<DialogueManager>.main.LastTrigger;
            if (CustomEnd != null) { CustomEnd(end); return; }
            switch (end)
            {
                case ClubDialogue.TipSmallEnd: Pay(TipSmall, () => ClubEffects.Tipped(this, false)); break;
                case ClubDialogue.TipBigEnd: Pay(TipBig, () => ClubEffects.Tipped(this, true)); break;
                case ClubDialogue.RainEnd: Pay(RainPrice, () => ClubEffects.MakeItRain(this, false)); break;
                case ClubDialogue.PourEnd: Pay(PourPrice, () => ClubEffects.MakeItRain(this, true)); break;
                case ClubDialogue.FollowEnd: FollowerComp?.StartFollowing(); break;
                case ClubDialogue.HomeEnd: FollowerComp?.StopFollowing(); break;
                case ClubDialogue.PrivateEnd: Pay(PrivateDancePrice, () => ClubEffects.PrivateDance(this)); break;
                case ClubDialogue.DustGoldEnd:
                    Pay(DustPrice, () => { if (!GiveDust()) Singleton<Inventory>.main.AddGold(DustPrice); });
                    break;
                case ClubDialogue.DustTonicEnd:
                    var inv = Singleton<Inventory>.main;
                    if (KymiaTonic != null && inv.ConsumeItemTypeOf(KymiaTonic, 1) == 0 && !GiveDust())
                        inv.AddItem(KymiaTonic.CreateItem(1)); // no room: she gives the tonic back
                    break;
            }
        }

        // Into your bag; false (and she says so) when it's full.
        bool GiveDust()
        {
            if (Singleton<Inventory>.main.AddItem(Glimmerdust.Data.CreateItem(1)) == 0)
            {
                AkUnitySoundEngine.PostEvent("Play_UI_General_Select_Important", gameObject);
                return true;
            }
            Say("Your bag's full, sugar. Make room and come back.");
            return false;
        }

        static void Pay(int coins, Action then)
        {
            var inv = Singleton<Inventory>.main;
            if (!inv.HasEnoughGold(coins)) return;
            inv.AddGold(-coins);
            then();
        }

        private void Update()
        {
            if (_bark == null) return;
            if (_bark.gameObject.activeSelf && (Time.time > _barkHideAt || Talking)) _bark.gameObject.SetActive(false);
            if (Time.time < _nextBark || Talking) return;
            _nextBark = Time.time + Random.Range(14f, 35f);
            var player = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
            if (player == null || (player.transform.position - transform.position).sqrMagnitude > 7f * 7f) return;
            var line = BarkSection != null ? ClubLines.Pick(BarkSection, "chat", null) : ClubLines.Pick("girl:" + SpeakerName, "bark");
            Say(ClubLines.Fill(line, SpeakerName, Everyone()).Replace("{PlayerName}", PlayerName()));
        }

        internal void Say(string text, float seconds = 4.5f)
        {
            if (_bark == null) return;
            _bark.text = text;
            _bark.gameObject.SetActive(true);
            _barkHideAt = Time.time + seconds;
        }

        static string PlayerName() => string.IsNullOrEmpty(GameData.playerData?.playerName) ? "Wanderer" : GameData.playerData.playerName;

        static TMP_FontAsset _font;

        // World-space text in the game's own font, like its signs and NPC lines.
        internal static TextMeshPro MakeBarkText(Transform parent, float height)
        {
            if (_font == null)
            {
                var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                _font = fonts.FirstOrDefault(f => f.name == "Emberfont") ?? fonts.FirstOrDefault();
            }
            var go = new GameObject("Bark");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, height, 0f);
            var tmp = go.AddComponent<TextMeshPro>();
            if (_font != null) tmp.font = _font;
            tmp.fontSize = 2.2f;
            tmp.alignment = TextAlignmentOptions.Bottom;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.rectTransform.sizeDelta = new Vector2(5.5f, 1.6f);
            tmp.rectTransform.pivot = new Vector2(0.5f, 0f);
            tmp.color = new Color(1f, 0.92f, 0.97f);
            tmp.outlineWidth = 0.25f;
            tmp.outlineColor = new Color32(20, 10, 25, 255);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sortingLayerName = "Player";
            mr.sortingOrder = 600;
            go.SetActive(false);
            return tmp;
        }
    }
}
