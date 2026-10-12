using System;

namespace GameServer.DataBase
{
    public sealed record DBHero
    {
        public Int64 Id { get; init; }
        public Int32 UserId { get; init; }
        public Int32 TemplateId { get; init; }
        public bool IsPinned { get; init; }
        public bool IsLocked { get; init; }

        public DBHero(Int64 id, Int32 userId, Int32 templateId, bool isPinned, bool isLocked)
        {
            Id = id;
            UserId = userId;
            TemplateId = templateId;
            IsPinned = isPinned;
            IsLocked = isLocked;
        }
    }
}
