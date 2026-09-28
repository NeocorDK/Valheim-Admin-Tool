using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ValheimAdmin.Web
{
    /// <summary>
    /// Admin password and sessions of the plugin's own web server. The password is set in the
    /// plugin config ([Web] AdminPassword) and replaced by a PBKDF2 hash on start, like the agent's.
    /// Sessions are random tokens in an HttpOnly cookie; their hashes are kept on disk so a server
    /// restart does not sign the admin out.
    /// </summary>
    public sealed class WebAuth
    {
        public const string CookieName = "va_auth";
        private const int Iterations = 100000;
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
        private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(10);

        private readonly string sessionsFile;
        private readonly object gate = new object();
        private readonly Dictionary<string, DateTime> sessions = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, KeyValuePair<int, DateTime>> failures = new Dictionary<string, KeyValuePair<int, DateTime>>();
        private readonly Dictionary<string, Queue<DateTime>> attempts = new Dictionary<string, Queue<DateTime>>();

        public WebAuth(string dataDir)
        {
            sessionsFile = Path.Combine(dataDir, "sessions.json");
            LoadSessions();
        }

        /// <summary>Hashes a new plain password from the config; creates and logs one if there is none.</summary>
        public static void PreparePassword()
        {
            var plain = BepInExPlugin.WebAdminPassword;
            var hash = BepInExPlugin.WebAdminPasswordHash;
            if (!string.IsNullOrEmpty(plain.Value))
            {
                hash.Value = Hash(plain.Value);
                plain.Value = "";
            }
            if (string.IsNullOrEmpty(hash.Value))
            {
                var bytes = new byte[9];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                string generated = Convert.ToBase64String(bytes).Replace('/', 'x').Replace('+', 'y');
                hash.Value = Hash(generated);
                BepInExPlugin.Warn("Web panel admin password: " + generated + "   (change it with [Web] AdminPassword in the Valheim Admin config)");
            }
        }

        public static string Hash(string password)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            byte[] hash = Pbkdf2(password, salt, 32);
            return "pbkdf2$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
        }

        private static bool Check(string password, string stored)
        {
            string[] parts = (stored ?? "").Split('$');
            if (parts.Length != 3 || parts[0] != "pbkdf2") return false;
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);
            byte[] actual = Pbkdf2(password, salt, expected.Length);
            int diff = 0;
            for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ actual[i];
            return diff == 0;
        }

        /// <summary>
        /// PBKDF2-HMAC-SHA256 (RFC 8018), same output as .NET's Rfc2898DeriveBytes with SHA256, written
        /// out because Unity's Mono may lack that overload.
        /// </summary>
        private static byte[] Pbkdf2(string password, byte[] salt, int length)
        {
            var result = new byte[length];
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(password)))
            {
                int blocks = (length + 31) / 32;
                for (int block = 1; block <= blocks; block++)
                {
                    var input = new byte[salt.Length + 4];
                    Buffer.BlockCopy(salt, 0, input, 0, salt.Length);
                    input[salt.Length] = (byte)(block >> 24);
                    input[salt.Length + 1] = (byte)(block >> 16);
                    input[salt.Length + 2] = (byte)(block >> 8);
                    input[salt.Length + 3] = (byte)block;
                    byte[] u = hmac.ComputeHash(input);
                    var t = (byte[])u.Clone();
                    for (int i = 1; i < Iterations; i++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int j = 0; j < t.Length; j++) t[j] ^= u[j];
                    }
                    Buffer.BlockCopy(t, 0, result, (block - 1) * 32, Math.Min(32, length - (block - 1) * 32));
                }
            }
            return result;
        }

        /// <summary>Returns a session token, or throws HttpError (401 wrong password, 429 locked or too fast).</summary>
        public string Login(string ip, string password)
        {
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                if (!attempts.TryGetValue(ip, out var queue)) attempts[ip] = queue = new Queue<DateTime>();
                while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromMinutes(1)) queue.Dequeue();
                if (queue.Count >= 10) throw new HttpError(429, "Too many attempts, try again later");
                queue.Enqueue(now);
                if (failures.TryGetValue(ip, out var f) && f.Key >= 5 && now - f.Value < LockTime)
                    throw new HttpError(429, "Too many attempts, try again later");
            }

            // A password changed in the config file applies without a restart.
            try
            {
                BepInExPlugin.Instance.Config.Reload();
                PreparePassword();
            }
            catch (Exception e)
            {
                BepInExPlugin.Dbgl("Config reload failed: " + e.Message);
            }

            if (!Check(password ?? "", BepInExPlugin.WebAdminPasswordHash.Value))
            {
                lock (gate)
                {
                    DateTime now = DateTime.UtcNow;
                    failures[ip] = failures.TryGetValue(ip, out var f) && now - f.Value < LockTime
                        ? new KeyValuePair<int, DateTime>(f.Key + 1, f.Value)
                        : new KeyValuePair<int, DateTime>(1, now);
                }
                System.Threading.Thread.Sleep(700);
                throw new HttpError(401, "Wrong password");
            }

            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            lock (gate)
            {
                failures.Remove(ip);
                sessions[Digest(token)] = DateTime.UtcNow + SessionLifetime;
                SaveSessions();
            }
            return token;
        }

        public bool IsValid(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            lock (gate)
                return sessions.TryGetValue(Digest(token), out DateTime until) && until > DateTime.UtcNow;
        }

        public void Logout(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            lock (gate)
                if (sessions.Remove(Digest(token)))
                    SaveSessions();
        }

        private static string Digest(string token)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token)));
        }

        private void LoadSessions()
        {
            try
            {
                if (!File.Exists(sessionsFile)) return;
                foreach (var kv in Json.ParseObject(File.ReadAllText(sessionsFile)))
                {
                    var until = new DateTime(Convert.ToInt64(kv.Value), DateTimeKind.Utc);
                    if (until > DateTime.UtcNow) sessions[kv.Key] = until;
                }
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Loading web sessions failed: " + e.Message);
            }
        }

        private void SaveSessions()
        {
            try
            {
                var data = sessions.Where(kv => kv.Value > DateTime.UtcNow).ToDictionary(kv => kv.Key, kv => (object)kv.Value.Ticks);
                File.WriteAllText(sessionsFile, Json.Serialize(data));
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Saving web sessions failed: " + e.Message);
            }
        }
    }
}
