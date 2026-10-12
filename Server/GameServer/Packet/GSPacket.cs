using System;

using CSharp.Protocol;
using GameServer.DataBase;
using GameServer.Matchmaking;

namespace GameServer.Packet
{
    public sealed partial class GSPacket
    {
        // 처리 함수의 true는 오류 응답을 포함한 처리 완료 또는 추가 처리 불필요를 의미한다.
        // false는 연결 종료가 필요한 실패이며, 실제 종료는 GSPacketHandler.HandleAsync에서 수행한다.
        private readonly DataBaseRepository _repository;
        private readonly MatchManager _matchManager;

        public GSPacket(DataBaseRepository repository, MatchManager matchManager)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(matchManager);

            _repository = repository;
            _matchManager = matchManager;
        }

        public async ValueTask<bool> HandleReqLogin(GameSession session, REQ_LOGIN packet)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(packet);

            Int64 curSessionID = session._sessionID;
            ACK_LOGIN ackPacket = new()
            {
                Result = LOGIN_ERROR.LoginFail
            };

            try
            {
                if (session.User is null && !packet.IsCreate && packet.Pw.Length == 0 &&
                    !string.IsNullOrWhiteSpace(packet.Id) && packet.Id.Length <= 64)
                {
                    DBUser? user = await _repository._loginDB.FindAsync(packet.Id);
                    if (user is not null)
                    {
                        IReadOnlyList<DBHero> heroes = await _repository._heroDB.LoadAsync(user.Id);

                        if (!session.IsCurrentConnection(curSessionID))
                        {
                            return true;
                        }

                        if (session.Login(user))
                        {
                            session.Heroes.Load(heroes);
                            session.MarkLoginCompleted(curSessionID);
                            ackPacket.Result = LOGIN_ERROR.LoginSuccess;
                            ackPacket.User = user.ToPacket();
                        }
                    }
                }
            }
            catch (InvalidDataException exception)
            {
                Console.WriteLine($"로그인 데이터 오류: {exception.Message}");
                return false;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"로그인 실패: {exception.Message}");
                ackPacket.Result = LOGIN_ERROR.LoginFail;
            }

            if (!session.IsCurrentConnection(curSessionID))
            {
                return true;
            }

            session.SendPacket((ushort)PACKET_ID.PKT_ACK_LOGIN, ackPacket);
            return true;
        }

        public async ValueTask<bool> HandleReqHeroInfoLoad(GameSession session, REQ_HERO_LIST packet)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(packet);

            Int64 curSessionID = session._sessionID;
            ACK_HERO_LIST ackPacket = new()
            {
                Result = session.User is null ? RESULT_CODE.NotLoggedIn : RESULT_CODE.Ok
            };

            if (session.User is not null)
            {
                ackPacket.Heroes.AddRange(session.Heroes.Values.Select(hero => hero.ToPacket()));
            }

            if (session.IsCurrentConnection(curSessionID))
            {
                session.SendPacket((ushort)PACKET_ID.PKT_ACK_HERO_INFO_LOAD, ackPacket);
            }

            return true;
        }

        public async ValueTask<bool> HandleReqHeroUpdate(GameSession session, REQ_HERO_UPDATE packet)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(packet);

            Int64 curSessionID = session._sessionID;
            ACK_HERO_UPDATE ackPacket = new()
            {
                Result = RESULT_CODE.NotLoggedIn
            };

            try
            {
                if (session.User is not null)
                {
                    ackPacket = await session.Heroes.UpdateAsync
                    (
                        _repository._heroDB, session.User.Id, packet,() => session.IsCurrentConnection(curSessionID)
                    );
                }
            }
            catch (Exception exception)
            {
                // 커밋 응답 유실 시 저장 결과가 불명확하므로 재접속 후 DB를 다시 읽는다.
                Console.WriteLine($"Hero 갱신 실패: {exception.Message}");
                return false;
            }

            if (!session.IsCurrentConnection(curSessionID))
            {
                return true;
            }

            session.SendPacket((ushort)PACKET_ID.PKT_ACK_HERO_UPDATE, ackPacket);
            return true;
        }

        public async ValueTask<bool> HandleReqPvpMatchPending(GameSession session, REQ_MATCH_JOIN packet)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(packet);

            Int64 curSessionID = session._sessionID;
            ACK_MATCH ackPacket = new()
            {
                Result = RESULT_CODE.NotLoggedIn
            };

            try
            {
                if (session.User is not null)
                {
                    ackPacket = await _matchManager.JoinAsync(session.User, session);
                }
            }
            catch (InvalidDataException exception)
            {
                Console.WriteLine($"매칭 데이터 오류: {exception.Message}");
                return false;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"매칭 요청 실패: {exception.Message}");
                ackPacket.Result = RESULT_CODE.StorageError;
            }

            if (!session.IsCurrentConnection(curSessionID))
            {
                return true;
            }

            session.SendPacket((ushort)PACKET_ID.PKT_ACK_PVP_MATCH_PENDING, ackPacket);
            return true;
        }

        public async ValueTask<bool> HandleReqPvpMatchPendingCancel(GameSession session, REQ_MATCH_CANCEL packet)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(packet);

            Int64 curSessionID = session._sessionID;
            ACK_MATCH ackPacket = new()
            {
                Result = RESULT_CODE.NotLoggedIn
            };

            try
            {
                if (session.User is not null)
                {
                    ackPacket = await _matchManager.CancelAsync(session.User.Id, curSessionID);
                }
            }
            catch (InvalidDataException exception)
            {
                Console.WriteLine($"매칭 데이터 오류: {exception.Message}");
                return false;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"매칭 요청 실패: {exception.Message}");
                ackPacket.Result = RESULT_CODE.StorageError;
            }

            if (!session.IsCurrentConnection(curSessionID))
            {
                return true;
            }

            session.SendPacket((ushort)PACKET_ID.PKT_ACK_PVP_MATCH_PENDING_CANCEL, ackPacket);
            return true;
        }

        public async ValueTask<bool> HandleReqPvpMatchLeave(GameSession session, REQ_MATCH_LEAVE packet)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(packet);

            Int64 curSessionID = session._sessionID;
            ACK_MATCH ackPacket = new()
            {
                Result = RESULT_CODE.NotLoggedIn
            };

            try
            {
                if (session.User is not null)
                {
                    ackPacket = await _matchManager.LeaveAsync(session.User.Id, curSessionID);
                }
            }
            catch (InvalidDataException exception)
            {
                Console.WriteLine($"매칭 데이터 오류: {exception.Message}");
                return false;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"매칭 요청 실패: {exception.Message}");
                ackPacket.Result = RESULT_CODE.StorageError;
            }

            if (!session.IsCurrentConnection(curSessionID))
            {
                return true;
            }

            session.SendPacket((ushort)PACKET_ID.PKT_ACK_PVP_MATCH_LEAVE, ackPacket);
            return true;
        }
    }
}
