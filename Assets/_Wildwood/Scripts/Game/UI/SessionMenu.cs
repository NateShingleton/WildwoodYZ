using Unity.Netcode;
using UnityEngine;
using Wildwood.Net;

namespace Wildwood.Game.UI
{
    /// <summary>
    /// Temporary host/join panel for testing, drawn with Unity's built-in debug GUI.
    /// It will be replaced by a real menu once lobbies and invites exist.
    /// </summary>
    public class SessionMenu : MonoBehaviour
    {
        /// <summary>Screen area the panel covers (GUI coordinates, origin top-left).</summary>
        public static Rect ScreenRect { get; private set; }

        [SerializeField] string address = "127.0.0.1";
        [SerializeField] string port = "7777";

        string _status = "";
        bool _wasActive;

        void Update()
        {
            bool active = Session.IsActive;
            if (_wasActive && !active)
            {
                var manager = NetworkManager.Singleton;
                string reason = manager != null ? manager.DisconnectReason : "";
                _status = string.IsNullOrEmpty(reason) ? "Disconnected." : $"Disconnected: {reason}";
            }
            _wasActive = active;
        }

        void OnDisable()
        {
            ScreenRect = Rect.zero;
        }

        void OnGUI()
        {
            var area = new Rect(16f, 16f, 260f, 260f);
            GUILayout.BeginArea(area);
            GUILayout.BeginVertical(GUI.skin.box);

            var manager = NetworkManager.Singleton;
            if (manager == null)
            {
                GUILayout.Label("No NetworkManager in the scene.");
            }
            else if (!manager.IsListening)
            {
                DrawLobby();
            }
            else
            {
                DrawInSession(manager);
            }

            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                Rect panel = GUILayoutUtility.GetLastRect();
                ScreenRect = new Rect(area.x + panel.x, area.y + panel.y, panel.width, panel.height);
            }
            GUILayout.EndArea();
        }

        void DrawLobby()
        {
            GUILayout.Label("Wildwood test session");
            if (GUILayout.Button("Host", GUILayout.Height(30)))
            {
                _status = Session.Host(ParsePort()) ? "" : "Couldn't start hosting. Check the Console.";
            }

            GUILayout.Space(6);
            GUILayout.Label("Join address");
            address = GUILayout.TextField(address);
            GUILayout.Label("Port");
            port = GUILayout.TextField(port);
            if (GUILayout.Button("Join", GUILayout.Height(30)))
            {
                _status = Session.Join(address, ParsePort()) ? "Connecting..." : "Couldn't start joining. Check the Console.";
            }

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
        }

        void DrawInSession(NetworkManager manager)
        {
            string role = manager.IsHost ? "Hosting"
                : manager.IsConnectedClient ? "Connected"
                : "Connecting...";
            string players = manager.IsServer ? $" ({manager.ConnectedClientsIds.Count} players)" : "";
            GUILayout.Label(role + players);

            bool mouseCaptured = Cursor.lockState == CursorLockMode.Locked;
            GUILayout.Label(mouseCaptured ? "Esc frees the mouse" : "Click the game to look around");
            if (!mouseCaptured && GUILayout.Button("Leave session"))
            {
                Session.Leave();
            }
        }

        ushort ParsePort()
        {
            return ushort.TryParse(port, out var value) ? value : Session.DefaultPort;
        }
    }
}
