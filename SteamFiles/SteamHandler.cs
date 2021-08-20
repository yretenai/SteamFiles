using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Newtonsoft.Json;
using SteamFiles.Processor;
using SteamKit2;

namespace SteamFiles {
    using RuleDictionary = Dictionary<string, List<Regex>>;

    [PublicAPI]
    public class SteamHandler {
        private readonly object SteamLock = new();

        public SteamHandler() {
            var path = Environment.GetEnvironmentVariable("FILE_DETECTION_RULE_SETS_PATH");
            if (!Directory.Exists(path)) {
                throw new InvalidDataException("Set enviornment variable FILE_DETECTION_RULE_SETS_PATH");
            }

            var rules = Path.Combine(path!, "rules.ini");
            if (!File.Exists(rules)) {
                throw new InvalidDataException("Can't find rules.ini");
            }

            Rules = Ruleset.Parse(rules);

            Client = new SteamClient();
            User = Client.GetHandler<SteamUser>()!;
            Apps = Client.GetHandler<SteamApps>()!;

            LogonDetails.Username = Environment.GetCommandLineArgs()[^1];
            Console.Out.WriteLine("Password: ");
            LogonDetails.Password = Console.In.ReadLine();
            Console.Out.WriteLine("2FA: ");
            LogonDetails.TwoFactorCode = Console.In.ReadLine();
            LogonDetails.ShouldRememberPassword = false;
            LogonDetails.LoginID = (uint)new Random((int)DateTimeOffset.Now.ToUnixTimeSeconds()).Next();

            Callbacks = new CallbackManager(Client);

            Callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
            Callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
            Callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);
            Callbacks.Subscribe<SteamUser.LoggedOnCallback>(LoggedOnCallback);
            Callbacks.Subscribe<SteamUser.SessionTokenCallback>(SessionTokenCallback);
        }

        private SteamUser.LogOnDetails LogonDetails { get; set; } = new();
        private LogOnCredentials Credentials { get; } = new();

        private bool Running { get; set; }
        private bool Connected { get; set; }
        private bool Connecting { get; set; }
        private bool ExpectingDisconnectRemote { get; set; }
        private bool DidDisconnect { get; set; }
        private bool IsConnectionRecovery { get; set; }
        private int ConnectionBackoff { get; set; }
        private DateTime ConnectTime { get; set; }
        private Dictionary<uint, byte[]> DepotKeys { get; } = new();

        public SteamClient Client { get; set; }
        public SteamUser User { get; set; }
        public SteamApps Apps { get; set; }
        public CallbackManager Callbacks { get; set; }
        public Thread? CallbackThread { get; set; }
        public RuleDictionary Rules { get; set; }
        public uint CellId { get; private set; }

        public void Run() {
            CallbackThread = new Thread(RunCallbacks);
            CallbackThread.Start();

            Thread.Sleep(TimeSpan.FromSeconds(1));

            Connect();
        }

        private void RunCallbacks() {
            Running = true;

            while (Running) {
                Callbacks.RunWaitAllCallbacks(TimeSpan.FromSeconds(1));
            }

            Running = false;

            Client.Disconnect();
            Environment.Exit(0);
        }

        private void Connect() {
            Connected = false;
            Connecting = true;
            ConnectionBackoff = 0;

            ResetConnectionFlags();

            ConnectTime = DateTime.Now;
            Console.WriteLine("Connecting to Steam3...");
            Client.Connect();
        }

        private void ResetConnectionFlags() {
            ExpectingDisconnectRemote = false;
            DidDisconnect = false;
            IsConnectionRecovery = false;
        }

        private void ConnectedCallback(SteamClient.ConnectedCallback connected) {
            Connecting = false;
            Connected = true;
            Console.WriteLine("Logging into Steam3...");
            User.LogOn(LogonDetails);
        }

        private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected) {
            DidDisconnect = true;

            if (!IsConnectionRecovery &&
                (disconnected.UserInitiated || ExpectingDisconnectRemote)) {
                Console.WriteLine("Disconnected from Steam");
            } else if (ConnectionBackoff >= 10) {
                Console.WriteLine("Could not connect to Steam after 10 tries");
                Running = false;
            } else if (Running) {
                Console.WriteLine(Connecting ? "Connection to Steam failed. Trying again" : "Lost connection to Steam. Reconnecting");
                Thread.Sleep(1000 * ++ConnectionBackoff);
                ResetConnectionFlags();
                Client.Connect();
            }
        }

        private void LoggedOnCallback(SteamUser.LoggedOnCallback loggedOn) {
            if (loggedOn.Result != EResult.OK) {
                Console.WriteLine("Unable to login to Steam3: {0} / {1}", loggedOn.Result, loggedOn.ExtendedResult);
                Running = false;
                return;
            }

            Credentials.LoggedOn = true;

            LogonDetails.CellID = loggedOn.CellID;
            CellId = loggedOn.CellID;
        }

        private void SessionTokenCallback(SteamUser.SessionTokenCallback sessionToken) {
            Credentials.SessionToken = sessionToken.SessionToken;
        }

        private async void LicenseListCallback(SteamApps.LicenseListCallback licenses) {
            Console.WriteLine("Got license list, request app and depot list");
            var pics = await Apps.PICSGetProductInfo(ArraySegment<uint>.Empty, licenses.LicenseList.Select(x => x.PackageID));

            var appIds = new HashSet<uint>();
            var depotIds = new HashSet<uint>();
            foreach (var (_, value) in pics.Results?.SelectMany(x => x.Packages) ?? ArraySegment<KeyValuePair<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>>.Empty) {
                foreach (var appIdStr in value.KeyValues["appids"].Children) {
                    if (uint.TryParse(appIdStr.Value, out var appid)) {
                        appIds.Add(appid);
                    }
                }

                foreach (var depotIdStr in value.KeyValues["depotids"].Children) {
                    if (uint.TryParse(depotIdStr.Value, out var depotid)) {
                        depotIds.Add(depotid);
                    }
                }
            }

            Console.WriteLine("{0} apps, {1} depots", appIds.Count, depotIds.Count);

            Console.WriteLine("Requesting app tokens");
            var accessTokens = await Apps.PICSGetAccessTokens(appIds, ArraySegment<uint>.Empty);
            var appTokens = new Dictionary<uint, ulong>();
            foreach (var (appId, accessToken) in accessTokens.AppTokens) {
                appTokens[appId] = accessToken;
            }

            Console.WriteLine("{0} tokens", appTokens.Count);

            Console.WriteLine("Requesting app infos");
            var appPics = await Apps.PICSGetProductInfo(appIds.Select(x => new SteamApps.PICSRequest { AccessToken = appTokens.ContainsKey(x) ? appTokens[x] : 0, ID = x }), ArraySegment<SteamApps.PICSRequest>.Empty);

            var tags = new Dictionary<string, HashSet<TagInfo>>();

            Console.WriteLine("Requesting CDN server list");
            using var cdn = new CDNPool(this);
            await cdn.WaitUntilServers();
            using var cdnClient = new CDNClient(Client);

            foreach (var (appId, app) in appPics.Results?.SelectMany(x => x.Apps) ?? ArraySegment<KeyValuePair<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>>.Empty) {
                if (app.KeyValues["common"]["type"].AsString()?.ToLower() != "game") {
                    continue;
                }

                Console.WriteLine("Processing {0}", app.KeyValues["common"]["name"].Value ?? $"SteamApp{appId}");

                await ProcessApp(appId, app.KeyValues, depotIds, tags, cdn, cdn.GetConnectionForAppId(appId), cdnClient);
            }

            await File.WriteAllTextAsync("Detected.json", JsonConvert.SerializeObject(tags, Formatting.Indented));

            Running = false;
        }

        private async Task ProcessApp(uint appId, KeyValue app, HashSet<uint> depotIds, Dictionary<string, HashSet<TagInfo>> tags, CDNPool pool, CDNClient.Server? cdn, CDNClient cdnClient) {
            if (cdn == null) {
                // what?
                return;
            }

            var manifests = new Dictionary<uint, ulong>();
            var game = new TagInfo(appId, app["common"]["name"].Value ?? $"SteamApp{appId}");
            foreach (var depot in app["depots"].Children.Where(depot => depot["manifests"]["public"].Value != null)) {
                if (depot["config"]["oslist"].AsString()?.Length > 0) {
                    if (!depot["config"]["oslist"].AsString()!.Contains("win")) {
                        continue;
                    }
                }

                if (uint.TryParse(depot.Name, out var depotId) &&
                    depotIds.Contains(depotId) &&
                    ulong.TryParse(depot["manifests"]["public"].AsString(), out var manifestId)) {
                    manifests[depotId] = manifestId;
                }
            }

            if (!Directory.Exists("Manifests")) {
                Directory.CreateDirectory("Manifests");
            }

            foreach (var (depotId, manifest) in manifests) {
                var manifestPath = Path.Combine("Manifests", $"{depotId}.json");
                List<string> files;
                if (!File.Exists(manifestPath)) {
                    if (!DepotKeys.ContainsKey(depotId)) {
                        var depotKey = await Apps.GetDepotDecryptionKey(depotId, appId);
                        if (depotKey.Result == EResult.OK) {
                            DepotKeys[depotId] = depotKey.DepotKey;
                        } else {
                            DepotKeys[depotId] = Array.Empty<byte>();
                        }
                    }

                    var key = DepotKeys[depotId];
                    var auth = await pool.AuthenticateConnection(appId, depotId, cdn);
                    var manifestData = await cdnClient.DownloadManifestAsync(depotId, manifest, cdn, auth, key.Length > 0 ? key : null);
                    files = (manifestData.Files ?? new List<DepotManifest.FileData>()).Select(x => x.FileName.Replace("\\", "/")).ToList();
                    await File.WriteAllTextAsync(manifestPath, JsonConvert.SerializeObject(files));
                } else {
                    files = JsonConvert.DeserializeObject<List<string>>(await File.ReadAllTextAsync(manifestPath));
                }

                var detected = Ruleset.Run(files, Rules);

                foreach (var detectedTag in detected) {
                    if (!tags.TryGetValue(detectedTag, out var detectedGames)) {
                        detectedGames = new HashSet<TagInfo>();
                        tags[detectedTag] = detectedGames;
                    }

                    detectedGames.Add(game);
                }
            }
        }

        [PublicAPI]
        public class LogOnCredentials {
            public bool LoggedOn { get; set; }
            public ulong SessionToken { get; set; }

            public bool IsValid => LoggedOn;
        }

        public record TagInfo(uint AppId, string Name);
    }
}
