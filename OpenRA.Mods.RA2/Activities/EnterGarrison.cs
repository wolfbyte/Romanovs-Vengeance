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
using System.Diagnostics;
namespace OpenRA.Mods.RA2.Activities
{
    class EnterGarrison : Enter
    {
        readonly Garrisoner garrisoner;
        Target garrisonableBuilding;
        Actor enterActor;
        Garrison enterGarrison;

        public EnterGarrison(Actor self, Target garrisonableBuilding)
            : base(self, garrisonableBuilding)
        {
            this.garrisonableBuilding = garrisonableBuilding;
            garrisoner = self.TraitsImplementing<Garrisoner>().Single();
        }

        // self == the garrisoner (e1)
        // TargetActor == garrisonable building CaWash
        protected override bool TryStartEnter(Actor self, Actor targetActor)
        {
            enterActor = targetActor;
            enterGarrison = targetActor.TraitOrDefault<Garrison>();

            // Make sure we can still enter the transport
            // (but not before, because this may stop the actor in the middle of nowhere)
            if (enterGarrison== null || !garrisoner.Reserve(self, enterGarrison))
            {
                Cancel(self, true);
                return false;
            }

            return true;
        }
        protected override void OnCancel(Actor self) {
            Debug.WriteLine("Fuck");
        }
        protected override void OnEnterComplete(Actor self, Actor targetActor)
        {
            self.World.AddFrameEndTask(w =>
            {
                // Make sure the target hasn't changed while entering
                // OnEnterComplete is only called if targetActor is alive
                if (targetActor != enterActor)
                    return;

                if (!enterGarrison.CanLoad(enterActor, self))
                    return;

                enterGarrison.Load(enterActor, self);
                w.Remove(self);

                // Preemptively cancel any activities to avoid an edge-case where successively queued
                // EnterTransports corrupt the actor state. Activities are cancelled again on unload
                self.CancelActivity();
            });
        }

    }
}
