using System.Runtime.Serialization;

namespace FireLauncher.Models
{
    [DataContract]
    public class LauncherSettings
    {
        [DataMember]
        public string ProfilesRoot { get; set; }

        [DataMember]
        public string SelectedProfileId { get; set; }

        [DataMember]
        public bool DiscordPresenceEnabled { get; set; }

        [DataMember]
        public string DiscordApplicationId { get; set; }
    }
}
