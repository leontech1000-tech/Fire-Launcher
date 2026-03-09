using System.Collections.Generic;
using System.Runtime.Serialization;

namespace FireLauncher.Models
{
    [DataContract]
    public class ForkProfile
    {
        [DataMember]
        public string Id { get; set; }

        [DataMember]
        public string DisplayName { get; set; }

        [DataMember]
        public string DefaultUsername { get; set; }

        [DataMember]
        public string DefaultServerIp { get; set; }

        [DataMember]
        public int DefaultServerPort { get; set; }

        [DataMember]
        public bool OnlineModeEnabled { get; set; }

        [DataMember]
        public string SelectedForkId { get; set; }

        [DataMember]
        public List<ForkDefinition> Forks { get; set; } = new List<ForkDefinition>();
    }
}
