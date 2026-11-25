using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using System;
using System.IO;
using System.Security.Cryptography;

namespace SpreadBot.Dashboard
{
    public class DashboardSettings
    {
        public bool Enabled { get; set; }

        //Bound to this machine only; `tailscale serve` (or another TLS proxy) is how a phone reaches it, and adds the HTTPS that
        //installing the app and push notifications need
        public string Url { get; set; } = "http://127.0.0.1:5080";

        //The HTTPS address the dashboard is opened at (e.g. https://box.tailnet-name.ts.net): push notifications are sent in its name
        //and link back to it. Without it there are no push notifications
        public string PublicUrl { get; set; }
    }

    /// <summary>
    /// The dashboard's access token and push notification (VAPID) keys, created once in <c>dashboard-secrets.json</c> next to the
    /// settings file. Never logged or served, except the VAPID public key.
    /// </summary>
    public class DashboardSecrets
    {
        public const string FileName = "dashboard-secrets.json";

        public string AccessToken { get; set; }
        public string VapidPublicKey { get; set; }
        public string VapidPrivateKey { get; set; }

        public static DashboardSecrets LoadOrCreate(string directory)
        {
            var path = Path.Combine(directory, FileName);

            var secrets = File.Exists(path) ? JsonConvert.DeserializeObject<DashboardSecrets>(File.ReadAllText(path)) : new DashboardSecrets();
            var changed = false;

            if (string.IsNullOrWhiteSpace(secrets.AccessToken))
            {
                secrets.AccessToken = Base64Url(RandomNumberGenerator.GetBytes(32));
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(secrets.VapidPublicKey) || string.IsNullOrWhiteSpace(secrets.VapidPrivateKey))
            {
                var keys = WebPush.VapidHelper.GenerateVapidKeys();
                secrets.VapidPublicKey = keys.PublicKey;
                secrets.VapidPrivateKey = keys.PrivateKey;
                changed = true;
            }

            if (changed)
            {
                Write(path, JsonConvert.SerializeObject(secrets, Formatting.Indented));
                Logger.Instance.LogMessage($"Created the dashboard's access token and push keys in {path}");
            }

            return secrets;
        }

        //Atomic, and owner-only from the moment the file exists (on Unix): the token is never readable by others, even briefly
        private static void Write(string path, string content)
        {
            var temporary = path + ".tmp";
            File.Delete(temporary);

            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var writer = new StreamWriter(new FileStream(temporary, options)))
                writer.Write(content);

            File.Move(temporary, path, overwrite: true);
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
