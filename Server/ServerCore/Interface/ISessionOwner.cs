using ServerCore.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore.Interface
{
    public interface ISessionOwner
    {
        void RetrunEventArg(SocketAsyncEventArgs read_args, SocketAsyncEventArgs write_args);
    }
}
