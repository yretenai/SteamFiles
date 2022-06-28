using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace SteamFiles {
    public class CDNPool : IDisposable {
        private readonly object Lock = new();

        public CDNPool(SteamHandler handler) {
            Handler = handler;
            new Thread(UpdatePool).Start();
        }

        public SteamHandler Handler { get; }
        private List<Server> Servers { get; } = new();
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
                    if (!Handler.Steam.IsConnected) {
                        continue;
                    }

                    var servers = ContentServerDirectoryService.LoadAsync(Handler.Steam.Configuration, (int)Handler.CellId, CancellationToken.None).Result;
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

        public Server[] GetConnectionsForAppId(uint appId) {
            lock (Lock) {
                return Servers.Where(x => (x.AllowedAppIds.Length == 0 || x.AllowedAppIds.Contains(appId)) && x.Type is "SteamCache" or "CDN").ToArray();
            }
        }

        public async Task WaitUntilServers() {
            while (!FirstLoopDone) {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }
}
