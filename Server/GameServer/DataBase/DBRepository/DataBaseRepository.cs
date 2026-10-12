using System;

using ServerCore.DataBase;

namespace GameServer.DataBase
{
    public sealed class DataBaseRepository
    {
        public readonly ILoginDB _loginDB;
        public readonly IHeroDB _heroDB;

        public DataBaseRepository(MysqlDataBase dataBase)
        {
            ArgumentNullException.ThrowIfNull(dataBase);

            _loginDB = new LoginDB(dataBase);
            _heroDB = new HeroDB(dataBase);
        }

        // 테스트에서는 외부 DB 대신 동일한 계약의 대역을 연결한다.
        public DataBaseRepository(ILoginDB loginDB, IHeroDB heroDB)
        {
            ArgumentNullException.ThrowIfNull(loginDB);
            ArgumentNullException.ThrowIfNull(heroDB);

            _loginDB = loginDB;
            _heroDB = heroDB;
        }
    }
}
