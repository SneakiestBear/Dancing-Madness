using DancingMadness.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using static DancingMadness.Core.State;

namespace DancingMadness.Content
{

    internal class UltDancingMad : Core.Content
    {

        public override FeaturesEnum Features => FeaturesEnum.None;

        // Kefka, Dancing Mad (Ultimate) -- territory 0x553
        private const uint TerritoryDancingMad = 1363;

        // Forsaken is the raidwide that opens the tower/AoE marker sequence.
        private const int AbilityForsaken = 0xBABC;
        // "the Path of Light" -- the tower soak. Each one advances the tower counter,
        // which drives which group (per the order string) is currently shown.
        private const int AbilityPathOfLight = 0xBABE;

        // (P4) casts that drive the progressive Kefka Says sequence. Decimal to match the
        // reverse-engineered values exactly (hex in comments).
        private const int AbilityKefkaSays = 49884;       // 0xC2DC -- phase start
        private const int AbilityFloodOfNaught1 = 50066;  // 0xC392
        private const int AbilityFloodOfNaught2 = 50067;  // 0xC393
        private const int AbilityFloodOfNaught3 = 50081;  // 0xC3A1
        private const int AbilityFloodOfNaught4 = 50082;  // 0xC3A2
        private const int AbilityUltimaUpsurge = 49738;   // 0xC24A
        private const int AbilityManaRelease = 47781;     // 0xBAA5
        private const int AbilityBlizzardBlowout = 47765; // 0xBA95

        private bool ZoneOk = false;
        private bool _subbed = false;

        private ForsakenAM _forsakenAm;
        private EarthquakeAM _earthquakeAm;
        private KefkaSaysAM _kefkaSaysAm;
        private KefkaSaysProgressiveAM _kefkaSaysProgressiveAm;

        #region ForsakenAM

        public class ForsakenAM : Automarker
        {

            [AttributeOrderNumber(1000)]
            public AutomarkerSigns Signs { get; set; }

            [AttributeOrderNumber(2000)]
            public AutomarkerPrio Prio { get; set; }

            // Which group takes each tower, in order (each enum name IS the order string).
            // Only the active group is marked; when it changes (e.g. A->B after tower 3 of
            // AAABBBBA) the old group's markers clear and the new group's appear.
            public enum TowerOrderEnum
            {
                AAABBBBA,
                ABBAABBA,
                AAAABBBB,
            }

            public class TowerOrderWidget : CustomPropertyInterface
            {

                private ForsakenAM _am;

                public TowerOrderWidget(ForsakenAM am)
                {
                    _am = am;
                }

                public override string Serialize()
                {
                    return string.Format("Order={0}", (int)_am._towerOrder);
                }

                public override void Deserialize(string data)
                {
                    string[] temp = data.Split(";");
                    foreach (string t in temp)
                    {
                        string[] item = t.Split("=", 2);
                        if (item.Length == 2 && item[0] == "Order" && int.TryParse(item[1], out int id) == true)
                        {
                            _am._towerOrder = (TowerOrderEnum)id;
                        }
                    }
                }

                public override void RenderEditor(string path)
                {
                    ImGui.TextWrapped(I18n.Translate(path));
                    string selname = I18n.Translate(path + "/" + _am._towerOrder.ToString());
                    if (ImGui.BeginCombo("##" + path, selname) == true)
                    {
                        foreach (TowerOrderEnum p in Enum.GetValues(typeof(TowerOrderEnum)))
                        {
                            string name = I18n.Translate(path + "/" + p.ToString());
                            if (ImGui.Selectable(name, String.Compare(name, selname) == 0) == true)
                            {
                                _am._towerOrder = p;
                            }
                        }
                        ImGui.EndCombo();
                    }
                }

            }

            [AttributeOrderNumber(2100)]
            public TowerOrderWidget TowerOrder { get; set; }

            internal TowerOrderEnum _towerOrder { get; set; } = TowerOrderEnum.AAABBBBA;

            // Debug: feeds fake Forsaken/tower packets so the whole sequence can be driven
            // by hand in any duty (party of 8) without being in the actual fight.
            public class SimulatorWidget : CustomPropertyInterface
            {

                private ForsakenAM _am;

                public SimulatorWidget(ForsakenAM am)
                {
                    _am = am;
                }

                public override string Serialize()
                {
                    return "";
                }

                public override void Deserialize(string data)
                {
                }

                public override void RenderEditor(string path)
                {
                    ImGui.TextWrapped(I18n.Translate(path));
                    if (ImGui.Button(I18n.Translate(path + "/Forsaken") + "##simforsaken") == true)
                    {
                        _am.SimForsaken();
                    }
                    for (int i = 1; i <= 8; i++)
                    {
                        if (i != 1 && i != 5)
                        {
                            ImGui.SameLine();
                        }
                        if (ImGui.Button(I18n.Translate(path + "/Tower") + " " + i.ToString() + "##simtower" + i.ToString()) == true)
                        {
                            _am.SimTower();
                        }
                    }
                }

            }

            [DebugOption]
            [AttributeOrderNumber(2200)]
            public SimulatorWidget Simulate { get; set; }

            [DebugOption]
            [AttributeOrderNumber(2500)]
            public AutomarkerTiming Timing { get; set; }

            [DebugOption]
            [AttributeOrderNumber(3000)]
            public System.Action Test { get; set; }

            // Headmarkers, one per AoE category. A player keeps their marker until they
            // take a tower and the game reassigns them, so each player's category is held
            // persistently in _current and only updated for the players who flash. In this
            // Dawntrail content the network sends real icon ids directly (no offset).
            private const uint HeadmarkerStack = 0x2CB; // stacks
            private const uint HeadmarkerAoe = 0x2CC;   // Spellscatter -> spread/AoE
            private const uint HeadmarkerCone = 0x2CD;  // Spellwave -> cone

            // A wave's headmarkers all arrive in one same-timestamp burst.
            private const double WaveDebounceSeconds = 0.4;
            // Path of Light hits arrive as a burst too; count one tower per burst.
            private const double TowerDebounceSeconds = 2.0;
            // The full sequence (8 towers ~10s apart) runs well over a minute.
            private const double ArmWindowSeconds = 120.0;

            private bool _armed = false;
            private bool _wavePending = false;
            private bool _redrawPending = false;
            private bool _firstWaveDone = false;
            private int _towerCount = 0;
            private DateTime _armUntil = DateTime.MinValue;
            private DateTime _lastMarkerTime = DateTime.MinValue;
            private DateTime _lastTowerTime = DateTime.MinValue;
            private List<uint> _stack = new List<uint>();
            private List<uint> _aoe = new List<uint>();
            private List<uint> _cone = new List<uint>();
            // This wave's raw (un-normalized) headmarkers, actorId -> raw markerId. The live
            // game sends the canonical icon ids plus a per-session offset; we learn that
            // offset from the first wave and normalize. (The simulator feeds canonical ids,
            // so it learns an offset of 0.)
            private Dictionary<uint, uint> _pendingRaw = new Dictionary<uint, uint>();
            private bool _offsetKnown = false;
            private uint _headmarkerOffset = 0;
            // Group A (2 stacks + their locked buddies) and Group B (the other 4),
            // decided once on the first wave; plus each player's current category.
            private List<uint> _groupA = new List<uint>();
            private List<uint> _groupB = new List<uint>();
            private Dictionary<uint, uint> _current = new Dictionary<uint, uint>();
            // Players marked on the last redraw, so we only clear the ones leaving.
            private HashSet<uint> _lastMarked = new HashSet<uint>();
            // When to re-assert marks once (self-heal a sign the game dropped during a
            // swap's clear+place burst). MinValue = nothing pending.
            private DateTime _reassertAt = DateTime.MinValue;

            private static readonly string[] StackRoles = new string[] { "Stack1", "Stack2" };
            private static readonly string[] ConeRoles = new string[] { "Cone1", "Cone2", "Cone3" };
            private static readonly string[] AoeRoles = new string[] { "Aoe1", "Aoe2" };

            public ForsakenAM(State state) : base(state)
            {
                Enabled = false;
                TowerOrder = new TowerOrderWidget(this);
                Simulate = new SimulatorWidget(this);
                // Fast but rate-limit-safe placement: the initial delay is just cosmetic
                // human-reaction time (cut short), the inter-mark spacing is what keeps the
                // game from dropping marks. Configurable in the UI. The re-assert failsafe
                // adapts to whatever this is set to.
                Timing = new AutomarkerTiming()
                {
                    TimingType = AutomarkerTiming.TimingTypeEnum.Explicit,
                    IniDelayMin = 0.1f,
                    IniDelayMax = 0.2f,
                    SubDelayMin = 0.1f,
                    SubDelayMax = 0.15f,
                };
                Signs = new AutomarkerSigns();
                // Every role is a configurable waymark (UCOB-style named markers).
                Signs.SetRole("Stack1", AutomarkerSigns.SignEnum.Bind1, false);
                Signs.SetRole("Stack2", AutomarkerSigns.SignEnum.Bind2, false);
                Signs.SetRole("Cone1", AutomarkerSigns.SignEnum.Attack1, false);
                Signs.SetRole("Cone2", AutomarkerSigns.SignEnum.Attack2, false);
                Signs.SetRole("Cone3", AutomarkerSigns.SignEnum.Attack3, false);
                Signs.SetRole("Aoe1", AutomarkerSigns.SignEnum.Ignore1, false);
                Signs.SetRole("Aoe2", AutomarkerSigns.SignEnum.Ignore2, false);
                // Numbering within a category, by role. Configurable in the UI; default
                // Healer, Tank, Melee, Ranged, Caster. Clear() first -- AutomarkerPrio's
                // constructor pre-fills _prioByRole, so adding without clearing duplicates.
                Prio = new AutomarkerPrio();
                Prio.Priority = AutomarkerPrio.PrioTypeEnum.Role;
                Prio._prioByRole.Clear();
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Healer);
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Tank);
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Melee);
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Ranged);
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Caster);
                Test = new System.Action(() => Signs.TestFunctionality(state, Prio, Timing, SelfMarkOnly, AsSoftmarker));
            }

            public override void Reset()
            {
                if (_armed == true || _firstWaveDone == true)
                {
                    Log(State.LogLevelEnum.Debug, null, "Reset");
                }
                _armed = false;
                _wavePending = false;
                _redrawPending = false;
                _firstWaveDone = false;
                _towerCount = 0;
                _armUntil = DateTime.MinValue;
                _lastMarkerTime = DateTime.MinValue;
                _lastTowerTime = DateTime.MinValue;
                _stack.Clear();
                _aoe.Clear();
                _cone.Clear();
                _pendingRaw.Clear();
                _offsetKnown = false;
                _headmarkerOffset = 0;
                _groupA.Clear();
                _groupB.Clear();
                _current.Clear();
                _lastMarked.Clear();
                _reassertAt = DateTime.MinValue;
            }

            public void Arm()
            {
                if (Active == false)
                {
                    return;
                }
                Log(State.LogLevelEnum.Debug, null, "Forsaken cast detected, arming sequence");
                Reset();
                _armed = true;
                _armUntil = DateTime.Now.AddSeconds(ArmWindowSeconds);
            }

            public void FeedHeadmarker(uint actorId, uint markerId)
            {
                if (_armed == false)
                {
                    return;
                }
                // Buffer the raw id; it's normalized and categorized in ProcessWave once the
                // burst settles, so the per-session offset can be learned from the whole wave.
                _pendingRaw[actorId] = markerId;
                _lastMarkerTime = DateTime.Now;
                _wavePending = true;
            }

            public void FeedTower()
            {
                if (_armed == false)
                {
                    return;
                }
                DateTime now = DateTime.Now;
                if ((now - _lastTowerTime).TotalSeconds < TowerDebounceSeconds)
                {
                    return; // same tower's burst of hits
                }
                _lastTowerTime = now;
                _towerCount++;
                Log(State.LogLevelEnum.Debug, null, "Tower {0} taken", _towerCount);
                _redrawPending = true;
            }

            protected override bool ExecutionImplementation()
            {
                if (_armed == true && DateTime.Now > _armUntil)
                {
                    _armed = false;
                    _wavePending = false;
                    _redrawPending = false;
                    _stack.Clear();
                    _aoe.Clear();
                    _cone.Clear();
                    _pendingRaw.Clear();
                    _offsetKnown = false;
                    _headmarkerOffset = 0;
                    if (_current.Count > 0)
                    {
                        _current.Clear();
                        _lastMarked.Clear();
                        _reassertAt = DateTime.MinValue;
                        _state.ClearAutoMarkers();
                    }
                }
                if (_wavePending == true && (DateTime.Now - _lastMarkerTime).TotalSeconds >= WaveDebounceSeconds)
                {
                    ProcessWave();
                }
                if (_redrawPending == true)
                {
                    _redrawPending = false;
                    RedrawMarkers();
                }
                if (_reassertAt != DateTime.MinValue && DateTime.Now >= _reassertAt)
                {
                    ReassertMarkers();
                }
                return true;
            }

            // On the first wave the 2 stack-marked players are the stacks; lock Group A as
            // those 2 plus each one's nearest other player, and Group B as the rest. Never
            // recomputed, so the buddy stays fixed even as the stack later passes around.
            private void DecideGroups(Party pty, List<uint> allFlashed)
            {
                _groupA = new List<uint>(_stack);
                HashSet<uint> taken = new HashSet<uint>(_stack);
                foreach (uint stackId in _stack)
                {
                    Party.PartyMember sm = pty.GetByActorId(stackId);
                    if (sm == null)
                    {
                        continue;
                    }
                    Party.PartyMember best = null;
                    float bestDist = float.MaxValue;
                    foreach (Party.PartyMember pm in pty.Members)
                    {
                        if (pm.GameObject == null || taken.Contains((uint)pm.ObjectId) == true)
                        {
                            continue;
                        }
                        float dx = sm.X - pm.X;
                        float dz = sm.Z - pm.Z;
                        float dist = (dx * dx) + (dz * dz);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best = pm;
                        }
                    }
                    if (best != null)
                    {
                        _groupA.Add((uint)best.ObjectId);
                        taken.Add((uint)best.ObjectId);
                    }
                }
                _groupB = (from id in allFlashed where _groupA.Contains(id) == false select id).ToList();
                Log(State.LogLevelEnum.Debug, null, "Group A {0} players, Group B {1} players", _groupA.Count, _groupB.Count);
            }

            // Merge a finished wave's headmarkers into the persistent per-player state.
            private void ProcessWave()
            {
                _wavePending = false;
                // Normalize this wave's raw headmarkers into categories. The three Forsaken
                // ids are consecutive (stack < AoE < cone), so the stack is the lowest; the
                // per-session offset is learned once as (lowest raw id) - canonical stack id.
                if (_pendingRaw.Count > 0)
                {
                    if (_offsetKnown == false)
                    {
                        uint min = uint.MaxValue;
                        foreach (uint raw in _pendingRaw.Values)
                        {
                            if (raw < min) { min = raw; }
                        }
                        _headmarkerOffset = min - HeadmarkerStack;
                        _offsetKnown = true;
                        Log(State.LogLevelEnum.Info, null, "[Forsaken] learned headmarker offset 0x{0:X} (raw 0x{1:X} = stack)", _headmarkerOffset, min);
                    }
                    foreach (KeyValuePair<uint, uint> kp in _pendingRaw)
                    {
                        uint norm = kp.Value - _headmarkerOffset;
                        if (norm == HeadmarkerStack) { if (_stack.Contains(kp.Key) == false) { _stack.Add(kp.Key); } }
                        else if (norm == HeadmarkerAoe) { if (_aoe.Contains(kp.Key) == false) { _aoe.Add(kp.Key); } }
                        else if (norm == HeadmarkerCone) { if (_cone.Contains(kp.Key) == false) { _cone.Add(kp.Key); } }
                    }
                    _pendingRaw.Clear();
                }
                if (_firstWaveDone == false)
                {
                    Party pty = _state.GetPartyMembers();
                    List<uint> all = new List<uint>();
                    all.AddRange(_stack);
                    all.AddRange(_aoe);
                    all.AddRange(_cone);
                    if (_stack.Count > 0)
                    {
                        DecideGroups(pty, all);
                    }
                    _firstWaveDone = true;
                }
                foreach (uint id in _stack) { _current[id] = HeadmarkerStack; }
                foreach (uint id in _aoe) { _current[id] = HeadmarkerAoe; }
                foreach (uint id in _cone) { _current[id] = HeadmarkerCone; }
                _stack.Clear();
                _aoe.Clear();
                _cone.Clear();
                _redrawPending = true;
            }

            // Whoever takes the next tower (order string indexed by towers taken so far).
            private List<uint> ActiveGroup()
            {
                if (_groupA.Count == 0)
                {
                    // groups never identified -- fall back to everyone we're tracking
                    return new List<uint>(_current.Keys);
                }
                string order = _towerOrder.ToString();
                if (_towerCount < 0 || _towerCount >= order.Length)
                {
                    return new List<uint>(); // sequence complete / out of range
                }
                char c = order[_towerCount];
                if (c == 'A') { return _groupA; }
                if (c == 'B') { return _groupB; }
                return new List<uint>();
            }

            // Show only the active group, each with their current category.
            private void RedrawMarkers()
            {
                // Consume the pending flag so a direct call (the simulator) isn't followed
                // by a second poll-driven redraw -- two placement passes can race and
                // double-toggle a marker off.
                _redrawPending = false;
                Party pty = _state.GetPartyMembers();
                List<uint> stack, cone, aoe;
                CollectActiveCategories(out stack, out cone, out aoe);
                Log(State.LogLevelEnum.Debug, null, "Redraw (tower {0}) -- stack {1} aoe {2} cone {3}", _towerCount, stack.Count, aoe.Count, cone.Count);
                // Clear ONLY players leaving the marked set. Clearing players we then
                // re-mark races with placement: the game's mark is a toggle, so a player
                // who keeps the same sign reads as "already marked" (placement skipped)
                // while the queued clear toggles it off -- dropping that sign. Players
                // whose sign merely changes are replaced correctly by ExecuteAutomarkers.
                HashSet<uint> nowMarked = new HashSet<uint>();
                foreach (uint id in stack) { nowMarked.Add(id); }
                foreach (uint id in cone) { nowMarked.Add(id); }
                foreach (uint id in aoe) { nowMarked.Add(id); }
                foreach (uint id in _lastMarked)
                {
                    if (nowMarked.Contains(id) == false)
                    {
                        Party.PartyMember pm = pty.GetByActorId(id);
                        if (pm != null && pm.GameObject != null)
                        {
                            _state.ClearMarkerOn(pm.GameObject, true, true);
                        }
                    }
                }
                _lastMarked = nowMarked;
                PlaceMarkers(pty, stack, cone, aoe);
                // Re-assert once, ~1s later, to self-heal a sign the game dropped (notably
                // the shared sign in a group swap's clear+place burst). It re-places only
                // the dropped one (correct marks are skipped). 1s by default; only stretches
                // if the timing is set slow enough that the placement chain wouldn't be done
                // (re-asserting before then would race and double-toggle a mark).
                _reassertAt = DateTime.Now.AddSeconds(Math.Max(1.0, ReassertDelaySeconds()));
            }

            // How long the placement chain can take (first mark + up to 3 subsequent) plus a
            // small buffer for the leaver-clears and queue latency. Adapts to the Timing.
            private double ReassertDelaySeconds()
            {
                AutomarkerTiming t = Timing;
                while (t != null && t.TimingType == AutomarkerTiming.TimingTypeEnum.Inherit && t.Parent != null)
                {
                    t = t.Parent;
                }
                double ini = t != null ? t.IniDelayMax : 0.7;
                double sub = t != null ? t.SubDelayMax : 0.3;
                return ini + (3.0 * sub) + 0.25;
            }

            // Re-place the active group's marks without clearing or re-scheduling; the game
            // skips marks already correct, so only a dropped one gets fixed.
            private void ReassertMarkers()
            {
                _reassertAt = DateTime.MinValue;
                Party pty = _state.GetPartyMembers();
                List<uint> stack, cone, aoe;
                CollectActiveCategories(out stack, out cone, out aoe);
                PlaceMarkers(pty, stack, cone, aoe);
            }

            private void CollectActiveCategories(out List<uint> stack, out List<uint> cone, out List<uint> aoe)
            {
                stack = new List<uint>();
                cone = new List<uint>();
                aoe = new List<uint>();
                foreach (uint id in ActiveGroup())
                {
                    uint icon;
                    if (_current.TryGetValue(id, out icon) == false)
                    {
                        continue;
                    }
                    if (icon == HeadmarkerStack) { stack.Add(id); }
                    else if (icon == HeadmarkerAoe) { aoe.Add(id); }
                    else if (icon == HeadmarkerCone) { cone.Add(id); }
                }
            }

            private void PlaceMarkers(Party pty, List<uint> stack, List<uint> cone, List<uint> aoe)
            {
                AutomarkerPayload ap = new AutomarkerPayload(_state, SelfMarkOnly, AsSoftmarker);
                AssignCategory(ap, pty, stack, StackRoles, -1);
                // On odd towers the group has a single cone and a single AoE; those players
                // must take a specific tower, so a lone cone is assigned Cone 3 and a lone AoE
                // AoE 2 (not the "1" slot). Even towers have two of each, numbered normally.
                AssignCategory(ap, pty, cone, ConeRoles, 2);
                AssignCategory(ap, pty, aoe, AoeRoles, 1);
                _state.ExecuteAutomarkers(ap, Timing);
            }

            // loneSlot: when exactly one player is in the category, place them on this role
            // index instead of the first one (-1 disables). Used for the odd-tower cone/AoE.
            private void AssignCategory(AutomarkerPayload ap, Party pty, List<uint> actorIds, string[] roles, int loneSlot)
            {
                List<Party.PartyMember> members = pty.GetByActorIds(actorIds);
                Prio.SortByPriority(members);
                if (loneSlot >= 0 && members.Count == 1 && roles.Length > loneSlot)
                {
                    ap.Assign(Signs.Roles[roles[loneSlot]], members[0].GameObject);
                    return;
                }
                for (int i = 0; i < members.Count && i < roles.Length; i++)
                {
                    ap.Assign(Signs.Roles[roles[i]], members[i].GameObject);
                }
            }

            // ---- Simulation (debug) ----
            // Feeds the same data the live packets would, then drives ProcessWave/Redraw
            // directly (the normal polling is zone-gated, so it wouldn't run out of fight).

            private void SimAssignGroup(List<uint> grp, bool odd)
            {
                if (grp == null || grp.Count == 0)
                {
                    return;
                }
                // Solo: follow the real parity so you can see each marker type in the right
                // place. Odd-tower assignments (Forsaken, after T2/T4/T6) are stacks; even
                // ones (after T1/T3/T5/T7) are cone/AoE -- alternated so both get shown.
                if (grp.Count == 1)
                {
                    uint icon;
                    if ((_towerCount % 2) == 0)
                    {
                        icon = HeadmarkerStack;
                    }
                    else
                    {
                        icon = (((_towerCount / 2) % 2) == 0) ? HeadmarkerCone : HeadmarkerAoe;
                    }
                    FeedHeadmarker(grp[0], icon);
                    return;
                }
                // Party: odd tower = 2 stacks then cone/AoE; even tower = alternating
                // cone/AoE (so a 4-person group shows 2 cones + 2 AoEs and never a stack).
                // Assigns to whoever is present.
                uint[] pattern = odd
                    ? new uint[] { HeadmarkerStack, HeadmarkerStack, HeadmarkerCone, HeadmarkerAoe }
                    : new uint[] { HeadmarkerCone, HeadmarkerAoe, HeadmarkerCone, HeadmarkerAoe };
                for (int i = 0; i < grp.Count; i++)
                {
                    FeedHeadmarker(grp[i], pattern[i % pattern.Length]);
                }
            }

            internal void SimForsaken()
            {
                Party pty = _state.GetPartyMembers();
                List<Party.PartyMember> members = (from m in pty.Members where m.GameObject != null orderby m.Index select m).ToList();
                if (members.Count < 1)
                {
                    Log(State.LogLevelEnum.Info, null, "Simulate: no party members found");
                    return;
                }
                Arm();
                if (_armed == false)
                {
                    Log(State.LogLevelEnum.Info, null, "Simulate: enable the automarker first");
                    return;
                }
                // Opener: the two chain (stack) markers go on a healer + a ranged/caster to
                // mirror a real pull (debug only -- the live fight reads the game's own chain
                // headmarkers); the rest split cone/AoE. With a full party this is the real
                // 2/3/3 split; with fewer it just marks whoever is present.
                List<uint> stacks = PickSimStacks(members);
                int other = 0;
                foreach (Party.PartyMember m in members)
                {
                    uint id = (uint)m.ObjectId;
                    uint icon;
                    if (stacks.Contains(id))
                    {
                        icon = HeadmarkerStack;
                    }
                    else
                    {
                        icon = ((other % 2) == 0) ? HeadmarkerCone : HeadmarkerAoe;
                        other++;
                    }
                    FeedHeadmarker(id, icon);
                }
                ProcessWave();
                // Snap to the proper tower-1 categories for whoever's present
                // (full group: 2 stack + 1 cone + 1 AoE; solo: cycles markers).
                SimAssignGroup(_groupA, true);
                SimAssignGroup(_groupB, false);
                ProcessWave();
                RedrawMarkers();
            }

            // The two stack (chain) holders for the simulated opener: a healer and a
            // physical-ranged/caster, matching the real Forsaken assignment so a dry run is
            // representative. Falls back to the first two present players if the party lacks
            // those roles (e.g. testing solo or an odd comp).
            private List<uint> PickSimStacks(List<Party.PartyMember> members)
            {
                uint healer = 0;
                uint ranged = 0;
                foreach (Party.PartyMember m in members)
                {
                    AutomarkerPrio.PrioRoleEnum role = AutomarkerPrio.JobToRole(m.Job);
                    if (healer == 0 && role == AutomarkerPrio.PrioRoleEnum.Healer)
                    {
                        healer = (uint)m.ObjectId;
                    }
                    else if (ranged == 0 && (role == AutomarkerPrio.PrioRoleEnum.Ranged || role == AutomarkerPrio.PrioRoleEnum.Caster))
                    {
                        ranged = (uint)m.ObjectId;
                    }
                }
                List<uint> stacks = new List<uint>();
                if (healer != 0 && ranged != 0)
                {
                    stacks.Add(healer);
                    stacks.Add(ranged);
                }
                else
                {
                    for (int i = 0; i < members.Count && i < 2; i++)
                    {
                        stacks.Add((uint)members[i].ObjectId);
                    }
                }
                return stacks;
            }

            internal void SimTower()
            {
                if (_armed == false)
                {
                    return;
                }
                // Bypass the burst debounce so each manual click counts as one tower.
                _lastTowerTime = DateTime.MinValue;
                FeedTower();
                string order = _towerOrder.ToString();
                if (_towerCount < order.Length)
                {
                    char gc = order[_towerCount];
                    List<uint> grp = (gc == 'A') ? _groupA : ((gc == 'B') ? _groupB : null);
                    bool odd = (((_towerCount + 1) % 2) == 1);
                    SimAssignGroup(grp, odd);
                    ProcessWave();
                }
                RedrawMarkers();
            }

        }

        #endregion

        #region EarthquakeAM

        // Phase 3 "Earthquake": Accretion (0x644) lands on two players (one healer, one DPS)
        // at the same instant as the shared "in line" debuffs. The Accretion holder who also
        // has First in Line takes Chain 1; the one who also has Second in Line takes Chain 2.
        public class EarthquakeAM : Automarker
        {

            [AttributeOrderNumber(1000)]
            public AutomarkerSigns Signs { get; set; }

            public class SimulatorWidget : CustomPropertyInterface
            {

                private EarthquakeAM _am;

                public SimulatorWidget(EarthquakeAM am)
                {
                    _am = am;
                }

                public override string Serialize()
                {
                    return "";
                }

                public override void Deserialize(string data)
                {
                }

                public override void RenderEditor(string path)
                {
                    ImGui.TextWrapped(I18n.Translate(path));
                    if (ImGui.Button(I18n.Translate(path + "/Earthquake") + "##simquake") == true)
                    {
                        _am.SimEarthquake();
                    }
                }

            }

            [DebugOption]
            [AttributeOrderNumber(2200)]
            public SimulatorWidget Simulate { get; set; }

            [DebugOption]
            [AttributeOrderNumber(2500)]
            public AutomarkerTiming Timing { get; set; }

            // Shared "in line" debuffs (also used by The Omega Protocol); Accretion picks the
            // two chain takers out of the eight.
            internal const uint StatusAccretion = 0x644;
            internal const uint StatusFirstInLine = 0xBBC;
            internal const uint StatusSecondInLine = 0xBBD;

            private const double StatusDebounceSeconds = 0.5;

            private HashSet<uint> _accretion = new HashSet<uint>();
            private HashSet<uint> _first = new HashSet<uint>();
            private HashSet<uint> _second = new HashSet<uint>();
            // Actors currently wearing a chain mark, so we can clear each one individually
            // when its in-line debuff falls off.
            private HashSet<uint> _marked = new HashSet<uint>();
            private bool _pending = false;
            private bool _placed = false;
            private DateTime _lastStatus = DateTime.MinValue;

            public EarthquakeAM(State state) : base(state)
            {
                Signs = new AutomarkerSigns();
                Signs.SetRole("Chain1", AutomarkerSigns.SignEnum.Bind1, false);
                Signs.SetRole("Chain2", AutomarkerSigns.SignEnum.Bind2, false);
                Simulate = new SimulatorWidget(this);
                // Only two marks, so a fast but rate-limit-safe profile is plenty.
                Timing = new AutomarkerTiming()
                {
                    TimingType = AutomarkerTiming.TimingTypeEnum.Explicit,
                    IniDelayMin = 0.1f,
                    IniDelayMax = 0.2f,
                    SubDelayMin = 0.1f,
                    SubDelayMax = 0.15f,
                };
            }

            public override void Reset()
            {
                _accretion.Clear();
                _first.Clear();
                _second.Clear();
                _marked.Clear();
                _pending = false;
                if (_placed == true)
                {
                    _placed = false;
                    _state.ClearAutoMarkers();
                }
            }

            // Live: the parent forwards Accretion / First / Second in Line gains and losses.
            public void FeedStatus(uint actorId, uint statusId, bool gained)
            {
                if (Active == false)
                {
                    return;
                }
                if (gained == false)
                {
                    // A chain holder's own in-line debuff falling off means that player has
                    // resolved -> clear just their mark. Accretion drops earlier and must NOT
                    // trigger a clear, so only First/Second in Line count here.
                    if ((statusId == StatusFirstInLine || statusId == StatusSecondInLine) && _marked.Contains(actorId) == true)
                    {
                        ClearOne(actorId);
                    }
                    return;
                }
                if (statusId == StatusAccretion) { _accretion.Add(actorId); }
                else if (statusId == StatusFirstInLine) { _first.Add(actorId); }
                else if (statusId == StatusSecondInLine) { _second.Add(actorId); }
                else { return; }
                _lastStatus = DateTime.Now;
                _pending = true;
            }

            private void ClearOne(uint actorId)
            {
                Party.PartyMember pm = _state.GetPartyMembers().GetByActorId(actorId);
                if (pm != null && pm.GameObject != null)
                {
                    _state.ClearMarkerOn(pm.GameObject, true, true);
                }
                _marked.Remove(actorId);
                _accretion.Remove(actorId);
                _first.Remove(actorId);
                _second.Remove(actorId);
                if (_marked.Count == 0)
                {
                    _placed = false;
                }
            }

            protected override bool ExecutionImplementation()
            {
                // The debuffs land in one burst; place once it settles.
                if (_pending == true && (DateTime.Now - _lastStatus).TotalSeconds >= StatusDebounceSeconds)
                {
                    _pending = false;
                    PlaceMarkers();
                }
                return true;
            }

            private void PlaceMarkers()
            {
                if (_accretion.Count == 0)
                {
                    return;
                }
                Party pty = _state.GetPartyMembers();
                AutomarkerPayload ap = new AutomarkerPayload(_state, SelfMarkOnly, AsSoftmarker);
                // Chain 1 = the Accretion holder who also has First in Line; Chain 2 = the
                // Accretion holder who also has Second in Line.
                _marked.Clear();
                foreach (uint id in _accretion)
                {
                    string role = null;
                    if (_first.Contains(id) == true) { role = "Chain1"; }
                    else if (_second.Contains(id) == true) { role = "Chain2"; }
                    if (role == null) { continue; }
                    Party.PartyMember pm = pty.GetByActorId(id);
                    if (pm != null && pm.GameObject != null)
                    {
                        ap.Assign(Signs.Roles[role], pm.GameObject);
                        _marked.Add(id);
                    }
                }
                _state.ClearAutoMarkers();
                _state.ExecuteAutomarkers(ap, Timing);
                _placed = true;
            }

            // Debug: stamp Accretion+First on a healer (-> Chain 1) and Accretion+Second on a
            // DPS (-> Chain 2), mirroring a real resolve. Falls back to the first two players.
            public void SimEarthquake()
            {
                if (Active == false)
                {
                    return;
                }
                Party pty = _state.GetPartyMembers();
                List<Party.PartyMember> members = (from m in pty.Members where m.GameObject != null orderby m.Index select m).ToList();
                if (members.Count == 0)
                {
                    return;
                }
                Reset();
                Party.PartyMember healer = members.FirstOrDefault(m => AutomarkerPrio.JobToRole(m.Job) == AutomarkerPrio.PrioRoleEnum.Healer);
                Party.PartyMember dps = members.FirstOrDefault(m =>
                {
                    AutomarkerPrio.PrioRoleEnum r = AutomarkerPrio.JobToRole(m.Job);
                    return r == AutomarkerPrio.PrioRoleEnum.Melee || r == AutomarkerPrio.PrioRoleEnum.Ranged || r == AutomarkerPrio.PrioRoleEnum.Caster;
                });
                if (healer == null || dps == null || healer == dps)
                {
                    healer = members[0];
                    dps = members.Count > 1 ? members[1] : members[0];
                }
                _accretion.Add((uint)healer.ObjectId);
                _first.Add((uint)healer.ObjectId);
                _accretion.Add((uint)dps.ObjectId);
                _second.Add((uint)dps.ObjectId);
                PlaceMarkers();
            }

        }

        #endregion

        #region KefkaSaysAM

        // "Kefka Says" spreads. All 8 players get Forked Lightning or Compressed Water; which
        // element is the threat is told by Neo Exdeath's real/fake indicator (status 0x808:
        // param 0x462 = real -> mark lightning, 0x461 = fake -> mark water), set per Grand Cross.
        // Spreads happen on the first two Grand Crosses only, in two separate sets ~15s apart.
        // Each set is marked when its debuffs land, using the indicator active at that moment
        // and the debuff's timer at application (>55s -> Ignore, else Chain), support = slot 1,
        // DPS = slot 2. This handles mixed / double-real / double-fake. Marks are added
        // progressively (the second set doesn't clear the first) and each clears when that
        // player's debuff expires.
        public class KefkaSaysAM : Automarker
        {

            [AttributeOrderNumber(1000)]
            public AutomarkerSigns Signs { get; set; }

            [AttributeOrderNumber(2000)]
            public AutomarkerPrio Prio { get; set; }

            public class SimulatorWidget : CustomPropertyInterface
            {

                private KefkaSaysAM _am;

                public SimulatorWidget(KefkaSaysAM am)
                {
                    _am = am;
                }

                public override string Serialize()
                {
                    return "";
                }

                public override void Deserialize(string data)
                {
                }

                public override void RenderEditor(string path)
                {
                    ImGui.TextWrapped(I18n.Translate(path));
                    if (ImGui.Button(I18n.Translate(path + "/KefkaSays") + "##simkefka") == true)
                    {
                        _am.SimKefkaSays();
                    }
                }

            }

            [DebugOption]
            [AttributeOrderNumber(2200)]
            public SimulatorWidget Simulate { get; set; }

            [DebugOption]
            [AttributeOrderNumber(2500)]
            public AutomarkerTiming Timing { get; set; }

            // The two element debuffs (land on all 8 players). The threat element is told by
            // the indicator below, not by the debuff itself.
            internal const uint StatusForkedLightning = 0x15A8;
            internal const uint StatusCompressedWater = 0x15A9;
            // Indicator on Neo Exdeath shown for each Grand Cross. param 0x462 = real (mark the
            // lightning players), 0x461 = fake (mark the water players). NOTE: other bosses
            // (e.g. Chaos) also carry status 0x808 with different params, so we only act on
            // these two specific values.
            internal const uint StatusRealFakeIndicator = 0x808;
            // Param/stacks values are HEX (ACT logs them in hex; e.g. Chaos shows "45F").
            // The packet delivers the raw ushort, so these MUST be 0x462/0x461 (1122/1121),
            // not decimal 462/461 -- otherwise SetRealFake never matches and every set
            // defaults to marking the water players.
            private const int ParamReal = 0x462;
            private const int ParamFake = 0x461;

            // >55s left when the debuff lands = Ignore (spread), otherwise Chain. Captured at
            // application because the timer counts down (a later read could misclassify).
            private const double IgnoreThresholdSeconds = 55.0;
            private const double StatusDebounceSeconds = 0.5;

            private static readonly string[] IgnoreRoles = new string[] { "Ignore1", "Ignore2" };
            private static readonly string[] ChainRoles = new string[] { "Chain1", "Chain2" };

            // Latest indicator (true = real -> lightning). _setReal locks it when a fresh set
            // of debuffs starts landing, so each progressive set uses its own Grand Cross.
            private bool _currentReal = false;
            private bool _setReal = false;
            // The current set's debuffs: actor -> element id, and actor -> remaining duration.
            private Dictionary<uint, uint> _pendElem = new Dictionary<uint, uint>();
            private Dictionary<uint, float> _pendDur = new Dictionary<uint, float>();
            private HashSet<uint> _marked = new HashSet<uint>();
            private bool _pending = false;
            private DateTime _lastStatus = DateTime.MinValue;

            public static bool IsMarkable(uint statusId)
            {
                return statusId == StatusForkedLightning || statusId == StatusCompressedWater;
            }

            public KefkaSaysAM(State state) : base(state)
            {
                Signs = new AutomarkerSigns();
                Signs.SetRole("Ignore1", AutomarkerSigns.SignEnum.Ignore1, false);
                Signs.SetRole("Ignore2", AutomarkerSigns.SignEnum.Ignore2, false);
                Signs.SetRole("Chain1", AutomarkerSigns.SignEnum.Bind1, false);
                Signs.SetRole("Chain2", AutomarkerSigns.SignEnum.Bind2, false);
                // Support (tank/healer) first, so it takes slot 1 and the DPS slot 2.
                Prio = new AutomarkerPrio();
                Prio.Priority = AutomarkerPrio.PrioTypeEnum.Role;
                Prio._prioByRole.Clear();
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Tank);
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Healer);
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Melee);
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Ranged);
                Prio._prioByRole.Add(AutomarkerPrio.PrioRoleEnum.Caster);
                Simulate = new SimulatorWidget(this);
                Timing = new AutomarkerTiming()
                {
                    TimingType = AutomarkerTiming.TimingTypeEnum.Explicit,
                    IniDelayMin = 0.1f,
                    IniDelayMax = 0.2f,
                    SubDelayMin = 0.1f,
                    SubDelayMax = 0.15f,
                };
            }

            public override void Reset()
            {
                _pendElem.Clear();
                _pendDur.Clear();
                _marked.Clear();
                _pending = false;
                _currentReal = false;
                _setReal = false;
                _state.ClearAutoMarkers();
            }

            // Neo Exdeath's real/fake indicator for the upcoming Grand Cross. Only the two
            // specific param values are this indicator; ignore any other 0x808 (e.g. Chaos's)
            // so it can't flip the real/fake state.
            public void SetRealFake(int param)
            {
                if (Active == false)
                {
                    return;
                }
                if (param == ParamReal)
                {
                    _currentReal = true;
                }
                else if (param == ParamFake)
                {
                    _currentReal = false;
                }
            }

            // The parent forwards gains/losses of the two element debuffs. We buffer one "set"
            // (a burst) and place it once it settles, using the indicator captured when the
            // set started landing.
            public void FeedStatus(uint actorId, uint statusId, bool gained, float duration)
            {
                if (Active == false)
                {
                    return;
                }
                if (gained == false)
                {
                    // Debuff expired/resolved -> clear just that player's mark.
                    if (_marked.Contains(actorId) == true)
                    {
                        ClearOne(actorId);
                    }
                    return;
                }
                if (statusId != StatusForkedLightning && statusId != StatusCompressedWater)
                {
                    return;
                }
                if (_pendElem.Count == 0)
                {
                    // New set -> lock in which element is the threat for it.
                    _setReal = _currentReal;
                }
                _pendElem[actorId] = statusId;
                _pendDur[actorId] = duration;
                _lastStatus = DateTime.Now;
                _pending = true;
            }

            private void ClearOne(uint actorId)
            {
                Party.PartyMember pm = _state.GetPartyMembers().GetByActorId(actorId);
                if (pm != null && pm.GameObject != null)
                {
                    _state.ClearMarkerOn(pm.GameObject, true, true);
                }
                _marked.Remove(actorId);
            }

            protected override bool ExecutionImplementation()
            {
                if (_pending == true && (DateTime.Now - _lastStatus).TotalSeconds >= StatusDebounceSeconds)
                {
                    _pending = false;
                    PlaceSet();
                }
                return true;
            }

            private void PlaceSet()
            {
                if (_pendElem.Count == 0)
                {
                    return;
                }
                // Real -> the lightning players of this set are the threat; fake -> the water
                // players. Their timer decides Ignore (long) vs Chain (short).
                uint markElem = _setReal == true ? StatusForkedLightning : StatusCompressedWater;
                List<uint> ignore = new List<uint>();
                List<uint> chain = new List<uint>();
                foreach (KeyValuePair<uint, uint> kp in _pendElem)
                {
                    if (kp.Value != markElem)
                    {
                        continue;
                    }
                    if (_pendDur.TryGetValue(kp.Key, out float dur) == true && dur > IgnoreThresholdSeconds)
                    {
                        ignore.Add(kp.Key);
                    }
                    else
                    {
                        chain.Add(kp.Key);
                    }
                }
                _pendElem.Clear();
                _pendDur.Clear();
                if (ignore.Count == 0 && chain.Count == 0)
                {
                    return;
                }
                Party pty = _state.GetPartyMembers();
                AutomarkerPayload ap = new AutomarkerPayload(_state, SelfMarkOnly, AsSoftmarker);
                // Progressive: add this set's marks without clearing the previous set's.
                AssignCategory(ap, pty, ignore, IgnoreRoles);
                AssignCategory(ap, pty, chain, ChainRoles);
                _state.ExecuteAutomarkers(ap, Timing);
            }

            private void AssignCategory(AutomarkerPayload ap, Party pty, List<uint> ids, string[] roles)
            {
                List<Party.PartyMember> members = pty.GetByActorIds(ids);
                Prio.SortByPriority(members);
                for (int i = 0; i < members.Count && i < roles.Length; i++)
                {
                    ap.Assign(Signs.Roles[roles[i]], members[i].GameObject);
                    _marked.Add((uint)members[i].ObjectId);
                }
            }

            // Debug: mark two supports and two DPS as if Kefka Says just went out -- one
            // support+DPS pair on a long timer (Ignore 1/2), the other pair short (Chain 1/2).
            public void SimKefkaSays()
            {
                if (Active == false)
                {
                    return;
                }
                Party pty = _state.GetPartyMembers();
                List<Party.PartyMember> members = (from m in pty.Members where m.GameObject != null orderby m.Index select m).ToList();
                if (members.Count == 0)
                {
                    return;
                }
                Reset();
                List<Party.PartyMember> supports = members.Where(m =>
                {
                    AutomarkerPrio.PrioRoleEnum r = AutomarkerPrio.JobToRole(m.Job);
                    return r == AutomarkerPrio.PrioRoleEnum.Tank || r == AutomarkerPrio.PrioRoleEnum.Healer;
                }).ToList();
                List<Party.PartyMember> dps = members.Where(m =>
                {
                    AutomarkerPrio.PrioRoleEnum r = AutomarkerPrio.JobToRole(m.Job);
                    return r == AutomarkerPrio.PrioRoleEnum.Melee || r == AutomarkerPrio.PrioRoleEnum.Ranged || r == AutomarkerPrio.PrioRoleEnum.Caster;
                }).ToList();
                List<uint> ignore = new List<uint>();
                List<uint> chain = new List<uint>();
                if (supports.Count >= 2 && dps.Count >= 2)
                {
                    ignore.Add((uint)supports[0].ObjectId);
                    ignore.Add((uint)dps[0].ObjectId);
                    chain.Add((uint)supports[1].ObjectId);
                    chain.Add((uint)dps[1].ObjectId);
                }
                else
                {
                    for (int i = 0; i < members.Count && i < 4; i++)
                    {
                        if (i < 2) { ignore.Add((uint)members[i].ObjectId); }
                        else { chain.Add((uint)members[i].ObjectId); }
                    }
                }
                AutomarkerPayload ap = new AutomarkerPayload(_state, SelfMarkOnly, AsSoftmarker);
                AssignCategory(ap, pty, ignore, IgnoreRoles);
                AssignCategory(ap, pty, chain, ChainRoles);
                _state.ExecuteAutomarkers(ap, Timing);
            }

        }

        #endregion

        #region KefkaSaysProgressiveAM

        // (P4) Kefka Says -- PROGRESSIVE variant. Instead of one spread resolve, it walks the
        // whole add-phase sequence and only ever shows what's needed for the *next* mechanic,
        // swapping as each step resolves. This is a SEPARATE automarker from "Kefka Says
        // spreads": disabled by default and toggled independently, so only one is ever active.
        // Ported from an upstream Lemegeton attempt; adapted to this fork (marking deferred to
        // the poll thread via a render queue, bounds-guarded, and the double-mark bug removed).
        public class KefkaSaysProgressiveAM : Automarker
        {

            // Neo Exdeath element spreads. Which one means "you spread" flips with the real/fake
            // indicator: real -> Forked Lightning holders spread; fake -> Compressed Water do.
            private const uint StatusForkedLightning = 5544;   // 0x15A8
            private const uint StatusCompressedWater = 5545;   // 0x15A9
            private const uint StatusCursedShriek = 5543;      // 0x15A7 (gaze: look toward / away)
            private const uint StatusDynamicFluid = 5548;      // 0x15AC (Chaos: real donut / fake tornado)
            private const uint StatusEntropy = 5547;           // 0x15AB (Chaos: real tornado / fake donut)
            private const uint StatusThunderCharged = 1485;    // 0x5CD  (tags the boss for donut/tornado)

            // Real/fake indicator (status 0x808). Routed by boss name (see FeedRealFake), so
            // these params only distinguish real vs fake within one boss. Neo Exdeath uses both
            // exact values (0x462/0x461, confirmed). Chaos only needs its confirmed fake value
            // (0x45F) -- anything else on Chaos is treated as real, so 0x460 isn't relied upon.
            public const uint StatusRealFakeIndicator = 2056; // 0x808
            private const int ExdeathReal = 1122;             // 0x462
            private const int ExdeathFake = 1121;             // 0x461
            private const int ChaosFake = 1119;               // 0x45F

            // Which forwarded statuses this AM consumes (indicator handled separately).
            public static bool Handles(uint statusId)
            {
                return statusId == StatusForkedLightning
                    || statusId == StatusCompressedWater
                    || statusId == StatusCursedShriek
                    || statusId == StatusDynamicFluid
                    || statusId == StatusEntropy
                    || statusId == StatusThunderCharged;
            }

            [AttributeOrderNumber(1000)]
            public AutomarkerSigns Signs1 { get; set; }   // spreads

            [AttributeOrderNumber(1100)]
            public AutomarkerSigns Signs2 { get; set; }   // gazes

            [AttributeOrderNumber(1200)]
            public AutomarkerSigns Signs3 { get; set; }   // boss donut / tornado

            [DebugOption]
            [AttributeOrderNumber(2500)]
            public AutomarkerTiming Timing { get; set; }

            private bool _exdeathReal = true;
            private bool _chaosReal = true;
            private bool _armed = false;
            private bool _placed = false;

            // Spread/gaze holders carry a resolve time so the early vs late set can pick the
            // right pair (ascending = this resolve, descending = the following one).
            private List<Tuple<IGameObject, DateTime>> _spreads = new List<Tuple<IGameObject, DateTime>>();
            private List<Tuple<IGameObject, DateTime, bool>> _gazes = new List<Tuple<IGameObject, DateTime, bool>>();

            // Gazes are marked when their Cursed Shriek debuff drops to this many seconds
            // remaining -- a few seconds ahead of the Thunder Charged / Blizzard III Blowout
            // casts that gate the boss donut, so players get the look call earlier. The casts
            // stay wired as a fallback; _gazesMarked keeps either path from double-placing.
            private const double GazeLeadSeconds = 8.0;
            private readonly HashSet<IGameObject> _gazesMarked = new HashSet<IGameObject>();
            private List<Tuple<bool, DateTime>> _fireWater = new List<Tuple<bool, DateTime>>();
            private IGameObject _kefker = null;

            // The set switch runs on the network thread; marking must happen on the poll thread,
            // so transitions are queued and drained in ExecutionImplementation (in order).
            private readonly object _renderLock = new object();
            private Queue<int> _renderQueue = new Queue<int>();

            private int _currentSet = 0;
            private int CurrentSet
            {
                get { return _currentSet; }
                set
                {
                    if (_currentSet != value)
                    {
                        _currentSet = value;
                        Log(State.LogLevelEnum.Debug, null, "[KefkaProgressive] set -> {0}", _currentSet);
                        lock (_renderLock) { _renderQueue.Enqueue(_currentSet); }
                    }
                }
            }

            public KefkaSaysProgressiveAM(State state) : base(state)
            {
                // Off by default so it never fights "Kefka Says spreads" -- the user turns on
                // whichever one they want.
                Enabled = false;
                Signs1 = new AutomarkerSigns();
                Signs2 = new AutomarkerSigns();
                Signs3 = new AutomarkerSigns();
                Signs1.SetRole("Forked1", AutomarkerSigns.SignEnum.Ignore1, false);
                Signs1.SetRole("Forked2", AutomarkerSigns.SignEnum.Ignore2, false);
                Signs2.SetRole("LookAt1", AutomarkerSigns.SignEnum.Bind1, false);
                Signs2.SetRole("LookAt2", AutomarkerSigns.SignEnum.Bind2, false);
                Signs2.SetRole("LookAway1", AutomarkerSigns.SignEnum.Ignore1, false);
                Signs2.SetRole("LookAway2", AutomarkerSigns.SignEnum.Ignore2, false);
                Signs3.SetRole("Donut", AutomarkerSigns.SignEnum.Circle, false);
                Signs3.SetRole("Tornado", AutomarkerSigns.SignEnum.Plus, false);
                Timing = new AutomarkerTiming()
                {
                    TimingType = AutomarkerTiming.TimingTypeEnum.Explicit,
                    IniDelayMin = 0.1f,
                    IniDelayMax = 0.2f,
                    SubDelayMin = 0.1f,
                    SubDelayMax = 0.15f,
                };
            }

            public override void Reset()
            {
                _armed = false;
                _exdeathReal = true;
                _chaosReal = true;
                _spreads.Clear();
                _gazes.Clear();
                _gazesMarked.Clear();
                _fireWater.Clear();
                _kefker = null;
                _currentSet = 0;
                lock (_renderLock) { _renderQueue.Clear(); }
                if (_placed == true)
                {
                    _placed = false;
                    _state.ClearAutoMarkers();
                }
            }

            // Kefka Says cast opens the phase: arm from a clean slate.
            public void Arm()
            {
                if (Active == false)
                {
                    return;
                }
                Reset();
                _armed = true;
                Log(State.LogLevelEnum.Debug, null, "[KefkaProgressive] armed");
            }

            // Real/fake indicator (0x808). Routed by boss name (like "Kefka Says spreads"), so
            // Kefka's own 0x808 can't corrupt either flag. Neo Exdeath keeps the proven exact-
            // param check (0x462/0x461); Chaos treats anything that isn't the confirmed fake
            // value (0x45F) as real, so it doesn't depend on 0x460 being exactly right.
            public void FeedRealFake(string bossName, int param)
            {
                if (Active == false)
                {
                    return;
                }
                if (bossName == "Neo Exdeath")
                {
                    if (param == ExdeathReal) { _exdeathReal = true; }
                    else if (param == ExdeathFake) { _exdeathReal = false; }
                }
                else if (bossName == "Chaos")
                {
                    _chaosReal = param != ChaosFake;
                }
            }

            public void FeedStatus(uint dest, uint statusId, float duration, bool gained)
            {
                if (Active == false || _armed == false)
                {
                    return;
                }
                IGameObject de = _state.GetActorById(dest);
                switch (statusId)
                {
                    case StatusForkedLightning:
                        if (gained == true)
                        {
                            if (_exdeathReal == true && de != null)
                            {
                                _spreads.Add(new Tuple<IGameObject, DateTime>(de, DateTime.Now.AddSeconds(duration)));
                            }
                        }
                        else if (_currentSet == 1 || _currentSet == 7)
                        {
                            AdvanceSet();
                        }
                        break;
                    case StatusCompressedWater:
                        if (gained == true && _exdeathReal == false && de != null)
                        {
                            _spreads.Add(new Tuple<IGameObject, DateTime>(de, DateTime.Now.AddSeconds(duration)));
                        }
                        break;
                    case StatusDynamicFluid:
                        if (gained == true)
                        {
                            _fireWater.Add(new Tuple<bool, DateTime>(_chaosReal, DateTime.Now.AddSeconds(duration)));
                        }
                        break;
                    case StatusEntropy:
                        if (gained == true)
                        {
                            _fireWater.Add(new Tuple<bool, DateTime>(_chaosReal == false, DateTime.Now.AddSeconds(duration)));
                        }
                        break;
                    case StatusCursedShriek:
                        if (gained == true)
                        {
                            if (de != null)
                            {
                                _gazes.Add(new Tuple<IGameObject, DateTime, bool>(de, DateTime.Now.AddSeconds(duration), _exdeathReal));
                            }
                        }
                        else if (_currentSet == 3 || _currentSet == 10)
                        {
                            AdvanceSet();
                        }
                        break;
                    case StatusThunderCharged:
                        if (gained == true)
                        {
                            _kefker = de;
                            AdvanceSet();
                        }
                        break;
                }
            }

            // A phase cast (Flood of Naught / Ultima Upsurge begin / Mana Release / Blizzard
            // Blowout) steps the sequence forward once.
            public void AdvanceOnCast()
            {
                if (Active == false || _armed == false)
                {
                    return;
                }
                AdvanceSet();
            }

            // Ultima Upsurge actually landing advances the mid-phase step (set 5 -> 6).
            public void AdvanceOnUpsurgeHit()
            {
                if (Active == false || _armed == false)
                {
                    return;
                }
                if (_currentSet == 5)
                {
                    AdvanceSet();
                }
            }

            private void AdvanceSet()
            {
                CurrentSet = _currentSet + 1;
            }

            protected override bool ExecutionImplementation()
            {
                CheckGazeTimers();
                List<int> todo = null;
                lock (_renderLock)
                {
                    if (_renderQueue.Count > 0)
                    {
                        todo = new List<int>(_renderQueue);
                        _renderQueue.Clear();
                    }
                }
                if (todo != null)
                {
                    foreach (int set in todo)
                    {
                        PerformMarking(set);
                    }
                }
                return true;
            }

            // Poll thread: place a gaze pair once its debuff drops to GazeLeadSeconds
            // remaining, ahead of the boss cast that would otherwise trigger it. Runs every
            // frame; PlaceGazes records the pair so the cast fallback won't place it again.
            private void CheckGazeTimers()
            {
                if (Active == false || _armed == false)
                {
                    return;
                }
                DateTime now = DateTime.Now;
                List<Tuple<IGameObject, DateTime, bool>> due =
                    (from ix in _gazes
                     let remaining = (ix.Item2 - now).TotalSeconds
                     where _gazesMarked.Contains(ix.Item1) == false
                        && remaining > 0
                        && remaining <= GazeLeadSeconds
                     orderby ix.Item2 ascending
                     select ix).Take(2).ToList();
                if (due.Count >= 2)
                {
                    PlaceGazes(due);
                }
            }

            // A resolve step (case 2/4/8/10) wipes every party marker globally. If a gaze was
            // placed early and hasn't gone off yet, forget that we marked it so CheckGazeTimers
            // re-places it next frame. The 1s margin keeps a gaze that just resolved (remaining
            // ~0) from being re-armed as a ghost.
            private void RearmPendingGazes()
            {
                DateTime now = DateTime.Now;
                _gazesMarked.RemoveWhere(go =>
                    _gazes.Any(g => ReferenceEquals(g.Item1, go) == true
                        && (g.Item2 - now).TotalSeconds > 1.0));
            }

            private void PerformMarking(int set)
            {
                Party pty = _state.GetPartyMembers();
                switch (set)
                {
                    case 1: // early spreads (Flood of Naught)
                        {
                            List<IGameObject> spreads = (from ix in _spreads orderby ix.Item2 ascending select ix.Item1).Take(2).ToList();
                            PlaceSpreads(pty, spreads);
                        }
                        break;
                    case 3: // early gaze (Thunder Charged)
                        {
                            List<Tuple<IGameObject, DateTime, bool>> gazes = (from ix in _gazes orderby ix.Item2 ascending select ix).Take(2).ToList();
                            PlaceGazes(gazes);
                        }
                        break;
                    case 5: // boss donut/tornado + late spreads (Ultima Upsurge begin)
                        {
                            AutomarkerPayload ap = new AutomarkerPayload(_state, SelfMarkOnly, AsSoftmarker);
                            List<Tuple<bool, DateTime>> fw = (from ix in _fireWater orderby ix.Item2 ascending select ix).Take(1).ToList();
                            if (_kefker != null && fw.Count >= 1)
                            {
                                ap.Assign(fw[0].Item1 == true ? Signs3.Roles["Donut"] : Signs3.Roles["Tornado"], _kefker);
                            }
                            List<IGameObject> spreads = (from ix in _spreads orderby ix.Item2 descending select ix.Item1).Take(2).ToList();
                            if (AssignSpreads(ap, spreads) == true || _kefker != null)
                            {
                                _state.ExecuteAutomarkers(ap, Timing);
                                _placed = true;
                            }
                        }
                        break;
                    case 7: // late spreads resolve -> drop only the boss marker
                        if (_kefker != null)
                        {
                            bool soft = _state.cfg.AutomarkerSoft == true || AsSoftmarker;
                            _state.ClearMarkerOn(_kefker, soft == false, soft);
                        }
                        break;
                    case 9: // late gaze (Blizzard III Blowout)
                        {
                            List<Tuple<IGameObject, DateTime, bool>> gazes = (from ix in _gazes orderby ix.Item2 descending select ix).Take(2).ToList();
                            PlaceGazes(gazes);
                        }
                        break;
                    case 11: // late boss donut/tornado (Cursed Shriek resolve)
                        if (_kefker != null)
                        {
                            List<Tuple<bool, DateTime>> fw = (from ix in _fireWater orderby ix.Item2 descending select ix).Take(1).ToList();
                            if (fw.Count >= 1)
                            {
                                AutomarkerPayload ap = new AutomarkerPayload(_state, SelfMarkOnly, AsSoftmarker);
                                ap.Assign(fw[0].Item1 == true ? Signs3.Roles["Donut"] : Signs3.Roles["Tornado"], _kefker);
                                _state.ExecuteAutomarkers(ap, Timing);
                                _placed = true;
                            }
                        }
                        break;
                    case 12: // late firewater resolve -> drop boss marker
                        if (_kefker != null)
                        {
                            bool soft = _state.cfg.AutomarkerSoft == true || AsSoftmarker;
                            _state.ClearMarkerOn(_kefker, soft == false, soft);
                        }
                        break;
                    case 2:
                    case 4:
                    case 8:
                    case 10: // resolve steps -> clear for the next mechanic
                        if (_placed == true)
                        {
                            _placed = false;
                            _state.ClearAutoMarkers();
                            // The clear above is global; bring back any gaze still live so this
                            // step can't wipe a gaze that hasn't resolved yet.
                            RearmPendingGazes();
                        }
                        break;
                    // case 6: Ultima Upsurge hit -- nothing to display.
                }
            }

            private void PlaceSpreads(Party pty, List<IGameObject> spreads)
            {
                AutomarkerPayload ap = new AutomarkerPayload(_state, SelfMarkOnly, AsSoftmarker);
                if (AssignSpreads(ap, spreads) == false)
                {
                    return;
                }
                _state.ExecuteAutomarkers(ap, Timing);
                _placed = true;
            }

            private bool AssignSpreads(AutomarkerPayload ap, List<IGameObject> spreads)
            {
                if (spreads.Count < 2)
                {
                    Log(State.LogLevelEnum.Debug, null, "[KefkaProgressive] only {0} spread(s) known -- skipping", spreads.Count);
                    return false;
                }
                ap.Assign(Signs1.Roles["Forked1"], spreads[0]);
                ap.Assign(Signs1.Roles["Forked2"], spreads[1]);
                return true;
            }

            private void PlaceGazes(List<Tuple<IGameObject, DateTime, bool>> gazes)
            {
                if (gazes.Count < 2)
                {
                    Log(State.LogLevelEnum.Debug, null, "[KefkaProgressive] only {0} gaze(s) known -- skipping", gazes.Count);
                    return;
                }
                // The 8s poll timer and the boss-cast fallback can both reach this for the same
                // pair -- only place it once.
                if (gazes.All(g => _gazesMarked.Contains(g.Item1) == true))
                {
                    return;
                }
                AutomarkerPayload ap = new AutomarkerPayload(_state, SelfMarkOnly, AsSoftmarker);
                // Each gaze carries its own real/fake flag (Item3). A real gaze applies the gaze
                // effect, so look away from it; a fake one is safe, so look at it. Mark them
                // individually -- within a resolution one is real and one is fake.
                int lookAt = 0;
                int lookAway = 0;
                foreach (Tuple<IGameObject, DateTime, bool> gaze in gazes)
                {
                    if (gaze.Item3 == true)
                    {
                        lookAway++;
                        ap.Assign(Signs2.Roles["LookAway" + lookAway], gaze.Item1);
                    }
                    else
                    {
                        lookAt++;
                        ap.Assign(Signs2.Roles["LookAt" + lookAt], gaze.Item1);
                    }
                    _gazesMarked.Add(gaze.Item1);
                }
                _state.ExecuteAutomarkers(ap, Timing);
                _placed = true;
            }

        }

        #endregion

        public UltDancingMad(State st) : base(st)
        {
            st.OnZoneChange += OnZoneChange;
        }

        protected override bool ExecutionImplementation()
        {
            // Run the items' per-frame logic always (not only in the Dancing Mad zone) so
            // the debug simulator works in any duty. The automarker is inert unless armed
            // (by a real Forsaken cast in-zone, or by the simulator), so this is cheap.
            return base.ExecutionImplementation();
        }

        private void SubscribeToEvents()
        {
            lock (this)
            {
                if (_subbed == true)
                {
                    return;
                }
                _subbed = true;
                Log(LogLevelEnum.Debug, null, "[Forsaken] subscribing to live events");
                _state.OnCastBegin += OnCastBegin;
                _state.OnAction += OnAction;
                _state.OnHeadMarker += OnHeadMarker;
                _state.OnStatusChange += OnStatusChange;
            }
        }

        private void UnsubscribeFromEvents()
        {
            lock (this)
            {
                if (_subbed == false)
                {
                    return;
                }
                Log(LogLevelEnum.Debug, null, "Unsubscribing from events");
                _state.OnHeadMarker -= OnHeadMarker;
                _state.OnStatusChange -= OnStatusChange;
                _state.OnAction -= OnAction;
                _state.OnCastBegin -= OnCastBegin;
                _subbed = false;
            }
        }

        private void OnCastBegin(uint src, uint dest, ushort actionId, float castTime, float rotation)
        {
            if (actionId == AbilityForsaken)
            {
                Log(LogLevelEnum.Debug, null, "[Forsaken] cast detected -> arming automarker");
                _forsakenAm.Arm();
            }
            // Progressive Kefka Says: Kefka Says opens the phase, the rest step the sequence.
            switch (actionId)
            {
                case AbilityKefkaSays:
                    Log(LogLevelEnum.Debug, null, "[KefkaProgressive] Kefka Says -> arming");
                    _kefkaSaysProgressiveAm.Arm();
                    break;
                case AbilityFloodOfNaught1:
                case AbilityFloodOfNaught2:
                case AbilityFloodOfNaught3:
                case AbilityFloodOfNaught4:
                case AbilityUltimaUpsurge:
                case AbilityManaRelease:
                case AbilityBlizzardBlowout:
                    _kefkaSaysProgressiveAm.AdvanceOnCast();
                    break;
            }
        }

        private void OnAction(uint src, uint dest, ushort actionId)
        {
            if (actionId == AbilityPathOfLight)
            {
                Log(LogLevelEnum.Debug, null, "[Forsaken] tower (Path of Light) detected");
                _forsakenAm.FeedTower();
            }
            if (actionId == AbilityUltimaUpsurge)
            {
                _kefkaSaysProgressiveAm.AdvanceOnUpsurgeHit();
            }
        }

        private void OnHeadMarker(uint dest, uint markerId)
        {
            _forsakenAm.FeedHeadmarker(dest, markerId);
        }

        private void OnStatusChange(uint src, uint dest, uint statusId, bool gained, float duration, int stacks)
        {
            if (statusId == EarthquakeAM.StatusAccretion
                || statusId == EarthquakeAM.StatusFirstInLine
                || statusId == EarthquakeAM.StatusSecondInLine)
            {
                Log(LogLevelEnum.Info, null, "[Earthquake] status 0x{0:X} {1} on actor 0x{2:X}", statusId, gained == true ? "gained" : "lost", dest);
                _earthquakeAm.FeedStatus(dest, statusId, gained);
            }
            if (statusId == KefkaSaysAM.StatusRealFakeIndicator && dest >= 0x40000000)
            {
                // Kefka, Neo Exdeath and Chaos all carry 0x808, but only Neo Exdeath's
                // indicator drives the Grand Cross spreads -- match it by name (SetRealFake
                // also guards on the 461/462 values as a backstop).
                var go = _state.GetActorById(dest);
                string nm = go != null ? go.Name.ToString() : "";
                Log(LogLevelEnum.Info, null, "[KefkaSays] indicator param={0} {1} on '{2}' (0x{3:X})", stacks, gained == true ? "on" : "off", nm, dest);
                if (gained == true && nm == "Neo Exdeath")
                {
                    _kefkaSaysAm.SetRealFake(stacks);
                }
            }
            if (KefkaSaysAM.IsMarkable(statusId) == true)
            {
                Log(LogLevelEnum.Info, null, "[KefkaSays] debuff 0x{0:X} {1} ({2:0}s) on actor 0x{3:X}", statusId, gained == true ? "gained" : "lost", duration, dest);
                _kefkaSaysAm.FeedStatus(dest, statusId, gained, duration);
            }
            // Progressive Kefka Says (separate, user-toggled AM). Route the indicator by boss
            // name (Neo Exdeath / Chaos) so Kefka's own 0x808 can't flip the real/fake state.
            if (statusId == KefkaSaysProgressiveAM.StatusRealFakeIndicator && dest >= 0x40000000 && gained == true)
            {
                var indicatorGo = _state.GetActorById(dest);
                string indicatorName = indicatorGo != null ? indicatorGo.Name.ToString() : "";
                Log(LogLevelEnum.Info, null, "[KefkaProgressive] indicator param={0} (0x{0:X}) on '{1}'", stacks, indicatorName);
                _kefkaSaysProgressiveAm.FeedRealFake(indicatorName, stacks);
            }
            if (KefkaSaysProgressiveAM.Handles(statusId) == true)
            {
                _kefkaSaysProgressiveAm.FeedStatus(dest, statusId, duration, gained);
            }
        }

        private void OnCombatChange(bool inCombat)
        {
            Reset();
            if (inCombat == true)
            {
                SubscribeToEvents();
            }
            else
            {
                UnsubscribeFromEvents();
            }
        }

        private void OnZoneChange(uint newZone)
        {
            bool newZoneOk = (newZone == TerritoryDancingMad);
            if (newZoneOk == true && ZoneOk == false)
            {
                Log(State.LogLevelEnum.Info, null, "Content available");
                _forsakenAm = (ForsakenAM)Items["ForsakenAM"];
                _earthquakeAm = (EarthquakeAM)Items["EarthquakeAM"];
                _kefkaSaysAm = (KefkaSaysAM)Items["KefkaSaysAM"];
                _kefkaSaysProgressiveAm = (KefkaSaysProgressiveAM)Items["KefkaSaysProgressiveAM"];
                _state.OnCombatChange += OnCombatChange;
                LogItems();
            }
            else if (newZoneOk == false && ZoneOk == true)
            {
                Log(State.LogLevelEnum.Info, null, "Content unavailable");
                _state.OnCombatChange -= OnCombatChange;
            }
            ZoneOk = newZoneOk;
        }

    }

}
