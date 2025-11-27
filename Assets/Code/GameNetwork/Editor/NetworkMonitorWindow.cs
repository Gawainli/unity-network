#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Proxima.Runtime.Network
{
    /// <summary>
    /// 网络监控窗口，用于在Unity编辑器中监视网络客户端状态和数据包日志
    /// </summary>
    public class NetworkMonitorWindow : EditorWindow
    {
        [MenuItem("GameNetwork/Monitor")]
        public static void ShowWindow()
        {
            var window = GetWindow<NetworkMonitorWindow>("Net Monitor");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        private Vector2 _scrollPos;
        private int _selectedClientIndex = 0;
        private bool _autoScroll = true;
        private bool _showPingPong = false; // 控制是否显示 Ping/Pong 包
        private FieldInfo _clientsField;

        private void OnEnable()
        {
            // 通过反射获取 NetworkModule 中的 Clients 字段
            _clientsField = typeof(NetworkModule).GetField("Clients", BindingFlags.NonPublic | BindingFlags.Static);
        }

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                DrawCenterMessage("Waiting for Play Mode...");
                return;
            }

            var clientsDict = _clientsField?.GetValue(null) as Dictionary<string, NetworkClient>;
            if (clientsDict == null || clientsDict.Count == 0)
            {
                DrawCenterMessage("No active Network Clients.");
                return;
            }

            var clientNames = clientsDict.Keys.ToArray();
            var clients = clientsDict.Values.ToArray();

            if (_selectedClientIndex >= clients.Length) _selectedClientIndex = 0;
            var currentClient = clients[_selectedClientIndex];

            // 工具栏：客户端切换、显示控制选项、日志清除按钮
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            _selectedClientIndex = GUILayout.Toolbar(_selectedClientIndex, clientNames, EditorStyles.toolbarButton);
            GUILayout.FlexibleSpace();
            _showPingPong = GUILayout.Toggle(_showPingPong, "Show Ping", EditorStyles.toolbarButton);
            _autoScroll = GUILayout.Toggle(_autoScroll, "Auto Scroll", EditorStyles.toolbarButton);

            if (GUILayout.Button("Clear Logs", EditorStyles.toolbarButton))
            {
                while (currentClient.DebugLogs.TryDequeue(out _)) { }
            }
            GUILayout.EndHorizontal();

            DrawClientStatus(currentClient);
            DrawLogs(currentClient);

            Repaint();
        }

        private void DrawClientStatus(NetworkClient client)
        {
            EditorGUILayout.BeginVertical("box");

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Client: {client.Name}", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            GUIStyle stateStyle = new GUIStyle(EditorStyles.label);
            stateStyle.normal.textColor = GetStateColor(client.State);
            GUILayout.Label($"State: {client.State}", stateStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Protocol: {client.Protocol}");
            GUILayout.Space(10);
            GUILayout.Label($"Target: {client.Ip}:{client.Port}");
            GUILayout.FlexibleSpace();

            GUIStyle rttStyle = new GUIStyle(EditorStyles.label);
            rttStyle.normal.textColor = client.RTT > 200 ? Color.red : (client.RTT > 100 ? Color.yellow : Color.green);
            GUILayout.Label($"RTT: {client.RTT} ms", rttStyle);
            GUILayout.EndHorizontal();

            if (client.Config != null)
            {
                EditorGUILayout.HelpBox(
                    $"Config: Timeout={client.Config.ConnectionTimeout}ms | Endian={(client.Config.IsLittleEndian ? "Little" : "Big")} | MaxFrame={client.Config.MaxFrameTime}ms",
                    MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawLogs(NetworkClient client)
        {
            GUILayout.Label("Packet Logs (Last 50)", EditorStyles.boldLabel);

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, "box");

            foreach (var log in client.DebugLogs)
            {
                // 根据设置过滤 Ping/Pong 包 (ID 1001/1002)
                if (!_showPingPong && (log.Id == 1001 || log.Id == 1002))
                {
                    continue;
                }

                DrawLogLine(log);
            }

            if (_autoScroll)
            {
                GUILayout.FlexibleSpace();
                _scrollPos.y = float.MaxValue;
            }

            GUILayout.EndScrollView();
        }

        private void DrawLogLine(NetLog log)
        {
            Color bgColor = Color.white;
            string icon = "";

            switch (log.Type)
            {
                case "Send":
                    bgColor = new Color(0.8f, 1f, 0.8f, 0.2f);
                    icon = "⬆";
                    break;
                case "Recv":
                    bgColor = new Color(0.8f, 0.9f, 1f, 0.2f);
                    icon = "⬇";
                    break;
                case "RPC":
                    bgColor = new Color(1f, 0.9f, 0.6f, 0.2f);
                    icon = "⇄";
                    break;
            }

            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = bgColor;

            GUILayout.BeginHorizontal("helpbox");

            GUILayout.Label(log.Time, EditorStyles.miniLabel, GUILayout.Width(70));
            GUILayout.Label(icon, GUILayout.Width(20));
            GUILayout.Label(log.Type, EditorStyles.miniLabel, GUILayout.Width(35));
            GUILayout.Label($"ID: {log.Id}", EditorStyles.boldLabel, GUILayout.Width(60));
            GUILayout.Label($"Len: {log.Size}", EditorStyles.miniLabel, GUILayout.Width(60));

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUI.backgroundColor = originalColor;
        }

        private Color GetStateColor(NetworkState state)
        {
            switch (state)
            {
                case NetworkState.Connected: return Color.green;
                case NetworkState.Connecting: return Color.yellow;
                case NetworkState.Reconnecting: return new Color(1f, 0.5f, 0f);
                default: return Color.red;
            }
        }

        private void DrawCenterMessage(string msg)
        {
            GUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(msg, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
        }
    }
}
#endif
