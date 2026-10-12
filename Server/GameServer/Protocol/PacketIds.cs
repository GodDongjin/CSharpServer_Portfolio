// 자동 생성 파일. PacketRoutes.json을 수정하고 GenPackets.bat를 실행한다.
using System;

namespace CSharp.Protocol
{
    public static class PacketIds
    {
        public const UInt16 Login = 1002;
        public const UInt16 LoginReply = 1003;
        public const UInt16 HeroList = 1006;
        public const UInt16 HeroListReply = 1007;
        public const UInt16 HeroUpdate = 1010;
        public const UInt16 HeroUpdateReply = 1011;
        public const UInt16 MatchJoin = 1018;
        public const UInt16 MatchJoinReply = 1019;
        public const UInt16 MatchCancel = 1021;
        public const UInt16 MatchCancelReply = 1022;
        public const UInt16 MatchNotice = 1023;
        public const UInt16 MatchLeave = 1100;
        public const UInt16 MatchLeaveReply = 1101;
        public const UInt16 HeartbeatPing = 1102;
        public const UInt16 HeartbeatPong = 1103;
    }
}