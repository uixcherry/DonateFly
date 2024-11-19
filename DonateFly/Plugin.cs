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

        private readonly object _lock = new object();
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

            Provider.onServerShutdown += onServerShutdown;

            _ = SendServerStatusAsync(1);
        }

        protected override void Unload()
        {
            Provider.onServerShutdown -= onServerShutdown;

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
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Logger.Log("Cancellation requested, stopping the request loop.");
                        break;
                    }

                    await Task.Delay(Configuration.Instance.RequestFrequency * 60 * 1000, cancellationToken).ConfigureAwait(false);

                    if (_isEnabled)
                    {
                        bool success = await RetryHttpRequest(async () => await SendRequestAsync(), 3).ConfigureAwait(false);
                        if (!success)
                        {
                            Logger.LogError("Request loop encountered persistent errors and stopped attempts.");
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    Logger.Log("Request loop canceled.");
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex, "Unexpected error occurred in the request loop.");
                }
            }
        }

        private async Task SendRequestAsync()
        {
            try
            {
                List<string> players = GetOnlinePlayers();
                int maxPlayers = GetMaxPlayers();

                Dictionary<string, object> requestData = new Dictionary<string, object>
                {
                    { "server_id", Configuration.Instance.ServerID },
                    { "server_key", Configuration.Instance.ServerKey },
                    { "request_frequency", Configuration.Instance.RequestFrequency },
                    { "players", players },
                    { "max_players", maxPlayers },
                    { "status", 1 }
                };

                string json = JsonConvert.SerializeObject(requestData);
                Logger.Log($"Request JSON: {json}");

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://donatefly.shop/plugin/notifications/");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.UserAgent = $"{_assemblyName}/{_assemblyVersion}";
                request.Timeout = 10000;

                using (StreamWriter streamWriter = new StreamWriter(await request.GetRequestStreamAsync().ConfigureAwait(false)))
                {
                    await streamWriter.WriteAsync(json).ConfigureAwait(false);
                }

                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        Logger.Log("Received response from DonateFly.");
                        lock (_lock)
                        {
                            _failedRequestCount = 0;
                        }

                        List<Purchase> result = await GetPurchasesFromResponseAsync(response).ConfigureAwait(false);
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
            }
            catch (WebException webEx)
            {
                Logger.LogError($"DonateFly API Error: {webEx.Status} - {webEx.Message}");
                HandleFailedRequest();

                if (webEx.Response is HttpWebResponse errorResponse)
                {
                    using (StreamReader reader = new StreamReader(errorResponse.GetResponseStream()))
                    {
                        string errorBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                        Logger.LogError($"Error Details: {errorBody}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "An error occurred while processing a DonateFly request.");
                HandleFailedRequest();
            }
        }

        private async Task SendServerStatusAsync(int status)
        {
            try
            {
                Dictionary<string, object> requestData = new Dictionary<string, object>
                {
                    { "server_id", Configuration.Instance.ServerID },
                    { "server_key", Configuration.Instance.ServerKey },
                    { "status", status }
                };

                string json = JsonConvert.SerializeObject(requestData);
                Logger.Log($"[{DateTime.UtcNow}] Sending server status {status}: {json}");

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://donatefly.shop/plugin/status/");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.UserAgent = $"{_assemblyName}/{_assemblyVersion}";
                request.Timeout = 10000;

                using (StreamWriter streamWriter = new StreamWriter(await request.GetRequestStreamAsync().ConfigureAwait(false)))
                {
                    await streamWriter.WriteAsync(json).ConfigureAwait(false);
                }

                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        Logger.Log("Server status sent successfully.");
                    }
                    else
                    {
                        Logger.LogError($"Failed to send server status: {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "An error occurred while sending server status.");
            }
        }

        private void HandleFailedRequest()
        {
            lock (_lock)
            {
                _failedRequestCount++;
                if (_failedRequestCount >= MaxFailedRequests)
                {
                    Logger.LogError("Server is considered stopped due to multiple failed requests.");
                }
            }
        }

        private List<string> GetOnlinePlayers()
        {
            List<string> playerList = new List<string>();
            foreach (SteamPlayer player in Provider.clients)
            {
                playerList.Add(player.playerID.steamID.ToString());
            }
            return playerList;
        }

        private int GetMaxPlayers()
        {
            return Provider.maxPlayers;
        }

        private async Task<List<Purchase>> GetPurchasesFromResponseAsync(HttpWebResponse response)
        {
            using (StreamReader streamReader = new StreamReader(response.GetResponseStream()))
            {
                string responseText = await streamReader.ReadToEndAsync().ConfigureAwait(false);
                Logger.Log($"Response JSON: {responseText}");
                return JsonConvert.DeserializeObject<List<Purchase>>(responseText);
            }
        }

        private void ProcessPurchases(List<Purchase> purchases)
        {
            foreach (Purchase purchase in purchases)
            {
                if (!IsValidPurchase(purchase))
                {
                    Logger.LogWarning($"Invalid purchase data received: {JsonConvert.SerializeObject(purchase)}");
                    continue;
                }

                try
                {
                    CSteamID steamId = new CSteamID(ulong.Parse(purchase.SteamId));
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
            return executedByRocket;
        }

        private async Task<bool> RetryHttpRequest(Func<Task> action, int maxRetries = 3)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await action().ConfigureAwait(false);
                    return true;
                }
                catch (WebException)
                {
                    if (attempt == maxRetries)
                    {
                        Logger.LogError("Max retry attempts reached, aborting request.");
                        return false;
                    }
                    Logger.LogWarning($"Retry attempt {attempt} failed, retrying...");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(false);
                }
            }
            return false;
        }

        private void onServerShutdown()
        {
            _ = SendServerStatusAsync(0);
        }

        /// <summary>
        /// Represents a purchase data structure.
        /// </summary>
        public class Purchase
        {
            public int Id { get; set; }
            public string SteamId { get; set; }
            public string Command { get; set; }
        }
    }
}