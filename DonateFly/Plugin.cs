using Newtonsoft.Json;
using Rocket.API;
using Rocket.Core;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
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

        private string _assemblyName;
        private string _assemblyVersion;

        protected override void Load()
        {
            Instance = this;
            _isEnabled = true;

            _assemblyName = GetType().Assembly.GetName().Name;
            _assemblyVersion = GetType().Assembly.GetName().Version.ToString();

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
                if (_isEnabled) SendRequest();
            }
        }

        private void SendRequest()
        {
            try
            {
                if (Configuration.Instance == null)
                {
                    Logger.LogError("Configuration instance is null.");
                    return;
                }

                var requestData = new
                {
                    server_id = Configuration.Instance.ServerID,
                    server_key = Configuration.Instance.ServerKey,
                    request_frequency = Configuration.Instance.RequestFrequency
                };

                var json = JsonConvert.SerializeObject(requestData);
                Logger.Log($"Request JSON: {json}");

                var request = (HttpWebRequest)WebRequest.Create("https://donatefly.shop/plugin/notifications/");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.UserAgent = $"{_assemblyName}/{_assemblyVersion}";
                request.Timeout = 10000;

                using (var streamWriter = new StreamWriter(request.GetRequestStream()))
                {
                    streamWriter.Write(json);
                }

                using (var response = request.GetResponse() as HttpWebResponse)
                {
                    if (response != null && response.StatusCode == HttpStatusCode.OK)
                    {
                        Logger.Log("Received response from DonateFly.");

                        var result = GetPurchasesFromResponse(response);
                        if (result != null)
                        {
                            ProcessPurchases(result);
                        }
                        else
                        {
                            Logger.LogWarning("Received null or empty response from DonateFly.");
                        }
                    }
                    else
                    {
                        Logger.LogError($"Unexpected response from DonateFly: {response?.StatusCode}");
                    }
                }
            }
            catch (WebException webEx)
            {
                Logger.LogError($"DonateFly API Error: {webEx.Status} - {webEx.Message}");

                if (webEx.Response is HttpWebResponse errorResponse)
                {
                    using (var reader = new StreamReader(errorResponse.GetResponseStream()))
                    {
                        string errorBody = reader.ReadToEnd();
                        Logger.LogError($"Error Details: {errorBody}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "An error occurred while processing a DonateFly purchase.");
            }
        }

        private List<Purchase> GetPurchasesFromResponse(HttpWebResponse response)
        {
            using (var streamReader = new StreamReader(response.GetResponseStream()))
            {
                var responseText = streamReader.ReadToEnd();
                Logger.Log($"Response JSON: {responseText}");
                return JsonConvert.DeserializeObject<List<Purchase>>(responseText);
            }
        }

        private void ProcessPurchases(List<Purchase> purchases)
        {
            foreach (var purchase in purchases)
            {
                if (!IsValidPurchase(purchase))
                {
                    Logger.LogWarning($"Invalid purchase data received: {JsonConvert.SerializeObject(purchase)}");
                    continue;
                }

                try
                {
                    var steamId = new CSteamID(ulong.Parse(purchase.SteamId));
                    Logger.Log($"Processing purchase {purchase.Id} for SteamID {steamId}: Command - {purchase.Command}");

                    string formattedCommand = string.Format(purchase.Command, steamId);
                    Logger.Log($"Executing command from console: {formattedCommand}");

                    bool executed = ExecuteCommand(formattedCommand);
                    if (!executed)
                    {
                        Logger.LogError($"Failed to execute command for purchase {purchase.Id}");
                    }
                }
                catch (FormatException ex)
                {
                    Logger.LogError($"Error parsing SteamID in purchase {purchase.Id}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error processing purchase {purchase.Id}: {ex.Message}");
                }
            }
        }

        private bool IsValidPurchase(Purchase purchase) =>
            purchase.Id > 0 &&
            !string.IsNullOrEmpty(purchase.SteamId) &&
            !string.IsNullOrEmpty(purchase.Command);


        private bool ExecuteCommand(string command)
        {
            bool executedByRocket = R.Commands.Execute(new ConsolePlayer(), command);
            if (executedByRocket) return true;

            bool executedByCommander = Commander.execute(new CSteamID(0), command);
            return executedByCommander;
        }

        public class Purchase
        {
            public int Id { get; set; }
            public string SteamId { get; set; }
            public string Command { get; set; }
        }
    }
}