using System;
using System.Xml.Serialization;

namespace DancingMadness.Core
{

    [XmlInclude(typeof(DancingMadness.Action.ChatMessage))]
    [XmlInclude(typeof(DancingMadness.Action.Notification))]
    [XmlInclude(typeof(DancingMadness.Action.IngameCommand))]
    public abstract class Action
    {
        
        internal Guid Id = Guid.NewGuid();

        public abstract void Execute(Context ctx);
        
        public virtual string Describe()
        {
            return I18n.Translate("Timelines/ActionTypes/" + GetType().Name);
        }

        public virtual Action Duplicate()
        {
            return (Action)MemberwiseClone();
        }

    }

}
