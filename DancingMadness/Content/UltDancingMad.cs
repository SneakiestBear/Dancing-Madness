using DancingMadness.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
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

        private bool ZoneOk = false;
        private bool _subbed = false;

        private ForsakenAM _forsakenAm;

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
        }

        private void OnAction(uint src, uint dest, ushort actionId)
        {
            if (actionId == AbilityPathOfLight)
            {
                Log(LogLevelEnum.Debug, null, "[Forsaken] tower (Path of Light) detected");
                _forsakenAm.FeedTower();
            }
        }

        private void OnHeadMarker(uint dest, uint markerId)
        {
            _forsakenAm.FeedHeadmarker(dest, markerId);
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
