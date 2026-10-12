using System;

using CSharp.Protocol;

namespace GameServer.DataBase
{
    public sealed record DBUser
    {
        public Int32 Id { get; init; }
        public string LoginId { get; init; }
        public string Name { get; init; }
        public Int32 Score { get; init; }

        public DBUser(Int32 id, string loginId, string name, Int32 score)
        {
            Id = id;
            LoginId = loginId;
            Name = name;
            Score = score;
        }

        public USER_INFO ToPacket()
        {
            return new()
            {
                Id = Id,
                Name = Name,
                Score = Score
            };
        }
    }
}
