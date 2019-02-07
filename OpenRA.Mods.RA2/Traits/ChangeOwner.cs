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
            self.ChangeOwner(newOwner);
            
         }
    }
}