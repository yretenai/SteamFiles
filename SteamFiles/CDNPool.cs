using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SteamKit2;

namespace SteamFiles {
    [PublicAPI]
    public class CDNPool : IDisposable {
        private readonly object Lock = new();

        public CDNPool(SteamHandler handler) {
            Handler = handler;
            new Thread(UpdatePool).Start();
        }

        public SteamHandler Handler { get; }
        private List<CDNClient.Server> Servers { get; } = new();
        public bool Running { get; private set; }

        private Dictionary<string, string> CDNKeys { get; } = new();
        private bool FirstLoopDone { get; set; }

        public void Dispose() {
            Running = false;
            GC.SuppressFinalize(this);
        }

        private void UpdatePool() {
            Running = true;

            while (Running) {
                try {
                    if (!Handler.Client.IsConnected) {
                        continue;
                    }

                    var servers = ContentServerDirectoryService.LoadAsync(Handler.Client.Configuration, (int)Handler.CellId, CancellationToken.None).Result;
                    if (servers.Count == 0) {
                        continue;
                    }

                    var eligibleServers = servers.Where(x => x.Type is "SteamCache" or "CDN").OrderBy(x => x.WeightedLoad).ToArray();
                    lock (Lock) {
                        Servers.Clear();
                        if (!Running) {
                            FirstLoopDone = true;
                            return;
                        }

                        Servers.AddRange(eligibleServers);
                    }

                    FirstLoopDone = eligibleServers.Length > 0;
                } catch (SteamKitWebRequestException ex) {
                    if (ex.StatusCode == HttpStatusCode.TooManyRequests) {
                        Thread.Sleep(TimeSpan.FromMinutes(1));
                    } else {
                        throw;
                    }
                } finally {
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                }
            }

            Running = false;
        }

        public CDNClient.Server? GetConnectionForAppId(uint appId) {
            lock (Lock) {
                return Servers.FirstOrDefault(x => x.AllowedAppIds == null || x.AllowedAppIds.Contains(appId));
            }
        }

        public async Task WaitUntilServers() {
            while (!FirstLoopDone) {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        public async Task<string> AuthenticateConnection(uint appId, uint depotId, CDNClient.Server server) {
            var host = server.Host!;
            if (host.EndsWith(".steampipe.steamcontent.com")) {
                host = "steampipe.steamcontent.com";
            } else if (host.EndsWith(".steamcontent.com")) {
                host = "steamcontent.com";
            }

            var cdnKey = $"{depotId}/{host}";

            if (!CDNKeys.TryGetValue(cdnKey, out var token)) {
                var request = await Handler.Apps.GetCDNAuthToken(appId, depotId, host);
                token = request.Token;
                CDNKeys[cdnKey] = token;
            }

            return token;
        }
    }
}
