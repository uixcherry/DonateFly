using DonateFly.Data;
using Newtonsoft.Json;
using Rocket.API;
using Rocket.API.Collections;
using Rocket.Core;
using Rocket.Core.Plugins;
using SDG.Unturned;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Logger = Rocket.Core.Logging.Logger;

namespace DonateFly
{
    public class Plugin : RocketPlugin<Configuration>
    {
        #region Fields and Properties
        public static Plugin Instance { get; private set; }
        private string _userAgent;
        private readonly object _commandLock = new object();

        private readonly Dictionary<ulong, CachedItems> _itemsCache = new Dictionary<ulong, CachedItems>();
        private readonly Dictionary<ulong, DateTime> _cooldowns = new Dictionary<ulong, DateTime>();
        private Dictionary<string, object> _baseRequestData;

        private const int REQUEST_TIMEOUT = 15000;
        #endregion

        #region Rocket Plugin Lifecycle
        protected override void Load()
        {
            Instance = this;
            _userAgent = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}/{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}";

            ServicePointManager.DefaultConnectionLimit = 10;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

            _baseRequestData = new Dictionary<string, object>
            {
                { "server_id", Configuration.Instance.ServerID },
                { "server_key", Configuration.Instance.ServerKey }
            };

            Provider.onServerShutdown += OnServerShutdown;
            Logger.Log($"DonateFly loaded. Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        }

        protected override void Unload()
        {
            Provider.onServerShutdown -= OnServerShutdown;

            _itemsCache.Clear();
            _cooldowns.Clear();

            Instance = null;
            Logger.Log("DonateFly unloaded.");
        }
        #endregion

        #region API Methods
        public List<PlayerItem> GetPlayerItems(ulong steamId)
        {
            if (IsCacheValid(steamId))
            {
                return _itemsCache[steamId].Items;
            }

            Dictionary<string, object> requestData = new Dictionary<string, object>(_baseRequestData)
            {
                { "steam_id", steamId.ToString() }
            };

            try
            {
                HttpWebRequest request = CreateWebRequest($"{Configuration.Instance.ApiUrl}/items/player/");
                SendJsonRequest(request, requestData);

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        List<PlayerItem> items = ParseResponse<List<PlayerItem>>(response);

                        items.Sort((a, b) => b.ReceivedDate.CompareTo(a.ReceivedDate));

                        UpdateCache(steamId, items);

                        return items;
                    }
                    else
                    {
                        LogHttpError("Не удалось получить список предметов", response.StatusCode);
                        return GetCachedOrEmpty(steamId);
                    }
                }
            }
            catch (WebException ex)
            {
                LogWebException("Ошибка сети при получении списка предметов", ex);
                return GetCachedOrEmpty(steamId);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка при получении списка предметов: {ex.Message}");
                return GetCachedOrEmpty(steamId);
            }
        }

        public ItemRedeemResult RedeemItem(ulong steamId, int itemId)
        {
            Dictionary<string, object> requestData = new Dictionary<string, object>(_baseRequestData)
            {
                { "steam_id", steamId.ToString() },
                { "item_id", itemId }
            };

            try
            {
                HttpWebRequest request = CreateWebRequest($"{Configuration.Instance.ApiUrl}/items/redeem/");
                SendJsonRequest(request, requestData);

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        ItemRedeemResult result = ParseResponse<ItemRedeemResult>(response);

                        if (!string.IsNullOrEmpty(result.Command))
                        {
                            string formattedCommand = string.Format(result.Command, steamId);
                            bool executed = ExecuteCommand(formattedCommand);
                            result.Success = executed;

                            if (executed && _itemsCache.ContainsKey(steamId))
                            {
                                _itemsCache[steamId].Items.RemoveAll(item => item.Id == itemId);
                            }
                        }

                        return result;
                    }
                    else
                    {
                        LogHttpError("Не удалось активировать предмет", response.StatusCode);
                        return new ItemRedeemResult
                        {
                            Success = false,
                            Message = $"Ошибка сервера: {response.StatusCode}"
                        };
                    }
                }
            }
            catch (WebException ex)
            {
                string errorDetails = GetWebExceptionDetails(ex);
                LogWebException("Ошибка сети при активации предмета", ex);
                return new ItemRedeemResult
                {
                    Success = false,
                    Message = $"Ошибка сети: {errorDetails}"
                };
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка при активации предмета: {ex.Message}");
                return new ItemRedeemResult
                {
                    Success = false,
                    Message = $"Ошибка: {ex.Message}"
                };
            }
        }
        #endregion

        #region HTTP Methods
        private HttpWebRequest CreateWebRequest(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.UserAgent = _userAgent;
            request.Timeout = REQUEST_TIMEOUT;
            request.KeepAlive = true;

            request.Headers.Add("Accept-Encoding", "gzip,deflate");
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            return request;
        }

        private void SendJsonRequest(HttpWebRequest request, object data)
        {
            string json = JsonConvert.SerializeObject(data);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            request.ContentLength = bytes.Length;

            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private T ParseResponse<T>(HttpWebResponse response)
        {
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream))
            {
                string json = reader.ReadToEnd();
                return JsonConvert.DeserializeObject<T>(json);
            }
        }

        private string GetWebExceptionDetails(WebException ex)
        {
            if (ex.Response == null)
                return ex.Message;

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)ex.Response)
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    return $"{response.StatusCode}: {reader.ReadToEnd()}";
                }
            }
            catch
            {
                return ex.Message;
            }
        }

        private void LogHttpError(string message, HttpStatusCode statusCode)
        {
            Logger.LogError($"{message}: {statusCode}");
        }

        private void LogWebException(string message, WebException ex)
        {
            string details = GetWebExceptionDetails(ex);
            Logger.LogError($"{message}: {ex.Status} - {details}");
        }
        #endregion

        #region Cache Methods
        private bool IsCacheValid(ulong steamId)
        {
            if (_itemsCache.TryGetValue(steamId, out CachedItems cache))
            {
                return (DateTime.UtcNow - cache.LastUpdated).TotalSeconds < Configuration.Instance.CacheLifetimeSeconds;
            }
            return false;
        }

        private void UpdateCache(ulong steamId, List<PlayerItem> items)
        {
            _itemsCache[steamId] = new CachedItems
            {
                Items = items,
                LastUpdated = DateTime.UtcNow
            };
        }

        private List<PlayerItem> GetCachedOrEmpty(ulong steamId)
        {
            if (_itemsCache.TryGetValue(steamId, out CachedItems cache))
            {
                return cache.Items;
            }
            return new List<PlayerItem>();
        }
        #endregion

        #region Command Methods
        public bool ExecuteCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
                return false;

            try
            {
                lock (_commandLock)
                {
                    Logger.Log($"Выполнение команды: {command}");
                    bool result = R.Commands.Execute(new ConsolePlayer(), command);
                    Logger.Log($"Результат выполнения: {result}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка выполнения команды: {ex.Message}");
                return false;
            }
        }

        public bool CheckCommandCooldown(ulong steamId)
        {
            if (_cooldowns.TryGetValue(steamId, out DateTime lastUsed))
            {
                TimeSpan elapsed = DateTime.UtcNow - lastUsed;
                if (elapsed.TotalSeconds < Configuration.Instance.CommandCooldownSeconds)
                {
                    return false;
                }
            }

            _cooldowns[steamId] = DateTime.UtcNow;
            return true;
        }

        public int GetCooldownSecondsRemaining(ulong steamId)
        {
            if (_cooldowns.TryGetValue(steamId, out DateTime lastUsed))
            {
                TimeSpan elapsed = DateTime.UtcNow - lastUsed;
                int remaining = Configuration.Instance.CommandCooldownSeconds - (int)elapsed.TotalSeconds;
                return remaining > 0 ? remaining : 0;
            }
            return 0;
        }
        #endregion

        #region Helper Methods
        private void SendServerStatus(int status)
        {
            Dictionary<string, object> requestData = new Dictionary<string, object>(_baseRequestData)
            {
                { "status", status }
            };

            try
            {
                HttpWebRequest request = CreateWebRequest($"{Configuration.Instance.ApiUrl}/status/");
                SendJsonRequest(request, requestData);
                request.GetResponse().Close();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка отправки статуса сервера: {ex.Message}");
            }
        }

        private void OnServerShutdown()
        {
            try
            {
                SendServerStatus(0);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка отправки статуса при выключении: {ex.Message}");
            }
        }
        #endregion

        #region Translations
        public override TranslationList DefaultTranslations => new TranslationList
        {
            { "donate_help", "Управление купленными донат-предметами" },
            { "donate_syntax", "/donate [list|get <id>]" },
            { "donate_usage", "Используйте: {0}" },
            { "donate_use_get", "Используйте: /donate get <id>" },
            { "donate_id_must_be_number", "ID должен быть числом" },
            { "donate_loading_items", "Загружаем ваши предметы..." },
            { "donate_no_items", "У вас нет доступных предметов" },
            { "donate_available_items", "Ваши доступные предметы:" },
            { "donate_item_format", "[{0}] {1} - {2} (Получен: {3})" },
            { "donate_how_to_get", "Используйте /donate get <id> чтобы получить предмет" },
            { "donate_redeeming", "Активируем предмет #{0}..." },
            { "donate_success", "Предмет успешно активирован!" },
            { "donate_error", "Ошибка: {0}" },
            { "donate_cooldown", "Подождите ещё {0} сек. перед использованием команды" },
            { "donate_page_info", "Страница {0} из {1}" },
            { "donate_page_help", "Используйте /donate list <страница> для просмотра других страниц" }
        };
        #endregion
    }
}