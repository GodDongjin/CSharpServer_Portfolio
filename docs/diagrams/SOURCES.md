# 도식 편집 원본

[전체 구성도 SVG](overview.svg)는 1000px 너비의 고정 배치로 접속 준비, 일반 요청 처리, 연결 감시·세션 반환을 구분합니다. 상자는 클래스 인스턴스가 아니라 처리 단계입니다. GameSession은 Session을 상속한 연결 객체 하나이며, 수신·작업 큐·송신은 그 객체의 기능입니다. `data-step`은 일반 요청 처리 단계, `data-branch`는 요청 종류별 분기를 표시합니다.

아래 전체 구성도 Mermaid는 SVG에 담은 흐름의 편집용 명세입니다. 실선은 처리 진행, 점선은 결과가 응답으로 이어지는 흐름이며 자동 배치 결과는 SVG와 다를 수 있습니다. 나머지 7개 호출 흐름도는 Mermaid 11.4.1과 맑은 고딕으로 렌더링한 SVG입니다. 도식을 수정하면 대응 SVG와 본문 설명도 함께 갱신하고, README 표시 폭에서 글자와 연결선을 확인합니다.

<a id="overview"></a>

## 서버 전체 구성

접속 준비는 새 연결을 수락할 때 수행하며, 일반 요청 처리는 연결 이후 패킷마다 반복합니다. 연결 감시와 세션 반환은 별도로 표시합니다. 일반 요청 그림은 정상 처리와 필요한 응답을 요약하며, 오류 응답·실패·종료의 상세 분기는 개별 호출 흐름도를 참고합니다. 매칭 성립 알림은 별도 TickAsync에서 시작합니다.

```text
flowchart TB
    subgraph Connection["01 접속 준비"]
        direction LR
        Client["클라이언트 / TCP 접속"] --> Listen["Service · Listen / 연결 수락"]
        Listen --> Manager["SessionManager / 풀에서 세션 할당"]
        Manager --> Ready["GameSession : Session / 초기화 후 수신 시작"]
    end

    subgraph Request["02 일반 요청 처리"]
        Receive["① 수신 · 패킷 조립 / Session"] --> Parse["② 패킷 ID 확인 · Protobuf 파싱 / GSPacketHandler"]
        Parse --> Queue["③ 세션별 작업 큐 / Session.EnqueueAsyncJob / 하나씩 await"]
        Queue --> Logic["④ 게임 로직 실행 / GSPacket"]
        Logic --> Login["로그인 · Hero 최초 로드 / LoginDB · HeroDB → DB Worker → MySQL / 연결 메모리에 보관"]
        Logic --> Hero["Hero 조회는 메모리 / 변경은 HeroDB → DB Worker → MySQL / 커밋 후 현재 연결 확인·반영"]
        Logic --> Match["MatchManager: 대기·방 상태 / RedisMatchmakingStore → Redis / Redis에는 티켓 저장"]
        Login -.->|처리 결과 / 응답이 필요한 경우| Send["⑤ 응답 등록 · 송신 / GSPacket → Session.SendPacket → SendAsync"]
        Hero -.-> Send
        Match -.-> Send
        Send --> Reply["클라이언트가 응답 수신"]
    end

    subgraph Lifetime["03 연결 감시와 세션 반환"]
        Heartbeat["SocketHeartBeat / 로그인·Ping·Pong 제한 / Pong은 ②에서 확인하고 게임 큐 생략"]
        Disconnect["연결 종료"] --> Wait["진행 중 I/O·작업 완료"]
        Wait --> Release["반환 큐"]
        Release --> Reset["초기화 · 자원 정리"]
        Reset --> Reuse["다음 연결에서 재사용"]
    end
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
