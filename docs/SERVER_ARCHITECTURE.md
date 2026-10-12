# 서버 구성과 주요 호출 흐름

2026-10-12 현재 코드 기준입니다. [전체 구성도](../README.md#서버-전체-구성)는 클래스의 관계를, 아래 도식은 주요 메서드의 실행 순서를 설명합니다. 실선 흐름은 호출·진행, 점선은 반환 결과나 비동기 후속 처리를 나타냅니다. 실패 경로는 상태와 수명에 영향을 주는 경우를 중심으로 표시했습니다.

**도식 표기:** 파랑은 네트워크, 초록은 게임 로직, 보라는 저장소, 주황은 큐·작업자, 노랑 마름모는 조건, 빨강은 실패 처리입니다. 둥근 도형은 시작·결과, 이중 테두리는 큐·작업 경계, 원통은 DB를 뜻합니다.

시퀀스 다이어그램은 호출 번호와 배경 영역으로 준비·DB 처리·상태 반영 단계를 구분합니다.

## 1. 시작 · 연결 수락 · 세션 할당

```mermaid
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Segoe UI, Malgun Gothic, sans-serif", "fontSize": "15px", "primaryColor": "#e0f2fe", "primaryTextColor": "#0f172a", "primaryBorderColor": "#0369a1", "lineColor": "#64748b", "secondaryColor": "#dcfce7", "tertiaryColor": "#f8fafc", "textColor": "#0f172a", "actorBkg": "#e0f2fe", "actorBorder": "#0369a1", "actorTextColor": "#0f172a", "signalColor": "#475569", "signalTextColor": "#0f172a", "noteBkgColor": "#fef3c7", "noteTextColor": "#0f172a", "noteBorderColor": "#a16207", "labelBoxBkgColor": "#f1f5f9", "labelTextColor": "#0f172a"}, "flowchart": {"curve": "linear", "nodeSpacing": 35, "rankSpacing": 50}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 30}}}%%
sequenceDiagram
    autonumber
    participant P as Program
    participant M as SessionManager
    participant S as Service
    participant L as Listen
    participant G as GameSession (Session 상속)
    rect rgb(240, 249, 255)
    P->>P: DB·Redis 연결 확인 / 게임 서비스·패킷 처리기 구성
    P->>M: Initialize(maxCount, GameSession factory)
    M->>M: 세션 풀 준비 / StartRelease 시작
    P->>S: 생성자 (ReadPool·WritePool 준비)
    P->>S: StartServer()
    S->>L: StartListen() / AcceptAsync()
    S->>S: SocketHeartBeat.RunAsync 시작
    P->>P: MatchManager.Start()
    end
    rect rgb(240, 253, 244)
    Note over L,G: 새 TCP 연결의 Accept 완료
    L->>S: ReadPool.Pop() / WritePool.Pop()
    L->>M: CreateSession(socket, events, owner, handler)
    M->>M: 사용 슬롯 확보 / 세션 풀에서 꺼내기
    M->>G: Initialize()
    M->>M: 활성 세션 사전에 등록
    M-->>L: 초기화된 세션
    L->>G: StartSession()
    G->>G: ReceiveAsync 등록
    end
```

세션은 접속 때마다 새로 생성하는 것이 아니라 초기화 시 준비한 객체를 재사용합니다. 위 세션 생성 흐름은 정상 경로입니다. 이벤트나 슬롯이 부족하면 연결을 거절하고, 생성 이전에 확보한 자원은 `Listen`이 반환합니다. 세션 생성 후의 자원 반환은 세션 종료 경로가 담당합니다. `StartListen()` 호출 중 연결이 먼저 수락될 수도 있어, Heartbeat·매칭 작업자의 시작과 개별 접속 시점 사이에 엄격한 선후 관계를 가정하지 않습니다.

코드: [Program](../Server/GameServer/Program.cs), [Listen](../Server/ServerCore/Network/Listen.cs), [SessionManager](../Server/ServerCore/Network/SessionManager.cs)

## 2. 패킷 수신 · 파싱 · 게임 로직 · 응답

```mermaid
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Segoe UI, Malgun Gothic, sans-serif", "fontSize": "15px", "primaryColor": "#e0f2fe", "primaryTextColor": "#0f172a", "primaryBorderColor": "#0369a1", "lineColor": "#64748b", "secondaryColor": "#dcfce7", "tertiaryColor": "#f8fafc", "textColor": "#0f172a", "actorBkg": "#e0f2fe", "actorBorder": "#0369a1", "actorTextColor": "#0f172a", "signalColor": "#475569", "signalTextColor": "#0f172a", "noteBkgColor": "#fef3c7", "noteTextColor": "#0f172a", "noteBorderColor": "#a16207", "labelBoxBkgColor": "#f1f5f9", "labelTextColor": "#0f172a"}, "flowchart": {"curve": "linear", "nodeSpacing": 35, "rankSpacing": 50}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 30}}}%%
flowchart TD
    R["Session.ProcessReceive<br/>수신 버퍼에 누적"] --> F["OnRecv<br/>헤더와 길이 확인 · 완성된 패킷 추출"]
    F --> H["OnRecvPack → GSPacketHandler.HandlePacket<br/>패킷 ID별 delegate 호출"]
    H --> P["Protobuf 파싱"]
    P --> T{"일반 요청 / Pong"}
    T -->|일반 요청| Q[["Session.EnqueueAsyncJob<br/>연결 ID 확인 후 큐 등록"]]
    Q --> W[["ProcessRecvQueueAsync<br/>등록된 작업을 하나씩 await"]]
    W --> A["GSPacketHandler.HandleAsync<br/>현재 연결 확인"]
    A --> G["GSPacket.HandleReq...<br/>게임 로직 실행"]
    G --> O{"처리 결과"}
    O -->|정상 또는 게임 오류 응답| S(["Session.SendPacket → Send<br/>직렬화 · 송신 버퍼 등록"])
    S --> IO["SendAsync / ProcessSend<br/>부분 송신이면 남은 구간 전송"]
    O -->|false 또는 처리 예외| D["Disconnect(요청 당시 연결 ID)"]
    T -->|Heartbeat Pong| HB["ReceiveHeartbeatPong<br/>연결 ID · sequence · 제한 시간 확인"]
    classDef network fill:#e0f2fe,stroke:#0369a1,color:#0f172a,stroke-width:1.5px
    classDef game fill:#dcfce7,stroke:#15803d,color:#0f172a,stroke-width:1.5px
    classDef storage fill:#ede9fe,stroke:#7c3aed,color:#0f172a,stroke-width:1.5px
    classDef queue fill:#fff7ed,stroke:#c2410c,color:#0f172a,stroke-width:1.5px
    classDef decision fill:#fef3c7,stroke:#a16207,color:#0f172a,stroke-width:1.5px
    classDef error fill:#fee2e2,stroke:#b91c1c,color:#0f172a,stroke-width:1.5px
    classDef terminal fill:#f1f5f9,stroke:#475569,color:#0f172a,stroke-width:1.5px
    class R,F,H,P,IO,HB network
    class Q,W queue
    class A,G game
    class T,O decision
    class D error
    class S terminal
```

위 응답 경로는 응답을 보내는 일반 요청의 대표 흐름입니다. `true`는 오류 ACK를 포함한 처리 완료 또는 추가 처리 불필요를 뜻하며, 항상 송신했다는 뜻은 아닙니다. 예를 들어 이미 종료된 연결은 응답 없이 끝날 수 있습니다. 일반 핸들러의 파싱 실패·큐 등록 실패는 `false`로 수신 경로에 전달되어 연결 종료로 이어집니다. Pong은 게임 작업 큐에 넣지 않아 DB 작업 뒤에서 대기하지 않습니다.

코드: [Session](../Server/ServerCore/Network/Session.cs), [GSPacketHandler](../Server/GameServer/Packet/GSPacketHandler.cs), [GSPacket](../Server/GameServer/Packet/GSPacket.cs)

## 3. 로그인과 Hero 최초 로드

```mermaid
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Segoe UI, Malgun Gothic, sans-serif", "fontSize": "15px", "primaryColor": "#e0f2fe", "primaryTextColor": "#0f172a", "primaryBorderColor": "#0369a1", "lineColor": "#64748b", "secondaryColor": "#dcfce7", "tertiaryColor": "#f8fafc", "textColor": "#0f172a", "actorBkg": "#e0f2fe", "actorBorder": "#0369a1", "actorTextColor": "#0f172a", "signalColor": "#475569", "signalTextColor": "#0f172a", "noteBkgColor": "#fef3c7", "noteTextColor": "#0f172a", "noteBorderColor": "#a16207", "labelBoxBkgColor": "#f1f5f9", "labelTextColor": "#0f172a"}, "flowchart": {"curve": "linear", "nodeSpacing": 35, "rankSpacing": 50}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 30}}}%%
flowchart TD
    P[["GSPacket.HandleReqLogin"]] --> Input{"데모 로그인 입력이 유효한가?"}
    Input -->|아니오| Error[["현재 연결이면 실패 ACK"]]
    Input -->|예| User[["LoginDB.FindAsync<br/>DB Worker를 통해 사용자 조회"]]
    User --> Exists{"사용자가 있는가?"}
    Exists -->|아니오| Error
    Exists -->|예| Hero[["HeroDB.LoadAsync<br/>DB Worker를 통해 Hero 목록 조회"]]
    Hero --> Current{"현재 연결인가?"}
    Current -->|아니오| Skip(["상태 반영 · 응답 생략"])
    Current -->|예| Login[["GameSession.Login<br/>UserSessions.TryLogin으로 중복 등록 방지"]]
    Login --> Success{"로그인 등록 성공?"}
    Success -->|아니오| Error
    Success -->|예| Load[["Heroes.Load → MarkLoginCompleted"]]
    Load --> Ack(["현재 연결 재확인 → 성공 ACK"])
    classDef network fill:#e0f2fe,stroke:#0369a1,color:#0f172a,stroke-width:1.5px
    classDef game fill:#dcfce7,stroke:#15803d,color:#0f172a,stroke-width:1.5px
    classDef storage fill:#ede9fe,stroke:#7c3aed,color:#0f172a,stroke-width:1.5px
    classDef queue fill:#fff7ed,stroke:#c2410c,color:#0f172a,stroke-width:1.5px
    classDef decision fill:#fef3c7,stroke:#a16207,color:#0f172a,stroke-width:1.5px
    classDef error fill:#fee2e2,stroke:#b91c1c,color:#0f172a,stroke-width:1.5px
    classDef terminal fill:#f1f5f9,stroke:#475569,color:#0f172a,stroke-width:1.5px
    class P,Login,Load game
    class User,Hero storage
    class Input,Exists,Current,Success decision
    class Error error
    class Skip,Ack terminal
```


일반적인 입력·조회·로그인 등록 분기를 표시했습니다. 로그인 데이터 오류는 연결 종료로, 일반 조회 예외는 로그인 실패 응답으로 이어집니다. 사용자가 없으면 Hero 조회는 하지 않습니다. 이후 Hero 목록 요청은 로그인 시 읽은 메모리 상태를 조회하며 매번 MySQL에 접근하지 않습니다.

코드: [GSPacket.HandleReqLogin](../Server/GameServer/Packet/GSPacket.cs), [LoginDB](../Server/GameServer/DataBase/DBRepository/LoginDB.cs), [HeroDB](../Server/GameServer/DataBase/DBRepository/HeroDB.cs)

## 4. Hero 갱신의 DB·메모리 반영 순서

```mermaid
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Segoe UI, Malgun Gothic, sans-serif", "fontSize": "15px", "primaryColor": "#e0f2fe", "primaryTextColor": "#0f172a", "primaryBorderColor": "#0369a1", "lineColor": "#64748b", "secondaryColor": "#dcfce7", "tertiaryColor": "#f8fafc", "textColor": "#0f172a", "actorBkg": "#e0f2fe", "actorBorder": "#0369a1", "actorTextColor": "#0f172a", "signalColor": "#475569", "signalTextColor": "#0f172a", "noteBkgColor": "#fef3c7", "noteTextColor": "#0f172a", "noteBorderColor": "#a16207", "labelBoxBkgColor": "#f1f5f9", "labelTextColor": "#0f172a"}, "flowchart": {"curve": "linear", "nodeSpacing": 35, "rankSpacing": 50}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 30}}}%%
sequenceDiagram
    autonumber
    participant P as GSPacket
    participant H as HeroManager
    participant R as HeroDB
    participant W as DB Worker / DbJob
    participant DB as MySQL
    rect rgb(240, 253, 244)
    P->>H: UpdateAsync(..., isCurrent)
    H->>H: 필드 허용 여부 · Hero 소유권 확인
    H->>R: UpdateAsync(userId, heroId, field, value)
    end
    rect rgb(245, 243, 255)
    R->>W: MysqlDataBase.ExecuteAsync로 작업 등록
    W->>W: MySqlDataSource에서 연결 확보
    W->>DB: HeroDB의 작업 delegate 실행 / BeginTransaction
    W->>DB: UPDATE → SELECT
    W->>DB: CommitAsync
    DB-->>W: 커밋 완료
    W->>W: DB 연결 Dispose 후 작업 Completion 완료
    W-->>R: 저장된 DBHero
    R-->>H: 결과 반환
    end
    rect rgb(240, 253, 244)
    H->>H: 현재 연결 재확인 / 결과 ID·소유자 검증
    H->>H: 메모리 Hero 스냅샷 교체
    H-->>P: ACK_HERO_UPDATE
    P->>P: 현재 연결 재확인 / SendPacket
    end
```

이는 유효한 요청의 저장 성공 경로입니다. 입력·소유권 검사 실패는 저장 전에 오류 결과를 반환합니다. DB가 Hero를 찾지 못하면 메모리를 갱신하지 않습니다. 커밋 후 연결이 끊겼다면 DB 결과는 남지만 이전 연결의 메모리·응답에는 반영하지 않습니다. 저장 예외는 `HandleReqHeroUpdate`가 `false`로 반환하여 공통 처리부에서 해당 연결을 종료합니다. 저장 결과가 불명확할 때는 재로그인 조회로 확정 상태를 읽습니다.

코드: [HeroManager.UpdateAsync](../Server/GameServer/Hero/HeroManager.cs), [HeroDB.UpdateAsync](../Server/GameServer/DataBase/DBRepository/HeroDB.cs), [DbJob.ExecuteAsync](../Server/ServerCore/DataBase/DataBaseJob.cs)

## 5. 매칭 참가 · 성립 · 연결 종료

```mermaid
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Segoe UI, Malgun Gothic, sans-serif", "fontSize": "15px", "primaryColor": "#e0f2fe", "primaryTextColor": "#0f172a", "primaryBorderColor": "#0369a1", "lineColor": "#64748b", "secondaryColor": "#dcfce7", "tertiaryColor": "#f8fafc", "textColor": "#0f172a", "actorBkg": "#e0f2fe", "actorBorder": "#0369a1", "actorTextColor": "#0f172a", "signalColor": "#475569", "signalTextColor": "#0f172a", "noteBkgColor": "#fef3c7", "noteTextColor": "#0f172a", "noteBorderColor": "#a16207", "labelBoxBkgColor": "#f1f5f9", "labelTextColor": "#0f172a"}, "flowchart": {"curve": "linear", "nodeSpacing": 35, "rankSpacing": 50}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 30}}}%%
flowchart TD
    J(["GSPacket.HandleReqPvpMatchPending"]) --> M(["MatchManager.JoinAsync<br/>gate 획득 · 연결/중복 검사"])
    M --> R(["RedisMatchmakingStore.AddAsync<br/>티켓 SET NX · TTL 120초"])
    R --> P(["연결 재확인 → 메모리 pending 등록<br/>Pending ACK 반환"])
    P -.-> T(["별도 매칭 작업자: TickAsync<br/>정리 요청·만료 처리 후 후보 선택"])
    T --> C(["RedisMatchmakingStore.ClaimAsync<br/>Lua로 두 티켓 값 확인 후 삭제"])
    C --> OK{"티켓 소비 성공<br/>두 연결 모두 유효?"}
    OK -->|예| Room(["메모리 pending 제거<br/>Room · 사용자별 방 인덱스 등록"])
    Room --> N(["GameSession.Notify(connectionId, reply)<br/>세션 작업 큐에 알림 등록"])
    N --> S(["현재 연결 확인 → SendPacket"])
    OK -->|아니오| Fail(["FailPair<br/>대기 해제 · 티켓 정리 예약 · 종료 알림"])
    D(["GameSession.OnDisconnected"]) --> Schedule(["ScheduleDisconnect<br/>사용자 ID · 연결 ID만 큐에 기록"])
    Schedule -.-> Next(["다음 TickAsync → DisconnectAsync<br/>같은 연결의 대기·방 정리"])
    Next --> Cleanup(["FlushCleanupAsync<br/>토큰 일치 티켓 삭제 · 실패 시 재시도"])
    Next --> Notify(["상대 연결이 유효하면 종료 알림"])
    classDef network fill:#e0f2fe,stroke:#0369a1,color:#0f172a,stroke-width:1.5px
    classDef game fill:#dcfce7,stroke:#15803d,color:#0f172a,stroke-width:1.5px
    classDef storage fill:#ede9fe,stroke:#7c3aed,color:#0f172a,stroke-width:1.5px
    classDef queue fill:#fff7ed,stroke:#c2410c,color:#0f172a,stroke-width:1.5px
    classDef decision fill:#fef3c7,stroke:#a16207,color:#0f172a,stroke-width:1.5px
    classDef error fill:#fee2e2,stroke:#b91c1c,color:#0f172a,stroke-width:1.5px
    classDef terminal fill:#f1f5f9,stroke:#475569,color:#0f172a,stroke-width:1.5px
    class J,M,P,Room,N,S,D,Notify game
    class R,C,Cleanup storage
    class T,Schedule,Next queue
    class OK decision
    class Fail error
```

`JoinAsync`는 대기 등록을 수행하고 실제 짝짓기는 별도 `TickAsync`에서 합니다. 매칭 작업자는 Tick 완료 후 100ms 대기하므로 정확히 100ms마다 완료되는 타이머가 아닙니다. 참가 실패 경로는 중복/연결 오류 또는 저장소 오류 결과를 반환하며, Redis 등록 결과가 불명확하면 티켓을 정리 대상으로 남깁니다.

참가·취소·퇴장·매칭·정리는 `_gate`로 직렬화합니다. `ScheduleDisconnect`는 정리 예약이며 Redis 작업 완료를 기다리지 않습니다. 세션 반환은 이 매칭 정리 완료에 종속되지 않고, 이전 연결 ID·토큰 검사가 뒤늦은 정리를 보호합니다. 게임 상태는 메모리, 티켓은 Redis에 있다는 책임 분리가 중요합니다.

코드: [MatchManager](../Server/GameServer/Matchmaking/MatchManager.cs), [RedisMatchmakingStore](../Server/GameServer/Matchmaking/RedisMatchmakingStore.cs), [GameSession](../Server/GameServer/Sessions/GameSession.cs)

## 6. 연결 종료 · 세션 반환 · 재사용

```mermaid
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Segoe UI, Malgun Gothic, sans-serif", "fontSize": "15px", "primaryColor": "#e0f2fe", "primaryTextColor": "#0f172a", "primaryBorderColor": "#0369a1", "lineColor": "#64748b", "secondaryColor": "#dcfce7", "tertiaryColor": "#f8fafc", "textColor": "#0f172a", "actorBkg": "#e0f2fe", "actorBorder": "#0369a1", "actorTextColor": "#0f172a", "signalColor": "#475569", "signalTextColor": "#0f172a", "noteBkgColor": "#fef3c7", "noteTextColor": "#0f172a", "noteBorderColor": "#a16207", "labelBoxBkgColor": "#f1f5f9", "labelTextColor": "#0f172a"}, "flowchart": {"curve": "linear", "nodeSpacing": 35, "rankSpacing": 50}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 30}}}%%
flowchart TD
    D["Session.Disconnect(connectionId)<br/>현재 연결 확인 · 종료 표시 · 소켓 닫기"]
    D --> Hook["GameSession.OnDisconnected<br/>매칭 정리 요청 예약"]
    Hook --> Check{"TryRelease<br/>종료 처리 중 아님 + 수신/송신/작업 pending 모두 0?"}
    Check -->|아니오| Pending[["남은 I/O·작업 처리"]]
    Pending -->|완료 경로에서 재확인| Check
    Check -->|예 · 중복 반환 방지| Register[["SessionManager.ReleaseRegister<br/>반환 Channel에 Session 등록"]]
    Register --> Worker[["StartRelease의 단일 소비자"]]
    Worker --> Reset["Session.Reset → GameSession.OnReset<br/>유저 등록 해제 · Hero/세션 상태 초기화"]
    Reset --> Event["Session.RetrunEventArgs<br/>Service 이벤트 풀 반환 · 참조 정리"]
    Event --> Pool[["활성 사전 제거 → 세션 풀에 등록<br/>사용 슬롯 반환"]]
    Pool --> Next(["다음 연결의 CreateSession에서 재사용"])
    classDef network fill:#e0f2fe,stroke:#0369a1,color:#0f172a,stroke-width:1.5px
    classDef game fill:#dcfce7,stroke:#15803d,color:#0f172a,stroke-width:1.5px
    classDef storage fill:#ede9fe,stroke:#7c3aed,color:#0f172a,stroke-width:1.5px
    classDef queue fill:#fff7ed,stroke:#c2410c,color:#0f172a,stroke-width:1.5px
    classDef decision fill:#fef3c7,stroke:#a16207,color:#0f172a,stroke-width:1.5px
    classDef error fill:#fee2e2,stroke:#b91c1c,color:#0f172a,stroke-width:1.5px
    classDef terminal fill:#f1f5f9,stroke:#475569,color:#0f172a,stroke-width:1.5px
    class D,Event network
    class Hook,Reset game
    class Pending,Register,Worker,Pool queue
    class Check decision
    class Next terminal
```

`TryRelease`는 별도 감시 루프가 아니라 종료·I/O·큐 작업 완료 경로에서 호출됩니다. 세션을 반환 Channel에 넣는 시점과 풀에 다시 공개하는 시점은 다릅니다. 이벤트와 참조 정리가 끝나기 전에는 새 연결에 재사용하지 않습니다. 코드의 `RetrunEventArgs` 이름은 현재 철자를 그대로 표기했습니다.

코드: [Session.Disconnect / TryRelease / Reset](../Server/ServerCore/Network/Session.cs), [SessionManager.StartRelease](../Server/ServerCore/Network/SessionManager.cs)

## 7. 서버 종료

```mermaid
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Segoe UI, Malgun Gothic, sans-serif", "fontSize": "15px", "primaryColor": "#e0f2fe", "primaryTextColor": "#0f172a", "primaryBorderColor": "#0369a1", "lineColor": "#64748b", "secondaryColor": "#dcfce7", "tertiaryColor": "#f8fafc", "textColor": "#0f172a", "actorBkg": "#e0f2fe", "actorBorder": "#0369a1", "actorTextColor": "#0f172a", "signalColor": "#475569", "signalTextColor": "#0f172a", "noteBkgColor": "#fef3c7", "noteTextColor": "#0f172a", "noteBorderColor": "#a16207", "labelBoxBkgColor": "#f1f5f9", "labelTextColor": "#0f172a"}, "flowchart": {"curve": "linear", "nodeSpacing": 35, "rankSpacing": 50}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 30}}}%%
flowchart TD
    Stop(["Ctrl+C → Program의 종료 요청 완료"]) --> Service["Service.StopServerAsync<br/>반복 호출은 같은 Task 반환"]
    Service --> Listen["Listen.StopAsyncListen<br/>새 연결 수락 중단"]
    Listen --> HB["Heartbeat 취소 후 종료 대기"]
    HB --> All[["SessionManager.AllReleaseSessionAsync<br/>활성 연결에 Disconnect 요청"]]
    All --> Released[["모든 세션의 실제 반환 완료 대기"]]
    Released --> Channel[["반환 Channel 완료 → 반환 작업자 종료 대기"]]
    Channel --> Pools["Service의 이벤트 풀 Dispose"]
    Pools --> Match["MatchManager.DisposeAsync<br/>작업자 종료 · 대기/방/티켓 정리"]
    Match --> DB["MysqlDataBase.DisposeAsync<br/>DB Worker 작업 배출·종료 → DataSource 해제"]
    DB --> Redis["RedisManager.ShutdownAsync"]
    Redis --> Done(["서버 정상 종료 완료"])
    classDef network fill:#e0f2fe,stroke:#0369a1,color:#0f172a,stroke-width:1.5px
    classDef game fill:#dcfce7,stroke:#15803d,color:#0f172a,stroke-width:1.5px
    classDef storage fill:#ede9fe,stroke:#7c3aed,color:#0f172a,stroke-width:1.5px
    classDef queue fill:#fff7ed,stroke:#c2410c,color:#0f172a,stroke-width:1.5px
    classDef decision fill:#fef3c7,stroke:#a16207,color:#0f172a,stroke-width:1.5px
    classDef error fill:#fee2e2,stroke:#b91c1c,color:#0f172a,stroke-width:1.5px
    classDef terminal fill:#f1f5f9,stroke:#475569,color:#0f172a,stroke-width:1.5px
    class Service,Listen,HB,Pools network
    class Match game
    class DB,Redis storage
    class All,Released,Channel queue
    class Stop,Done terminal
```

`Program.WaitForShutdownAsync`는 각 단계가 10초 이상 지속되면 경고를 반복하며 같은 작업을 계속 기다립니다. 강제 종료 시간 제한은 아닙니다. 실제 종료 오류가 발생하면 기록하고 예외를 전달하며, 이후 단계를 정상 완료한 것으로 처리하지 않습니다. Redis 티켓 삭제 실패는 매칭 정리의 재시도/TTL 정책을 따릅니다.

코드: [Program](../Server/GameServer/Program.cs), [Service.StopServerCoreAsync](../Server/ServerCore/Network/Service.cs), [SessionManager](../Server/ServerCore/Network/SessionManager.cs), [MysqlDataBase](../Server/ServerCore/DataBase/DataBase.cs)

## 프로토콜 생성은 실행 흐름과 별개

`bin/*.proto + PacketRoutes.json → GenPackets.bat / GenPacketHandlers.ps1 → GameServer/Protocol/*.cs 및 패킷 등록 코드` 순서로 생성합니다. 이는 개발 단계의 코드 생성 과정이며 서버 실행 중 자동으로 도는 작업이 아닙니다. [생성 안내](../Server/GameServer/Protocol/README.md)를 참고하세요.
