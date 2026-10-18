using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Wildwood.Net
{
    /// <summary>
    /// Starts and stops a co-op session. One player hosts and owns the world; friends join.
    /// For now friends join by IP address (same PC or same home network).
    /// Lobbies, relay and Steam invites come later and will plug in here.
    /// </summary>
    public static class Session
    {
        public const ushort DefaultPort = 7777;

        /// <summary>True while hosting, connected, or trying to connect.</summary>
        public static bool IsActive => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

        public static bool Host(ushort port = DefaultPort)
        {
            var manager = NetworkManager.Singleton;
            if (!TryGetTransport(manager, out var transport)) return false;

            // Listen on every network adapter so players on the same home network can join.
            transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
            return manager.StartHost();
        }

        public static bool Join(string address, ushort port = DefaultPort)
        {
            var manager = NetworkManager.Singleton;
            if (!TryGetTransport(manager, out var transport)) return false;

            transport.SetConnectionData(address.Trim(), port);
            return manager.StartClient();
        }

        public static void Leave()
        {
            var manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening) manager.Shutdown();
        }

        static bool TryGetTransport(NetworkManager manager, out UnityTransport transport)
        {
            transport = null;
            if (manager == null)
            {
                Debug.LogError("[Wildwood] There's no NetworkManager in the scene. Run Wildwood > Build Sandbox Scene.");
                return false;
            }
            if (manager.IsListening)
            {
                Debug.LogWarning("[Wildwood] Already in a session.");
                return false;
            }
            transport = manager.GetComponent<UnityTransport>();
            if (transport == null)
            {
                Debug.LogError("[Wildwood] The NetworkManager is missing its UnityTransport component.");
                return false;
            }
            return true;
        }
    }
}
