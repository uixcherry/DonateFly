using Newtonsoft.Json;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace DonateFly
{
    public class Plugin : RocketPlugin<Configuration>
    {
        public static Plugin Instance { get; private set; }
        private bool _isEnabled = false;

        protected override void Load()
        {
            Instance = this;
            _isEnabled = true;
            ValidateConfiguration();
            SendRequest();
            StartRequestLoop();
        }

        protected override void Unload()
        {
            Instance = null;
            _isEnabled = false;
        }

        private void ValidateConfiguration()
        {
            if (Configuration.Instance.RequestFrequency < 5)
            {
                Logger.LogWarning("Request frequency is too low. Setting to minimum (5 minutes).");
                Configuration.Instance.RequestFrequency = 5;
                Configuration.Save();
            }
        }

        private async void StartRequestLoop()
        {
            while (_isEnabled)
            {
                await Task.Delay(Configuration.Instance.RequestFrequency * 60 * 1000);
                SendRequest();
            }
        }

        private void SendRequest()
        {
            try
            {
                var requestData = new
                {
                    server_id = Configuration.Instance.ServerID,
                    server_key = Configuration.Instance.ServerKey,
                    request_frequency = Configuration.Instance.RequestFrequency
                };

                var json = JsonConvert.SerializeObject(requestData);
                Logger.Log($"Request JSON: {json}");

                var request = WebRequest.CreateHttp("https://donatefly.shop/plugin/notifications/");
                request.Method = "POST";
                request.ContentType = "application/json";

                using (var streamWriter = new StreamWriter(request.GetRequestStream()))
                {
                    streamWriter.Write(json);
                }

                var response = request.GetResponse() as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.OK)
                {
                    using (var streamReader = new StreamReader(response.GetResponseStream()))
                    {
                        var result = JsonConvert.DeserializeObject<List<Purchase>>(streamReader.ReadToEnd());
                        foreach (var purchase in result)
                        {
                            Logger.Log($"Processing purchase {purchase.Id} for SteamID {purchase.SteamId}: Command - {purchase.Command}");
                            var player = UnturnedPlayer.FromCSteamID(new CSteamID(ulong.Parse(purchase.SteamId)));
                            if (player != null)
                            {
                                Commander.execute(player.CSteamID, purchase.Command);
                            }
                            else
                            {
                                Logger.LogWarning($"Player with SteamID {purchase.SteamId} not found. Command not executed.");
                            }
                        }
                    }
                }
                else
                {
                    Logger.LogError($"Unexpected response from DonateFly: {response?.StatusCode}");
                }
            }
            catch (WebException webEx)
            {
                if (webEx.Response is HttpWebResponse errorResponse)
                {
                    Logger.LogError($"DonateFly API Error: {errorResponse.StatusCode} - {errorResponse.StatusDescription}");
                    using (var reader = new StreamReader(errorResponse.GetResponseStream()))
                    {
                        string errorBody = reader.ReadToEnd();
                        Logger.LogError($"Error Details: {errorBody}");
                    }
                }
                else
                {
                    Logger.LogError($"Network Error: {webEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending request: {ex.Message}");
            }
        }

        public class Purchase
        {
            public int Id { get; set; }
            public string SteamId { get; set; }
            public string Command { get; set; }
        }
    }
}