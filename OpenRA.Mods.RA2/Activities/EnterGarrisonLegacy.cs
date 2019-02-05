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
using System.Drawing;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;
using OpenRA.Mods.RA2.Traits;
using OpenRA.Mods.Common.Activities;

namespace OpenRA.Mods.RA2.Activities
{
	public class EnterGarrisonLegacy : Activity
	{
		public enum ReserveStatus { None, TooFar, Pending, Ready }
		enum EnterState { ApproachingOrEntering, WaitingToEnter, Inside, Exiting, Done }

		readonly IMove move;
		readonly int maxTries = 0;
		readonly EnterBehaviour enterBehaviour;
		readonly bool repathWhileMoving;
		readonly Color? targetLineColor;
        readonly Garrisoner garrisoner;
        int ticksToRetry = 10; // Number of ticks to try to re-aquire a valid target before giving up
        int ticksTried = 0;
        Actor garrisonableBuilding;
        Garrison garrison;

        public Target Target { get { return target; } }
		Target target;
		EnterState nextState = EnterState.ApproachingOrEntering; // Hint/starting point for next state
		bool isEnteringOrInside = false; // Used to know if exiting should be used
		WPos savedPos; // Position just before entering
		Activity inner;
		bool firstApproach = true;

		public EnterGarrisonLegacy(Actor self, Actor garrisonableBuilding, int maxTries = 0, bool repathWhileMoving = true)
        {
			move = self.Trait<IMove>();
			this.target = Target.FromActor(garrisonableBuilding);
            garrison = garrisonableBuilding.Trait<Garrison>();
            garrisoner = self.TraitsImplementing<Garrisoner>().Single();
            this.garrisonableBuilding = garrisonableBuilding;
            this.maxTries = maxTries;
			this.enterBehaviour = EnterBehaviour.Exit;
			this.repathWhileMoving = repathWhileMoving;
			this.targetLineColor = null;
		}

		// CanEnter(target) should to be true; otherwise, Enter may abort.
		// Tries counter starts at 1 (reset every tick)
		protected virtual bool TryGetAlternateTarget(Actor self, int tries, ref Target target)
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
		protected virtual bool CanReserve(Actor self) { return garrison.Unloading || garrison.CanLoad(garrisonableBuilding, self); }
        protected virtual ReserveStatus Reserve(Actor self)
		{
            if (target.Type == TargetType.Actor)
            {
                var status = !CanReserve(self) ? ReserveStatus.None : move.CanEnterTargetNow(self, target) ? ReserveStatus.Ready : ReserveStatus.TooFar;
                if (status != ReserveStatus.Ready)
                    return status;
                if (garrisoner.Reserve(self, garrison))
                    return ReserveStatus.Ready;
                return ReserveStatus.Pending;
            }
            else
            {
                return ReserveStatus.Pending;
            }
        }

		protected virtual void Unreserve(Actor self, bool abort)
        {
            garrisoner.Unreserve(self);
        }

		protected void OnInside(Actor self)
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

		/// <summary>
		/// Called when the actor is ready to transition from approaching to entering the target.
		/// Return true to start entering, or false to wait in the WaitingToEnter state.
		/// </summary>
		protected virtual bool TryStartEnter(Actor self) { return true; }

		protected bool TryGetAlternateTargetInCircle(
			Actor self, WDist radius, Action<Target> update, Func<Actor, bool> primaryFilter, Func<Actor, bool>[] preferenceFilters = null)
		{
			var diff = new WVec(radius, radius, WDist.Zero);
			var candidates = self.World.ActorMap.ActorsInBox(self.CenterPosition - diff, self.CenterPosition + diff)
				.Where(primaryFilter).Select(a => new { Actor = a, Ls = (self.CenterPosition - a.CenterPosition).HorizontalLengthSquared })
				.Where(p => p.Ls <= radius.LengthSquared).OrderBy(p => p.Ls).Select(p => p.Actor);
			if (preferenceFilters != null)
				foreach (var filter in preferenceFilters)
				{
					var preferredCandidate = candidates.FirstOrDefault(filter);
					if (preferredCandidate == null)
						continue;
					target = Target.FromActor(preferredCandidate);
					update(target);
					return true;
				}

			var candidate = candidates.FirstOrDefault();
			if (candidate == null)
				return false;
			target = Target.FromActor(candidate);
			update(target);
			return true;
		}

		// Called when inner activity is this and returns inner activity for next tick.
		protected virtual Activity InsideTick(Actor self) { return null; }

		// Abort entering and/or leave if necessary
		protected virtual void AbortOrExit(Actor self)
		{
			if (nextState == EnterState.Done)
				return;
			nextState = isEnteringOrInside ? EnterState.Exiting : EnterState.Done;
			if (inner == this)
				inner = null;
			else if (inner != null)
				inner.Cancel(self);
			if (isEnteringOrInside)
				Unreserve(self, true);
		}

		// Cancel inner activity and mark as done unless already leaving or done
		protected void Done(Actor self)
		{
			if (nextState == EnterState.Done)
				return;
			nextState = EnterState.Done;
			if (inner == this)
				inner = null;
			else if (inner != null)
				inner.Cancel(self);
		}

		public override bool Cancel(Actor self, bool keepQueue = false)
		{
			AbortOrExit(self);
			if (nextState < EnterState.Exiting)
				return base.Cancel(self);
			else
				NextActivity = null;
			return true;
		}

		ReserveStatus TryReserveElseTryAlternateReserve(Actor self)
		{
			for (var tries = 0;;)
			{
				switch (Reserve(self))
				{
					case ReserveStatus.None:
						if (++tries > maxTries || !TryGetAlternateTarget(self, tries, ref target))
							return ReserveStatus.None;
						continue;
					case ReserveStatus.TooFar:
						// Always goto to transport on first approach
						if (firstApproach)
						{
							firstApproach = false;
							return ReserveStatus.TooFar;
						}

						if (++tries > maxTries)
							return ReserveStatus.TooFar;
						Target t = target;
						if (!TryGetAlternateTarget(self, tries, ref t))
							return ReserveStatus.TooFar;

						var targetPosition = target.Positions.PositionClosestTo(self.CenterPosition);
						var alternatePosition = t.Positions.PositionClosestTo(self.CenterPosition);
						if ((targetPosition - self.CenterPosition).HorizontalLengthSquared <= (alternatePosition - self.CenterPosition).HorizontalLengthSquared)
							return ReserveStatus.TooFar;
						target = t;
						continue;
					case ReserveStatus.Pending:
						return ReserveStatus.Pending;
					case ReserveStatus.Ready:
						return ReserveStatus.Ready;
				}
			}
		}

		EnterState FindAndTransitionToNextState(Actor self)
		{
			switch (nextState)
			{
				case EnterState.ApproachingOrEntering:

					// Reserve to enter or approach
					isEnteringOrInside = false;
					switch (TryReserveElseTryAlternateReserve(self))
					{
						case ReserveStatus.None:
							return EnterState.Done; // No available target -> abort to next activity
						case ReserveStatus.TooFar:
						{
							var moveTarget = repathWhileMoving ? target : Target.FromPos(target.Positions.PositionClosestTo(self.CenterPosition));
							inner = move.MoveToTarget(self, moveTarget, targetLineColor: targetLineColor); // Approach
							return EnterState.ApproachingOrEntering;
						}

						case ReserveStatus.Pending:
							return EnterState.ApproachingOrEntering; // Retry next tick
						case ReserveStatus.Ready:
							break; // Reserved target -> start entering target
					}

					// Can we enter yet?
					if (!TryStartEnter(self))
						return EnterState.WaitingToEnter;

					// Entering
					isEnteringOrInside = true;
					savedPos = self.CenterPosition; // Save position of self, before entering, for returning on exit

					inner = move.MoveIntoTarget(self, target); // Enter

					if (inner != null)
					{
						nextState = EnterState.Inside; // Should be inside once inner activity is null
						return EnterState.ApproachingOrEntering;
					}

					// Can enter but there is no activity for it, so go inside without one
					goto case EnterState.Inside;

				case EnterState.Inside:
					// Might as well teleport into target if there is no MoveIntoTarget activity
					if (nextState == EnterState.ApproachingOrEntering)
						nextState = EnterState.Inside;

					// Otherwise, try to recover from moving target
					else if (target.Positions.PositionClosestTo(self.CenterPosition) != self.CenterPosition)
					{
						nextState = EnterState.ApproachingOrEntering;
						Unreserve(self, false);
						if (Reserve(self) == ReserveStatus.Ready)
						{
							inner = move.MoveIntoTarget(self, target); // Enter
							if (inner != null)
								return EnterState.ApproachingOrEntering;

							nextState = EnterState.ApproachingOrEntering;
							goto case EnterState.ApproachingOrEntering;
						}

						nextState = EnterState.ApproachingOrEntering;
						isEnteringOrInside = false;
						inner = move.MoveIntoWorld(self, self.World.Map.CellContaining(savedPos));

						return EnterState.ApproachingOrEntering;
					}

					OnInside(self);

					if (enterBehaviour == EnterBehaviour.Suicide)
						self.Kill(self);
					else if (enterBehaviour == EnterBehaviour.Dispose)
						self.Dispose();

					// Return if Abort(Actor) or Done(self) was called from OnInside.
					if (nextState >= EnterState.Exiting)
						return EnterState.Inside;

					inner = this; // Start inside activity
					nextState = EnterState.Exiting; // Exit once inner activity is null (unless Done(self) is called)
					return EnterState.Inside;

				// TODO: Handle target moved while inside or always call done for movable targets and use a separate exit activity
				case EnterState.Exiting:
					inner = move.MoveIntoWorld(self, self.World.Map.CellContaining(savedPos));

					// If not successfully exiting, retry on next tick
					if (inner == null)
						return EnterState.Exiting;
					isEnteringOrInside = false;
					nextState = EnterState.Done;
					return EnterState.Exiting;

				case EnterState.Done:
					return EnterState.Done;
			}

			return EnterState.Done; // dummy to quiet dumb compiler
		}

		Activity CanceledTick(Actor self)
		{
			if (inner == null)
				return ActivityUtils.RunActivity(self, NextActivity);
            
			inner.Cancel(self);
			inner.Queue(NextActivity);
			return ActivityUtils.RunActivity(self, inner);
		}

		public override Activity Tick(Actor self)
		{
            if (IsCanceled)
				return CanceledTick(self);


            // Check target validity if not exiting or done
            if (nextState != EnterState.Done && (!target.IsValidFor(self) || target.Type != TargetType.Actor)) {
                if (ticksTried >= ticksToRetry) AbortOrExit(self);
                target = Target.FromActor(garrisonableBuilding);
                if (target.Type == TargetType.Actor) ticksTried = 0;
                ticksTried++;
            }
			// If no current activity, tick next activity
			if (inner == null && FindAndTransitionToNextState(self) == EnterState.Done)
				return CanceledTick(self);

			// Run inner activity/InsideTick
			inner = inner == this ? InsideTick(self) : ActivityUtils.RunActivity(self, inner);

			// If we are finished, move on to next activity
			if (inner == null && nextState == EnterState.Done)
				return NextActivity;

			return this;
		}
	}
}
