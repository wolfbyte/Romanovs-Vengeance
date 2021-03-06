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

using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Mods.RA2.Traits;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;

namespace OpenRA.Mods.RA2.Activities
{
    public class UnloadGarrison : Activity
    {
        readonly Actor self;
        readonly Garrison garrison;
        readonly INotifyUnload[] notifiers;
        readonly bool unloadAll;

        public UnloadGarrison(Actor self, bool unloadAll)
        {
            this.self = self;
            garrison = self.Trait<Garrison>();
            notifiers = self.TraitsImplementing<INotifyUnload>().ToArray();
            this.unloadAll = unloadAll;
        }

        public Pair<CPos, SubCell>? ChooseExitSubCell(Actor passenger)
        {
            var pos = passenger.Trait<IPositionable>();

            return garrison.CurrentAdjacentCells
                .Shuffle(self.World.SharedRandom)
                .Select(c => Pair.New(c, pos.GetAvailableSubCell(c)))
                .Cast<Pair<CPos, SubCell>?>()
                .FirstOrDefault(s => s.Value.Second != SubCell.Invalid);
        }

        IEnumerable<CPos> BlockedExitCells(Actor passenger)
        {
            var pos = passenger.Trait<IPositionable>();

            // Find the cells that are blocked by transient actors
            return garrison.CurrentAdjacentCells
                .Where(c => pos.CanEnterCell(c, null, true) != pos.CanEnterCell(c, null, false));
        }

        public override Activity Tick(Actor self)
        {
            garrison.Unloading = false;
            if (IsCanceled || garrison.IsEmpty(self))
                return NextActivity;

            foreach (var inu in notifiers)
                inu.Unloading(self);

            var actor = garrison.Peek(self);
            var spawn = self.CenterPosition;

            var exitSubCell = ChooseExitSubCell(actor);
            if (exitSubCell == null)
            {
                self.NotifyBlocker(BlockedExitCells(actor));

                return ActivityUtils.SequenceActivities(new Wait(10), this);
            }

            garrison.Unload(self);
            self.World.AddFrameEndTask(w =>
            {
                if (actor.Disposed)
                    return;

                var move = actor.Trait<IMove>();
                var pos = actor.Trait<IPositionable>();

                actor.CancelActivity();
                pos.SetVisualPosition(actor, spawn);
                actor.QueueActivity(move.MoveIntoWorld(actor, exitSubCell.Value.First, exitSubCell.Value.Second));
                actor.SetTargetLine(Target.FromCell(w, exitSubCell.Value.First, exitSubCell.Value.Second), Color.Green, false);
                w.Add(actor);
            });

            if (!unloadAll || garrison.IsEmpty(self))
                return NextActivity;

            garrison.Unloading = true;
            return this;
        }
    }
}
