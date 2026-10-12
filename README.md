# CSharp Server Portfolio

C# / .NET 기반 비동기 TCP 게임 서버 개인 포트폴리오입니다. 기존에 직접 개발한 C++ IOCP 서버를 바탕으로 C# 서버 코어와 게임 로직을 설계·구현했습니다. 세션 수명 관리, Hero 저장의 DB·메모리 일관성, PvP 매칭과 연결 종료 처리에 집중했습니다.

## 핵심 설계

**ServerCore는 공통 서버 기반을, GameServer는 게임 상태와 규칙을 담당합니다.** 네트워크 I/O, 세션별 게임 처리, DB 작업의 실행 단위를 구분하고 각 상태의 소유 위치와 자원 반환 시점을 정했습니다.

| 설계 영역 | 구성과 동작 |
|---|---|
| **계층과 의존 관계** | GameServer가 ServerCore를 참조합니다. 통신·세션·저장소 실행 기반은 코어에, 패킷별 게임 처리·Hero·매칭·Repository는 게임 서버에 둡니다. |
| **비동기 TCP 통신** | `SocketAsyncEventArgs`와 이벤트 객체 풀을 사용합니다. 길이 헤더로 패킷을 구분하고, 수신 버퍼와 송신 구간을 관리합니다. |
| **세션 실행 모델과 수명** | 세션별 비동기 큐가 게임 요청을 순차 실행합니다. 연결 ID로 상태를 구분하고 I/O·작업·정리가 완료된 세션을 풀에서 재사용합니다. |
| **데이터 접근과 상태 반영** | 사용자 ID로 DB Worker를 배정합니다. MySQL의 저장 데이터를 기준으로 삼고, DB 저장이 완료된 게임 데이터를 현재 연결의 메모리에 반영합니다. |
| **매칭 상태와 저장소 역할** | MatchManager가 대기열·방을 메모리에서 관리합니다. 상태 변경은 직렬화하고 Redis에는 연결별 매칭 티켓을 보관합니다. |
| **연결 감시와 서버 종료** | 단일 Heartbeat 작업자가 연결을 검사합니다. 종료 시 세션·매칭·DB·Redis의 의존 순서에 따라 작업 완료와 자원 해제를 기다립니다. |

[설계 상세](#핵심-설계-상세) · [서버 전체 구성](#서버-전체-구성) · [실행 방법](#실행-방법)

## 검증 결과

### 대표 성능 결과

**동시 접속 60명**을 기준으로 조회·저장·혼합 요청의 결과를 비교했습니다.

| 요청 유형 | 측정 시간 | 성공 요청 | 실패 요청 | 처리량 (요청/초) | p95 (ms) | p99 (ms) |
|---|---:|---:|---:|---:|---:|---:|
| 데이터 조회 | 8.0초 | 128,809 | 0 | 16,097.62 | 6.69 | — |
| 데이터 저장 | 8.19초 | 2,740 | 0 | 334.37 | 203.05 | — |
| 혼합 요청 — 조회 80%·저장 20% | 8.19초 | 13,030 | 0 | 1,590.34 | 189.86 | — |
| 1시간 지속 부하 — 조회 80%·저장 20% | 3,600.06초 | **1,799,156** | **0** | **499.76** | **76** | **129** |

단기 부하는 응답을 받은 뒤 다음 요청을 보내는 방식이며, 1시간 부하는 **목표 500요청/초**로 실행했습니다. 두 시험의 처리량은 부하 방식이 다르므로 구분해서 해석합니다. `—`는 해당 기록에 수치가 없는 항목입니다.

[단기 부하 전체 14개 시나리오](docs/LOAD_TEST_RESULTS.md) · [1시간 지속 부하 상세](docs/SOAK_TEST_RESULTS.md)

### 기능·세션 검증

| 검증 항목 | 검증 규모·기준 | 대표 결과 |
|---|---|---|
| 자동 테스트 | ServerCore 49개 + GameServer 31개 | **80 / 80개 통과** |
| 세션 반환 | 동시 60명, 준비 3회 + 측정 50회, 로그인 총 3,180회 | 회차 종료 후 활성 세션 **0개**, 유휴 세션 **100 / 100개** |
| 관리 메모리 | GC 후 관리 메모리, 준비 후 → 측정 50회 후 | **8.627 → 8.622MiB** |

자동 테스트는 2026-10-12 프로토콜 전환 후 재확인한 결과입니다. 세션·메모리 시험은 약 6분간 반복했으며, 정리 후 사용자·매칭 상태·DB 대기 작업이 비워지고 게임 객체의 지속 누적이 관찰되지 않았습니다.

[자동 테스트·통합 검증 기록](docs/PORTFOLIO_REVIEW.md) · [세션 재사용·메모리 상세](docs/MEMORY_TEST_RESULTS.md)

### 그 밖의 검증

- **실제 MySQL·Redis 통합:** 저장 후 DB 직접 조회·재로그인 복구, 매칭 취소·성립·상대 연결 종료, Redis 잔여 티켓 정리를 확인했습니다. [검증 기록](docs/PORTFOLIO_REVIEW.md)
- **접속 한계·연결 감시:** 기존 100개 연결 유지와 추가 10개 연결 거절, 90개 연결의 45초 유지·Ping/Pong·후속 조회를 확인했습니다. 로그인 제한 시간과 Pong 미응답 종료도 별도로 확인했습니다. [부하 기록](docs/LOAD_TEST_RESULTS.md) · [연결 감시 기록](docs/PORTFOLIO_REVIEW.md)

<details>
<summary>측정 조건과 재현 범위</summary>

성능 시험은 서버·DB·클라이언트를 동일 PC에서 실행했습니다. 단기 부하는 Debug, 1시간 부하는 Release 구성입니다. 조회는 연결별 메모리 캐시, 저장은 실제 MySQL 트랜잭션을 사용했습니다. 지속 부하의 p95·p99는 1ms 히스토그램 상한값이며, 표의 처리량은 해당 로컬 조건의 측정값입니다.

메모리 결과는 GC 후 관리 메모리를 비교한 값이며, 약 6분의 반복 시험에서 관찰한 범위입니다. 모든 실행 경로의 누수 부재를 보장하는 결과는 아닙니다.

자동 테스트와 기본 통합 스모크는 저장소에 포함되어 있습니다. 부하·장시간·메모리 시험의 별도 실행기와 원본 덤프는 현재 공개본에 포함하지 않았습니다. 상세 문서에서 측정 조건과 범위를 확인할 수 있습니다.

</details>

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

### 1. ServerCore와 GameServer의 책임 분리

서버는 하나의 프로세스에서 실행되며, **GameServer → ServerCore** 방향으로 프로젝트를 참조합니다. 공통 실행 기반과 게임 도메인의 경계를 프로젝트 수준에서 나눴습니다.

- **ServerCore:** 연결 수락, TCP 송수신, 패킷 경계 처리, 세션 큐·풀, Heartbeat, DB Worker와 Redis 연결을 담당합니다.
- **GameServer:** 패킷별 게임 핸들러, 로그인 사용자, Hero 상태, 매칭 규칙, 게임 데이터의 SQL과 Repository를 담당합니다.
- **연결 지점:** `GameSession`은 `Session`을 상속해 사용자·Hero 상태와 종료 동작을 확장합니다. 코어는 `IPacketHandler`를 통해 패킷을 전달하고, `Program`이 Repository·MatchManager·패킷 핸들러·Service를 구성해 연결합니다.

게임 데이터의 의미와 처리 규칙은 GameServer에 두고, 코어의 통신·작업 실행 기능을 사용하도록 구성했습니다.

구현: [GameSession](Server/GameServer/Sessions/GameSession.cs), [IPacketHandler](Server/ServerCore/Interface/IPacketHandler.cs), [Service](Server/ServerCore/Network/Service.cs), [Program](Server/GameServer/Program.cs)

### 2. SocketAsyncEventArgs 기반 TCP 통신과 버퍼 관리

`Service`가 수신·송신용 `SocketAsyncEventArgs` 풀을 보유하고, `Session`이 연결별 비동기 I/O와 버퍼 상태를 관리합니다. 패킷은 **길이·ID 헤더와 protobuf 본문**으로 구성합니다.

- **수신:** 길이 헤더로 완성된 패킷을 구분합니다. 미완성 데이터는 다음 수신까지 보존하고, 한 번에 여러 패킷이 들어오면 차례로 분리합니다. protobuf는 수신 버퍼가 유효한 동안 파싱해 게임 작업 큐로 전달합니다.
- **송신:** 대기 버퍼들을 `BufferList`로 묶어 전송합니다. 부분 송신 시 실제 전송량만큼 구간을 이동하고 나머지를 이어 보내며, 각 버퍼의 전송이 끝나면 반환합니다.
- **자원 한도:** 세션별 대기 작업은 30개, 대기·진행 중 송신 버퍼의 할당 용량 합계는 1MiB로 제한합니다. 한도 초과는 게임 패킷 처리 경로에서 연결 종료로 이어집니다.

수신 데이터의 소비 위치, 송신 데이터의 완료 위치, 버퍼를 반환할 수 있는 시점을 각각 관리합니다. 한도는 현재 구현의 정책값이며 부하 특성에 따른 조정 대상입니다.

구현: [Session](Server/ServerCore/Network/Session.cs), [Service](Server/ServerCore/Network/Service.cs), [GSPacketHandler](Server/GameServer/Packet/GSPacketHandler.cs)  
관련 검증: [버퍼·패킷 테스트](Server/ServerCore.Tests/BufferAndPacketTests.cs), [송신 제한·부분 송신 테스트](Server/ServerCore.Tests/SessionSendLimitTests.cs)

### 3. 세션별 순차 실행과 연결 수명 관리

**세션을 게임 요청의 실행 단위**로 사용합니다. 각 세션은 비동기 작업 큐를 가지며, 세션 객체의 재사용과 개별 연결의 수명은 연결 ID로 구분합니다.

- **게임 작업 실행:** 같은 세션에서는 앞선 작업의 `await`가 끝난 뒤 다음 작업을 실행합니다. 서로 다른 세션은 독립된 큐 소비 작업으로 진행하며, 게임 핸들러는 소켓 I/O 콜백 밖에서 실행합니다.
- **연결 식별:** 작업 등록·외부 알림·종료 요청에는 요청 당시 연결 ID를 사용합니다. ID 검사와 수명 상태 전이는 잠금으로 보호하고, DB를 기다리는 동안에는 해당 잠금을 유지하지 않습니다.
- **풀 반환:** 연결 종료 후 종료 훅, 수신·송신 I/O, 세션 큐 작업의 완료를 확인합니다. 이후 `Channel<Session>`의 단일 반환 작업자가 `Reset → 이벤트 객체 반환·참조 해제 → 세션 풀 반환`을 수행합니다.

이 모델에서 같은 세션의 후속 요청은 진행 중인 DB 작업을 기다립니다. 연결이 끊긴 뒤에도 작업과 정리가 완료될 때까지 해당 세션 슬롯을 유지합니다.

구현: [Session](Server/ServerCore/Network/Session.cs), [SessionManager](Server/ServerCore/Network/SessionManager.cs)  
관련 검증: [세션 큐 테스트](Server/ServerCore.Tests/SessionQueueTests.cs), [수명 테스트](Server/ServerCore.Tests/SessionLifecycleTests.cs), [반환 순서 테스트](Server/ServerCore.Tests/SessionManagerTests.cs)

### 4. 사용자별 DB 작업 분배와 Hero 상태 관리

DB 접근은 **게임별 Repository → MysqlDataBase → DbWorkerPool**로 이어집니다. SQL과 데이터 변환은 GameServer가 담당하고, 작업 분배·실행·연결 관리는 ServerCore가 담당합니다.

- **작업 분배:** `userId % WorkerCount`로 Worker를 선택합니다. 같은 사용자의 DB 작업은 같은 Worker에서 등록 순서대로 실행하며, 서로 다른 Worker는 독립적으로 실행합니다. Worker별로 실행 중 작업을 포함해 10,000개까지 받습니다.
- **상태 소유:** MySQL에 사용자·Hero를 영속 저장합니다. 로그인 시 읽은 Hero는 `GameSession.Heroes`가 현재 연결 동안 보관하고, 목록 요청은 이 메모리를 조회합니다.
- **변경 반영:** `HeroDB`가 트랜잭션 안에서 갱신·재조회 후 커밋합니다. `HeroManager`는 요청 당시 연결이 유효한지 확인한 뒤 저장된 스냅샷으로 메모리를 교체하고 응답합니다.

같은 Worker에 배정된 다른 사용자의 DB 작업도 순차 실행됩니다. Hero 캐시는 연결 전용이며, 저장 결과가 불명확한 예외에서는 연결을 종료하고 재로그인 시 DB를 다시 읽습니다.

구현: [DbWorkerPool·DbWorker](Server/ServerCore/DataBase/DataBaseWorker.cs), [HeroDB](Server/GameServer/DataBase/DBRepository/HeroDB.cs), [HeroManager](Server/GameServer/Hero/HeroManager.cs)  
관련 검증: [Hero 저장소 대역 테스트](Server/GameServer.Tests/HeroAndMatchTests.cs), [실제 MySQL 저장·재로그인 검증](docs/PORTFOLIO_REVIEW.md)

### 5. MatchManager 중심의 매칭 상태 관리

`MatchManager`가 **대기 사용자, 매칭 방, 사용자별 방 인덱스**를 소유합니다. 방과 대기열은 서버 메모리에 두고, Redis는 연결별 매칭 티켓을 보관하는 역할로 사용합니다.

- **매칭 규칙:** 대기 시간 순으로 후보를 살펴보고, 점수 차이 1,000 이내에서 가장 가까운 상대를 선택해 1:1 방을 구성합니다.
- **상태 변경:** 참가·취소·성립·퇴장·연결 종료를 하나의 `SemaphoreSlim`으로 직렬화합니다. 매칭 알림은 연결 ID를 포함해 대상 세션의 작업 큐로 전달합니다.
- **Redis 연동:** 티켓에는 연결 ID와 고유 토큰을 저장합니다. Lua로 두 티켓의 값을 확인·삭제한 뒤 연결 유효성을 재확인하고 방을 만듭니다. 개별 정리도 토큰이 일치하는 티켓에 적용하며, 삭제 재시도와 120초 TTL을 사용합니다.

현재 대기열과 방의 소유 범위는 단일 서버 프로세스입니다. Redis 호출은 매칭의 직렬 처리 구간에 포함되며, 취소 응답은 로컬 대기 해제를 의미합니다.

구현: [MatchManager](Server/GameServer/Matchmaking/MatchManager.cs), [RedisMatchmakingStore](Server/GameServer/Matchmaking/RedisMatchmakingStore.cs), [GameSession](Server/GameServer/Sessions/GameSession.cs)  
관련 검증: [매칭 상태·정리 재시도 테스트](Server/GameServer.Tests/HeroAndMatchTests.cs), [실제 Redis 매칭 검증](docs/PORTFOLIO_REVIEW.md)

### 6. Heartbeat와 의존 순서에 따른 서버 종료

연결 감시는 단일 Heartbeat 작업자가 담당하고, 서버 전체의 시작·종료 순서는 `Program`이 조정합니다.

- **로그인·생존 확인:** 접속 후 DB 조회를 포함해 30초 안에 로그인을 완료해야 합니다. 로그인 후 Ping 간격은 10초, 유효한 Pong 대기는 30초이며, 작업자가 1초마다 `Stopwatch` 기준으로 검사합니다.
- **Pong 처리 경로:** 게임 작업 큐를 거치지 않고 수신 경로에서 연결 ID·현재 Ping 번호·기한을 확인합니다. 게임 요청의 DB 대기와 연결 생존 확인의 실행 경로를 분리했습니다.
- **종료 순서:** 새 연결 수락을 중단하고 `세션·반환 작업자 → 매칭 → DB Worker·DB 연결 → Redis` 순서로 완료를 기다립니다. 각 작업이 사용하는 저장소는 해당 작업이 끝난 뒤 해제합니다.

종료 대기가 길어지면 10초마다 경고합니다. 진행 중 저장 작업을 일괄 취소하지 않으므로 종료 시간의 상한은 없으며, 실제 정리 오류는 호출자에게 전달합니다.

구현: [SocketHeartBeat](Server/ServerCore/Network/SocketHeartBeat.cs), [Session](Server/ServerCore/Network/Session.cs), [Program](Server/GameServer/Program.cs)  
관련 검증: [Heartbeat 테스트](Server/ServerCore.Tests/HeartbeatTests.cs), [종료 대기 테스트](Server/GameServer.Tests/ShutdownTests.cs)

자동 테스트의 저장소 대역 검증과 실제 MySQL·Redis 통합 검증은 [검토 기록](docs/PORTFOLIO_REVIEW.md)에서 구분합니다. 실제 저장소 장애 주입·커밋 응답 유실·DB 작업 진행 중 종료는 추가 검증 범위입니다.

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

## 관련 자료

- [검토 결과와 남은 검증](docs/PORTFOLIO_REVIEW.md)
- [세션 종료·재사용 수정 기록](docs/SESSION_LIFETIME_REVIEW.md)
- [공개 범위 정리](docs/PORTFOLIO_REDUCTION_PLAN.md)
- [업로드 전 최종 확인](docs/RELEASE_CHECKLIST.md)
- [공개 ZIP 제작·업로드 안내](docs/PUBLISHING.md)
