
namespace ServerCore.Network
{
    public enum EnqueueJobResult
    {
        Success,
        QueueFull,
        Disconnected
    }
}

namespace ServerCore.Network
{
    public enum SERVICE_TYPE
    {
        NONE,
        CHAT_SERVER,
        LOGIN_SERVER,
        GAME_SERVER,
    }
}
