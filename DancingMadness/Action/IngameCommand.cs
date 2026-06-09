using DancingMadness.Core;
using System.Xml.Serialization;

namespace DancingMadness.Action
{

    public class IngameCommand : Core.Action
    {

        [XmlAttribute]
        public string Command { get; set; } = "";

        public override void Execute(Context ctx)
        {
            ctx.State.PostCommand(ctx.ParseText(this, Command));
        }

    }

}
