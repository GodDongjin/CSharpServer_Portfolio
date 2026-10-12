using System;

using CSharp.Protocol;
using GameServer.DataBase;

namespace GameServer.Hero
{
    // 세션 큐에서 순차 접근하며, DB 커밋 후에만 Hero 스냅샷을 교체한다.
    public sealed class HeroManager
    {
        private readonly Dictionary<Int64, HeroData> _heroDic = new();

        public IReadOnlyCollection<HeroData> Values
        {
            get { return _heroDic.Values; }
        }

        public void Load(IEnumerable<DBHero> heroes)
        {
            _heroDic.Clear();
            foreach (DBHero hero in heroes)
            {
                _heroDic.Add(hero.Id, new HeroData(hero));
            }
        }

        public HeroData? GetHeroData(Int64 heroUID)
        {
            return _heroDic.GetValueOrDefault(heroUID);
        }

        public void Clear()
        {
            _heroDic.Clear();
        }

        public async Task<ACK_HERO_UPDATE> UpdateAsync(IHeroDB heroDB, Int32 userId, REQ_HERO_UPDATE request, Func<bool> isCurrent)
        {
            if (request.Field is not (HERO_FIELD.Pinned or HERO_FIELD.Locked))
            {
                return new()
                {
                    Result = RESULT_CODE.InvalidRequest
                };
            }

            DBHero? current = GetHeroData(request.HeroId)?._dbHero;
            if (current is null || current.UserId != userId)
            {
                return new()
                {
                    Result = RESULT_CODE.NotFound
                };
            }

            // DB 저장에 성공하기 전에는 메모리 상태를 변경하지 않는다.
            DBHero? saved = await heroDB.UpdateAsync(userId, request.HeroId, request.Field, request.Value);
            if (!isCurrent())
            {
                return new()
                {
                    Result = RESULT_CODE.Disconnected
                };
            }

            if (saved is null)
            {
                return new()
                {
                    Result = RESULT_CODE.NotFound
                };
            }

            if (saved.Id != request.HeroId || saved.UserId != userId)
            {
                throw new InvalidDataException("Repository returned an unrelated Hero.");
            }

            _heroDic[saved.Id] = new HeroData(saved);
            return new()
            {
                Result = RESULT_CODE.Ok,
                Hero = _heroDic[saved.Id].ToPacket()
            };
        }
    }
}
