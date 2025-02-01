using Rocket.API;

namespace DonateFly
{
    public class Configuration : IRocketPluginConfiguration
    {
        public int RequestFrequency { get; set; }
        public string ServerID { get; set; }
        public string ServerKey { get; set; }
        public string ApiUrl { get; set; }

        public void LoadDefaults()
        {
            ServerID = "your_server_id";
            ServerKey = "your_server_key";
            RequestFrequency = 5;
            ApiUrl = "https://donatefly.shop/plugin";
        }
    }
}