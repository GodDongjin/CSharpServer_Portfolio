using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore.Network
{
    public class SocketAsyncEventArgsPool : IDisposable
    {
        private readonly Stack<SocketAsyncEventArgs> _pool;
        private bool _disposed;

        public SocketAsyncEventArgsPool(int capacity)
        {
            _pool = new Stack<SocketAsyncEventArgs>(capacity);
        }

        public void Push(SocketAsyncEventArgs item)
        {
            if (item == null)
                return;

            lock (_pool)
            {
                if (_disposed)
                {
                    item.Dispose();
                    return;
                }

                _pool.Push(item);
            }
        }

        public SocketAsyncEventArgs? Pop()
        {
            lock (_pool)
            {
                if (_pool.Count > 0)
                {
                    return _pool.Pop();
                }
                else
                {
                    return null;
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_pool)
                {
                    return (_pool.Count);
                }
            }
        }

        public void Dispose()
        {
            List<SocketAsyncEventArgs> items;

            lock (_pool)
            {
                if (_disposed)
                    return;

                _disposed = true;
                items = _pool.ToList();
                _pool.Clear();
            }

            foreach (SocketAsyncEventArgs item in items)
                item.Dispose();
        }
    }
}
