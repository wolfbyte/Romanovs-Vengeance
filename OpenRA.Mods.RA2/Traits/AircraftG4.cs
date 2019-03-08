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
using System.Drawing;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;


namespace OpenRA.Mods.RA2.Traits
{
	public class AircraftG4Info : AircraftInfo, ITraitInfo, IPositionableInfo, IFacingInfo, IMoveInfo, ICruiseAltitudeInfo,
		IActorPreviewInitInfo, IEditorActorOptions
	{
		[Desc("Darky Description")]
		public readonly bool HoverLand = false;

		new public virtual object Create(ActorInitializer init) { return new AircraftG4(init, this); }
	}

	public class AircraftG4 : Aircraft, ITick, ISync, IFacing, IPositionable, IMove, IIssueOrder, IResolveOrder, IOrderVoice, IDeathActorInitModifier,
		INotifyCreated, INotifyAddedToWorld, INotifyRemovedFromWorld, INotifyActorDisposing, INotifyBecomingIdle,
		IActorPreviewInitModifier, IIssueDeployOrder, IObservesVariables
	{
		static readonly Pair<CPos, SubCell>[] NoCells = { };

		new public readonly AircraftG4Info Info;
		readonly Actor self;

		//RepairableInfo repairableInfo;
		RearmableInfo rearmableInfo;
		//AttackMove attackMove;
		ConditionManager conditionManager;
		IDisposable reservation;
		IEnumerable<int> speedModifiers;

		//[Sync] public int Facing { get; set; }
		//[Sync] public WPos CenterPosition { get; private set; }
		//public CPos TopLeft { get { return self.World.Map.CellContaining(CenterPosition); } }
		//public int TurnSpeed { get { return Info.TurnSpeed; } }
		//public Actor ReservedActor { get; private set; }
		//public bool MayYieldReservation { get; private set; }
		new public bool ForceLanding { get; private set; }

		bool airborne;
		bool cruising;
		bool firstTick = true;
		int airborneToken = ConditionManager.InvalidConditionToken;
		int cruisingToken = ConditionManager.InvalidConditionToken;

		bool isMoving;
		bool isMovingVertically;

		WPos cachedPosition;
		bool? landNow;

		public AircraftG4(ActorInitializer init, AircraftInfo info) : base(init,info)
		{
			Info = (AircraftG4Info)info;
			self = init.Self;

			if (init.Contains<LocationInit>())
				SetPosition(self, init.Get<LocationInit, CPos>());

			if (init.Contains<CenterPositionInit>())
				SetPosition(self, init.Get<CenterPositionInit, WPos>());

			Facing = init.Contains<FacingInit>() ? init.Get<FacingInit, int>() : Info.InitialFacing;
		}

		new protected virtual void Tick(Actor self)
		{
			if (firstTick)
			{
				firstTick = false;

				// TODO: AircraftG4 husks don't properly unreserve.
				if (self.Info.HasTraitInfo<FallsToEarthInfo>())
					return;

				ReserveSpawnBuilding();

				var host = GetActorBelow();
				if (host == null)
					return;

				// Darky Mods here - #DM001
				if (Info.TakeOffOnCreation)
				{
					if (Info.VTOL)
					{
						var rp =  host.TraitOrDefault<RallyPoint>();
						self.QueueActivity(new HeliFlyAndLandWhenIdle(self,Target.FromCell(self.World,rp.Location),Info));
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

		new public Pair<CPos, SubCell>[] OccupiedCells() { return NoCells; }

		new protected virtual void OnBecomingIdle(Actor self)
		{
			if (Info.VTOL && Info.LandWhenIdle)
			{
				if (Info.TurnToLand)
					self.QueueActivity(new Turn(self, Info.InitialFacing));

				self.QueueActivity(new HeliLand(self, true));
			}
			else if (!Info.CanHover)
				self.QueueActivity(new FlyCircle(self, -1, Info.IdleTurnSpeed > -1 ? Info.IdleTurnSpeed : TurnSpeed));

			// Temporary HACK for the AutoCarryall special case (needs CanHover, but also HeliFlyCircle on idle).
			// Will go away soon (in a separate PR) with the arrival of ActionsWhenIdle.
			else if (Info.CanHover && self.Info.HasTraitInfo<AutoCarryallInfo>() && Info.IdleTurnSpeed > -1)
				self.QueueActivity(new HeliFlyCircle(self, Info.IdleTurnSpeed > -1 ? Info.IdleTurnSpeed : TurnSpeed));
		}

		#region Implement IMove
		// darky - This is called when another actor wants to use the landing zone
		// darky - This is called at spawn
		// darky - #DM002
		new public Activity MoveTo(CPos cell, int nearEnough)
		{
			if (!Info.CanHover && !Info.VTOL)
				return new Fly(self, Target.FromCell(self.World, cell));

			return new HeliFly(self, Target.FromCell(self.World, cell));
		}

		new public Activity MoveTo(CPos cell, Actor ignoreActor)
		{
			if (!Info.CanHover) // darky
				return new Fly(self, Target.FromCell(self.World, cell));

			return new HeliFly(self, Target.FromCell(self.World, cell));
		}

		new public Activity MoveWithinRange(Target target, WDist range,
			WPos? initialTargetPosition = null, Color? targetLineColor = null)
		{
			if (!Info.CanHover)
				return new Fly(self, target, WDist.Zero, range, initialTargetPosition, targetLineColor);

			return new HeliFly(self, target, WDist.Zero, range, initialTargetPosition, targetLineColor);
		}

		new public Activity MoveWithinRange(Target target, WDist minRange, WDist maxRange,
			WPos? initialTargetPosition = null, Color? targetLineColor = null)
		{
			if (!Info.CanHover)
				return new Fly(self, target, minRange, maxRange,
					initialTargetPosition, targetLineColor);

			return new HeliFly(self, target, minRange, maxRange,
				initialTargetPosition, targetLineColor);
		}

		new public Activity MoveFollow(Actor self, Target target, WDist minRange, WDist maxRange,
			WPos? initialTargetPosition = null, Color? targetLineColor = null)
		{
			if (!Info.CanHover)
				return new FlyFollow(self, target, minRange, maxRange,
					initialTargetPosition, targetLineColor);

			return new Follow(self, target, minRange, maxRange,
				initialTargetPosition, targetLineColor);
		}

		new public Activity MoveIntoWorld(Actor self, CPos cell, SubCell subCell = SubCell.Any)
		{
			if (!Info.CanHover)
				return new Fly(self, Target.FromCell(self.World, cell));

			return new HeliFly(self, Target.FromCell(self.World, cell, subCell));
		}

		new public Activity MoveToTarget(Actor self, Target target,
			WPos? initialTargetPosition = null, Color? targetLineColor = null)
		{
			if (!Info.CanHover)
				return new Fly(self, target, WDist.FromCells(3), WDist.FromCells(5),
					initialTargetPosition, targetLineColor);

			return ActivityUtils.SequenceActivities(
				new HeliFly(self, target, initialTargetPosition, targetLineColor),
				new Turn(self, Info.InitialFacing));
		}

		new public Activity MoveIntoTarget(Actor self, Target target)
		{
			if (!Info.VTOL || !Info.HoverLand)
				return new Land(self, target);

			return new HeliLand(self, false);
		}

		new public Activity VisualMove(Actor self, WPos fromPos, WPos toPos)
		{
			// TODO: Ignore repulsion when moving
			if (!Info.CanHover)
				return ActivityUtils.SequenceActivities(
					new CallFunc(() => SetVisualPosition(self, fromPos)),
					new Fly(self, Target.FromPos(toPos)));

			return ActivityUtils.SequenceActivities(
				new CallFunc(() => SetVisualPosition(self, fromPos)),
				new HeliFly(self, Target.FromPos(toPos)));
		}

		#endregion

		new public void ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "Move")
			{
				var cell = self.World.Map.Clamp(order.TargetLocation);
				var altitude = self.World.Map.DistanceAboveTerrain(CenterPosition);
				if (!Info.MoveIntoShroud && !self.Owner.Shroud.IsExplored(cell))
					return;

				if (!order.Queued)
					UnReserve();

				var target = Target.FromCell(self.World, cell);

				self.SetTargetLine(target, Color.Green);
				// darky - This is the main place to make changes
				// darky -  Check if we have a condition here. I wish to check the conditionManager to see if we are landed. If we are landed, queue HeliFly, otherwise, queue fly
				if(altitude.Length == 0)
					self.QueueActivity(order.Queued, new HeliFlyAndLandWhenIdle(self, target, Info));
				else
					self.QueueActivity(order.Queued, new Fly(self, target));
				//if (!Info.CanHover)
				//	self.QueueActivity(order.Queued, new Fly(self, target));
					
				//else
				//	self.QueueActivity(order.Queued, new HeliFlyAndLandWhenIdle(self, target, Info));
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
				{ // darky
					if (!Info.CanHover && !Info.HoverLand)
						self.QueueActivity(new ReturnToBase(self, Info.AbortOnResupply));
					else
						self.QueueActivity(new HeliReturnToBase(self, Info.AbortOnResupply));
				}
				else
				{
					self.SetTargetLine(Target.FromActor(targetActor), Color.Green);

					if (!Info.CanHover && !Info.VTOL && !Info.HoverLand) // darky
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

				if (!Info.CanHover || !Info.HoverLand)
					self.QueueActivity(order.Queued, new ReturnToBase(self, Info.AbortOnResupply, null, false));
				else
					self.QueueActivity(order.Queued, new HeliReturnToBase(self, Info.AbortOnResupply, null, false));
			}
		}
	}
}
