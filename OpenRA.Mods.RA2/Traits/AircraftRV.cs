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

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;


namespace OpenRA.Mods.RA2.Traits
{
	public class AircraftRVInfo : AircraftInfo, ITraitInfo, IPositionableInfo, IFacingInfo, IMoveInfo, ICruiseAltitudeInfo,
	IActorPreviewInitInfo, IEditorActorOptions
	{
		new public virtual object Create(ActorInitializer init) { return new AircraftRV(init, this); }
	}

	public class AircraftRV : Aircraft, ITick, ISync, IFacing, IPositionable, IMove, IIssueOrder, IResolveOrder, IOrderVoice, IDeathActorInitModifier,
	INotifyCreated, INotifyAddedToWorld, INotifyRemovedFromWorld, INotifyActorDisposing, INotifyBecomingIdle,
	IActorPreviewInitModifier, IIssueDeployOrder, IObservesVariables
	{
		new public readonly AircraftRVInfo Info;
		readonly Actor self;
		RearmableInfo rearmableInfo;
		new public bool ForceLanding { get; private set; }
		bool airborne;
		bool cruising;
		bool firstTick = true;
		bool isMoving;
		bool isMovingVertically;
		WPos cachedPosition;
		bool? landNow;

		public AircraftRV(ActorInitializer init, AircraftInfo info) : base(init, info)
		{
			Info = (AircraftRVInfo)info;
			self = init.Self;

			if (init.Contains<LocationInit>())
				SetPosition(self, init.Get<LocationInit, CPos>());

			if (init.Contains<CenterPositionInit>())
				SetPosition(self, init.Get<CenterPositionInit, WPos>());

			Facing = init.Contains<FacingInit>() ? init.Get<FacingInit, int>() : Info.InitialFacing;
		}

		void ITick.Tick(Actor self)
		{
			Tick(self);
		}

		new protected void Tick(Actor self)
		{
			if (firstTick)
			{
				firstTick = false;

				// TODO: Aircraft husks don't properly unreserve.
				if (self.Info.HasTraitInfo<FallsToEarthInfo>())
					return;

				ReserveSpawnBuilding();

				var host = GetActorBelow();
				if (host == null)
					return;

				if (Info.TakeOffOnCreation)
				{
					if (Info.VTOL)
					{
						var rp = host.TraitOrDefault<RallyPoint>();
						self.QueueActivity(new HeliFlyAndLandWhenIdle(self, Target.FromCell(self.World, rp.Location), Info));
						self.TraitOrDefault<AircraftRV>().UnReserve();
					}
					else
						self.QueueActivity(new TakeOff(self));
				}

			}

			// Add land activity if LandOnCondidion resolves to true and the actor can land at the current location.
			if (!ForceLanding && landNow.HasValue && landNow.Value && airborne && CanLand(self.Location)
				&& !(self.CurrentActivity is HeliLand || self.CurrentActivity is Turn))
			{
				self.CancelActivity();

				if (Info.TurnToLand)
					self.QueueActivity(new Turn(self, Info.InitialFacing));

				self.QueueActivity(new HeliLand(self, true));

				ForceLanding = true;
			}

			// Add takeoff activity if LandOnCondidion resolves to false and the actor should not land when idle.
			if (ForceLanding && landNow.HasValue && !landNow.Value && !cruising && !(self.CurrentActivity is TakeOff))
			{
				ForceLanding = false;

				if (!Info.LandWhenIdle)
				{
					self.CancelActivity();
					self.QueueActivity(new TakeOff(self));
				}
			}

			var oldCachedPosition = cachedPosition;
			cachedPosition = self.CenterPosition;
			isMoving = (oldCachedPosition - cachedPosition).HorizontalLengthSquared != 0;
			isMovingVertically = (oldCachedPosition - cachedPosition).VerticalLengthSquared != 0;

			Repulse();
		}
		new public void ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "Move")
			{
				var cell = self.World.Map.Clamp(self.World.Map.CellContaining(order.Target.CenterPosition));
				var altitude = self.World.Map.DistanceAboveTerrain(CenterPosition);
				if (!Info.MoveIntoShroud && !self.Owner.Shroud.IsExplored(cell))
					return;

				if (!order.Queued)
					UnReserve();

				var target = Target.FromCell(self.World, cell);

				self.SetTargetLine(target, Color.Green);
				if(altitude.Length == 0 && Info.VTOL)
					self.QueueActivity(order.Queued, new HeliFlyAndLandWhenIdle(self, target, Info));
				else if (!Info.CanHover)
					self.QueueActivity(order.Queued, new Fly(self, target));
				else
					self.QueueActivity(order.Queued, new HeliFlyAndLandWhenIdle(self, target, Info));
			}
			else if (order.OrderString == "Enter" || order.OrderString == "Repair")
			{
				// Enter and Repair orders are only valid for own/allied actors,
				// which are guaranteed to never be frozen.
				if (order.Target.Type != TargetType.Actor)
					return;

				if (!order.Queued)
					UnReserve();

				var targetActor = order.Target.Actor;
				if (!Reservable.IsAvailableFor(targetActor, self))
				{
					if (!Info.CanHover && !Info.VTOL)
						self.QueueActivity(new ReturnToBase(self, Info.AbortOnResupply));
					else
						self.QueueActivity(new HeliReturnToBase(self, Info.AbortOnResupply));
				}
				else
				{
					self.SetTargetLine(Target.FromActor(targetActor), Color.Green);

					if (!Info.CanHover && !Info.VTOL)
					{
						self.QueueActivity(order.Queued, ActivityUtils.SequenceActivities(
							new ReturnToBase(self, Info.AbortOnResupply, targetActor),
							new ResupplyAircraft(self)));
					}
					else
					{
						MakeReservation(targetActor);

						Action enter = () =>
						{
							var exit = targetActor.FirstExitOrDefault(null);
							var offset = exit != null ? exit.Info.SpawnOffset : WVec.Zero;

							self.QueueActivity(new HeliFly(self, Target.FromPos(targetActor.CenterPosition + offset)));
							if (Info.TurnToDock)
								self.QueueActivity(new Turn(self, Info.InitialFacing));

							self.QueueActivity(new HeliLand(self, false));
							self.QueueActivity(new ResupplyAircraft(self));
						};

						self.QueueActivity(order.Queued, new CallFunc(enter));
					}
				}
			}
			else if (order.OrderString == "Stop")
			{
				self.CancelActivity();
				if (GetActorBelow() != null)
				{
					self.QueueActivity(new ResupplyAircraft(self));
					return;
				}

				UnReserve();
			}
			else if (order.OrderString == "ReturnToBase" && rearmableInfo != null && rearmableInfo.RearmActors.Any())
			{
				if (!order.Queued)
					UnReserve();

				if (!Info.CanHover && !Info.VTOL)
					self.QueueActivity(order.Queued, new ReturnToBase(self, Info.AbortOnResupply, null, false));
				else
					self.QueueActivity(order.Queued, new HeliReturnToBase(self, Info.AbortOnResupply, null, false));
			}
		}



	}
}