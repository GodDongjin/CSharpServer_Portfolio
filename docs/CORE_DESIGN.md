# 핵심 설계 상세

C# 서버의 모듈 경계, 실행 모델, 데이터 관리와 자원 수명을 설명합니다.  
전체 구성은 [README](../README.md#프로젝트-구조)에서 확인할 수 있습니다.

## 1. ServerCore와 GameServer의 책임 분리

서버는 하나의 프로세스에서 실행되며, **GameServer → ServerCore** 방향으로 프로젝트를 참조합니다.  
공통 실행 기반과 게임 도메인의 경계를 프로젝트 수준에서 나눴습니다.

<a id="servercore"></a>

**ServerCore**

<dl>
<dd>

연결 수락, TCP 송수신, 패킷 경계 처리, 세션 큐·풀, Heartbeat, DB Worker와 Redis 연결을 담당합니다.

</dd>
</dl>

<a id="gameserver"></a>

**GameServer**

<dl>
<dd>

패킷별 게임 핸들러, 로그인 사용자, Hero 상태, 매칭 규칙, 게임 데이터의 SQL과 Repository를 담당합니다.

</dd>
</dl>

<a id="연결-지점"></a>

**연결 지점**

<dl>
<dd>

`GameSession`은 `Session`을 상속해 사용자·Hero 상태와 종료 동작을 확장합니다.  
코어는 `IPacketHandler`를 통해 패킷을 전달하고, `Program`이 Repository·MatchManager·패킷 핸들러·Service를 구성해 연결합니다.

</dd>
</dl>

게임 데이터의 의미와 처리 규칙은 GameServer에 두고, 코어의 통신·작업 실행 기능을 사용하도록 구성했습니다.

**구현 코드:** [GameSession](../Server/GameServer/Sessions/GameSession.cs), [IPacketHandler](../Server/ServerCore/Interface/IPacketHandler.cs), [Service](../Server/ServerCore/Network/Service.cs), [Program](../Server/GameServer/Program.cs)

## 2. SocketAsyncEventArgs 기반 TCP 통신과 버퍼 관리

`Service`가 수신·송신용 `SocketAsyncEventArgs` 풀을 보유하고, `Session`이 연결별 비동기 I/O와 버퍼 상태를 관리합니다.  
패킷은 **길이·ID 헤더와 protobuf 본문**으로 구성합니다.

<a id="수신"></a>

**수신**

<dl>
<dd>

길이 헤더로 완성된 패킷을 구분합니다.  
미완성 데이터는 다음 수신까지 보존하고, 한 번에 여러 패킷이 들어오면 차례로 분리합니다.  
protobuf는 수신 버퍼가 유효한 동안 파싱해 게임 작업 큐로 전달합니다.

</dd>
</dl>

<a id="송신"></a>

**송신**

<dl>
<dd>

대기 버퍼들을 `BufferList`로 묶어 전송합니다.  
부분 송신 시 실제 전송량만큼 구간을 이동하고 나머지를 이어 보내며, 각 버퍼의 전송이 끝나면 반환합니다.

</dd>
</dl>

<a id="자원-한도"></a>

**자원 한도**

<dl>
<dd>

세션별 대기 작업은 30개, 대기·진행 중 송신 버퍼의 할당 용량 합계는 1MiB로 제한합니다.  
한도 초과는 게임 패킷 처리 경로에서 연결 종료로 이어집니다.

</dd>
</dl>

수신 데이터의 소비 위치, 송신 데이터의 완료 위치, 버퍼를 반환할 수 있는 시점을 각각 관리합니다.  
한도는 현재 구현의 정책값이며 부하 특성에 따른 조정 대상입니다.

**구현 코드:** [Session](../Server/ServerCore/Network/Session.cs), [Service](../Server/ServerCore/Network/Service.cs), [GSPacketHandler](../Server/GameServer/Packet/GSPacketHandler.cs)

**관련 검증:** [버퍼·패킷 테스트](../Server/ServerCore.Tests/BufferAndPacketTests.cs), [송신 제한·부분 송신 테스트](../Server/ServerCore.Tests/SessionSendLimitTests.cs)

## 3. 세션별 순차 실행과 연결 수명 관리

**세션을 게임 요청의 실행 단위**로 사용합니다.  
각 세션은 비동기 작업 큐를 가지며, 세션 객체의 재사용과 개별 연결의 수명은 연결 ID로 구분합니다.

<a id="게임-작업-실행"></a>

**게임 작업 실행**

<dl>
<dd>

같은 세션에서는 앞선 작업의 `await`가 끝난 뒤 다음 작업을 실행합니다.  
서로 다른 세션은 독립된 큐 소비 작업으로 진행하며, 게임 핸들러는 소켓 I/O 콜백 밖에서 실행합니다.

</dd>
</dl>

<a id="연결-식별"></a>

**연결 식별**

<dl>
<dd>

작업 등록·외부 알림·종료 요청에는 요청 당시 연결 ID를 사용합니다.  
ID 검사와 수명 상태 전이는 잠금으로 보호하고, DB를 기다리는 동안에는 해당 잠금을 유지하지 않습니다.

</dd>
</dl>

<a id="풀-반환"></a>

**풀 반환**

<dl>
<dd>

연결 종료 후 종료 훅, 수신·송신 I/O, 세션 큐 작업의 완료를 확인합니다.  
이후 `Channel<Session>`의 단일 반환 작업자가 `Reset → 이벤트 객체 반환·참조 해제 → 세션 풀 반환`을 수행합니다.

</dd>
</dl>

이 모델에서 같은 세션의 후속 요청은 진행 중인 DB 작업을 기다립니다.  
연결이 끊긴 뒤에도 작업과 정리가 완료될 때까지 해당 세션 슬롯을 유지합니다.

**구현 코드:** [Session](../Server/ServerCore/Network/Session.cs), [SessionManager](../Server/ServerCore/Network/SessionManager.cs)

**관련 검증:** [세션 큐 테스트](../Server/ServerCore.Tests/SessionQueueTests.cs), [수명 테스트](../Server/ServerCore.Tests/SessionLifecycleTests.cs), [반환 순서 테스트](../Server/ServerCore.Tests/SessionManagerTests.cs)

## 4. 사용자별 DB 작업 분배와 Hero 상태 관리

DB 접근은 **게임별 Repository → MysqlDataBase → DbWorkerPool**로 이어집니다.  
SQL과 데이터 변환은 GameServer가 담당하고, 작업 분배·실행·연결 관리는 ServerCore가 담당합니다.

<a id="작업-분배"></a>

**작업 분배**

<dl>
<dd>

`userId % WorkerCount`로 Worker를 선택합니다.  
같은 사용자의 DB 작업은 같은 Worker에서 등록 순서대로 실행하며, 서로 다른 Worker는 독립적으로 실행합니다.  
Worker별로 실행 중 작업을 포함해 10,000개까지 받습니다.

</dd>
</dl>

<a id="상태-소유"></a>

**상태 소유**

<dl>
<dd>

MySQL에 사용자·Hero를 영속 저장합니다.  
로그인 시 읽은 Hero는 `GameSession.Heroes`가 현재 연결 동안 보관하고, 목록 요청은 이 메모리를 조회합니다.

</dd>
</dl>

<a id="변경-반영"></a>

**변경 반영**

<dl>
<dd>

`HeroDB`가 트랜잭션 안에서 갱신·재조회 후 커밋합니다.  
`HeroManager`는 요청 당시 연결이 유효한지 확인한 뒤 저장된 스냅샷으로 메모리를 교체하고 응답합니다.

</dd>
</dl>

같은 Worker에 배정된 다른 사용자의 DB 작업도 순차 실행됩니다.  
Hero 캐시는 연결 전용이며, 저장 결과가 불명확한 예외에서는 연결을 종료하고 재로그인 시 DB를 다시 읽습니다.

**구현 코드:** [DbWorkerPool·DbWorker](../Server/ServerCore/DataBase/DataBaseWorker.cs), [HeroDB](../Server/GameServer/DataBase/DBRepository/HeroDB.cs), [HeroManager](../Server/GameServer/Hero/HeroManager.cs)

**관련 검증:** [Hero 저장소 대역 테스트](../Server/GameServer.Tests/HeroAndMatchTests.cs), [실제 MySQL 저장·재로그인 검증](PORTFOLIO_REVIEW.md)

## 5. MatchManager 중심의 매칭 상태 관리

`MatchManager`가 **대기 사용자, 매칭 방, 사용자별 방 인덱스**를 소유합니다.  
방과 대기열은 서버 메모리에 두고, Redis는 연결별 매칭 티켓을 보관하는 역할로 사용합니다.

<a id="매칭-규칙"></a>

**매칭 규칙**

<dl>
<dd>

대기 시간 순으로 후보를 살펴보고, 점수 차이 1,000 이내에서 가장 가까운 상대를 선택해 1:1 방을 구성합니다.

</dd>
</dl>

<a id="상태-변경"></a>

**상태 변경**

<dl>
<dd>

참가·취소·성립·퇴장·연결 종료를 하나의 `SemaphoreSlim`으로 직렬화합니다.  
매칭 알림은 연결 ID를 포함해 대상 세션의 작업 큐로 전달합니다.

</dd>
</dl>

<a id="redis-연동"></a>

**Redis 연동**

<dl>
<dd>

티켓에는 연결 ID와 고유 토큰을 저장합니다.  
Lua로 두 티켓의 값을 확인·삭제한 뒤 연결 유효성을 재확인하고 방을 만듭니다.  
개별 정리도 토큰이 일치하는 티켓에 적용하며, 삭제 재시도와 120초 TTL을 사용합니다.

</dd>
</dl>

현재 대기열과 방의 소유 범위는 단일 서버 프로세스입니다.  
Redis 호출은 매칭의 직렬 처리 구간에 포함되며, 취소 응답은 로컬 대기 해제를 의미합니다.

**구현 코드:** [MatchManager](../Server/GameServer/Matchmaking/MatchManager.cs), [RedisMatchmakingStore](../Server/GameServer/Matchmaking/RedisMatchmakingStore.cs), [GameSession](../Server/GameServer/Sessions/GameSession.cs)

**관련 검증:** [매칭 상태·정리 재시도 테스트](../Server/GameServer.Tests/HeroAndMatchTests.cs), [실제 Redis 매칭 검증](PORTFOLIO_REVIEW.md)

## 6. Heartbeat와 의존 순서에 따른 서버 종료

연결 감시는 단일 Heartbeat 작업자가 담당하고, 서버 전체의 시작·종료 순서는 `Program`이 조정합니다.

<a id="로그인생존-확인"></a>

**로그인·생존 확인**

<dl>
<dd>

접속 후 DB 조회를 포함해 30초 안에 로그인을 완료해야 합니다.  
로그인 후 Ping 간격은 10초, 유효한 Pong 대기는 30초이며, 작업자가 1초마다 `Stopwatch` 기준으로 검사합니다.

</dd>
</dl>

<a id="pong-처리-경로"></a>

**Pong 처리 경로**

<dl>
<dd>

게임 작업 큐를 거치지 않고 수신 경로에서 연결 ID·현재 Ping 번호·기한을 확인합니다.  
게임 요청의 DB 대기와 연결 생존 확인의 실행 경로를 분리했습니다.

</dd>
</dl>

<a id="종료-순서"></a>

**종료 순서**

<dl>
<dd>

새 연결 수락을 중단하고 `세션·반환 작업자 → 매칭 → DB Worker·DB 연결 → Redis` 순서로 완료를 기다립니다.  
각 작업이 사용하는 저장소는 해당 작업이 끝난 뒤 해제합니다.

</dd>
</dl>

종료 대기가 길어지면 10초마다 경고합니다.  
진행 중 저장 작업을 일괄 취소하지 않으므로 종료 시간의 상한은 없으며, 실제 정리 오류는 호출자에게 전달합니다.

**구현 코드:** [SocketHeartBeat](../Server/ServerCore/Network/SocketHeartBeat.cs), [Session](../Server/ServerCore/Network/Session.cs), [Program](../Server/GameServer/Program.cs)

**관련 검증:** [Heartbeat 테스트](../Server/ServerCore.Tests/HeartbeatTests.cs), [종료 대기 테스트](../Server/GameServer.Tests/ShutdownTests.cs)

## 검증 범위

자동 테스트의 저장소 대역 검증과 실제 MySQL·Redis 통합 검증은 [검토 기록](PORTFOLIO_REVIEW.md)에서 구분합니다.  
실제 저장소 장애 주입·커밋 응답 유실·DB 작업 진행 중 종료는 추가 검증 범위입니다.

[README로 돌아가기](../README.md)
