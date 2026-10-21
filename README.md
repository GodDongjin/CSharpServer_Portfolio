# C# TCP Game Server Portfolio

C# / .NET 기반 비동기 TCP 게임 서버 개인 포트폴리오입니다. 
기존에 직접 개발한 C++ IOCP 서버를 바탕으로 C# 서버 코어와 게임 로직을 설계·구현했습니다.

`ServerCore`는 비동기 TCP, 세션과 송수신 버퍼, 작업 큐, DB Worker, Redis 연결 기반을 담당합니다.  
`GameServer`는 로그인, 게임 데이터 조회·변경, PvP 매칭, 연결별 상태 관리를 구현합니다.  
`ServerSmokeTester`는 실제 MySQL·Redis를 사용하는 통합 스모크 테스트를 지원합니다.

## 주요 검증 결과

- **동시 60명 · 1시간 지속 부하:** 조회 80%·저장 20%, **1,799,156건 성공 / 실패 0건**, 실제 **499.76요청/초**.
- **세션 재사용·메모리:** 로그인 **3,180회**, 회차 종료 후 세션 **100 / 100개 반환**, GC 후 관리 메모리 **8.627 → 8.622MiB**.
- **기능 검증:** 자동 테스트 **80 / 80개 통과**, 실제 MySQL 저장·재로그인 복구와 Redis 매칭 검증 완료.

부하·통합·메모리 검증은 2026-10-07~12 기록이며, 자동 테스트는 2026-10-21 파일 정리 후 재확인했습니다. 측정 조건과 요청별 처리량·지연은 [검증 결과](#검증-결과)에 정리했습니다.

## 목차

[핵심 설계](#핵심-설계) · [기술 스택](#기술-스택) · [프로젝트 구조](#프로젝트-구조) · [구성 요소](#구성-요소)

[패킷 흐름](#패킷-흐름) · [안정성](#안정성) · [설정](#설정) · [실행 방법](#실행-방법) · [프로토콜 자동 생성](#프로토콜-자동-생성)

[검증 결과](#검증-결과) · [범위와 한계](#범위와-한계) · [관련 자료](#관련-자료)

## 핵심 설계

**계층과 의존 관계**

> 통신과 세션 관리, 저장소 실행 기반은 ServerCore에 모으고, GameServer에는 패킷 처리와 게임 데이터 관리, 매칭 기능, Repository를 뒀습니다. 의존 방향은 **GameServer → ServerCore**.

**비동기 TCP 통신**

> `SocketAsyncEventArgs` 기반 비동기 송수신과 이벤트 객체 재사용. 길이 헤더로 패킷 경계를 구분하며, 수신 데이터와 아직 전송하지 못한 버퍼 구간을 관리합니다.

**세션 실행 모델과 수명**

> 같은 세션의 요청은 앞선 비동기 작업이 끝난 뒤 실행하도록 했습니다. 연결 ID로 이전 접속의 작업을 구분하며, 세션은 I/O와 작업 처리를 마치고 정리까지 끝나야 풀에 반환됩니다.

**데이터 접근과 상태 반영**

> DB 작업을 사용자 ID에 따라 Worker에 분배하고, **MySQL 저장 완료 → 현재 연결의 메모리 반영** 순서로 처리합니다.

**매칭 상태와 저장소 역할**

> 대기열과 방은 `MatchManager`의 메모리에, 연결별 매칭 티켓은 Redis에 보관합니다. 참가와 취소, 매칭 성립 등 상태 변경은 직렬 처리.

**연결 감시와 서버 종료**

> 단일 Heartbeat 작업자로 연결 감시. 서버 종료 시에는 **세션 → 매칭 → DB → Redis** 순서로 각 작업의 완료를 기다린 뒤 자원을 해제합니다.

실행 경계와 상태 관리 방식, 구현 코드와 검증 근거는 [핵심 설계 상세](docs/CORE_DESIGN.md)에 정리했습니다.

## 기술 스택

- C# / .NET 10
- TCP / `SocketAsyncEventArgs`
- Protocol Buffers
- MySQL 8.4 / MySqlConnector
- Redis 7.4 / StackExchange.Redis
- xUnit
- Docker Compose — 로컬 MySQL·Redis 실행

## 프로젝트 구조

```text
CSharpServer_Portfolio
├─ Server
│  ├─ ServerCore
│  │  ├─ Network
│  │  ├─ Packet
│  │  ├─ DataBase
│  │  └─ Redis
│  ├─ GameServer
│  │  ├─ Config
│  │  ├─ Sessions
│  │  ├─ Packet
│  │  ├─ Protocol
│  │  ├─ Hero
│  │  ├─ Matchmaking
│  │  └─ DataBase
│  ├─ ServerCore.Tests
│  ├─ GameServer.Tests
│  └─ Library/protoc-35.1-win64/bin
├─ ServerSmokeTester
├─ database
├─ docs
├─ compose.yml
└─ CSharpServer.slnx
```

### 서버 전체 구성

서버는 **하나의 GameServer 프로세스** 안에서 동작합니다. **GameServer** 프로젝트가 게임 기능을 담당하고, **ServerCore** 라이브러리의 네트워크·세션·저장소 기반을 사용합니다. 클라이언트와 MySQL·Redis는 프로세스 밖에 표시했습니다.

![서버 구조도](docs/diagrams/overview.svg)

[확대해서 보기](docs/diagrams/overview.svg) · [편집 원본](docs/diagrams/SOURCES.md#overview)

큰 테두리는 프로세스, 색상 영역은 프로젝트, 내부 상자는 기능 묶음입니다. 프로젝트 사이의 화살표는 **참조 관계**, 양방향 선은 **외부 통신**을 나타냅니다.

- `GameSession`은 ServerCore의 `Session`을 상속하며, 로그인 사용자와 연결별 Hero 상태를 보관합니다.
- 매칭 대기·방 상태는 GameServer 메모리에, 사용자·Hero 데이터는 MySQL에, 매칭 티켓은 Redis에 보관합니다.
- `Program`이 설정과 객체 구성을 담당하며, `RedisManager`가 준비한 연결을 `RedisMatchmakingStore`에서 사용합니다.

패킷 처리와 종료 등의 실행 순서는 [주요 코드 호출 흐름](docs/SERVER_ARCHITECTURE.md)에서 별도로 설명합니다.

## 구성 요소

### ServerCore

서버 공통 네트워크와 작업 실행 기반을 담당합니다.

- `Listen`, `Service`: 연결 수락, 서버 시작·종료, 수신·송신 이벤트 객체 풀
- `Session`: TCP 송수신, 패킷 경계 처리, 연결별 비동기 작업 큐와 수명 관리
- `SessionManager`: 활성 세션과 세션 풀, 반환 작업자 관리
- `RecvBuffer`, `SendBuffer`: 수신 데이터 보존과 송신 구간·버퍼 관리
- `SocketHeartBeat`: 로그인 제한 시간과 Ping/Pong 검사
- `MysqlDataBase`, `DbWorkerPool`, `DbWorker`: 사용자별 DB 작업 분배·실행
- `RedisManager`: Redis 연결 초기화·공유·종료

### GameServer

게임 기능과 상태, 데이터 접근 규칙을 담당합니다.

- `GameSession`, `UserSessions`: 로그인 사용자, 연결 ID와 연결별 게임 데이터 관리
- `GSPacketHandler`, `GSPacket`: 패킷 ID별 분배, 세션 큐 등록과 게임 요청 처리
- `HeroManager`: 현재 연결의 게임 데이터 조회와 DB 저장 결과 반영
- `MatchManager`: 매칭 대기열·방, 참가·취소·성립·퇴장 처리
- `DataBaseRepository`, `LoginDB`, `HeroDB`: 게임 데이터의 SQL·조회·갱신
- `RedisMatchmakingStore`: 연결 ID·토큰 기반 매칭 티켓 저장·확인·정리
- `Program`: 설정 로딩, 객체 구성과 서버 실행·종료 순서

### 테스트 프로젝트

- [ServerCore.Tests](Server/ServerCore.Tests): 버퍼·패킷, 세션 큐, 수명·재사용, Heartbeat와 루프백 TCP 검증
- [GameServer.Tests](Server/GameServer.Tests): 게임 데이터 저장·상태 반영, 매칭, 프로토콜과 종료 대기 검증
- [ServerSmokeTester](ServerSmokeTester): 실제 MySQL·Redis를 사용하는 로그인, 데이터 갱신·복구, 매칭 통합 검증

## 패킷 흐름

일반 게임 패킷은 `수신 → 길이·ID 검사 → protobuf 파싱 → 세션 큐 → 게임 핸들러 → 응답 등록` 순서로 처리합니다.

- **로그인:** 계정과 게임 데이터를 DB에서 읽고 현재 연결에 사용자 상태를 등록합니다.
- **데이터 조회·변경:** 조회는 연결별 메모리에서 처리하고, 변경은 DB 커밋 후 메모리에 반영합니다.
- **PvP 매칭:** 참가 요청으로 대기 상태를 등록하고, 별도 매칭 작업자가 상대를 선택해 두 세션에 결과를 알립니다.
- **Heartbeat:** Pong은 수신 경로에서 처리하며 게임 작업 큐를 거치지 않습니다.

일반 게임 핸들러의 `false` 반환과 예외는 공통 처리부에서 해당 연결의 종료로 처리합니다. 상세 호출 순서와 분기는 [주요 코드 호출 흐름](docs/SERVER_ARCHITECTURE.md)에 정리했습니다.

## 안정성

- 패킷 크기·ID 검사와 protobuf 파싱 실패 처리
- 세션별 대기 작업 30개, 대기·진행 중 송신 버퍼 용량 합계 1MiB 제한
- 연결 ID 검사로 이전 연결의 작업과 현재 연결 구분
- I/O·세션 작업·정리 완료 후 세션 재사용
- DB 커밋 후 메모리 반영, 저장 결과가 불명확한 경우 재로그인 시 DB 재조회
- Redis 티켓 토큰 확인, 정리 재시도와 TTL 적용
- 로그인 제한 시간 30초, 로그인 후 Ping 간격 10초·Pong 대기 30초
- 세션·매칭·DB·Redis 순서의 종료 대기

구현과 테스트 근거는 [핵심 설계 상세](docs/CORE_DESIGN.md), 세션 반환 과정의 수정 기록은 [세션 수명 검토](docs/SESSION_LIFETIME_REVIEW.md)에서 확인할 수 있습니다.

## 설정

기본 설정은 [config.json](Server/GameServer/Config/config.json)에 있습니다. 개인 환경에서 덮어쓸 값은 같은 폴더의 `config.local.json`에 두며, Git·공개 ZIP·게시 출력에서 제외합니다.

주요 서버·DB 실행 설정:

```json
{
  "Server": {
    "IpAddress": "127.0.0.1",
    "Port": 7777,
    "MaxConnections": 100
  },
  "Database": {
    "WorkerCount": 4,
    "MaximumPoolSize": 20
  }
}
```

| 대상 | 로컬 주소 / 계정 |
|---|---|
| 게임 서버 | `127.0.0.1:7777` |
| MySQL | `127.0.0.1:3307`, DB `csharp_demo`, 사용자 `demo`, 암호 `demo-local-only` |
| Redis | `127.0.0.1:6380` |
| 데모 로그인 | `demo-a`, `demo-b` — 비밀번호 없는 시드 계정 |

공개 설정의 암호는 로컬 예제용입니다. [compose.yml](compose.yml)이 MySQL 8.4·Redis 7.4를 실행하며, `database/*.sql`은 새 MySQL 볼륨을 처음 만들 때 적용됩니다.

## 실행 방법

필수: **.NET 10 SDK**. 실제 저장소를 사용하는 경우 Docker Desktop 또는 Docker Engine과 Compose가 추가로 필요합니다. Windows Docker Desktop은 WSL 2 엔진이 정상 실행된 상태여야 합니다.

### 1. 저장소 받기

```powershell
git clone https://github.com/GodDongjin/CSharpServer_Portfolio.git
cd CSharpServer_Portfolio
```

이후 명령은 저장소 루트에서 실행합니다.

### 2. 자동 테스트

```powershell
dotnet restore CSharpServer.slnx
dotnet test CSharpServer.slnx -c Release --no-restore
```

서버·MySQL·Redis 실행 없이 검사할 수 있습니다. Windows에서는 `run-tests.bat`, Visual Studio에서는 **테스트 → 테스트 탐색기 → 모두 실행**을 사용해도 됩니다.

### 3. MySQL·Redis 실행

```powershell
docker compose up -d
docker compose logs --tail 30 mysql
docker compose exec redis redis-cli ping
```

최초 MySQL 초기화가 완료되고 최종 서버가 `port: 3306`에서 `ready for connections` 상태인지 확인합니다. Redis 응답은 `PONG`입니다.

### 4. GameServer 실행

```powershell
dotnet run --project Server/GameServer -c Release
```

Visual Studio에서는 GameServer를 시작 프로젝트로 지정합니다.

### 5. 통합 스모크 실행

다른 터미널에서 실행합니다.

```powershell
dotnet run --project ServerSmokeTester -c Release -- --smoke
```

성공 시 다음을 출력합니다.

```text
PASS: login, Hero update/restore, match cancel/pair/disconnect.
```

두 명의 로그인, 데이터 조회·변경·원상복구, 매칭 취소·성립·상대 연결 종료를 검사합니다. 실행 도중 강제 종료하면 데이터 값 복구가 완료되지 않을 수 있습니다. 옵션 없이 실행하면 사용 안내를 출력합니다.

### 6. 정상 종료

게임 서버 터미널에서 **Ctrl+C를 누르고 정상 종료를 기다린 뒤**, 저장소를 중지합니다.

```powershell
docker compose stop
```

서버는 세션과 매칭, DB 작업의 완료를 기다린 뒤 연결 자원을 해제합니다. 대기가 길어지면 10초마다 경고하며 종료 시간의 상한은 보장하지 않습니다. `docker compose stop`은 DB 볼륨을 보존합니다.

## 프로토콜 자동 생성

프로토콜 원본은 기존 구조대로 `Server/Library/protoc-35.1-win64/bin`의 `Enum.proto`, `Struct.proto`, `Protocol.proto` 한 곳에서 관리합니다. 생성된 C#은 `Server/GameServer/Protocol`에 배치합니다. `GenPackets.bat`가 protoc로 C#을 생성하고, 직접 작성한 `GenPacketHandlers.ps1`이 패킷 ID·등록 코드·없는 핸들러의 뼈대를 갱신합니다. 기존 핸들러 구현과 등록 구간 밖의 Heartbeat Pong 처리는 보존합니다.

```powershell
.\Server\Library\protoc-35.1-win64\bin\GenPackets.bat
```

재생성에는 같은 폴더의 `protoc.exe`가 필요합니다. 실행 파일은 로컬 도구로 관리하며 Git·공개 ZIP에서는 제외합니다. 생성된 C# 파일은 저장소에 포함하므로 일반 빌드와 테스트에는 protoc가 필요하지 않습니다. 스모크 클라이언트는 동일한 생성 파일을 링크하여 컴파일합니다.

같은 `bin` 폴더의 `PacketRoutes.json`은 기존 패킷 ID·메시지·핸들러 이름의 대응표입니다. 메시지 순서 변경이나 삭제로 ID가 바뀌지 않으며, 같은 `ACK_MATCH`가 여러 응답 ID에서 사용되는 구조도 유지합니다. 상세 절차는 [프로토콜 생성 안내](Server/GameServer/Protocol/README.md)를 참고하세요.

## 검증 결과

검증 기록: 단기 부하 **2026-10-07**, 1시간 지속 부하 **2026-10-09**, 세션·메모리 **2026-10-12**, 자동 테스트 재확인 **2026-10-21**.

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

자동 테스트는 2026-10-21 파일 정리 후 80개 통과를 재확인한 결과입니다. 세션·메모리 시험은 약 6분간 반복했으며, 정리 후 사용자·매칭 상태·DB 대기 작업이 비워지고 게임 객체의 지속 누적이 관찰되지 않았습니다.

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

## 범위와 한계

- 데모 로그인만 제공하며 계정 생성·실서비스 인증은 포함하지 않습니다.
- TCP 연결이 유지되면 같은 세션을 사용하고, 끊기면 다시 로그인합니다. 재접속 유예나 매칭 방 복원은 제공하지 않습니다.
- 매칭 방은 프로세스 메모리에만 존재합니다. 다중 서버 매칭·전투·보상은 구현 범위에 포함하지 않습니다.
- Hero 캐시는 현재 연결 전용이며 외부 프로세스의 DB 변경을 실시간 동기화하지 않습니다.
- 실제 DB·Redis 장애 주입, 커밋 응답 유실, DB 작업 진행 중 서버 종료는 추가 통합 검증 항목입니다. 저장소 대역을 사용한 실패 테스트와 구분합니다.

## 관련 자료

- [핵심 설계 상세](docs/CORE_DESIGN.md)
- [주요 코드 호출 흐름](docs/SERVER_ARCHITECTURE.md)
- [검토 결과와 남은 검증](docs/PORTFOLIO_REVIEW.md)
- [세션 종료·재사용 수정 기록](docs/SESSION_LIFETIME_REVIEW.md)
- [공개 ZIP 제작·업로드 안내](docs/PUBLISHING.md)
