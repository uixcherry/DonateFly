using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DonateFly.Commands
{
    public class DonateCommand : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "donate";
        public string Help => Plugin.Instance.Translate("donate_help");
        public string Syntax => Plugin.Instance.Translate("donate_syntax");
        public List<string> Aliases => new List<string> { "df", "store" };
        public List<string> Permissions => new List<string> { "donateFly.donate" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            UnturnedPlayer player = (UnturnedPlayer)caller;

            if (!CheckCooldown(player))
                return;

            if (command.Length == 0)
            {
                ShowUsage(player);
                return;
            }

            switch (command[0].ToLower())
            {
                case "list":
                    int page = 1;
                    if (command.Length > 1 && int.TryParse(command[1], out int parsedPage) && parsedPage > 0)
                    {
                        page = parsedPage;
                    }
                    ShowItemsList(player, page);
                    break;

                case "get":
                    if (command.Length < 2)
                    {
                        UnturnedChat.Say(player, Plugin.Instance.Translate("donate_use_get"),
                                        Plugin.Instance.Configuration.Instance.GetInfoColor());
                        return;
                    }

                    if (!int.TryParse(command[1], out int itemId))
                    {
                        UnturnedChat.Say(player, Plugin.Instance.Translate("donate_id_must_be_number"),
                                        Plugin.Instance.Configuration.Instance.GetErrorColor());
                        return;
                    }

                    RedeemItem(player, itemId);
                    break;

                default:
                    ShowUsage(player);
                    break;
            }
        }

        private bool CheckCooldown(UnturnedPlayer player)
        {
            if (!Plugin.Instance.CheckCommandCooldown(player.CSteamID.m_SteamID))
            {
                int seconds = Plugin.Instance.GetCooldownSecondsRemaining(player.CSteamID.m_SteamID);
                UnturnedChat.Say(player, Plugin.Instance.Translate("donate_cooldown", seconds),
                                Plugin.Instance.Configuration.Instance.GetInfoColor());
                return false;
            }
            return true;
        }

        private void ShowUsage(UnturnedPlayer player)
        {
            UnturnedChat.Say(player, Plugin.Instance.Translate("donate_usage", Syntax),
                            Plugin.Instance.Configuration.Instance.GetInfoColor());
        }

        private void ShowItemsList(UnturnedPlayer player, int page)
        {
            UnturnedChat.Say(player, Plugin.Instance.Translate("donate_loading_items"),
                            Plugin.Instance.Configuration.Instance.GetInfoColor());

            List<PlayerItem> items = Plugin.Instance.GetPlayerItems(player.CSteamID.m_SteamID);

            if (items.Count == 0)
            {
                UnturnedChat.Say(player, Plugin.Instance.Translate("donate_no_items"),
                                Plugin.Instance.Configuration.Instance.GetInfoColor());
                return;
            }

            int itemsPerPage = Plugin.Instance.Configuration.Instance.ItemsPerPage;
            int totalPages = (int)Math.Ceiling(items.Count / (double)itemsPerPage);

            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            int startIndex = (page - 1) * itemsPerPage;
            int endIndex = Math.Min(startIndex + itemsPerPage, items.Count);

            UnturnedChat.Say(player, Plugin.Instance.Translate("donate_available_items"),
                            Plugin.Instance.Configuration.Instance.GetHeaderColor());

            if (totalPages > 1)
            {
                UnturnedChat.Say(player, Plugin.Instance.Translate("donate_page_info", page, totalPages),
                                Color.gray);
            }

            for (int i = startIndex; i < endIndex; i++)
            {
                PlayerItem item = items[i];
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("[{0}] {1} - {2} (Получен: {3})",
                                item.Id,
                                item.Name,
                                item.Description,
                                item.ReceivedDate.ToString("dd.MM.yyyy"));

                UnturnedChat.Say(player, sb.ToString(),
                                Plugin.Instance.Configuration.Instance.GetTextColor());
            }

            UnturnedChat.Say(player, Plugin.Instance.Translate("donate_how_to_get"),
                            Plugin.Instance.Configuration.Instance.GetInfoColor());

            if (totalPages > 1)
            {
                UnturnedChat.Say(player, Plugin.Instance.Translate("donate_page_help"),
                                Color.gray);
            }
        }

        private void RedeemItem(UnturnedPlayer player, int itemId)
        {
            UnturnedChat.Say(player, Plugin.Instance.Translate("donate_redeeming", itemId),
                            Plugin.Instance.Configuration.Instance.GetInfoColor());

            ItemRedeemResult result = Plugin.Instance.RedeemItem(player.CSteamID.m_SteamID, itemId);

            if (result.Success)
            {
                UnturnedChat.Say(player, Plugin.Instance.Translate("donate_success"),
                                Plugin.Instance.Configuration.Instance.GetSuccessColor());
            }
            else
            {
                UnturnedChat.Say(player, Plugin.Instance.Translate("donate_error", result.Message),
                                Plugin.Instance.Configuration.Instance.GetErrorColor());
            }
        }
    }
}