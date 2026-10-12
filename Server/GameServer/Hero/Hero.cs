using System;

using CSharp.Protocol;
using GameServer.DataBase;

namespace GameServer.Hero
{
    public sealed class HeroData
    {
        public DBHero _dbHero { get; }
        public Int64 _uid { get { return _dbHero.Id; } }
        public Int32 _userID { get { return _dbHero.UserId; } }
        public Int32 _table_id { get { return _dbHero.TemplateId; } }
        public bool _is_pinned { get { return _dbHero.IsPinned; } }
        public bool _is_locked { get { return _dbHero.IsLocked; } }

        public HeroData(DBHero dbHero)
        {
            ArgumentNullException.ThrowIfNull(dbHero);

            _dbHero = dbHero;
        }

        public HERO_INFO ToPacket()
        {
            return new HERO_INFO
            {
                Id = _uid,
                TemplateId = _table_id,
                IsPinned = _is_pinned,
                IsLocked = _is_locked
            };
        }
    }
}
