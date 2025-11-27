using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Proxima.Runtime.Network
{
    public static class NetworkModule
    {
        public static INetworkLogger Logger { get; private set; } = new UnityNetworkLogger();

        // 存储所有活跃的 Client
        private static readonly Dictionary<string, NetworkClient> Clients = new Dictionary<string, NetworkClient>();

        private static IMessageSerializer _defaultSerializer = new DefaultSerializer();
        private static Action<object> _defaultEventBus;

        public static IEnumerable<string> GetAllClientNames() => Clients.Keys;
        public static bool HasClient(string name) => Clients.ContainsKey(name);
        public static int GetClientCount() => Clients.Count;

        static NetworkModule()
        {
            if (_defaultSerializer is DefaultSerializer)
                _defaultSerializer = new JsonStringSerializer();
        }

        // --- 全局配置 ---
        public static void SetGlobalSerializer(IMessageSerializer s) => _defaultSerializer = s;
        public static void SetGlobalEventBus(Action<object> bus) => _defaultEventBus = bus;
        public static void SetLogger(INetworkLogger logger) => Logger = logger ?? new UnityNetworkLogger();

        public static void Update()
        {
            if (Clients.Count == 0) return;

            foreach (var client in Clients.Values)
            {
                client.Update();
            }
        }

        public static void Cleanup()
        {
            foreach (var client in Clients.Values)
            {
                client.Dispose();
            }

            Clients.Clear();
            Logger.Log("[NetworkModule] Shutdown.");
        }

        public static NetworkClient CreateClient(string name, NetworkConfig config,
            IMessageSerializer serializer = null)
        {
            if (Clients.ContainsKey(name))
            {
                Logger.LogWarning($"[NetworkModule] Client {name} already exists.");
                return Clients[name];
            }

            var client = new NetworkClient(name, config);
            client.Setup(serializer ?? _defaultSerializer, _defaultEventBus);

            Clients.Add(name, client);
            Logger.Log($"[NetworkModule] Client {name} created.");
            return client;
        }

        public static NetworkClient GetClient(string name) => Clients.TryGetValue(name, out var c) ? c : null;

        public static void DestroyClient(string name)
        {
            if (Clients.TryGetValue(name, out var client))
            {
                client.Dispose();
                Clients.Remove(name);
                Logger.Log($"[NetworkModule] Client {name} destroyed.");
            }
        }

        public static void Send<T>(T msg)
        {
            var attr = typeof(T).GetCustomAttribute<NetMessageAttribute>();
            if (attr == null)
            {
                Logger.LogError($"[Net] No Attribute: {typeof(T).Name}");
                return;
            }

            if (Clients.TryGetValue(attr.ChannelName, out var client))
            {
                client.Send(msg);
            }
            else
            {
                Logger.LogError($"[Net] Channel '{attr.ChannelName}' not found for message {typeof(T).Name}");
            }
        }

        public static async UniTask<TResp> Call<TReq, TResp>(TReq req)
        {
            var attr = typeof(TReq).GetCustomAttribute<NetMessageAttribute>();
            if (attr == null)
            {
                Logger.LogError($"[Net] No Attribute: {typeof(TReq).Name}");
                return default;
            }

            if (Clients.TryGetValue(attr.ChannelName, out var client))
            {
                return await client.Call<TResp>(attr.Id, req);
            }

            return default;
        }
    }
}