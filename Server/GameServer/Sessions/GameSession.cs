using System;
using System.Collections.Concurrent;

using CSharp.Protocol;
using GameServer.DataBase;
using GameServer.Hero;
using GameServer.Matchmaking;
using GameServer.Packet;
using ServerCore.Network;

namespace GameServer
{
    public sealed class UserSessions
    {
        private readonly ConcurrentDictionary<Int32, Int64> _connections = new();
        public bool TryLogin(Int32 userId, Int64 connectionId)
        {
            return _connections.TryAdd(userId, connectionId);
        }

        public void Logout(Int32 userId, Int64 connectionId)
        {
            ((ICollection<KeyValuePair<Int32, Int64>>)_connections).Remove(new(userId, connectionId));
        }
    }

    public sealed class GameSession : Session, IMatchPeer
    {
        private readonly MatchManager _matches;
        private readonly UserSessions _users;
        public DBUser? User { get; private set; }
        public HeroManager Heroes { get; } = new();

        public Int64 ConnectionId
        {
            get
            {
                return _sessionID;
            }
        }

        public GameSession(MatchManager matches, UserSessions users)
        {
            _matches = matches;
            _users = users;
        }

        public bool IsCurrent(Int64 connectionId)
        {
            return IsCurrentConnection(connectionId);
        }

        public bool Login(DBUser user)
        {
            if (User is not null || !_users.TryLogin(user.Id, ConnectionId))
            {
                return false;
            }

            User = user;
            _userID = user.Id;
            _accountIDX = user.Id;
            return true;
        }

        public void Notify(Int64 connectionId, ACK_MATCH reply)
        {
            // 알림을 만들 때의 연결 ID로 큐에 등록하고, SendPacket이 끝날 때까지 세션 수명을 유지한다.
            if (!EnqueueAsyncJob(connectionId, () =>
            {
                if (IsCurrent(connectionId))
                {
                    SendPacket((ushort)PACKET_ID.PKT_SEND_PVP_MATCH, reply);
                }

                return ValueTask.CompletedTask;
            }))
            {
                // 등록 실패가 이전 연결 때문일 수 있으므로 새 연결까지 종료하는 Disconnect()를 사용하지 않는다.
                Disconnect(connectionId);
            }
        }

        protected override void SendHeartbeatPing(Int64 connectionId, Int64 sequence)
        {
            SendPacket((ushort)PACKET_ID.PKT_SEND_HEARTBEAT_PING, new SEND_HEARTBEAT_PING { Sequence = sequence });
        }

        // base.Disconnect() 뒤에 처리하면 이미 재사용될 수 있어, 반환 전 실행되는 종료 훅으로 옮겼다.
        protected override void OnDisconnected(Int64 connectionId)
        {
            if (_userID > 0)
            {
                _matches.ScheduleDisconnect(_userID, connectionId);
            }
        }

        protected override void OnReset()
        {
            _users.Logout(_userID, ConnectionId);
            // 대기 중인 I/O와 세션 작업이 모두 끝난 뒤에만 Reset이 호출된다.
            User = null;
            Heroes.Clear();
            _userID = 0;
            _accountIDX = 0;
        }
    }
}
