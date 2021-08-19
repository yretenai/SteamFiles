using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SteamKit2;

namespace SteamFiles {
    [PublicAPI]
    public class SteamHandler {
        [PublicAPI]
        public class LogOnCredentials {
            public bool LoggedOn { get; set; }
            public ulong SessionToken { get; set; }

            public bool IsValid => LoggedOn;
        }

        private SteamUser.LogOnDetails LogonDetails { get; set; } = new();
        private LogOnCredentials Credentials { get; } = new();

        public Dictionary<uint, ulong> AppTokens { get; } = new();
        public Dictionary<uint, ulong> PackageTokens { get; } = new();
        public Dictionary<uint, byte[]> DepotKeys { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource<SteamApps.CDNAuthTokenCallback>> CDNAuthTokens { get; } = new();
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; } = new();
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; } = new();

        private bool Running { get; set; }
        private bool Connected { get; set; }
        private bool Connecting { get; set; }
        private bool ExpectingDisconnectRemote { get; set; }
        private bool DidDisconnect { get; set; }
        private bool IsConnectionRecovery { get; set; }
        private int ConnectionBackoff { get; set; }
        private DateTime ConnectTime { get; set; }

        public SteamClient Client { get; set; }
        public SteamUser User { get; set; }
        public SteamApps Apps { get; set; }
        public CallbackManager Callbacks { get; set; }
        public Thread? CallbackThread { get; set; }

        private readonly object SteamLock = new();

        public SteamHandler() {
            Client = new SteamClient();
            User = Client.GetHandler<SteamUser>()!;
            Apps = Client.GetHandler<SteamApps>()!;

            LogonDetails.Username = Environment.GetCommandLineArgs()[^3];
            LogonDetails.Password = Environment.GetCommandLineArgs()[^2];
            LogonDetails.TwoFactorCode = Environment.GetCommandLineArgs()[^1];
            LogonDetails.ShouldRememberPassword = false;
            LogonDetails.LoginID = (uint)new Random((int)DateTimeOffset.Now.ToUnixTimeSeconds()).Next();

            Callbacks = new CallbackManager(Client);

            Callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
            Callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
            // Callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);
            Callbacks.Subscribe<SteamUser.LoggedOnCallback>(LoggedOnCallback);
            Callbacks.Subscribe<SteamUser.SessionTokenCallback>(SessionTokenCallback);
        }

        public void Run() {
            CallbackThread = new Thread(RunCallbacks);
            CallbackThread.Start();

            Thread.Sleep(TimeSpan.FromSeconds(1));

            Connect();
        }

        private void RunCallbacks() {
            Running = true;

            while (Running) {
                Debug.WriteLine("Callbacks");
                Callbacks.RunWaitAllCallbacks(TimeSpan.FromSeconds(1));
            }

            Running = false;
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

            Console.WriteLine("Using Steam3 suggested CellID: " + loggedOn.CellID);
            LogonDetails.CellID = loggedOn.CellID;
        }

        private void SessionTokenCallback(SteamUser.SessionTokenCallback sessionToken) {
            Console.WriteLine("Got session token!");
            Credentials.SessionToken = sessionToken.SessionToken;
        }
    }
}
