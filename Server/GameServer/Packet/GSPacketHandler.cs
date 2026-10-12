using System;
using System.Buffers.Binary;
using System.Diagnostics;

using CSharp.Protocol;
using Google.Protobuf;
using ServerCore.Diagnostics;
using ServerCore.Network;
using ServerCore.Packet;

namespace GameServer.Packet
{
    // 패킷 ID별 처리 함수를 저장하고 호출하기 위한 delegate 타입
    public delegate bool PacketHandlerFunc(GameSession session, ReadOnlySpan<byte> payload);

    public enum PACKET_ID : ushort
    {
        PKT_REQ_LOGIN = 1002,
        PKT_ACK_LOGIN = 1003,
        PKT_REQ_HERO_INFO_LOAD = 1006,
        PKT_ACK_HERO_INFO_LOAD = 1007,
        PKT_REQ_HERO_UPDATE = 1010,
        PKT_ACK_HERO_UPDATE = 1011,
        PKT_REQ_PVP_MATCH_PENDING = 1018,
        PKT_ACK_PVP_MATCH_PENDING = 1019,
        PKT_REQ_PVP_MATCH_PENDING_CANCEL = 1021,
        PKT_ACK_PVP_MATCH_PENDING_CANCEL = 1022,
        PKT_SEND_PVP_MATCH = 1023,
        PKT_REQ_PVP_MATCH_LEAVE = 1100,
        PKT_ACK_PVP_MATCH_LEAVE = 1101,
        PKT_SEND_HEARTBEAT_PING = 1102,
        PKT_REQ_HEARTBEAT_PONG = 1103,
    }

    public sealed class GSPacketHandler : PacketHandler
    {
        private readonly PacketHandlerFunc?[] _handlers = new PacketHandlerFunc?[ushort.MaxValue + 1];
        private readonly GSPacket _packet;

        public GSPacketHandler(GSPacket packet)
        {
            ArgumentNullException.ThrowIfNull(packet);

            _packet = packet;
        }

        public override void Initialize()
        {
            // AUTO-GENERATED-BEGIN
            // 패킷 ID에 대응하는 처리 함수를 등록한다.
            Register(PACKET_ID.PKT_REQ_LOGIN, REQ_LOGIN.Parser, _packet.HandleReqLogin);
            Register(PACKET_ID.PKT_REQ_HERO_INFO_LOAD, REQ_HERO_LIST.Parser, _packet.HandleReqHeroInfoLoad);
            Register(PACKET_ID.PKT_REQ_HERO_UPDATE, REQ_HERO_UPDATE.Parser, _packet.HandleReqHeroUpdate);
            Register(PACKET_ID.PKT_REQ_PVP_MATCH_PENDING, REQ_MATCH_JOIN.Parser, _packet.HandleReqPvpMatchPending);
            Register(PACKET_ID.PKT_REQ_PVP_MATCH_PENDING_CANCEL, REQ_MATCH_CANCEL.Parser, _packet.HandleReqPvpMatchPendingCancel);
            Register(PACKET_ID.PKT_REQ_PVP_MATCH_LEAVE, REQ_MATCH_LEAVE.Parser, _packet.HandleReqPvpMatchLeave);
            // AUTO-GENERATED-END
            RegisterHeartbeatPong();
        }

        private void RegisterHeartbeatPong()
        {
            ushort id = (ushort)PACKET_ID.PKT_REQ_HEARTBEAT_PONG;
            
            if (_handlers[id] != null)
            {
                throw new InvalidOperationException($"패킷 ID가 중복 등록되었습니다: {id}");
            }

            _handlers[id] = (session, payload) =>
            {
                try
                {
                    REQ_HEARTBEAT_PONG packet = REQ_HEARTBEAT_PONG.Parser.ParseFrom(payload);
                    // DB 작업 뒤에서 기다리지 않고, 수신 경로의 수명 잠금 안에서 상태만 갱신한다.
                    session.ReceiveHeartbeatPong(session.ConnectionId, packet.Sequence);
                    return true;
                }
                catch (InvalidProtocolBufferException exception)
                {
                    Console.WriteLine($"Pong 파싱 실패: {exception.Message}");
                    return false;
                }
            };
        }

        private void Register<T>(PACKET_ID packetId, MessageParser<T> parser, Func<GameSession, T, ValueTask<bool>> handler) where T : IMessage<T>
        {
            ushort id = checked((ushort)packetId);

            if (_handlers[id] != null)
            {
                throw new InvalidOperationException($"패킷 ID가 중복 등록되었습니다: {id}");
            }

            _handlers[id] = (session, payload) =>
            {
                long packetStartTimestamp = Stopwatch.GetTimestamp();
                T packet;

                try
                {
                    // span이 유효한 동안 동기적으로 파싱한다.
                    packet = parser.ParseFrom(payload);
                }
                catch (InvalidProtocolBufferException exception)
                {
                    Console.WriteLine($"Protobuf 파싱 실패: packetId={id}, message={exception.Message}");
                    return false;
                }

                // 비동기 처리는 세션 큐에 등록하여 수신 순서를 유지한다.
                long queueEnqueuedTimestamp = Stopwatch.GetTimestamp();
                long curSessionID = session._sessionID;

                // 패킷 수신 당시 연결 ID를 큐 등록 시에도 확인한다. 기존 등록·핸들러 구조는 유지한다.
                if (!session.EnqueueAsyncJob(curSessionID, () => HandleAsync(
                    session,
                    packet,
                    handler,
                    id,
                    curSessionID,
                    packetStartTimestamp,
                    queueEnqueuedTimestamp)))
                {
                    Console.WriteLine($"세션 작업 큐 등록 실패: sessionId={session._sessionID}, packetId={id}");
                    return false;
                }

                return true;
            };
        }

        private static async ValueTask HandleAsync<T>(
            GameSession session,
            T packet,
            Func<GameSession, T, ValueTask<bool>> handler,
            ushort id,
            long curSessionID,
            long packetStartTimestamp,
            long queueEnqueuedTimestamp)
            where T : IMessage<T>
        {
            if (!session.IsCurrentConnection(curSessionID))
            {
                return;
            }

            ServerPerformanceMetrics.PacketTimingScope timing = ServerPerformanceMetrics.BeginPacket(id, packetStartTimestamp, queueEnqueuedTimestamp);

            string outcome = "success";

            try
            {
                bool result = await handler(session, packet);
                if (!result)
                {
                    // 게임 로직은 종료 필요 여부만 반환하고, 요청 당시 연결의 종료는 여기서 담당한다.
                    outcome = "handler_false";
                    Console.WriteLine($"패킷 비동기 처리 실패: packetId={id}");
                    session.Disconnect(curSessionID);
                }
            }
            catch (OperationCanceledException exception)
            {
                outcome = "canceled";
                Console.WriteLine($"패킷 처리 취소: packetId={id}, {exception.Message}");
                session.Disconnect(curSessionID);
            }
            catch (Exception exception)
            {
                outcome = "error";
                Console.WriteLine($"패킷 처리 오류: packetId={id}, {exception}");
                session.Disconnect(curSessionID);
            }
            finally
            {
                timing.Outcome = outcome;
                timing.Dispose();

                TimeSpan totalElapsed = Stopwatch.GetElapsedTime(packetStartTimestamp);
                Console.WriteLine(
                    $"[PACKET-TIMING] " +
                    $"sessionId={session._sessionID}, " +
                    $"accountId={session._accountIDX}, " +
                    $"packetId={id}, " +
                    $"queueWaitMs={timing.QueueWaitMs:F3}, " +
                    $"handlerMs={timing.HandlerMs:F3}, " +
                    $"sendEnqueueMs={FormatMilliseconds(timing.SendEnqueueMs)}, " +
                    $"requestToAckMs={FormatMilliseconds(timing.RequestToAckMs)}, " +
                    $"elapsedMs={totalElapsed.TotalMilliseconds:F3}, " +
                    $"outcome={outcome}");
            }
        }

        private static string FormatMilliseconds(double? milliseconds)
        {
            return milliseconds.HasValue ? milliseconds.Value.ToString("F3") : "n/a";
        }

        override public bool HandlePacket(Session session, ReadOnlySpan<byte> data)
        {
            if (session is not GameSession gameSession)
                return false;

            if (data.Length < PacketHeader.HeaderSize)
                return false;

            // 패킷 헤더: [size 2바이트][id 2바이트]
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2));
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));

            if (size != data.Length)
                return false;

            PacketHandlerFunc? handler = _handlers[id];
            if (handler == null)
            {
                return false;
            }

            ReadOnlySpan<byte> payload = data.Slice(PacketHeader.HeaderSize, size - PacketHeader.HeaderSize);
            return handler(gameSession, payload);
        }
    }
}
