using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using ServerCore.Network;
using ServerCore.Packet;

namespace ServerCore.Interface
{
    public interface IPacketHandler
    {
        void Initialize();
        SendBuffer MakeSendBuffer<T>(ushort packetId              , T message) where T : IMessage<T>;
        bool HandlePacket(Session session, ReadOnlySpan<byte> data);
    }
}
