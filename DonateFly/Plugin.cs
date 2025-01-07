﻿using Newtonsoft.Json;
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
        private volatile bool _isEnabled;
        private string _userAgent;
        private readonly object _syncLock = new object();
        private CancellationTokenSource _cancellationTokenSource;
        private const int REQUEST_TIMEOUT = 15000;
        private const string API_BASE_URL = "https://donatefly.shop/plugin/";
        private const int RETRY_DELAY_MS = 2000;
        private int _failedRequestCount;

        protected override void Load()
        {
            Instance = this;
            _isEnabled = true;
            _userAgent = $"{GetType().Assembly.GetName().Name}/{GetType().Assembly.GetName().Version}";
            Logger.Log($"DonateFly загружен. Версия: {_userAgent}");

            ValidateConfiguration();
            InitializeRequestLoop();
            Provider.onServerShutdown += OnServerShutdown;

            Task.Run(async () => await SendServerStatusAsync(1).ConfigureAwait(false));
        }

        protected override void Unload()
        {
            _isEnabled = false;
            Provider.onServerShutdown -= OnServerShutdown;
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            Instance = null;
            Logger.Log("DonateFly выгружен.");
        }

        private void InitializeRequestLoop()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Task.Run(async () => await StartRequestLoop(_cancellationTokenSource.Token).ConfigureAwait(false));
        }

        private void ValidateConfiguration()
        {
            if (Configuration.Instance.RequestFrequency < 5)
            {
                Logger.LogWarning("Частота запросов слишком низкая. Установлено минимальное значение (5 минут).");
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
                    await Task.Delay(Configuration.Instance.RequestFrequency * 60 * 1000, token).ConfigureAwait(false);
                    if (_isEnabled) await ProcessRequestWithRetry().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Ошибка в цикле запросов: {ex.Message}");
                    await Task.Delay(RETRY_DELAY_MS, token).ConfigureAwait(false);
                }
            }
        }

        private async Task ProcessRequestWithRetry()
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await SendRequestAsync().ConfigureAwait(false);
                    ResetFailedRequestCount();
                    return;
                }
                catch (WebException ex)
                {
                    Logger.LogError($"Попытка {attempt}/3: Ошибка сети: {ex.Status} - {ex.Message}");
                    if (attempt < 3) await Task.Delay(RETRY_DELAY_MS * attempt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Критическая ошибка при отправке запроса: {ex.Message}");
                    IncrementFailedRequestCount();
                    break;
                }
            }
        }

        private async Task SendRequestAsync()
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

            HttpWebRequest request = CreateWebRequest($"{API_BASE_URL}notifications/");
            await SendJsonRequest(request, requestData).ConfigureAwait(false);

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        List<Purchase> purchases = await ParseResponseAsync<List<Purchase>>(response).ConfigureAwait(false);
                        if (purchases?.Count > 0)
                        {
                            Logger.Log($"Получено {purchases.Count} новых покупок.");
                            ProcessPurchases(purchases);
                        }
                    }
                    else
                    {
                        Logger.LogError($"Неожиданный ответ от сервера: {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка при обработке ответа: {ex.Message}");
                throw;
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

                HttpWebRequest request = CreateWebRequest($"{API_BASE_URL}status/");
                await SendJsonRequest(request, requestData).ConfigureAwait(false);

                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        Logger.LogError($"Ошибка при отправке статуса сервера: {response.StatusCode}");
                    }
                }
            }
            catch (WebException ex)
            {
                Logger.LogError($"Ошибка сети при отправке статуса: {ex.Status} - {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Критическая ошибка при отправке статуса: {ex.Message}");
            }
        }

        private HttpWebRequest CreateWebRequest(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.UserAgent = _userAgent;
            request.Timeout = REQUEST_TIMEOUT;
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            return request;
        }

        private async Task SendJsonRequest(HttpWebRequest request, object data)
        {
            string json = JsonConvert.SerializeObject(data);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            request.ContentLength = bytes.Length;

            try
            {
                using (Stream stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка при отправке JSON данных: {ex.Message}");
                throw;
            }
        }

        private async Task<T> ParseResponseAsync<T>(HttpWebResponse response)
        {
            try
            {
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string json = await reader.ReadToEndAsync().ConfigureAwait(false);
                    return JsonConvert.DeserializeObject<T>(json);
                }
            }
            catch (JsonException ex)
            {
                Logger.LogError($"Ошибка при разборе JSON ответа: {ex.Message}");
                throw;
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
                    Logger.LogWarning($"Получены некорректные данные покупки: {JsonConvert.SerializeObject(purchase)}");
                    continue;
                }

                try
                {
                    CSteamID steamId = new CSteamID(ulong.Parse(purchase.SteamId));
                    string command = string.Format(purchase.Command, steamId);
                    Logger.Log($"Выполнение команды для покупки {purchase.Id}: {command}");

                    if (!ExecuteCommand(command))
                    {
                        Logger.LogError($"Не удалось выполнить команду для покупки {purchase.Id}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Ошибка при обработке покупки {purchase.Id}: {ex.Message}");
                }
            }
        }

        private bool IsValidPurchase(Purchase purchase) =>
            purchase != null && purchase.Id > 0 &&
            !string.IsNullOrEmpty(purchase.SteamId) &&
            !string.IsNullOrEmpty(purchase.Command);

        private bool ExecuteCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                Logger.LogError("Получена пустая команда.");
                return false;
            }
            return R.Commands.Execute(new ConsolePlayer(), command);
        }

        private void IncrementFailedRequestCount()
        {
            lock (_syncLock)
            {
                _failedRequestCount++;
                if (_failedRequestCount >= 3)
                {
                    Logger.LogError($"Достигнуто максимальное количество ошибок ({_failedRequestCount}).");
                }
            }
        }

        private void ResetFailedRequestCount()
        {
            lock (_syncLock)
            {
                _failedRequestCount = 0;
            }
        }

        private void OnServerShutdown()
        {
            Logger.Log("Отправка статуса выключения сервера...");
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