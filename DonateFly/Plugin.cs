using Newtonsoft.Json;
using Rocket.API;
using Rocket.Core;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using SDG.Unturned;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DonateFly
{
    public class Plugin : RocketPlugin<Configuration>
    {
        public static Plugin Instance { get; private set; }
        private bool _isEnabled = false;

        private string _assemblyName;
        private string _assemblyVersion;

        private int _failedRequestCount = 0;
        private const int MaxFailedRequests = 3;
        private readonly HttpClient _httpClient = new HttpClient();
        private CancellationTokenSource _cancellationTokenSource;

        protected override void Load()
        {
            Instance = this;
            _isEnabled = true;

            _assemblyName = GetType().Assembly.GetName().Name;
            _assemblyVersion = GetType().Assembly.GetName().Version.ToString();

            ValidateConfiguration();

            _cancellationTokenSource = new CancellationTokenSource();
            _ = StartRequestLoop(_cancellationTokenSource.Token);
        }

        protected override void Unload()
        {
            Instance = null;
            _isEnabled = false;
            _cancellationTokenSource.Cancel();
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

        private async Task StartRequestLoop(CancellationToken cancellationToken)
        {
            while (_isEnabled)
            {
                try
                {
                    await Task.Delay(Configuration.Instance.RequestFrequency * 60 * 1000, cancellationToken);
                    if (_isEnabled) await SendRequestAsync();
                }
                catch (TaskCanceledException)
                {
                    Logger.Log("Request loop canceled.");
                }
            }
        }

        private async Task SendRequestAsync()
        {
            try
            {
                var players = GetOnlinePlayers();
                var maxPlayers = GetMaxPlayers();

                var requestData = new
                {
                    server_id = Configuration.Instance.ServerID,
                    server_key = Configuration.Instance.ServerKey,
                    request_frequency = Configuration.Instance.RequestFrequency,
                    players = players,
                    max_players = maxPlayers
                };

                var json = JsonConvert.SerializeObject(requestData);
                Logger.Log($"Request JSON: {json}");

                var requestContent = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("https://donatefly.shop/plugin/notifications/", requestContent);

                if (response.IsSuccessStatusCode)
                {
                    Logger.Log("Received response from DonateFly.");
                    _failedRequestCount = 0;
                    var result = await GetPurchasesFromResponseAsync(response);
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
                    Logger.LogError($"Unexpected response from DonateFly: {response.StatusCode}");
                    HandleFailedRequest();
                }
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError($"DonateFly API Error: {ex.Message}");
                HandleFailedRequest();
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "An error occurred while processing a DonateFly purchase.");
                HandleFailedRequest();
            }
        }

        private void HandleFailedRequest()
        {
            _failedRequestCount++;
            if (_failedRequestCount >= MaxFailedRequests)
            {
                Logger.LogError("Server is considered stopped due to multiple failed requests.");
            }
        }

        private List<string> GetOnlinePlayers()
        {
            List<string> playerList = new List<string>();
            foreach (var player in Provider.clients)
            {
                playerList.Add(player.playerID.steamID.ToString());
            }
            return playerList;
        }

        private int GetMaxPlayers()
        {
            return Provider.maxPlayers;
        }

        private async Task<List<Purchase>> GetPurchasesFromResponseAsync(HttpResponseMessage response)
        {
            var responseText = await response.Content.ReadAsStringAsync();
            Logger.Log($"Response JSON: {responseText}");
            return JsonConvert.DeserializeObject<List<Purchase>>(responseText);
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
            if (string.IsNullOrEmpty(command))
            {
                Logger.LogError("Command is null or empty.");
                return false;
            }

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