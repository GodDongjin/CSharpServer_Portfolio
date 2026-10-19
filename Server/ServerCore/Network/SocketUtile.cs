using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore.Network
{
    public class SocketUtile
    {
        public void SocketReuse(Socket? socket, bool flag = false)
        {
            ArgumentNullException.ThrowIfNull(socket);

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        public void SocketReceiveBuffer(Socket? socket, Int32 byteSize = 65536)
        {
            ArgumentNullException.ThrowIfNull(socket);

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, byteSize);
        }

        public void SocketSendBuffer(Socket? socket, Int32 byteSize = 65536)
        {
            ArgumentNullException.ThrowIfNull(socket);

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, byteSize);
        }

        public void SocketNagle(Socket? socket, bool flag = false)
        {
            ArgumentNullException.ThrowIfNull(socket);

            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, flag);
        }

        public void SocketTimeOut(Socket? socket, Int32 time = 10000)
        {
            ArgumentNullException.ThrowIfNull(socket);

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, time);

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendTimeout, time);
        }

        public void SocketKeepAlive(Socket? socket, bool flag = false)
        {
            ArgumentNullException.ThrowIfNull(socket);

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, flag);
        }

        public void UDPSocketBroadcast(Socket? socket, bool flag = false)
        {
            ArgumentNullException.ThrowIfNull(socket);

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, flag);
        }

        public void SocketTTL(Socket? socket, Int32 count = 64)
        {
            ArgumentNullException.ThrowIfNull(socket);

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.IpTimeToLive, count);
        }
    }
}
