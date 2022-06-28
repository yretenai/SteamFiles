using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace SteamFiles {
    public class SteamHandler {
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
            Steam = new SteamClient();
            User = Steam.GetHandler<SteamUser>()!;
            Apps = Steam.GetHandler<SteamApps>()!;
            Content = Steam.GetHandler<SteamContent>()!;

            Console.Out.WriteLine("Username: ");
            LogonDetails.Username = Console.In.ReadLine();
            Console.Out.WriteLine("Password: ");
            LogonDetails.Password = Console.In.ReadLine();
            Console.Out.WriteLine("2FA: ");
            LogonDetails.TwoFactorCode = Console.In.ReadLine();
            LogonDetails.ShouldRememberPassword = false;
            LogonDetails.LoginID = (uint)new Random((int)DateTimeOffset.Now.ToUnixTimeSeconds()).Next();

            Callbacks = new CallbackManager(Steam);

            Callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
            Callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
            Callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);
            Callbacks.Subscribe<SteamUser.LoggedOnCallback>(LoggedOnCallback);
            Callbacks.Subscribe<SteamUser.SessionTokenCallback>(SessionTokenCallback);
        }

        public Dictionary<string, List<Regex>> Rules { get; set; }

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

        public SteamClient Steam { get; set; }
        public SteamUser User { get; set; }
        public SteamApps Apps { get; set; }
        public SteamContent Content { get; set; }
        public CallbackManager Callbacks { get; set; }
        public Thread? CallbackThread { get; set; }
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

            Steam.Disconnect();
            Environment.Exit(0);
        }

        private void Connect() {
            Connected = false;
            Connecting = true;
            ConnectionBackoff = 0;

            ResetConnectionFlags();

            ConnectTime = DateTime.Now;
            Console.WriteLine("Connecting to Steam3...");
            Steam.Connect();
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
                Steam.Connect();
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
            var pics = await Apps.PICSGetProductInfo(ArraySegment<SteamApps.PICSRequest>.Empty, licenses.LicenseList.Select(x => new SteamApps.PICSRequest(x.PackageID)));

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

            var tags = new Dictionary<string, Dictionary<uint, string>>();

            Console.WriteLine("Requesting CDN server list");
            using var cdn = new CDNPool(this);
            await cdn.WaitUntilServers();
            using var cdnClient = new Client(Steam);
            
            if (!Directory.Exists("PICS")) {
                Directory.CreateDirectory("PICS");
            }

            if (!Directory.Exists("Manifests")) {
                Directory.CreateDirectory("Manifests");
            }

            foreach (var (appId, app) in appPics.Results?.SelectMany(x => x.Apps) ?? ArraySegment<KeyValuePair<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>>.Empty) {
                if (app.KeyValues["common"]["type"].AsString()?.ToLower() != "game") {
                    continue;
                }

                Console.WriteLine("Processing {0}", app.KeyValues["common"]["name"].Value ?? $"SteamApp{appId}");

                var picsPath = Path.Combine("PICS", $"{appId}.vdf");
                app.KeyValues.SaveToFile(picsPath, false);
                var cdnServers = cdn.GetConnectionsForAppId(appId);
                foreach (var cdnServer in cdnServers) {
                    try {
                        await ProcessApp(appId, app.KeyValues, depotIds, tags, cdnServer, cdnClient);
                    }
                    catch(SteamKitWebRequestException skwre) {
                        if (skwre.StatusCode == HttpStatusCode.ServiceUnavailable) {
                            continue;
                        }

                        throw;
                    }

                    break;
                }
            }
            
            await File.WriteAllTextAsync("Detected.json", JsonSerializer.Serialize(tags));

            Running = false;
        }

        private async Task ProcessApp(uint appId, KeyValue app, HashSet<uint> depotIds, Dictionary<string, Dictionary<uint, string>> tags, Server? cdn, Client cdnClient) {
            if (cdn == null) {
                // what?
                return;
            }

            var manifests = new Dictionary<uint, ulong>();
            var game = app["common"]["name"].Value ?? $"SteamApp{appId}";
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

            foreach (var (depotId, manifest) in manifests) {
                var manifestPath = Path.Combine("Manifests", $"{depotId}.txt");
                string[] files;
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
                    var manifestRequestCode = await Content.GetManifestRequestCode(depotId, appId, manifest, "public");
                    var manifestData = await cdnClient.DownloadManifestAsync(depotId, manifest, manifestRequestCode, cdn, key.Length > 0 ? key : null);
                    files = (manifestData.Files ?? new List<DepotManifest.FileData>()).Select(x => x.FileName.Replace("\\", "/")).ToArray();
                    await File.WriteAllTextAsync(manifestPath, string.Join(Environment.NewLine, files));
                } else {
                    files = await File.ReadAllLinesAsync(manifestPath);
                }
                
                var detected = Ruleset.Run(files, Rules);

                foreach (var detectedTag in detected) {
                    if (!tags.TryGetValue(detectedTag, out var detectedGames)) {
                        detectedGames = new Dictionary<uint, string>();
                        tags[detectedTag] = detectedGames;
                    }

                    detectedGames[appId] = game;
                }
            }
        }

        private class LogOnCredentials {
            public bool LoggedOn { get; set; }
            public ulong SessionToken { get; set; }

            public bool IsValid => LoggedOn;
        }
    }
}
