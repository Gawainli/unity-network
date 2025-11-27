namespace Proxima.Runtime.Network
{
    public interface INetworkLogger
    {
        void Log(string message);
        void LogWarning(string message);
        void LogError(string message);
    }

    // 默认的 Unity 实现
    public class UnityNetworkLogger : INetworkLogger
    {
        public void Log(string message) => UnityEngine.Debug.Log(message);
        public void LogWarning(string message) => UnityEngine.Debug.LogWarning(message);
        public void LogError(string message) => UnityEngine.Debug.LogError(message);
    }
}