#region Copyright & License Information
/*
 * Copyright 2007-2019 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Traits;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.RA2.Traits
{
	public class RearmableG2Info : ITraitInfo
	{
		[Desc("Actors that this actor can dock to and get rearmed by.")]
		[FieldLoader.Require]
		[ActorReference] public readonly HashSet<string> RearmActors = new HashSet<string> { };

		[Desc("Name(s) of AmmoPool(s) that use this trait to rearm.")]
		public readonly HashSet<string> AmmoPools = new HashSet<string> { "primary" };

		public object Create(ActorInitializer init) { return new G2(this); }
	}

	public class G2 : INotifyCreated
	{
		public readonly RearmableG2Info Info;

		public G2(RearmableG2Info info)
		{
			Info = info;
		}

		public AmmoPool[] RearmableAmmoPools { get; private set; }

		void INotifyCreated.Created(Actor self)
		{
			RearmableAmmoPools = self.TraitsImplementing<AmmoPool>().Where(p => Info.AmmoPools.Contains(p.Info.Name)).ToArray();
		}
	}
}
