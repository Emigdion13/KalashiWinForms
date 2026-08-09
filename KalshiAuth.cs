using System.Security.Cryptography;
using System.Text;

namespace KalashiWinForms;

public class KalshiAuth
{
    private readonly RSA _rsa;
    public string KeyId { get; }

    public KalshiAuth(string keyId, string? privateKeyPath = null, string? privateKeyPem = null)
    {
        KeyId = keyId;

        string pem;
        if (!string.IsNullOrEmpty(privateKeyPath))
        {
            var path = privateKeyPath;
            if (path.StartsWith("~"))
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    path.TrimStart('~', '/', '\\'));
            pem = File.ReadAllText(path);
        }
        else if (!string.IsNullOrEmpty(privateKeyPem))
        {
            pem = privateKeyPem;
        }
        else
        {
            throw new ArgumentException("Either privateKeyPath or privateKeyPem is required");
        }

        _rsa = RSA.Create();
        _rsa.ImportFromPem(pem);
    }

    public string Sign(long timestampMs, string method, string path)
    {
        // Strip query params
        var pathWithoutQuery = path.Split('?')[0];
        var message = $"{timestampMs}{method}{pathWithoutQuery}";
        var messageBytes = Encoding.UTF8.GetBytes(message);

        var signature = _rsa.SignData(messageBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return Convert.ToBase64String(signature);
    }

    public Dictionary<string, string> GetHeaders(string method, string path)
    {
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signature = Sign(timestampMs, method, path);

        return new Dictionary<string, string>
        {
            ["KALSHI-ACCESS-KEY"] = KeyId,
            ["KALSHI-ACCESS-SIGNATURE"] = signature,
            ["KALSHI-ACCESS-TIMESTAMP"] = timestampMs.ToString(),
            ["Content-Type"] = "application/json"
        };
    }
}
