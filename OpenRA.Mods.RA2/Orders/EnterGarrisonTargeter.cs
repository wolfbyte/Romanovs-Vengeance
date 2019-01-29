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
using OpenRA.Traits;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.RA2.Traits;

namespace OpenRA.Mods.RA2.Orders
{
    public class EnterGarrisonTargeter<T> : UnitOrderTargeter where T : ITraitInfo
    {
        readonly OpenRA.Mods.RA2.Traits.AlternateTransportsMode mode;
        readonly Func<Actor, Actor, bool> canTarget;
        readonly Func<Actor, Actor, bool> useEnterCursor;

        public EnterGarrisonTargeter(string order, int priority,
            Func<Actor, Actor, bool> canTarget, Func<Actor, Actor, bool> useEnterCursor, OpenRA.Mods.RA2.Traits.AlternateTransportsMode mode)
            : base(order, 7, "enter", true, true)
        {
            this.canTarget = canTarget;
            this.useEnterCursor = useEnterCursor;
            this.mode = mode;
        }

        public override bool CanTargetActor(Actor self, Actor target, TargetModifiers modifiers, ref string cursor)
        {
            // TODO - darky - This is crap. Fix it. 
            if ((target.Owner.PlayerName == "Creeps" || target.Owner.PlayerName == "Neutral" || self.Owner.IsAlliedWith(target.Owner)) && target.Info.HasTraitInfo<T>())
            {
                cursor = useEnterCursor(self, target) ? "enter" : "enter-blocked";
                return true;
            }
            if (!self.Owner.IsAlliedWith(target.Owner) || !target.Info.HasTraitInfo<T>() || !canTarget(self, target) || target.Owner.PlayerName == "Creeps")
                return false;

            cursor = useEnterCursor(self, target) ? "enter" : "enter-blocked";
            return true;
        }

        public override bool CanTargetFrozenActor(Actor self, FrozenActor target, TargetModifiers modifiers, ref string cursor)
        {
            // TODO - darky - also terrible.
            if (target.Info.HasTraitInfo<T>())
            {
                return true;
            }
            return false;
        }
    }
}
