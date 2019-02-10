#region Copyright & License Information
/*
 * Copyright 2007-2017 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using OpenRA.Mods.RA2.Traits;
using OpenRA.Traits;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.RA2.Traits
{
    public class ChangeOwnerOnGarrisonerInfo : ChangeOwnerInfo, ITraitInfo, Requires<GarrisonInfo>
    {
        [Desc("The speech notification on enter first garrisoner on garrison.")]
        public readonly string EnterNotification = null;

        [Desc("The speech notification on exit last garrisoner on garrison")]
        public readonly string ExitNotification = null;

        public override object Create(ActorInitializer init) { return new ChangeOwnerOnGarrisoner(init.Self, this); }
    }

    public class ChangeOwnerOnGarrisoner : ChangeOwner, INotifyGarrisonerEntered, INotifyGarrisonerExited
    {
        readonly ChangeOwnerOnGarrisonerInfo info;
        readonly Garrison garrison;
        private readonly Player originalOwner;

        public ChangeOwnerOnGarrisoner(Actor self, ChangeOwnerOnGarrisonerInfo info)
        {
            this.info = info;
            garrison = self.Trait<Garrison>();
            originalOwner = self.Owner;
        }

        void INotifyGarrisonerEntered.OnGarrisonerEntered(Actor self, Actor garrisoner)
        {
            var newOwner = garrisoner.Owner;
            if (self.Owner != originalOwner || self.Owner == newOwner || self.Owner.IsAlliedWith(garrisoner.Owner))
                return;

            NeedChangeOwner(self, garrisoner, newOwner);

            Game.Sound.PlayNotification(self.World.Map.Rules, garrisoner.Owner, "Speech", info.EnterNotification, newOwner.Faction.InternalName);
        }

        void INotifyGarrisonerExited.OnGarrisonerExited(Actor self, Actor garrisoner)
        {
            if (garrison.GarrisonerCount > 0)
                return;

            Game.Sound.PlayNotification(self.World.Map.Rules, garrisoner.Owner, "Speech", info.ExitNotification, garrisoner.Owner.Faction.InternalName);
            NeedChangeOwner(self, garrisoner, originalOwner);
        }
    }
}