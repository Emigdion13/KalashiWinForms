namespace KalashiWinForms;

public class KalshiConfig
{
    public string KalshiEnvironment { get; set; } = "demo";
    public string KeyId { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public string PrivateKey { get; set; } = "";

    public string RestBaseUrl =>
        KalshiEnvironment == "demo"
            ? "https://external-api.demo.kalshi.co/trade-api/v2"
            : "https://external-api.kalshi.com/trade-api/v2";

    public string WebSocketUrl =>
        KalshiEnvironment == "demo"
            ? "wss://external-api-ws.demo.kalshi.co/trade-api/ws/v2"
            : "wss://external-api-ws.kalshi.com/trade-api/ws/v2";

    public string MarginRestBaseUrl =>
        KalshiEnvironment == "demo"
            ? "https://external-api.demo.kalshi.co/trade-api/v2/margin"
            : "https://external-api.kalshi.com/trade-api/v2/margin";

    public static KalshiConfig FromEnvFile(string envPath)
    {
        var config = new KalshiConfig();
        if (!File.Exists(envPath)) return config;

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;

            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();

            switch (key)
            {
                case "KALSHI_ENVIRONMENT":
                    config.KalshiEnvironment = value;
                    break;
                case "KALSHI_KEY_ID":
                    config.KeyId = value;
                    break;
                case "KALSHI_PRIVATE_KEY_PATH":
                    config.PrivateKeyPath = value.Replace("~",
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    break;
                case "KALSHI_PRIVATE_KEY":
                    config.PrivateKey = value;
                    break;
            }
        }

        return config;
    }
}
