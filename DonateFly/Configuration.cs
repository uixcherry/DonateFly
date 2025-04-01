using Rocket.API;
using UnityEngine;

namespace DonateFly
{
    public class Configuration : IRocketPluginConfiguration
    {
        public string ServerID { get; set; }
        public string ServerKey { get; set; }
        public string ApiUrl { get; set; }

        public int CacheLifetimeSeconds { get; set; }
        public int CommandCooldownSeconds { get; set; }

        public int ItemsPerPage { get; set; }

        public string SuccessColor { get; set; }
        public string ErrorColor { get; set; }
        public string InfoColor { get; set; }
        public string HeaderColor { get; set; }
        public string TextColor { get; set; }

        public void LoadDefaults()
        {
            ServerID = "your_server_id";
            ServerKey = "your_server_key";
            ApiUrl = "https://donatefly.shop/plugin";

            CacheLifetimeSeconds = 120;
            CommandCooldownSeconds = 3;

            ItemsPerPage = 5;

            SuccessColor = "#00FF00";
            ErrorColor = "#FF0000";
            InfoColor = "#FFFF00"; 
            HeaderColor = "#00FFFF";
            TextColor = "#FFFFFF";
        }

        public Color GetSuccessColor() => HexToColor(SuccessColor, Color.green);
        public Color GetErrorColor() => HexToColor(ErrorColor, Color.red);
        public Color GetInfoColor() => HexToColor(InfoColor, Color.yellow);
        public Color GetHeaderColor() => HexToColor(HeaderColor, Color.cyan);
        public Color GetTextColor() => HexToColor(TextColor, Color.white);

        private Color HexToColor(string hex, Color defaultColor)
        {
            if (string.IsNullOrEmpty(hex))
                return defaultColor;

            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6)
                {
                    int r = int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                    int g = int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                    int b = int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                    return new Color(r / 255f, g / 255f, b / 255f);
                }
                return defaultColor;
            }
            catch
            {
                return defaultColor;
            }
        }
    }
}