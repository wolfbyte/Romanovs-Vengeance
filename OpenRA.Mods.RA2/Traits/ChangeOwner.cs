using OpenRA.Traits;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using System.Diagnostics;
namespace OpenRA.Mods.RA2.Traits
{
    public abstract class ChangeOwnerInfo : ITraitInfo
    {
        public abstract object Create(ActorInitializer init);
    }

    public abstract class ChangeOwner
    {
        protected void NeedChangeOwner(Actor self, Actor actor, Player newOwner)
        {
            var oldOwner = self.Owner;
            //var activities = self.World.Actors.Where(x => x.CurrentActivity != null);
            // Need where current activity == enter garrison
            var activities_snap1 = self.World.Actors.Where(x => x.CurrentActivity != null).ToList();
            self.ChangeOwner(newOwner);
            
            var activities_snap2 = self.World.Actors.Where(x => x.CurrentActivity != null).ToList();
            int count = self.World.Actors.Where(x => x.CurrentActivity != null).ToList().Count();
            Debug.WriteLine("count is " + count.ToString());
            self.World.AddFrameEndTask(x => x.AddFrameEndTask(y => Debug.WriteLine("tick: " + y.WorldTick.ToString())));
            self.World.AddFrameEndTask((z) => {
            Debug.WriteLine("Delegate Tick: " + z.WorldTick.ToString());
                count = z.Actors.Where(x => x.CurrentActivity != null).ToList().Count();
                Debug.WriteLine("Delegate count is " + count.ToString());
            }
            );
                
            
            
            //foreach (var t in self.TraitsImplementing<INotifyCapture>())
            //   t.OnCapture(self, actor, oldOwner, newOwner);
        }
    }
}