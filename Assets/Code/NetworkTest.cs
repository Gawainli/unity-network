using UnityEngine;
using Proxima.Runtime.Network;
using Cysharp.Threading.Tasks;
using System;

// --- 协议定义 ---

// 普通聊天消息 (ID 从 2001 开始)
[Serializable]
[NetMessage(2001, "Lobby")]
public class ChatMsg
{
    public string Sender;
    public string Content;
}

// RPC 登录请求 (ID: 3001)
[Serializable]
[NetMessage(3001, "Lobby")]
public class LoginReq
{
    public string Username;
    public string Password;
}

// RPC 登录响应 (ID: 3002)
[Serializable]
public class LoginResp
{
    public bool Success;
    public string Token;
    public string Message;
}

// --- 测试脚本 ---
public class NetworkTest : MonoBehaviour
{
    // IP 和 Port 变量，可在 Inspector 修改
    public string serverIp = "127.0.0.1";
    public int serverPort = 8888;

    private NetworkClient _client;
    private string _logText = "";
    private Vector2 _scrollPos;

    private NetworkConfig _testConfig;

    void Start()
    {
        _testConfig = new NetworkConfig
        {
            EnableRttMonitoring = true,
            ConnectionTimeout = 5000,
            PingInterval = 1000,
        };

        _client = NetworkModule.CreateClient("Lobby", _testConfig);
        _client.Register<ChatMsg>(OnRecvChat);
        Log("Client Created. Ready.");
    }

    private void Update()
    {
        NetworkModule.Update();
    }

    private void OnDestroy()
    {
        NetworkModule.Cleanup();
    }

    private void OnRecvChat(ChatMsg msg)
    {
        Log($"<color=cyan>[Recv] {msg.Sender}: {msg.Content}</color>");
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 300, 600));

        // 使用变量 ServerIp 和 ServerPort
        if (GUILayout.Button($"1. Connect ({serverIp}:{serverPort})"))
        {
            _client.Connect(serverIp, serverPort, ProtocolType.Tcp);
        }

        if (GUILayout.Button("2. Send 'hello server'"))
        {
            var msg = new ChatMsg { Sender = "Client", Content = "hello server" };
            _client.Send(msg);
            Log($"[Send] {msg.Content}");
        }

        if (GUILayout.Button("3. Call RPC (Login)"))
        {
            TestRpc().Forget();
        }

        if (GUILayout.Button("4. Disconnect"))
        {
            _client.Disconnect();
            Log("Disconnected.");
        }

        GUILayout.Space(10);
        GUILayout.Label($"State: {_client.State}");
        GUILayout.Label($"RTT: {_client.RTT} ms");

        GUILayout.EndArea();

        GUILayout.BeginArea(new Rect(320, 10, Screen.width - 330, Screen.height - 20));
        _scrollPos = GUILayout.BeginScrollView(_scrollPos, "box");
        GUILayout.Label(_logText);
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private async UniTaskVoid TestRpc()
    {
        Log("[RPC] Sending LoginReq...");
        try
        {
            var req = new LoginReq { Username = "Admin", Password = "123" };
            var resp = await _client.Call<LoginReq, LoginResp>(req);
            Log($"[RPC Resp] {resp.Message} (Token: {resp.Token})");
        }
        catch (Exception e)
        {
            Log($"[RPC Error] {e.Message}");
        }
    }

    private void Log(string msg)
    {
        Debug.Log(msg);
        _logText += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        _scrollPos.y = float.MaxValue;
    }
}