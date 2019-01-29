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
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.RA2.Traits;
namespace OpenRA.Mods.RA2.Activities
{
    class EnterGarrison: Enter
    {
        readonly Garrisoner garrisoner;
        readonly int maxTries;
        Actor garrisonableBuilding;
        Garrison garrison;

        public EnterGarrison(Actor self, Actor garrisonableBuilding, int maxTries = 0, bool repathWhileMoving = true)
            : base(self, garrisonableBuilding, EnterBehaviour.Exit, maxTries, repathWhileMoving)
        {
            this.garrisonableBuilding = garrisonableBuilding;
            this.maxTries = maxTries;
            garrison = garrisonableBuilding.Trait<Garrison>();
            garrisoner = self.TraitsImplementing<Garrisoner>().Single();
           // garrisoner = self.TraitsImplementing<Garrisoner>().Single(p => garrison.Info.Types.Contains(p.Info.GarrisonType));
        }

        protected override void Unreserve(Actor self, bool abort) { garrisoner.Unreserve(self); }
        protected override bool CanReserve(Actor self) { return garrison.Unloading || garrison.CanLoad(garrisonableBuilding, self); }
        protected override ReserveStatus Reserve(Actor self)
        {
            var status = base.Reserve(self);
            if (status != ReserveStatus.Ready)
                return status;
            if (garrisoner.Reserve(self, garrison))
                return ReserveStatus.Ready;
            return ReserveStatus.Pending;
        }

        protected override void OnInside(Actor self)
        {
            self.World.AddFrameEndTask(w =>
            {
                if (self.IsDead || garrisonableBuilding.IsDead || !garrison.CanLoad(garrisonableBuilding, self))
                    return;

                garrison.Load(garrisonableBuilding, self);
                w.Remove(self);
            });

            Done(self);

            // Preemptively cancel any activities to avoid an edge-case where successively queued
            // EnterTransports corrupt the actor state. Activities are cancelled again on unload
            self.CancelActivity();
        }

        protected override bool TryGetAlternateTarget(Actor self, int tries, ref Target target)
        {
            if (tries > maxTries)
                return false;
            var type = target.Actor.Info.Name;
            return TryGetAlternateTargetInCircle(
                self, garrisoner.Info.AlternateTransportScanRange,
                t => { garrisonableBuilding = t.Actor; garrison = t.Actor.Trait<Garrison>(); }, // update garrisonableBuilding and garrison
                a => { var c = a.TraitOrDefault<Garrison>(); return c != null && c.Info.Types.Contains(garrisoner.Info.GarrisonType) && (c.Unloading || c.CanLoad(a, self)); },
                new Func<Actor, bool>[] { a => a.Info.Name == type }); // Prefer garrisonableBuildings of the same type
        }
    }
}
