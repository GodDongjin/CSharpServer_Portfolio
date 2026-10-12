# 세션 종료·재사용 수정 결과

2026-09-30. 기존 `Channel<Session>` 반환 구조를 유지하여 수정했다.

## 선택한 접속 정책

원래 TCP 연결이 유지되면 같은 세션에서 계속 처리한다. 연결이 종료되면 기존 세션을 정리하고, 새 TCP 연결에서 다시 로그인한다. 로그인·Hero·매칭 상태를 새 연결에 복구하거나 30초 동안 네트워크 세션을 보관하는 기능은 추가하지 않았다.

`SocketHearBeat`는 아직 비어 있다. 애플리케이션 heartbeat 송수신과 타임아웃 판단은 이번 수정에 포함하지 않는다.

## 반환 순서

```text
Disconnect
  → 종료 표시 및 소켓 닫기
  → OnDisconnected에서 연결 ID를 포함한 게임 정리 예약
  → 대기 중인 수신·송신·세션 비동기 작업 완료
  → Channel<Session>에 한 번 등록
  → SessionManager의 단일 반환 작업자가 Reset
  → Service의 이벤트 풀로 ReadArgs / WriteArgs 반환
  → Session의 이벤트 참조·packetHandler·owner 해제
  → 활성 세션 사전에서 제거
  → 세션 큐 등록 및 사용 슬롯 반환
```

Session은 Channel에 자신을 등록한다. 별도의 ReleaseRequest 객체는 사용하지 않는다. 채널 등록 후 객체를 다시 초기화하는 주체는 SessionManager이며, 이벤트 반환이 끝나기 전에는 사용 가능한 세션으로 공개하지 않는다.

## 변경한 보호 규칙

- `Session._lifetimeLock`: 초기화, 송수신 준비, 완료 콜백의 후속 처리, Disconnect, Reset, 이벤트 반환을 직렬화한다. 콜백이 pending을 0으로 바꾼 직후에도 잠금이 유지되므로 다음 I/O 등록 전에 Reset이 끼어들 수 없다.
- `_receivePending`, `_sendPending`, `_asyncJobPending`: 잠금 밖에서 대기 중인 I/O와 비동기 작업의 수명을 유지한다. I/O 등록이 예외로 끝나면 pending을 해제하고 반환 조건을 다시 검사한다.
- `_disconnecting`: 종료 훅이 끝나기 전의 반환을 막는다. Shutdown과 Close는 캡처한 같은 소켓을 사용한다.
- `_released`: 종료 조건을 만족했을 때 한 번만 반환을 등록한다. 단일 Channel 소비자는 초기화 시 한 번 시작하고 종료 시 완료를 기다린다.
- 비동기 큐 소비자는 `Task.Run`으로 시작한다. 사용자 작업을 I/O 콜백 잠금 안에서 실행하지 않으며, 작업 순서와 대기 30개 제한은 유지한다.
- `Reset`은 owner를 유지한다. `RetrunEventArgs`에서 이벤트 풀 반환이 성공한 뒤 owner와 이벤트 참조를 null로 만든다. 반환된 이벤트를 Session.Dispose가 다시 Dispose하지 않는다.
- 사용 중인 Session의 Reset, Dispose, 중복 Initialize는 예외로 차단한다. 정리 중 예외가 발생한 세션은 재사용하지 않고 새 연결도 거절하며, 서버 종료는 실패를 보고한다.

이 잠금은 동기 경로만 보호하며 비동기 작업의 await 동안 유지하지 않는다. 종료 훅과 수신 핸들러의 동기 부분은 짧게 끝내야 한다.

## 이전 연결의 늦은 요청

매칭처럼 Session 참조를 보관하는 외부 작업은 연결 ID를 함께 보관해야 한다. `EnqueueAsyncJob(connectionId, job)`과 `Disconnect(connectionId)`는 잠금 안에서 현재 연결 ID를 확인한다.

GameSession.Notify는 이 두 메서드를 사용한다. 이전 연결의 알림이 뒤늦게 도착하거나 새 연결의 작업 큐가 가득 차 있어도, 이전 알림 때문에 새 연결이 끊기지 않는다. 인수 없는 Disconnect나 직접 Send를 오래된 Session 참조로 호출하는 것은 이 계약에 포함되지 않는다.

GameSession의 종료 정리는 Disconnect 재정의에서 `OnDisconnected` 훅으로 옮겼다. 로그인·Hero 정리는 기존 OnReset에 남겨 두었고, 패킷 등록·핸들러 구조는 그대로 유지했다.

## 서버 종료

Listen은 연결 수락 결과의 세션 등록까지 종료와 같은 잠금으로 보호한다. SessionManager도 새 등록 차단과 종료 대상 확정을 같은 잠금으로 처리한다.

세션은 Reset과 이벤트 반환이 끝날 때까지 활성 사전에 남는다. `AllReleaseSessionAsync()`와 `StopServerAsync()`는 각각 최초 호출에서 종료 Task를 만들고, 이후 호출에서는 같은 Task를 반환한다. TaskCompletionSource로 전체 반환을 비동기로 기다리고 Channel을 닫은 뒤 소비자 종료를 기다린다. Manager는 프로세스당 한 번 초기화하는 기존 사용 방식을 유지한다.

Program은 세션·반환 작업자 → 매칭 → DB Worker·DB 연결 → Redis 순서로 정리한다. 각 단계에서 10초마다 경고만 남기며 같은 작업 완료를 계속 기다린다. DB Job에 일괄 타임아웃을 추가하거나 저장 작업을 취소하지 않는다. DB Worker 자체의 기존 큐 배출 순서는 유지한다.

기존 SERVER_STOP_RESULT, 시간 초과 재호출, 오류 후 무한 Delay는 제거했다. 실제 정리 오류는 예외로 전달하며 이후 의존 자원을 자동 해제하지 않는다. Main에서는 이를 기록한 뒤 다시 던지므로 최상위 오류로 프로그램이 비정상 종료될 수 있으며, 정상 정리 성공으로 보고하지 않는다. 정상 작업이 끝나지 않으면 경고하며 계속 기다리는 정책이다.

## 당시 검증 결과

.NET 10.0.401 SDK에서 Release 솔루션 빌드: 경고 0개, 오류 0개. 테스트 60개 통과(ServerCore 32개, GameServer 28개).

추가하거나 확장한 검증:

- 종료 훅이 멈춰 있는 동안 Reset·이벤트 반환이 실행되지 않음.
- 사용 중인 세션의 Reset·Dispose·이벤트 반환·중복 초기화 거절.
- 연결하지 않은 소켓의 ReceiveAsync 등록 실패 후 한 번만 반환.
- 실제 SessionManager와 Service 풀에서 이벤트 반환을 강제로 지연했을 때 슬롯과 세션을 먼저 공개하지 않음.
- 같은 Session과 이벤트 객체로 루프백 연결을 16회 반복하고, 동시 송신·종료 후 풀의 개수와 이벤트 UserToken 정리를 확인.
- 종료 훅·비동기 작업·이벤트 반환을 지연해도 종료 Task가 먼저 완료되지 않고, 반복 호출은 같은 Task를 반환함.
- 경고를 여러 번 출력해도 작업을 취소하거나 완료 처리하지 않으며, 실제 종료 예외는 후속 정리 전에 전달됨.
- 큐가 가득 찬 재사용 GameSession에 이전 연결의 매칭 알림을 넣어도 새 연결이 유지됨.

기존 프레이밍·큐 순서·Hero·매칭 테스트도 통과했다. DB/Redis는 테스트 대역을 사용했으며 실제 저장소 통합 시험, 모바일 백그라운드 시험, 장시간 부하 시험은 수행하지 않았다. 이 테스트 결과를 모든 실행 순서나 운영 안정성의 보증으로 해석하지 않는다.


위 60개 테스트와 미수행 항목은 당시 수정 기록입니다. 이후 자동 테스트 80개, 실제 저장소 통합, 부하 및 메모리 검증을 수행했습니다. 최신 범위는 [공개본 검토 결과](PORTFOLIO_REVIEW.md)를 참고하세요.
