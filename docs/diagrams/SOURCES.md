# 도식 편집 원본

본문은 아래 Mermaid 원본을 Mermaid 11.4.1과 맑은 고딕으로 렌더링한 SVG를 사용합니다. 도식을 수정하면 SVG도 함께 다시 생성하고 글자와 여백을 확인합니다. 코드 블록은 편집용이며 본문 표시에는 영향을 주지 않습니다.

<a id="overview"></a>

## 서버 전체 구성

```text
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Malgun Gothic", "fontSize": "16px", "primaryColor": "#fafafa", "primaryTextColor": "#30363d", "primaryBorderColor": "#8b949e", "lineColor": "#8b949e", "secondaryColor": "#f2f3f4", "tertiaryColor": "#fafafa", "textColor": "#30363d", "actorBkg": "#f6f7f8", "actorBorder": "#8b949e", "actorTextColor": "#30363d", "signalColor": "#8b949e", "signalTextColor": "#30363d", "noteBkgColor": "#f6f7f8", "noteTextColor": "#30363d", "noteBorderColor": "#8b949e", "labelBoxBkgColor": "#f6f7f8", "labelTextColor": "#30363d", "clusterBkg": "#f6f7f8", "clusterBorder": "#c5cbd1", "edgeLabelBackground": "#fafafa", "activationBkgColor": "#e6e8eb", "activationBorderColor": "#8b949e", "sequenceNumberColor": "#ffffff"}, "flowchart": {"curve": "linear", "nodeSpacing": 32, "rankSpacing": 42, "htmlLabels": false, "padding": 18, "wrappingWidth": 400}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 32, "wrap": false, "diagramMarginX": 32, "diagramMarginY": 24, "actorFontSize": 16, "noteFontSize": 16, "messageFontSize": 16}, "htmlLabels": false}}%%
flowchart TB
    Client(["클라이언트 / ServerSmokeTester"])
    subgraph Core["ServerCore"]
        Service["Service · Listen<br/>연결 수락 · 이벤트 풀 · 서버 종료"]
        Manager[["SessionManager<br/>세션 풀 · 활성 세션 · 반환 Channel"]]
        Session["Session<br/>수신 · 패킷 조립 · 작업 큐 · 송신"]
        Heartbeat["SocketHeartBeat<br/>로그인 제한 · Ping/Pong 감시"]
        DB[["MysqlDataBase<br/>DbWorkerPool → DbWorker<br/>DB 작업 큐 · 연결 확보 · 완료 통지"]]
    end
    subgraph Game["GameServer"]
        GameSession["GameSession : Session<br/>로그인 사용자 · HeroManager"]
        Handler["GSPacketHandler<br/>패킷 ID 확인 · 파싱 · 작업 등록"]
        Packet["GSPacket<br/>로그인 · Hero · 매칭 요청 처리"]
        Hero["HeroManager<br/>연결별 Hero 상태 · 저장 후 반영"]
        Repo["DataBaseRepository<br/>LoginDB · HeroDB"]
        Match["MatchManager<br/>매칭 대기 · 방 · 종료 정리"]
        Store["RedisMatchmakingStore<br/>티켓 등록 · Lua 확인/삭제"]
    end
    MySQL[("MySQL<br/>사용자 · Hero")]
    Redis[("Redis<br/>매칭 티켓")]
    Client -->|접속| Service
    Service -->|세션 할당 요청| Manager
    Manager -->|초기화 · 반환 후 재사용| GameSession
    GameSession -.->|상속| Session
    Client <-->|TCP| Session
    Heartbeat -->|활성 연결 스냅샷| Manager
    Heartbeat -->|연결별 검사| Session
    Session -->|완성된 패킷 전달| Handler
    Handler -->|일반 요청<br/>작업 큐 등록| Session
    Session -->|큐에서 게임<br/>핸들러 실행| Packet
    Packet --> Hero
    Packet -->|로그인 조회| Repo
    Hero -->|DB 저장 요청| Repo
    Repo --> DB
    DB --> MySQL
    Packet --> Match
    Match --> Store
    Store --> Redis
    classDef network fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef game fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef storage fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef queue fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef decision fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef error fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef terminal fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    class Service,Session,Heartbeat network
    class GameSession,Handler,Packet,Hero,Match game
    class Repo,Store,MySQL,Redis storage
    class Manager,DB queue
    class Client terminal
    style Core fill:#f6f7f8,stroke:#c5cbd1,stroke-width:1px,color:#30363d
    style Game fill:#f6f7f8,stroke:#c5cbd1,stroke-width:1px,color:#30363d
```

<a id="connection"></a>

## 시작과 연결 수락

```text
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Malgun Gothic", "fontSize": "16px", "primaryColor": "#fafafa", "primaryTextColor": "#30363d", "primaryBorderColor": "#8b949e", "lineColor": "#8b949e", "secondaryColor": "#f2f3f4", "tertiaryColor": "#fafafa", "textColor": "#30363d", "actorBkg": "#f6f7f8", "actorBorder": "#8b949e", "actorTextColor": "#30363d", "signalColor": "#8b949e", "signalTextColor": "#30363d", "noteBkgColor": "#f6f7f8", "noteTextColor": "#30363d", "noteBorderColor": "#8b949e", "labelBoxBkgColor": "#f6f7f8", "labelTextColor": "#30363d", "clusterBkg": "#f6f7f8", "clusterBorder": "#c5cbd1", "edgeLabelBackground": "#fafafa", "activationBkgColor": "#e6e8eb", "activationBorderColor": "#8b949e", "sequenceNumberColor": "#ffffff"}, "flowchart": {"curve": "linear", "nodeSpacing": 32, "rankSpacing": 42, "htmlLabels": false, "padding": 18, "wrappingWidth": 400}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 32, "wrap": false, "diagramMarginX": 32, "diagramMarginY": 24, "actorFontSize": 16, "noteFontSize": 16, "messageFontSize": 16}, "htmlLabels": false}}%%
sequenceDiagram
    autonumber
    participant P as Program
    participant M as SessionManager
    participant S as Service
    participant L as Listen
    participant G as GameSession<br/>(Session 상속)
    rect rgb(246, 247, 248)
    P->>P: DB·Redis 연결 확인<br/>게임 서비스·패킷 처리기 구성
    P->>M: Initialize(maxCount,<br/>GameSession factory)
    M->>M: 세션 풀 준비 / StartRelease 시작
    P->>S: 생성자<br/>ReadPool·WritePool 준비
    P->>S: StartServer()
    S->>L: StartListen() / AcceptAsync()
    S->>S: SocketHeartBeat.RunAsync 시작
    P->>P: MatchManager.Start()
    end
    rect rgb(246, 247, 248)
    Note over L,G: 새 TCP 연결의 Accept 완료
    L->>S: ReadPool.Pop() / WritePool.Pop()
    L->>M: CreateSession(socket, events,<br/>owner, handler)
    M->>M: 사용 슬롯 확보<br/>세션 풀에서 꺼내기
    M->>G: Initialize()
    M->>M: 활성 세션 사전에 등록
    M-->>L: 초기화된 세션
    L->>G: StartSession()
    G->>G: ReceiveAsync 등록
    end
```

<a id="packet"></a>

## 패킷 처리와 응답

```text
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Malgun Gothic", "fontSize": "16px", "primaryColor": "#fafafa", "primaryTextColor": "#30363d", "primaryBorderColor": "#8b949e", "lineColor": "#8b949e", "secondaryColor": "#f2f3f4", "tertiaryColor": "#fafafa", "textColor": "#30363d", "actorBkg": "#f6f7f8", "actorBorder": "#8b949e", "actorTextColor": "#30363d", "signalColor": "#8b949e", "signalTextColor": "#30363d", "noteBkgColor": "#f6f7f8", "noteTextColor": "#30363d", "noteBorderColor": "#8b949e", "labelBoxBkgColor": "#f6f7f8", "labelTextColor": "#30363d", "clusterBkg": "#f6f7f8", "clusterBorder": "#c5cbd1", "edgeLabelBackground": "#fafafa", "activationBkgColor": "#e6e8eb", "activationBorderColor": "#8b949e", "sequenceNumberColor": "#ffffff"}, "flowchart": {"curve": "linear", "nodeSpacing": 32, "rankSpacing": 42, "htmlLabels": false, "padding": 18, "wrappingWidth": 400}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 32, "wrap": false, "diagramMarginX": 32, "diagramMarginY": 24, "actorFontSize": 16, "noteFontSize": 16, "messageFontSize": 16}, "htmlLabels": false}}%%
flowchart TD
    R["Session.ProcessReceive<br/>수신 버퍼에 누적"] --> F["OnRecv<br/>헤더와 길이 확인 · 완성된 패킷 추출"]
    F --> H["OnRecvPack<br/>GSPacketHandler.HandlePacket<br/>패킷 ID별 delegate 호출"]
    H --> P["Protobuf 파싱"]
    P --> T{"일반 요청 / Pong"}
    T -->|일반 요청| Q[["Session.EnqueueAsyncJob<br/>연결 ID 확인 후 큐 등록"]]
    Q --> W[["ProcessRecvQueueAsync<br/>등록된 작업을 하나씩 await"]]
    W --> A["GSPacketHandler.HandleAsync<br/>현재 연결 확인"]
    A --> G["GSPacket.HandleReq...<br/>게임 로직 실행"]
    G -->|핸들러 내부 · 응답이 필요한 경우| S(["Session.SendPacket → Send<br/>직렬화 · 송신 버퍼 등록"])
    G -->|반환 또는 예외| O{"HandleAsync · 결과 확인"}
    S --> IO["SendAsync / ProcessSend<br/>부분 송신이면 남은 구간 전송"]
    O -->|false 또는 처리 예외| D["Disconnect(요청 당시 연결 ID)"]
    O -->|true| End(["처리 완료 · 추가 종료 없음"])
    T -->|Heartbeat Pong| HB["ReceiveHeartbeatPong<br/>연결 ID · sequence · 제한 시간 확인"]
    classDef network fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef game fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef storage fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef queue fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef decision fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef error fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef terminal fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    class R,F,H,P,IO,HB network
    class Q,W queue
    class A,G game
    class T,O decision
    class D error
    class S,End terminal
```

<a id="login"></a>

## 로그인과 Hero 로드

```text
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Malgun Gothic", "fontSize": "16px", "primaryColor": "#fafafa", "primaryTextColor": "#30363d", "primaryBorderColor": "#8b949e", "lineColor": "#8b949e", "secondaryColor": "#f2f3f4", "tertiaryColor": "#fafafa", "textColor": "#30363d", "actorBkg": "#f6f7f8", "actorBorder": "#8b949e", "actorTextColor": "#30363d", "signalColor": "#8b949e", "signalTextColor": "#30363d", "noteBkgColor": "#f6f7f8", "noteTextColor": "#30363d", "noteBorderColor": "#8b949e", "labelBoxBkgColor": "#f6f7f8", "labelTextColor": "#30363d", "clusterBkg": "#f6f7f8", "clusterBorder": "#c5cbd1", "edgeLabelBackground": "#fafafa", "activationBkgColor": "#e6e8eb", "activationBorderColor": "#8b949e", "sequenceNumberColor": "#ffffff"}, "flowchart": {"curve": "linear", "nodeSpacing": 32, "rankSpacing": 42, "htmlLabels": false, "padding": 18, "wrappingWidth": 400}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 32, "wrap": false, "diagramMarginX": 32, "diagramMarginY": 24, "actorFontSize": 16, "noteFontSize": 16, "messageFontSize": 16}, "htmlLabels": false}}%%
flowchart TD
    P["GSPacket.HandleReqLogin"] --> Input{"데모 로그인 입력이 유효한가?"}
    Input -->|아니오| Error["현재 연결이면 실패 ACK"]
    Input -->|예| User["LoginDB.FindAsync<br/>DB Worker를 통해 사용자 조회"]
    User --> Exists{"사용자가 있는가?"}
    Exists -->|아니오| Error
    Exists -->|예| Hero["HeroDB.LoadAsync<br/>DB Worker를 통해 Hero 목록 조회"]
    Hero --> Current{"현재 연결인가?"}
    Current -->|아니오| Skip(["상태 반영 · 응답 생략"])
    Current -->|예| Login["GameSession.Login<br/>UserSessions.TryLogin으로 중복 등록 방지"]
    Login --> Success{"로그인 등록 성공?"}
    Success -->|아니오| Error
    Success -->|예| Load["Heroes.Load → MarkLoginCompleted"]
    Load --> Ack(["현재 연결 재확인 → 성공 ACK"])
    classDef network fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef game fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef storage fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef queue fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef decision fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef error fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef terminal fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    class P,Login,Load game
    class User,Hero storage
    class Input,Exists,Current,Success decision
    class Error error
    class Skip,Ack terminal
```

<a id="hero"></a>

## Hero 저장과 메모리 반영

```text
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Malgun Gothic", "fontSize": "16px", "primaryColor": "#fafafa", "primaryTextColor": "#30363d", "primaryBorderColor": "#8b949e", "lineColor": "#8b949e", "secondaryColor": "#f2f3f4", "tertiaryColor": "#fafafa", "textColor": "#30363d", "actorBkg": "#f6f7f8", "actorBorder": "#8b949e", "actorTextColor": "#30363d", "signalColor": "#8b949e", "signalTextColor": "#30363d", "noteBkgColor": "#f6f7f8", "noteTextColor": "#30363d", "noteBorderColor": "#8b949e", "labelBoxBkgColor": "#f6f7f8", "labelTextColor": "#30363d", "clusterBkg": "#f6f7f8", "clusterBorder": "#c5cbd1", "edgeLabelBackground": "#fafafa", "activationBkgColor": "#e6e8eb", "activationBorderColor": "#8b949e", "sequenceNumberColor": "#ffffff"}, "flowchart": {"curve": "linear", "nodeSpacing": 32, "rankSpacing": 42, "htmlLabels": false, "padding": 18, "wrappingWidth": 400}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 32, "wrap": false, "diagramMarginX": 32, "diagramMarginY": 24, "actorFontSize": 16, "noteFontSize": 16, "messageFontSize": 16}, "htmlLabels": false}}%%
sequenceDiagram
    autonumber
    participant P as GSPacket
    participant H as HeroManager
    participant R as HeroDB
    participant W as DB Worker / DbJob
    participant DB as MySQL
    rect rgb(246, 247, 248)
    P->>H: UpdateAsync(..., isCurrent)
    H->>H: 필드 허용 여부 · Hero 소유권 확인
    H->>R: UpdateAsync(userId, heroId,<br/>field, value)
    end
    rect rgb(246, 247, 248)
    R->>W: MysqlDataBase.ExecuteAsync<br/>작업 등록
    W->>W: MySqlDataSource<br/>연결 확보
    W->>DB: HeroDB 작업 delegate 실행<br/>BeginTransaction
    W->>DB: UPDATE → SELECT
    W->>DB: CommitAsync
    DB-->>W: 커밋 완료
    W->>W: DB 연결 Dispose<br/>작업 Completion 완료
    W-->>R: 저장된 DBHero
    R-->>H: 결과 반환
    end
    rect rgb(246, 247, 248)
    H->>H: 현재 연결 재확인<br/>결과 ID·소유자 검증
    H->>H: 메모리 Hero 스냅샷 교체
    H-->>P: ACK_HERO_UPDATE
    P->>P: 현재 연결 재확인<br/>SendPacket
    end
```

<a id="match"></a>

## 매칭과 연결 종료

```text
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Malgun Gothic", "fontSize": "16px", "primaryColor": "#fafafa", "primaryTextColor": "#30363d", "primaryBorderColor": "#8b949e", "lineColor": "#8b949e", "secondaryColor": "#f2f3f4", "tertiaryColor": "#fafafa", "textColor": "#30363d", "actorBkg": "#f6f7f8", "actorBorder": "#8b949e", "actorTextColor": "#30363d", "signalColor": "#8b949e", "signalTextColor": "#30363d", "noteBkgColor": "#f6f7f8", "noteTextColor": "#30363d", "noteBorderColor": "#8b949e", "labelBoxBkgColor": "#f6f7f8", "labelTextColor": "#30363d", "clusterBkg": "#f6f7f8", "clusterBorder": "#c5cbd1", "edgeLabelBackground": "#fafafa", "activationBkgColor": "#e6e8eb", "activationBorderColor": "#8b949e", "sequenceNumberColor": "#ffffff"}, "flowchart": {"curve": "linear", "nodeSpacing": 32, "rankSpacing": 42, "htmlLabels": false, "padding": 18, "wrappingWidth": 400}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 32, "wrap": false, "diagramMarginX": 32, "diagramMarginY": 24, "actorFontSize": 16, "noteFontSize": 16, "messageFontSize": 16}, "htmlLabels": false}}%%
flowchart TD
    J["GSPacket.HandleReqPvpMatchPending"] --> M["MatchManager.JoinAsync<br/>gate 획득 · 연결/중복 검사"]
    M --> R["RedisMatchmakingStore.AddAsync<br/>티켓 SET NX · TTL 120초"]
    R --> P["연결 재확인 → 메모리 pending 등록<br/>Pending ACK 반환"]
    P -.-> T[["별도 매칭 작업자: TickAsync<br/>정리 요청·만료 처리 후 후보 선택"]]
    T --> C["RedisMatchmakingStore.ClaimAsync<br/>Lua로 두 티켓 값 확인 후 삭제"]
    C --> OK{"티켓 소비 성공<br/>두 연결 모두 유효?"}
    OK -->|예| Room["메모리 pending 제거<br/>Room · 사용자별 방 인덱스 등록"]
    Room --> N["GameSession.Notify<br/>(connectionId, reply)<br/>세션 작업 큐에 알림 등록"]
    N --> S["현재 연결 확인 → SendPacket"]
    OK -->|아니오| Fail["FailPair<br/>대기 해제 · 티켓 정리 예약 · 종료 알림"]
    D["GameSession.OnDisconnected"] --> Schedule[["ScheduleDisconnect<br/>사용자 ID · 연결 ID만 큐에 기록"]]
    Schedule -.-> Next["다음 TickAsync → DisconnectAsync<br/>같은 연결의 대기·방 정리"]
    Next --> Cleanup["FlushCleanupAsync<br/>토큰 일치 티켓 삭제 · 실패 시 재시도"]
    Next --> Notify["상대 연결이 유효하면 종료 알림"]
    classDef network fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef game fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef storage fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef queue fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef decision fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef error fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef terminal fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    class J,M,P,Room,N,S,D,Notify game
    class R,C,Cleanup storage
    class T,Schedule,Next queue
    class OK decision
    class Fail error
```

<a id="release"></a>

## 세션 반환과 재사용

```text
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Malgun Gothic", "fontSize": "16px", "primaryColor": "#fafafa", "primaryTextColor": "#30363d", "primaryBorderColor": "#8b949e", "lineColor": "#8b949e", "secondaryColor": "#f2f3f4", "tertiaryColor": "#fafafa", "textColor": "#30363d", "actorBkg": "#f6f7f8", "actorBorder": "#8b949e", "actorTextColor": "#30363d", "signalColor": "#8b949e", "signalTextColor": "#30363d", "noteBkgColor": "#f6f7f8", "noteTextColor": "#30363d", "noteBorderColor": "#8b949e", "labelBoxBkgColor": "#f6f7f8", "labelTextColor": "#30363d", "clusterBkg": "#f6f7f8", "clusterBorder": "#c5cbd1", "edgeLabelBackground": "#fafafa", "activationBkgColor": "#e6e8eb", "activationBorderColor": "#8b949e", "sequenceNumberColor": "#ffffff"}, "flowchart": {"curve": "linear", "nodeSpacing": 32, "rankSpacing": 42, "htmlLabels": false, "padding": 18, "wrappingWidth": 400}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 32, "wrap": false, "diagramMarginX": 32, "diagramMarginY": 24, "actorFontSize": 16, "noteFontSize": 16, "messageFontSize": 16}, "htmlLabels": false}}%%
flowchart TD
    D["Session.Disconnect(connectionId)<br/>현재 연결 확인 · 종료 표시 · 소켓 닫기"]
    D --> Hook["GameSession.OnDisconnected<br/>매칭 정리 요청 예약"]
    Hook --> Check{"TryRelease<br/>종료 요청 있음 · 종료 훅 완료<br/>수신/송신/작업 pending 모두 0?"}
    Check -->|아니오| Pending[["남은 I/O·작업 처리"]]
    Pending -->|완료 경로에서 재확인| Check
    Check -->|예 · 중복 반환 방지| Register[["SessionManager.ReleaseRegister<br/>반환 Channel에 Session 등록"]]
    Register --> Worker[["StartRelease의 단일 소비자"]]
    Worker --> Reset["Session.Reset<br/>GameSession.OnReset<br/>유저 등록 해제 · Hero/세션 상태 초기화"]
    Reset --> Event["Session.RetrunEventArgs<br/>Service 이벤트 풀 반환 · 참조 정리"]
    Event --> Pool[["활성 사전 제거 → 세션 풀에 등록<br/>사용 슬롯 반환"]]
    Pool --> Next(["다음 연결의 CreateSession에서 재사용"])
    classDef network fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef game fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef storage fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef queue fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef decision fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef error fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef terminal fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    class D,Event network
    class Hook,Reset game
    class Pending,Register,Worker,Pool queue
    class Check decision
    class Next terminal
```

<a id="shutdown"></a>

## 서버 종료

```text
%%{init: {"theme": "base", "themeVariables": {"fontFamily": "Malgun Gothic", "fontSize": "16px", "primaryColor": "#fafafa", "primaryTextColor": "#30363d", "primaryBorderColor": "#8b949e", "lineColor": "#8b949e", "secondaryColor": "#f2f3f4", "tertiaryColor": "#fafafa", "textColor": "#30363d", "actorBkg": "#f6f7f8", "actorBorder": "#8b949e", "actorTextColor": "#30363d", "signalColor": "#8b949e", "signalTextColor": "#30363d", "noteBkgColor": "#f6f7f8", "noteTextColor": "#30363d", "noteBorderColor": "#8b949e", "labelBoxBkgColor": "#f6f7f8", "labelTextColor": "#30363d", "clusterBkg": "#f6f7f8", "clusterBorder": "#c5cbd1", "edgeLabelBackground": "#fafafa", "activationBkgColor": "#e6e8eb", "activationBorderColor": "#8b949e", "sequenceNumberColor": "#ffffff"}, "flowchart": {"curve": "linear", "nodeSpacing": 32, "rankSpacing": 42, "htmlLabels": false, "padding": 18, "wrappingWidth": 400}, "sequence": {"mirrorActors": false, "actorMargin": 35, "messageMargin": 32, "wrap": false, "diagramMarginX": 32, "diagramMarginY": 24, "actorFontSize": 16, "noteFontSize": 16, "messageFontSize": 16}, "htmlLabels": false}}%%
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
    classDef network fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef game fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef storage fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef queue fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef decision fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef error fill:#fafafa,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    classDef terminal fill:#eef0f2,stroke:#9aa2aa,color:#30363d,stroke-width:1px
    class Service,Listen,HB,Pools network
    class Match game
    class DB,Redis storage
    class All,Released,Channel queue
    class Stop,Done terminal
```
