#region Copyright & License Information
/*
 * Copyright 2007-2018 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Mods.RA2.Activities;

namespace OpenRA.Mods.RA2.Traits
{
    [Desc("This actor can transport Garrisoner actors.")]
    public class GarrisonInfo : ITraitInfo, Requires<IOccupySpaceInfo>
    {
        [Desc("The maximum sum of Garrisoner.Weight that this actor can support.")]
        public readonly int MaxWeight = 0;

        [Desc("Number of pips to display when this actor is selected.")]
        public readonly int PipCount = 0;

        //TODO - darky - Refactor - Do we need types? 
        //darky - Could this be where we can add vehicle garrisons, for the TankBunker?
        [Desc("`Garrisoner.GarrisonType`s that can be loaded into this actor.")]
        public readonly HashSet<string> Types = new HashSet<string>();

        [Desc("A list of actor types that are initially spawned into this actor.")]
        public readonly string[] InitialUnits = { };

        [Desc("When this actor is sold should all of its garrisoners be unloaded?")]
        public readonly bool EjectOnSell = true;

        [Desc("When this actor dies should all of its garrisoners be unloaded?")]
        public readonly bool EjectOnDeath = false;

        [Desc("Terrain types that this actor is allowed to eject actors onto. Leave empty for all terrain types.")]
        public readonly HashSet<string> UnloadTerrainTypes = new HashSet<string>();

        [Desc("Voice to play when ordered to unload the garrisoners.")]
        [VoiceReference] public readonly string UnloadVoice = "Action";

        //darky - Needs Attention - Rename to GarrisonerFacing
        [Desc("Which direction the garrisoner will face (relative to the transport) when unloading.")]
        public readonly int GarrisonerFacing = 128;

        [Desc("Cursor to display when able to unload the garrisoners.")]
        public readonly string UnloadCursor = "deploy";

        [Desc("Cursor to display when unable to unload the garrisoners.")]
        public readonly string UnloadBlockedCursor = "deploy-blocked";

        //darky - Needs Attention - How is loading condition used?
        [GrantedConditionReference]
        [Desc("The condition to grant to self while waiting for garrison to load.")]
        public readonly string LoadingCondition = null;

        //darky - Needs Attention - How is loaded condition used? 
        [GrantedConditionReference]
        [Desc("The condition to grant to self while garrisoners are loaded.",
            "Condition can stack with multiple garrisoners.")]
        public readonly string LoadedCondition = null;


        [Desc("Conditions to grant when specified actors are loaded inside the transport.",
            "A dictionary of [actor id]: [condition].")]
        public readonly Dictionary<string, string> GarrisonerConditions = new Dictionary<string, string>();

        //darky - Needs attention - What is a linter. How does it work. What are my responsibilities here?
        [GrantedConditionReference]
        public IEnumerable<string> LinterGarrisonerConditions { get { return GarrisonerConditions.Values; } }

        public object Create(ActorInitializer init) { return new Garrison(init, this); }
    }

    public class Garrison : IPips, IIssueOrder, IResolveOrder, IOrderVoice, INotifyCreated, INotifyKilled,
        INotifyOwnerChanged, INotifyAddedToWorld, ITick, INotifySold, INotifyActorDisposing, IIssueDeployOrder,
        ITransformActorInitModifier
    {
        public readonly GarrisonInfo Info;
        readonly Actor self;
        readonly Stack<Actor> garrison = new Stack<Actor>();
        readonly HashSet<Actor> reserves = new HashSet<Actor>();
        readonly Dictionary<string, Stack<int>> garrisonerTokens = new Dictionary<string, Stack<int>>();
        readonly Lazy<IFacing> facing;
        readonly bool checkTerrainType;

        int totalWeight = 0;
        int reservedWeight = 0;
        // TODO - darky - Refactor - Remove Aircraft
        Aircraft aircraft;
        ConditionManager conditionManager;
        int loadingToken = ConditionManager.InvalidConditionToken;
        Stack<int> loadedTokens = new Stack<int>();

        CPos currentCell;
        public IEnumerable<CPos> CurrentAdjacentCells { get; private set; }
        public bool Unloading { get; internal set; }
        public IEnumerable<Actor> Garrisoners { get { return garrison; } }
        public int GarrisonerCount { get { return garrison.Count; } }

        public Garrison(ActorInitializer init, GarrisonInfo info)
        {
            self = init.Self;
            Info = info;
            Unloading = false;
            checkTerrainType = info.UnloadTerrainTypes.Count > 0;

            if (init.Contains<RuntimeGarrisonInit>())
            {
                garrison = new Stack<Actor>(init.Get<RuntimeGarrisonInit, Actor[]>());
                totalWeight = garrison.Sum(c => GetWeight(c));
            }
            else if (init.Contains<GarrisonInit>())
            {
                foreach (var u in init.Get<GarrisonInit, string[]>())
                {
                    var unit = self.World.CreateActor(false, u.ToLowerInvariant(),
                        new TypeDictionary { new OwnerInit(self.Owner) });

                    garrison.Push(unit);
                }

                totalWeight = garrison.Sum(c => GetWeight(c));
            }
            else
            {
                foreach (var u in info.InitialUnits)
                {
                    var unit = self.World.CreateActor(false, u.ToLowerInvariant(),
                        new TypeDictionary { new OwnerInit(self.Owner) });

                    garrison.Push(unit);
                }

                totalWeight = garrison.Sum(c => GetWeight(c));
            }

            facing = Exts.Lazy(self.TraitOrDefault<IFacing>);
        }

        void INotifyCreated.Created(Actor self)
        {
            aircraft = self.TraitOrDefault<Aircraft>();
            conditionManager = self.TraitOrDefault<ConditionManager>();

            if (conditionManager != null && garrison.Any())
            {
                foreach (var c in garrison)
                {
                    string garrisonerCondition;
                    if (Info.GarrisonerConditions.TryGetValue(c.Info.Name, out garrisonerCondition))
                        garrisonerTokens.GetOrAdd(c.Info.Name).Push(conditionManager.GrantCondition(self, garrisonerCondition));
                }

                if (!string.IsNullOrEmpty(Info.LoadedCondition))
                    loadedTokens.Push(conditionManager.GrantCondition(self, Info.LoadedCondition));
            }
        }

        static int GetWeight(Actor a) { return a.Info.TraitInfo<GarrisonerInfo>().Weight; }

        public IEnumerable<IOrderTargeter> Orders
        {
            get
            {
                yield return new DeployOrderTargeter("Unload", 10,
              () => CanUnload() ? Info.UnloadCursor : Info.UnloadBlockedCursor);
            }
        }

        public Order IssueOrder(Actor self, IOrderTargeter order, Target target, bool queued)
        {
            if (order.OrderID == "Unload")
                return new Order(order.OrderID, self, queued);

            return null;
        }

        Order IIssueDeployOrder.IssueDeployOrder(Actor self, bool queued)
        {
            return new Order("Unload", self, queued);
        }

        bool IIssueDeployOrder.CanIssueDeployOrder(Actor self) { return true; }

        public void ResolveOrder(Actor self, Order order)
        {
            if (order.OrderString == "Unload")
            {
                if (!CanUnload())
                    return;

                Unloading = true;
                self.CancelActivity();
                // darky - Needs Attention - Probably don't need aircraft here.
                if (aircraft != null)
                    self.QueueActivity(new HeliLand(self, true));
                self.QueueActivity(new UnloadGarrison(self, true));
            }
        }

        IEnumerable<CPos> GetAdjacentCells()
        {
            return Util.AdjacentCells(self.World, Target.FromActor(self)).Where(c => self.Location != c);
        }

        bool CanUnload()
        {
            if (checkTerrainType)
            {
                var terrainType = self.World.Map.GetTerrainInfo(self.Location).Type;

                if (!Info.UnloadTerrainTypes.Contains(terrainType))
                    return false;
            }
            // TODO - darky - Investigate - Remove Aircraft
            return !IsEmpty(self) && (aircraft == null || aircraft.CanLand(self.Location))
                && CurrentAdjacentCells != null && CurrentAdjacentCells.Any(c => Garrisoners.Any(p => p.Trait<IPositionable>().CanEnterCell(c)));
        }

        public bool CanLoad(Actor self, Actor a)
        {
            return (reserves.Contains(a) || HasSpace(GetWeight(a))) && self.IsAtGroundLevel();
        }

        internal bool ReserveSpace(Actor a)
        {
            if (reserves.Contains(a))
                return true;

            var w = GetWeight(a);
            if (!HasSpace(w))
                return false;

            if (conditionManager != null && loadingToken == ConditionManager.InvalidConditionToken && !string.IsNullOrEmpty(Info.LoadingCondition))
                loadingToken = conditionManager.GrantCondition(self, Info.LoadingCondition);

            reserves.Add(a);
            reservedWeight += w;

            return true;
        }

        internal void UnreserveSpace(Actor a)
        {
            if (!reserves.Contains(a))
                return;

            reservedWeight -= GetWeight(a);
            reserves.Remove(a);

            if (loadingToken != ConditionManager.InvalidConditionToken)
                loadingToken = conditionManager.RevokeCondition(self, loadingToken);
        }

        public string CursorForOrder(Actor self, Order order)
        {
            if (order.OrderString != "Unload")
                return null;

            return CanUnload() ? Info.UnloadCursor : Info.UnloadBlockedCursor;
        }

        public string VoicePhraseForOrder(Actor self, Order order)
        {
            if (order.OrderString != "Unload" || IsEmpty(self) || !self.HasVoice(Info.UnloadVoice))
                return null;

            return Info.UnloadVoice;
        }

        public bool HasSpace(int weight) { return totalWeight + reservedWeight + weight <= Info.MaxWeight; }
        public bool IsEmpty(Actor self) { return garrison.Count == 0; }

        public Actor Peek(Actor self) { return garrison.Peek(); }

        public Actor Unload(Actor self)
        {
            var a = garrison.Pop();

            totalWeight -= GetWeight(a);

            SetGarrisonerFacing(a);

            foreach (var npe in self.TraitsImplementing<INotifyGarrisonerExited>())
                npe.OnGarrisonerExited(self, a);

            foreach (var nec in a.TraitsImplementing<INotifyExitedGarrison>())
                nec.OnExitedGarrison(a, self);

            var p = a.Trait<Garrisoner>();
            p.Transport = null;

            Stack<int> garrisonerToken;
            if (garrisonerTokens.TryGetValue(a.Info.Name, out garrisonerToken) && garrisonerToken.Any())
                conditionManager.RevokeCondition(self, garrisonerToken.Pop());

            if (loadedTokens.Any())
                conditionManager.RevokeCondition(self, loadedTokens.Pop());

            return a;
        }
        //TODO - darky - Refactor SetGarrisonerFacing -> SetGarrisonerFacing
        void SetGarrisonerFacing(Actor garrisoner)
        {
            if (facing.Value == null)
                return;

            var garrisonerFacing = garrisoner.TraitOrDefault<IFacing>();
            if (garrisonerFacing != null)
                garrisonerFacing.Facing = facing.Value.Facing + Info.GarrisonerFacing;

            foreach (var t in garrisoner.TraitsImplementing<Turreted>())
                t.TurretFacing = facing.Value.Facing + Info.GarrisonerFacing;
        }

        public IEnumerable<PipType> GetPips(Actor self)
        {
            var numPips = Info.PipCount;

            for (var i = 0; i < numPips; i++)
                yield return GetPipAt(i);
        }

        PipType GetPipAt(int i)
        {
            var n = i * Info.MaxWeight / Info.PipCount;

            foreach (var c in garrison)
            {
                var pi = c.Info.TraitInfo<GarrisonerInfo>();
                if (n < pi.Weight)
                    return pi.PipType;
                else
                    n -= pi.Weight;
            }

            return PipType.Transparent;
        }

        public void Load(Actor self, Actor a)
        {
            garrison.Push(a);
            var w = GetWeight(a);
            totalWeight += w;
            if (reserves.Contains(a))
            {
                reservedWeight -= w;
                reserves.Remove(a);

                if (loadingToken != ConditionManager.InvalidConditionToken)
                    loadingToken = conditionManager.RevokeCondition(self, loadingToken);
            }

            // If not initialized then this will be notified in the first tick
            if (initialized)
            {
                foreach (var npe in self.TraitsImplementing<INotifyGarrisonerEntered>())
                    npe.OnGarrisonerEntered(self, a);

                foreach (var nec in a.TraitsImplementing<INotifyEnteredGarrison>())
                    nec.OnEnteredGarrison(a, self);
            }

            var p = a.Trait<Garrisoner>();
            p.Transport = self;

            string garrisonerCondition;
            if (conditionManager != null && Info.GarrisonerConditions.TryGetValue(a.Info.Name, out garrisonerCondition))
                garrisonerTokens.GetOrAdd(a.Info.Name).Push(conditionManager.GrantCondition(self, garrisonerCondition));

            if (conditionManager != null && !string.IsNullOrEmpty(Info.LoadedCondition))
                loadedTokens.Push(conditionManager.GrantCondition(self, Info.LoadedCondition));
        }

        void INotifyKilled.Killed(Actor self, AttackInfo e)
        {
            if (Info.EjectOnDeath)
                while (!IsEmpty(self) && CanUnload())
                {
                    var garrisoner = Unload(self);
                    var cp = self.CenterPosition;
                    var inAir = self.World.Map.DistanceAboveTerrain(cp).Length != 0;
                    var positionable = garrisoner.Trait<IPositionable>();
                    positionable.SetPosition(garrisoner, self.Location);

                    if (!inAir && positionable.CanEnterCell(self.Location, self, false))
                    {
                        self.World.AddFrameEndTask(w => w.Add(garrisoner));
                        var nbms = garrisoner.TraitsImplementing<INotifyBlockingMove>();
                        foreach (var nbm in nbms)
                            nbm.OnNotifyBlockingMove(garrisoner, garrisoner);
                    }
                    else
                        garrisoner.Kill(e.Attacker);
                }

            foreach (var c in garrison)
                c.Kill(e.Attacker);

            garrison.Clear();
        }

        void INotifyActorDisposing.Disposing(Actor self)
        {
            foreach (var c in garrison)
                c.Dispose();

            garrison.Clear();
        }

        void INotifySold.Selling(Actor self) { }
        // TODO - darky - Refactor INotifySold.Selling
        void INotifySold.Sold(Actor self)
        {
            if (!Info.EjectOnSell || garrison == null)
                return;

            while (!IsEmpty(self))
                SpawnGarrisoner(Unload(self));
        }

        void SpawnGarrisoner(Actor garrisoner)
        {
            self.World.AddFrameEndTask(w =>
            {
                w.Add(garrisoner);
                garrisoner.Trait<IPositionable>().SetPosition(garrisoner, self.Location);

                // TODO: this won't work well for >1 actor as they should move towards the next enterable (sub) cell instead
            });
        }

        void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
        {
            if (garrison == null)
                return;
            // TODO - darky - Refactor - Fix: there are no garrisoners.
            foreach (var p in Garrisoners)
                p.ChangeOwner(newOwner);
        }

        void INotifyAddedToWorld.AddedToWorld(Actor self)
        {
            // Force location update to avoid issues when initial spawn is outside map
            currentCell = self.Location;
            CurrentAdjacentCells = GetAdjacentCells();
        }

        bool initialized;
        void ITick.Tick(Actor self)
        {
            // Notify initial garrison load
            if (!initialized)
            {
                foreach (var c in garrison)
                {
                    // TODO - darky - Refactor ITick.Tick - Possible Crash
                    c.Trait<Garrisoner>().Transport = self;

                    foreach (var npe in self.TraitsImplementing<INotifyGarrisonerEntered>())
                        npe.OnGarrisonerEntered(self, c);

                    foreach (var nec in c.TraitsImplementing<INotifyEnteredGarrison>())
                        nec.OnEnteredGarrison(c, self);
                }

                initialized = true;
            }

            var cell = self.World.Map.CellContaining(self.CenterPosition);
            if (currentCell != cell)
            {
                currentCell = cell;
                CurrentAdjacentCells = GetAdjacentCells();
            }
        }

        void ITransformActorInitModifier.ModifyTransformActorInit(Actor self, TypeDictionary init)
        {
            init.Add(new RuntimeGarrisonInit(Garrisoners.ToArray()));
        }
    }

    public class RuntimeGarrisonInit : IActorInit<Actor[]>, ISuppressInitExport
    {
        [FieldFromYamlKey]
        readonly Actor[] value = { };
        public RuntimeGarrisonInit() { }
        public RuntimeGarrisonInit(Actor[] init) { value = init; }
        public Actor[] Value(World world) { return value; }
    }

    public class GarrisonInit : IActorInit<string[]>
    {
        [FieldFromYamlKey]
        readonly string[] value = { };
        public GarrisonInit() { }
        public GarrisonInit(string[] init) { value = init; }
        public string[] Value(World world) { return value; }
    }
}
