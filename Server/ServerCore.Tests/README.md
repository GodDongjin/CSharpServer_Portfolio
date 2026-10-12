# ServerCore.Tests

.NET 10 xUnit 테스트 프로젝트입니다. 2026-10-07 검증에서 49개가 통과했습니다. 외부 DB/Redis나 별도의 GameServer 실행 없이 버퍼, 패킷 프레이밍, 세션 큐, 종료/재사용과 루프백 TCP를 검증합니다.

루트에서 `run-tests.bat`를 실행하면 이 프로젝트와 GameServer.Tests를 함께 검사합니다. Visual Studio에서는 테스트 탐색기를 사용합니다.

```powershell
dotnet test Server/ServerCore.Tests
```

GameServer의 Hero/매칭 검증은 `Server/GameServer.Tests`에 있습니다. 실제 DB/Redis를 사용하는 스모크 실행은 루트 README를 참고하세요.
