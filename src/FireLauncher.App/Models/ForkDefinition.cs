using System.Runtime.Serialization;

namespace FireLauncher.Models
{
    [DataContract]
    public class ForkDefinition
    {
        [DataMember]
        public string Id { get; set; }

        [DataMember]
        public string DisplayName { get; set; }

        [DataMember]
        public string InstallDirectory { get; set; }

        [DataMember]
        public string ExecutablePath { get; set; }

        [DataMember]
        public bool Enabled { get; set; }

        [DataMember]
        public bool HasMultiplayer { get; set; }

        [DataMember]
        public bool SupportsUsernameArgument { get; set; }

        [DataMember]
        public bool SupportsServerIpArgument { get; set; }

        [DataMember]
        public bool SupportsPortArgument { get; set; }

        [DataMember]
        public string LaunchArgumentName { get; set; }

        [DataMember]
        public string LaunchArgumentIp { get; set; }

        [DataMember]
        public string LaunchArgumentPort { get; set; }

        [DataMember]
        public string Notes { get; set; }

        public override string ToString()
        {
            return DisplayName ?? Id ?? "Fork";
        }
    }
}
