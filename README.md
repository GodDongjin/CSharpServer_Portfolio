# CSharp Server Portfolio

C# / .NET 기반 비동기 TCP 게임 서버 개인 포트폴리오입니다. 기존에 직접 개발한 C++ IOCP 서버를 바탕으로 C# 서버 코어와 게임 로직을 설계·구현했습니다. 세션 수명 관리, Hero 저장의 DB·메모리 일관성, PvP 매칭과 연결 종료 처리에 집중했습니다.

## 핵심 설계

비동기 서버에서 **요청의 실행 순서, 연결별 상태의 소유권, 저장·정리의 완료 시점**을 명확히 하는 데 집중했습니다.

| 해결할 문제 | 설계 선택 |
|---|---|
| 연결 종료 뒤 도착한 완료 처리가 재사용된 세션에 영향을 주는 문제 | **연결 ID 검사 + 반환 조건 분리**: I/O·세션 작업·종료 훅이 끝나고 이벤트 객체까지 반환한 뒤 재사용 |
| DB를 기다리는 동안 같은 사용자의 후속 요청이 앞서는 문제 | **세션별 비동기 큐 + 사용자별 DB Worker 배정**: 세션 안에서는 완료 순서를 유지하고, DB는 같은 사용자를 같은 Worker로 분배 |
| TCP 부분 송수신과 느린 클라이언트로 인한 버퍼 누적 | **패킷 경계 복원 + 잔여 구간 송신 + 자원 상한**: 세션 대기 작업 30개, 송신 버퍼 용량 1MiB 제한 |
| DB 저장 실패에도 메모리만 변경되거나 저장 결과가 불명확한 문제 | **DB 커밋 후 메모리 반영**: 성공 응답의 기준을 커밋에 두고, 결과가 불명확하면 연결 종료 후 DB 재조회 |
| 매칭 취소·성립·재접속 정리가 서로 충돌하는 문제 | **상태 변경 직렬화 + Redis 토큰 검증**: 이전 티켓 정리는 새 티켓에 적용하지 않고, 두 티켓 확보 후 연결을 재확인 |
| DB 대기로 Pong 처리가 늦거나 종료 중 저장 자원이 먼저 해제되는 문제 | **Pong 별도 처리 + 의존 순서에 따른 종료**: 연결 생존 확인과 게임 작업을 분리하고, 진행 중 작업 완료 후 저장소 해제 |

[설계 상세와 검증 근거](#핵심-설계-상세) · [서버 전체 구성](#서버-전체-구성) · [실행 방법](#실행-방법)

## 검증 결과

아래 테스트와 개선 결과는 공개본에서 검증한 내용이며, 과거 서비스 운영 실적과 구분합니다.

| 검증 | 조건과 결과 | 기록 |
|---|---|---|
| 자동 테스트 | 2026-10-12 프로토콜 전환 후 재확인: ServerCore 49개 + GameServer 31개, **80개 통과** | [검토 결과](docs/PORTFOLIO_REVIEW.md) |
| 실제 저장소 통합 | MySQL 저장 후 직접 조회·재로그인 확인, Hero 복구, Redis 매칭, 연결 종료, 로그인 제한·Heartbeat 확인 | [검토 결과](docs/PORTFOLIO_REVIEW.md) |
| 여러 부하 시나리오 | 조회·저장·매칭·접속 한계 등 14개 시나리오에서 측정 실패 0건 | [부하 테스트](docs/LOAD_TEST_RESULTS.md) |
| 1시간 지속 부하 | 동시 60명, 조회 80%·저장 20%, **1,799,156건 성공 / 실패 0건**, 실제 499.76요청/초 | [장시간 테스트](docs/SOAK_TEST_RESULTS.md) |
| 세션 재사용·메모리 | 동시 60명, 준비 3회·측정 50회. 총 3,180회 로그인 후 세션 반환 확인. GC 후 관리 메모리 **8.627 → 8.622MiB** | [메모리 검증](docs/MEMORY_TEST_RESULTS.md) |

1시간 부하의 p95는 76ms, p99는 129ms였습니다(1ms 히스토그램 상한값). 서버·DB·클라이언트를 동일 PC에서 실행했고, 조회는 메모리 캐시, 저장은 실제 MySQL 트랜잭션을 사용했습니다. 이는 해당 조건의 로컬 측정값이며 운영 최대 처리량을 의미하지 않습니다.

메모리 검증에서는 종료 후 유저·매칭 상태·DB 대기 작업이 비워지고 세션 100개가 풀로 돌아왔습니다. 약 6분간의 반복 시험에서 생존 게임 객체의 지속 누적은 관찰되지 않았습니다. 모든 실행 경로의 누수 부재를 보장하는 결과는 아닙니다.

자동 테스트와 기본 통합 스모크는 저장소에 포함되어 있습니다. 부하·장시간·메모리 시험의 별도 실행기와 원본 덤프는 현재 공개본에 포함하지 않았으므로, [실행 방법](#실행-방법)의 명령만으로 해당 부하 결과를 재현할 수는 없습니다. 각 결과 문서에 측정 조건과 범위를 기록했습니다.

## 서버 전체 구성

서버는 **하나의 GameServer 프로세스** 안에서 동작합니다. **GameServer** 프로젝트가 게임 기능을 담당하고, **ServerCore** 라이브러리의 네트워크·세션·저장소 기반을 사용합니다. 클라이언트와 MySQL·Redis는 프로세스 밖에 표시했습니다.

![서버 구조도](docs/diagrams/overview.svg)

[확대해서 보기](docs/diagrams/overview.svg) · [편집 원본](docs/diagrams/SOURCES.md#overview)

큰 테두리는 프로세스, 색상 영역은 프로젝트, 내부 상자는 기능 묶음입니다. 프로젝트 사이의 화살표는 **참조 관계**, 양방향 선은 **외부 통신**을 나타냅니다.

- `GameSession`은 ServerCore의 `Session`을 상속하며, 로그인 사용자와 연결별 Hero 상태를 보관합니다.
- 매칭 대기·방 상태는 GameServer 메모리에, 사용자·Hero 데이터는 MySQL에, 매칭 티켓은 Redis에 보관합니다.
- `Program`이 설정과 객체 구성을 담당하며, `RedisManager`가 준비한 연결을 `RedisMatchmakingStore`에서 사용합니다.

패킷 처리와 종료 등의 실행 순서는 [주요 코드 호출 흐름](docs/SERVER_ARCHITECTURE.md)에서 별도로 설명합니다.

## 핵심 설계 상세

### 1. 연결 종료와 세션 재사용 시점을 분리

소켓을 닫아도 이미 시작한 I/O와 DB 작업의 완료 처리는 나중에 도착할 수 있습니다. 세션 객체를 먼저 풀에 돌려주면 이전 연결의 완료 처리가 새 연결의 상태를 변경하거나 연결을 종료할 수 있습니다.

- **연결의 신원:** 비동기 작업과 외부 알림에 요청 당시 연결 ID를 전달합니다. 작업 등록과 종료 요청은 수명 잠금 안에서 ID를 검사하여, 같은 객체를 사용하는 다음 연결과 구분합니다.
- **반환 조건:** 종료 훅이 끝나고 수신·송신 I/O와 세션 큐 작업이 모두 끝나야 반환을 한 번만 등록합니다. 잠금은 이 조건 검사와 상태 전이를 보호하며, DB를 기다리는 동안 유지하지 않습니다.
- **반환 순서:** `Channel<Session>`의 단일 작업자가 `Reset → SocketAsyncEventArgs 풀 반환·참조 해제 → 세션 풀 반환`을 수행합니다. 정리에 실패한 세션은 재사용하지 않고 종료 대기에 오류를 전달합니다.

**선택의 대가:** 연결이 끊겨도 진행 중 작업이 끝나기 전까지 세션 슬롯을 점유합니다. 강제 초기화로 슬롯을 빨리 회수하면 이전 작업과 새 연결을 다시 겹치게 만들 수 있으므로 실제 완료를 기다립니다.

**검증:** [수명 테스트](Server/ServerCore.Tests/SessionLifecycleTests.cs)는 실행 중 작업·종료 훅이 반환을 지연하는지, 중복 종료가 한 번만 반환되는지, 이전 연결 ID가 거부되는지 검사합니다. [반환 순서 테스트](Server/ServerCore.Tests/SessionManagerTests.cs)는 이벤트 반환을 지연시켜 세션이 먼저 재사용되지 않는지 확인합니다.

구현: [Session](Server/ServerCore/Network/Session.cs), [SessionManager](Server/ServerCore/Network/SessionManager.cs) · [수정 기록](docs/SESSION_LIFETIME_REVIEW.md)

### 2. 세션과 DB의 작업 순서를 각각 관리

Hero 변경 직후 목록을 요청했을 때, 변경 요청의 DB 대기 중 목록 요청이 먼저 실행되면 이전 상태를 응답할 수 있습니다. 한편 한 연결의 대기 때문에 모든 연결의 게임 처리가 멈춰서도 안 됩니다.

- **세션 안의 순서:** 수신 버퍼가 유효할 때 protobuf를 파싱하고, 게임 처리는 세션별 비동기 큐에 등록합니다. 한 작업의 `await`가 끝난 뒤 다음 작업을 실행하므로, DB 대기를 포함한 요청 순서를 유지합니다.
- **세션 사이의 실행:** 각 세션은 독립된 큐 소비 작업을 사용합니다. 게임 핸들러는 소켓 I/O 콜백에서 직접 실행하지 않으며, 한 세션이 기다리는 동안 다른 세션의 큐는 진행할 수 있습니다.
- **DB 실행 경계:** `userId % WorkerCount`로 DB Worker를 선택합니다. 같은 사용자의 DB 작업은 같은 Worker에서 등록 순서대로 실행하고, 서로 다른 Worker는 독립적으로 실행합니다.

**선택의 대가:** 같은 세션의 후속 요청은 앞선 DB 작업을 기다립니다. 다른 사용자라도 같은 DB Worker에 배정되면 서로 대기하므로, 이 구조가 사용자별 DB 지연 격리까지 보장하지는 않습니다.

**검증:** [세션 큐 테스트](Server/ServerCore.Tests/SessionQueueTests.cs)에서 첫 작업을 의도적으로 멈춘 뒤 같은 세션의 후속 작업은 대기하고, 다른 세션의 작업은 완료되는지 검사합니다. 이 테스트의 범위는 세션 큐이며 실제 DB Worker의 장애·편중 부하 검증과는 구분합니다.

구현: [GSPacketHandler](Server/GameServer/Packet/GSPacketHandler.cs), [Session](Server/ServerCore/Network/Session.cs), [DbWorkerPool·DbWorker](Server/ServerCore/DataBase/DataBaseWorker.cs)

### 3. TCP의 부분 완료와 자원 누적을 명시적으로 처리

TCP 수신 한 번이 패킷 하나와 일치하지 않으며, 송신 완료도 요청한 전체 바이트의 전송을 뜻하지 않습니다. 요청을 빠르게 보내거나 응답을 느리게 읽는 연결에는 작업과 버퍼가 계속 쌓일 수 있습니다.

- **수신 경계:** 길이 헤더를 기준으로 완성된 패킷만 처리합니다. 미완성 데이터는 다음 수신까지 보존하고, 한 번에 도착한 여러 패킷은 차례로 분리합니다.
- **부분 송신:** 실제 전송된 바이트만큼 구간을 이동하고 남은 데이터를 이어 보냅니다. 버퍼 전체의 전송이 끝난 뒤에만 반환합니다.
- **자원 상한:** 세션 큐의 대기 작업은 30개로 제한합니다. 송신은 대기 중·진행 중 버퍼의 **할당 용량 합계**를 1MiB로 제한하며, 두 한도 초과는 패킷 처리 경로에서 연결 종료로 이어집니다. DB Worker도 실행 중 작업을 포함해 각각 10,000개까지만 받아 초과 등록을 실패시킵니다.

**선택의 대가:** 초과 요청을 계속 쌓아 두지 않으므로 순간적인 과부하에서도 요청 실패나 연결 종료가 발생할 수 있습니다. 위 수치는 현재 구현의 상한이며 최적 용량을 뜻하지 않습니다.

**검증:** [세션 큐 테스트](Server/ServerCore.Tests/SessionQueueTests.cs)는 대기 30개 허용·다음 작업 거부·큐 회복을 검사합니다. [송신 제한 테스트](Server/ServerCore.Tests/SessionSendLimitTests.cs)는 대기·진행 중 용량 합산, 정확한 경계값, 부분 송신의 잔여 데이터와 버퍼 반환을 검사합니다.

구현: [Session](Server/ServerCore/Network/Session.cs), [DbWorker](Server/ServerCore/DataBase/DataBaseWorker.cs)

### 4. Hero 저장의 성공 기준을 DB 커밋에 둠

메모리를 먼저 바꾸면 DB 저장 실패 시 서버가 응답하는 상태와 영속 데이터가 달라집니다. 반대로 커밋 응답이 유실되면 서버가 예외를 받아도 DB에는 이미 반영되었을 수 있습니다.

- **조회와 저장:** 로그인 시 DB에서 읽은 Hero를 현재 연결에 보관하고 목록은 메모리에서 조회합니다. 변경은 소유권·입력을 검사한 뒤 MySQL 트랜잭션 안에서 갱신·재조회하고 커밋합니다.
- **메모리 반영:** 저장소 호출이 성공한 후 요청 당시 연결이 유효한지 다시 확인하고, DB가 반환한 Hero 스냅샷으로 메모리를 교체한 뒤 응답합니다.
- **불명확한 결과:** 잘못된 입력·소유권 오류는 오류 응답으로 처리합니다. 저장 예외는 해당 연결을 종료하고 재로그인 시 DB를 다시 읽도록 하여, 확인되지 않은 메모리 상태로 계속 처리하지 않습니다.

**선택의 대가:** 변경 응답에는 DB 커밋 지연이 포함되며, 결과가 불명확한 경우 클라이언트의 재로그인이 필요합니다. 캐시는 연결 전용이어서 외부 프로세스의 DB 변경을 실시간 동기화하지 않습니다.

**검증:** [Hero 테스트](Server/GameServer.Tests/HeroAndMatchTests.cs)는 저장소 대역의 완료를 지연하거나 실패시켜 저장 성공 전 메모리 유지, 오류 시 기존 값 유지, 연결 무효화 후 반영 생략을 검사합니다. 실제 MySQL 저장 후 직접 조회·재로그인 확인은 [통합 검증 기록](docs/PORTFOLIO_REVIEW.md)에 있으며, 커밋 응답 유실 주입은 추가 검증 항목입니다.

구현: [HeroManager](Server/GameServer/Hero/HeroManager.cs), [HeroDB](Server/GameServer/DataBase/DBRepository/HeroDB.cs), [GSPacket](Server/GameServer/Packet/GSPacket.cs)

### 5. 매칭 상태 전이와 Redis 티켓 소유권을 함께 확인

매칭 참가·취소·성립·연결 종료가 겹치면 이미 취소된 사용자를 방에 넣을 수 있습니다. 재접속 뒤에는 이전 연결의 늦은 정리가 같은 사용자 키의 새 티켓을 삭제하는 문제도 생깁니다.

- **로컬 상태 전이:** 참가·취소·성립·퇴장·연결 종료 처리를 하나의 `SemaphoreSlim`으로 직렬화합니다. 대기열·방·사용자별 방 인덱스를 같은 경계 안에서 변경합니다.
- **티켓 소유권:** Redis 값에 연결 ID와 고유 토큰을 함께 저장합니다. Lua에서 값이 일치할 때만 삭제하고, 매칭 성립 시에는 두 티켓의 값을 함께 확인·삭제합니다. Redis 호출 후에도 두 연결이 유효한지 다시 검사한 뒤 방을 만듭니다.
- **정리 실패:** Redis 삭제가 실패하면 정리 큐에 남겨 재시도하고, 티켓에는 120초 TTL을 둡니다. 취소 응답은 로컬 대기 해제를 뜻하며 Redis 삭제 완료까지 보장하지 않습니다.

**선택의 대가:** 매칭 잠금을 Redis 호출 중에도 유지하므로 Redis 지연은 다른 매칭 요청에 영향을 줍니다. 대기열과 방은 단일 프로세스가 소유하며, Redis 티켓 사용만으로 다중 서버 매칭을 구현한 것은 아닙니다.

**검증:** [매칭 테스트](Server/GameServer.Tests/HeroAndMatchTests.cs)는 저장소 대역으로 중복 참가·취소, 이전 연결의 종료, 티켓 확보 중 연결 종료, 정리 실패 후 재시도를 검사합니다. 실제 Redis 토큰 불일치·재시작·TTL 만료에 대한 장애 주입은 추가 검증 항목입니다.

구현: [MatchManager](Server/GameServer/Matchmaking/MatchManager.cs), [RedisMatchmakingStore](Server/GameServer/Matchmaking/RedisMatchmakingStore.cs)

### 6. 연결 생존 확인과 저장 완료 대기를 분리

Pong을 일반 게임 작업과 같은 큐에 넣으면 DB 대기 뒤에 밀려 정상 연결을 끊을 수 있습니다. 서버 종료 때 DB·Redis를 먼저 해제하면 아직 실행 중인 세션 작업과 매칭 정리가 사용할 자원을 잃습니다.

- **연결 생존 확인:** 로그인은 DB 조회를 포함해 접속 후 30초 안에 완료해야 합니다. 로그인 후 Ping 간격은 10초, 유효한 Pong 대기는 30초이며, 단일 검사 작업자가 1초마다 `Stopwatch` 기준으로 확인합니다.
- **Pong 별도 처리:** Pong은 게임 큐를 거치지 않고 수신 경로에서 연결 ID·현재 Ping 번호·기한을 확인합니다. 잘못된 번호나 중복 응답으로 기한을 연장하지 않습니다.
- **종료의 완료 기준:** 새 연결 수락을 중단하고 `세션·반환 작업자 → 매칭 → DB Worker·DB 연결 → Redis` 순서로 기다립니다. 10초마다 경고하되 저장 작업을 일괄 취소하지 않으며, 실제 정리 오류는 호출자에게 전달합니다.

**선택의 대가:** 종료 시간의 상한을 보장하지 않습니다. 연결을 끊거나 경고를 기록한 시점과 작업·자원 정리가 완료된 시점을 구분합니다.

**검증:** [Heartbeat 테스트](Server/ServerCore.Tests/HeartbeatTests.cs)는 제한 시간 경계, 잘못된 Pong, 세션 재사용 후 이전 연결의 응답을 검사합니다. [종료 테스트](Server/GameServer.Tests/ShutdownTests.cs)는 경고가 종료 작업을 취소·완료시키지 않고 실제 오류가 전파되는지 검사합니다. 실제 DB 작업 진행 중 종료는 추가 통합 검증 항목입니다.

구현: [SocketHeartBeat](Server/ServerCore/Network/SocketHeartBeat.cs), [Session](Server/ServerCore/Network/Session.cs), [Program](Server/GameServer/Program.cs)

## 구조와 기술

| 구성 | 역할 |
|---|---|
| [ServerCore](Server/ServerCore) | SocketAsyncEventArgs 기반 TCP, 패킷 분할·병합 수신, 세션 큐, 버퍼 및 DB 작업 큐 |
| [GameServer](Server/GameServer) | 로그인, Hero 조회·변경, PvP 매칭 |
| [Library/protoc-35.1-win64/bin](Server/Library/protoc-35.1-win64/bin) | protobuf 원본·패킷 ID 대응표·직접 작성한 생성 도구 |
| [GameServer/Protocol](Server/GameServer/Protocol) | 생성된 protobuf C#과 PacketIds; 서버·스모크 클라이언트가 함께 사용 |
| [ServerCore.Tests](Server/ServerCore.Tests), [GameServer.Tests](Server/GameServer.Tests) | xUnit 자동 테스트, 저장소 대역 및 루프백 TCP 검증 |
| [ServerSmokeTester](ServerSmokeTester) | 실제 DB·Redis를 사용하는 통합 스모크 클라이언트 |
| [database](database), [compose.yml](compose.yml) | 로컬 MySQL 8.4·Redis 7.4, 스키마와 가상 시드 데이터 |

DB 접근에는 MySqlConnector, Redis 접근에는 StackExchange.Redis를 사용합니다. DB DTO·DBRepository·Hero 구성을 유지하며, 패킷은 `GSPacketHandler`의 ID별 등록 배열을 통해 처리합니다. 게임 핸들러의 `false` 반환은 연결 종료가 필요한 실패를 의미하고, 실제 종료는 공통 처리부에서 수행합니다.

## 실행 방법

필수: **.NET 10 SDK**. 실제 저장소를 사용하는 경우 Docker Desktop 또는 Docker Engine과 Compose가 추가로 필요합니다. Windows Docker Desktop은 WSL 2 엔진이 정상 실행된 상태여야 합니다. 아래 명령은 모두 저장소 루트에서 실행합니다.

### 서버 없이 자동 테스트

```powershell
dotnet restore CSharpServer.slnx
dotnet test CSharpServer.slnx -c Release --no-restore
```

Windows에서는 `run-tests.bat`로도 실행할 수 있습니다. Visual Studio에서는 **테스트 → 테스트 탐색기 → 모두 실행**을 사용합니다.

### MySQL·Redis와 게임 서버 실행

```powershell
docker compose up -d
docker compose logs --tail 30 mysql
docker compose exec redis redis-cli ping
```

최초 MySQL 초기화가 완료되고 최종 서버가 `port: 3306`에서 `ready for connections` 상태가 된 것을 확인합니다. Redis 응답은 `PONG`입니다. 이어서 게임 서버를 실행합니다.

```powershell
dotnet run --project Server/GameServer -c Release
```

다른 터미널에서 통합 스모크를 실행합니다.

```powershell
dotnet run --project ServerSmokeTester -c Release -- --smoke
```

성공 시 다음을 출력합니다.

```text
PASS: login, Hero update/restore, match cancel/pair/disconnect.
```

스모크는 두 명의 로그인, Hero 조회·변경·원상복구, 매칭 취소·성립·상대 연결 종료를 검사합니다. 실행 도중 강제 종료하면 Hero 값 복구가 완료되지 않을 수 있습니다. Visual Studio에서 서버를 실행할 때는 GameServer를 시작 프로젝트로 지정합니다.

| 대상 | 로컬 주소 / 계정 |
|---|---|
| 게임 서버 | `127.0.0.1:7777` |
| MySQL | `127.0.0.1:3307`, DB `csharp_demo`, 사용자 `demo`, 암호 `demo-local-only` |
| Redis | `127.0.0.1:6380` |
| 데모 로그인 | `demo-a`, `demo-b` — 비밀번호 없는 시드 계정 |

설정은 [config.json](Server/GameServer/Config/config.json)에 있습니다. 개인 환경에서 덮어쓸 값은 같은 폴더의 `config.local.json`에 두며 Git·공개 ZIP·게시 출력에서 제외합니다. 공개 암호는 로컬 예제용입니다. `database/*.sql`은 새 MySQL 볼륨을 처음 만들 때만 적용됩니다.

종료할 때는 게임 서버 터미널에서 **Ctrl+C를 누르고 정상 종료를 기다린 뒤**, 저장소를 중지합니다.

```powershell
docker compose stop
```

DB 볼륨은 보존됩니다. Docker Desktop과 WSL 자체는 이 명령으로 종료되지 않습니다.

## 프로토콜 자동 생성

프로토콜 원본은 기존 구조대로 `Server/Library/protoc-35.1-win64/bin`의 `Enum.proto`, `Struct.proto`, `Protocol.proto` 한 곳에서 관리합니다. 생성된 C#은 `Server/GameServer/Protocol`에 배치합니다. `GenPackets.bat`가 protoc로 C#을 생성하고, 직접 작성한 `GenPacketHandlers.ps1`이 패킷 ID·등록 코드·없는 핸들러의 뼈대를 갱신합니다. 기존 핸들러 구현과 등록 구간 밖의 Heartbeat Pong 처리는 보존합니다.

```powershell
.\Server\Library\protoc-35.1-win64\bin\GenPackets.bat
```

재생성에는 같은 폴더의 `protoc.exe`가 필요합니다. 실행 파일은 로컬 도구로 관리하며 Git·공개 ZIP에서는 제외합니다. 생성된 C# 파일은 저장소에 포함하므로 일반 빌드와 테스트에는 protoc가 필요하지 않습니다. 스모크 클라이언트는 동일한 생성 파일을 링크하여 컴파일합니다.

같은 `bin` 폴더의 `PacketRoutes.json`은 기존 패킷 ID·메시지·핸들러 이름의 대응표입니다. 메시지 순서 변경이나 삭제로 ID가 바뀌지 않으며, 같은 `ACK_MATCH`가 여러 응답 ID에서 사용되는 구조도 유지합니다. 상세 절차는 [프로토콜 생성 안내](Server/GameServer/Protocol/README.md)를 참고하세요.

## 범위와 한계

- 데모 로그인만 제공하며 계정 생성·실서비스 인증은 포함하지 않습니다.
- TCP 연결이 유지되면 같은 세션을 사용하고, 끊기면 다시 로그인합니다. 재접속 유예나 매칭 방 복원은 제공하지 않습니다.
- 매칭 방은 프로세스 메모리에만 존재합니다. 다중 서버 매칭·전투·보상은 구현 범위에 포함하지 않습니다.
- Hero 캐시는 현재 연결 전용이며 외부 프로세스의 DB 변경을 실시간 동기화하지 않습니다.
- 실제 DB·Redis 장애 주입, 커밋 응답 유실, DB 작업 진행 중 서버 종료는 추가 통합 검증 항목입니다. 저장소 대역을 사용한 실패 테스트와 구분합니다.

## 관련 자료와 공개본

- [업로드 전 최종 확인](docs/RELEASE_CHECKLIST.md)
- [검토 결과와 남은 검증](docs/PORTFOLIO_REVIEW.md)
- [세션 종료·재사용 수정 기록](docs/SESSION_LIFETIME_REVIEW.md)
- [공개 범위 정리](docs/PORTFOLIO_REDUCTION_PLAN.md)

PowerShell 7에서 다음 명령으로 공개용 ZIP을 만들 수 있습니다.

```powershell
pwsh -File ./export-portfolio.ps1
```

`artifacts/CSharpServer.zip`에는 공개 소스·문서·테스트·로컬 실행 자료를 담으며, 기존 Git 이력·로컬 설정·빌드 산출물·로그·`TestResults`는 제외합니다.

GitHub에 올릴 때는 ZIP을 새 폴더에 풀고, 그 안의 `CSharpServer` 폴더 내용을 저장소 루트로 사용합니다. 기존 작업 폴더의 `.git`과 로컬 백업을 함께 복사하지 않습니다.
