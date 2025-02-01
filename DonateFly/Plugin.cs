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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DonateFly
{
    public class Plugin : RocketPlugin<Configuration>
    {
        public static Plugin Instance { get; private set; }
        private volatile bool _isEnabled;
        private string _userAgent;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cancellationTokenSource;
        private Task _requestLoopTask;
        private const int REQUEST_TIMEOUT = 15000;
        private const int RETRY_DELAY_MS = 2000;
        private const int MAX_RETRIES = 3;
        private const string API_URL = "https://donatefly.shop/plugin";

        protected override async void Load()
        {
            Instance = this;
            _isEnabled = true;
            _userAgent = $"{Assembly.GetExecutingAssembly().GetName().Name}/{Assembly.GetExecutingAssembly().GetName().Version}";

            ValidateConfiguration();

            _cancellationTokenSource = new CancellationTokenSource();
            Provider.onServerShutdown += OnServerShutdown;

            _requestLoopTask = StartRequestLoop(_cancellationTokenSource.Token);
            await SendServerStatusAsync(1).ConfigureAwait(false);

            Logger.Log($"DonateFly loaded. Version: {Assembly.GetExecutingAssembly().GetName().Version}");
        }

        protected override async void Unload()
        {
            _isEnabled = false;
            Provider.onServerShutdown -= OnServerShutdown;

            _cancellationTokenSource?.Cancel();

            try
            {
                if (_requestLoopTask != null)
                    await _requestLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            _cancellationTokenSource?.Dispose();
            _semaphore?.Dispose();
            Instance = null;

            Logger.Log("DonateFly unloaded.");
        }

        private void ValidateConfiguration()
        {
            if (Configuration.Instance.RequestFrequency < 5)
            {
                Logger.LogWarning("Request frequency is too low. Setting to minimum value (5 minutes).");
                Configuration.Instance.RequestFrequency = 5;
                Configuration.Save();
            }
        }

        private async Task StartRequestLoop(CancellationToken token)
        {
            while (_isEnabled && !token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Configuration.Instance.RequestFrequency * 60 * 1000, token);
                    if (_isEnabled)
                        await ProcessRequestWithRetry().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error in request loop: {ex.Message}");
                    await Task.Delay(RETRY_DELAY_MS, token);
                }
            }
        }

        private async Task ProcessRequestWithRetry()
        {
            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                try
                {
                    await SendRequestAsync().ConfigureAwait(false);
                    return;
                }
                catch (WebException ex)
                {
                    Logger.LogError($"Attempt {attempt}/{MAX_RETRIES}: Network error: {ex.Status} - {ex.Message}");
                    if (attempt < MAX_RETRIES)
                        await Task.Delay(RETRY_DELAY_MS * attempt);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Critical error while sending request: {ex.Message}");
                    break;
                }
            }
        }

        private async Task SendRequestAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                Dictionary<string, object> requestData = new Dictionary<string, object>
               {
                   { "server_id", Configuration.Instance.ServerID },
                   { "server_key", Configuration.Instance.ServerKey },
                   { "request_frequency", Configuration.Instance.RequestFrequency },
                   { "players", GetOnlinePlayers() },
                   { "max_players", Provider.maxPlayers },
                   { "status", 1 }
               };

                string fullUrl = $"{API_URL}/notifications/";
                Logger.Log($"Sending request to: {fullUrl}");
                Logger.Log($"Request data: {JsonConvert.SerializeObject(requestData)}");

                HttpWebRequest request = CreateWebRequest(fullUrl);
                await SendJsonRequest(request, requestData).ConfigureAwait(false);

                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        List<Purchase> purchases = await ParseResponseAsync<List<Purchase>>(response).ConfigureAwait(false);
                        if (purchases?.Count > 0)
                        {
                            Logger.Log($"Received {purchases.Count} new purchases.");
                            ProcessPurchases(purchases);
                        }
                    }
                    else
                    {
                        Logger.LogError($"Unexpected server response: {response.StatusCode}");
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task SendServerStatusAsync(int status)
        {
            await _semaphore.WaitAsync();
            try
            {
                Dictionary<string, object> requestData = new Dictionary<string, object>
                {
                    { "server_id", Configuration.Instance.ServerID },
                    { "server_key", Configuration.Instance.ServerKey },
                    { "status", status }
                };

                string fullUrl = $"{Configuration.Instance.ApiUrl}/status/";
                using (HttpWebResponse response = await SendHttpRequestAsync(fullUrl, requestData).ConfigureAwait(false))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        Logger.LogError($"Error sending server status. Server returned: {response.StatusCode} - {response.StatusDescription}");
                    }
                }
            }
            catch (WebException ex)
            {
                string errorDetails = await GetWebExceptionDetailsAsync(ex);
                Logger.LogError($"Error sending server status: {ex.Status} - {errorDetails}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Critical error sending server status: {ex.GetType().Name} - {ex.Message}");
                if (ex.InnerException != null)
                {
                    Logger.LogError($"Inner error: {ex.InnerException.Message}");
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<string> GetWebExceptionDetailsAsync(WebException ex)
        {
            if (ex.Response == null)
                return ex.Message;

            using (HttpWebResponse errorResponse = (HttpWebResponse)ex.Response)
            using (Stream stream = errorResponse.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream))
            {
                string errorContent = await reader.ReadToEndAsync();
                return $"Status: {errorResponse.StatusCode}, Content: {errorContent}";
            }
        }

        private async Task<HttpWebResponse> SendHttpRequestAsync(string url, object data)
        {
            HttpWebRequest request = CreateWebRequest(url);
            await SendJsonRequest(request, data).ConfigureAwait(false);
            return (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
        }

        private HttpWebRequest CreateWebRequest(string url)
        {
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.UserAgent = _userAgent;
            request.Timeout = REQUEST_TIMEOUT;
            return request;
        }

        private async Task SendJsonRequest(HttpWebRequest request, object data)
        {
            string json = JsonConvert.SerializeObject(data);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            request.ContentLength = bytes.Length;

            using (Stream stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
        }

        private async Task<T> ParseResponseAsync<T>(HttpWebResponse response)
        {
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                string json = await reader.ReadToEndAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<T>(json);
            }
        }

        private List<string> GetOnlinePlayers()
        {
            List<string> players = new List<string>();
            foreach (SteamPlayer player in Provider.clients)
            {
                players.Add(player.playerID.steamID.ToString());
            }
            return players;
        }

        private void ProcessPurchases(List<Purchase> purchases)
        {
            foreach (Purchase purchase in purchases)
            {
                if (!IsValidPurchase(purchase))
                {
                    Logger.LogWarning($"Received invalid purchase data: {JsonConvert.SerializeObject(purchase)}");
                    continue;
                }

                try
                {
                    CSteamID steamId = new CSteamID(ulong.Parse(purchase.SteamId));
                    string command = string.Format(purchase.Command, steamId);
                    Logger.Log($"Executing command for purchase {purchase.Id}: {command}");

                    if (!ExecuteCommand(command))
                    {
                        Logger.LogError($"Failed to execute command for purchase {purchase.Id}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error processing purchase {purchase.Id}: {ex.Message}");
                }
            }
        }

        private bool IsValidPurchase(Purchase purchase)
        {
            return purchase != null &&
                   purchase.Id > 0 &&
                   !string.IsNullOrEmpty(purchase.SteamId) &&
                   !string.IsNullOrEmpty(purchase.Command);
        }

        private bool ExecuteCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                Logger.LogError("Received empty command.");
                return false;
            }
            return R.Commands.Execute(new ConsolePlayer(), command);
        }

        private void OnServerShutdown()
        {
            Task.Run(async () => await SendServerStatusAsync(0).ConfigureAwait(false)).Wait(1000);
        }

        public class Purchase
        {
            public int Id { get; set; }
            public string SteamId { get; set; }
            public string Command { get; set; }
        }
    }
}