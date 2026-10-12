# 포트폴리오 축소 완료 내역

2026-10-12 최종 공개 범위. 이전 병합으로 다시 들어온 콘텐츠 파일도 로컬 백업으로 이동했습니다.

## 삭제 범위

아레나, 랭킹, 건설, 무기, 프리셋 편집, 성장, 특성, 전투 중계, 보상을 제거했습니다. 해당 기능의 핸들러·도메인·DB 접근 코드뿐 아니라 아래 자료도 함께 제거했습니다.

- 전체 콘텐츠를 담던 protobuf 메시지·enum 및 서버/클라이언트의 중복 생성 C#.
- 기존 기능의 테이블·저장 프로시저·DB 문서와 패킷 시퀀스 문서.
- GameData 테이블/생성기/스냅샷과 해당 테스트 프로젝트.
- 오래된 Client와 전투/보상용 부하 테스트 설정·시나리오·데이터.
- 이전 전체 게임 프로토콜 복제본. 직접 작성한 패킷 생성 스크립트는 공개 범위에 포함합니다.

Hero의 성장/특성/장비 필드도 남겨 두지 않았습니다. 현재 Hero는 ID, 소유자, 가상 템플릿 ID, 고정/잠금 여부만 가집니다.

## 남긴 기능과 재구성

| 기능 | 공개본 범위 |
|---|---|
| ServerCore | 기존 라이브러리와 49개 회귀 테스트 |
| 로그인 | 가상 시드 사용자 조회, 중복 로그인 거절 |
| Hero | 목록, 고정/잠금 변경, DB 커밋 후 메모리 반영 |
| PvP | 참가/취소/만료/매칭/퇴장/연결 종료, 메모리 방 |
| MySQL | users, heroes 두 테이블과 가상 데이터만 새로 정의 |
| Redis | 토큰 기반 대기 예약, 두 티켓의 Lua 원자적 소비, 삭제 재시도/TTL |
| 프로토콜 | Library/protoc-35.1-win64/bin 원본과 기존 자동 생성 스크립트, GameServer/Protocol 생성 결과 |
| 검증 | GameServer 31개 테스트와 로컬 통합 스모크 클라이언트 |

이 작업은 단순 파일 삭제를 넘어 데모용 프로토콜·스키마·매칭 구현을 재구성했습니다. 과거 게임 클라이언트나 기존 운영 DB와의 호환성을 제공하지 않습니다. 과거 매칭 기록 테이블도 제거했으며 승패·전적 저장을 하지 않습니다.

## 로컬 설정

기존 개인 설정은 로컬 TestResults 백업으로 옮겼습니다. 공개 기본 설정은 가상 계정과 로컬 데모 구성입니다. 기존 Git 이력은 공개 ZIP에 포함하지 않습니다.

실행 방법, 검증 한계, 상태 관리 규칙은 루트 README를 참고하세요.

## 패킷 구조 복원

기능 축소 과정에서 도입했던 GamePacketHandler 통합 구조를 제거했습니다. 원래 `Packet/GSPacketHandler.cs`와 `Packet/GSPacket.cs`, `PACKET_ID`, 배열 기반 등록, `Initialize()`, `Register<T>()`, `HandleAsync<T>()`, `HandleReq…()` 형태를 복원했습니다. 로그인·Hero 목록/갱신·매칭 참가/취소/퇴장만 등록합니다. 현재 최소 프로토콜·DB와 연결 종료 보호 로직은 유지합니다.

## DB DTO·DBRepository·Hero 배치 복원

추가했던 Domain/Models.cs와 Persistence/MySqlRepositories.cs를 제거하고 기존 폴더 배치를 복원했습니다.

- DataBase/DTO/UserDTO.cs, HeroDTO.cs: DBUser·DBHero 조회 결과.
- DataBase/DBRepository/DataBaseRepository.cs: LoginDB·HeroDB 접근을 묶는 기존 진입점.
- DataBase/DBRepository/LoginDB.cs, HeroDB.cs: 실제 SQL과 DB 대역 연결용 인터페이스.
- Hero/Hero.cs: HeroData의 읽기 전용 DB 스냅샷과 패킷 변환.
- Hero/HeroManager.cs: 목록 관리, 소유권 검증, DB 저장·연결 확인 후 메모리 반영.

별도 HeroUpdateService는 제거했습니다. DTO의 불변 스냅샷과 테스트용 인터페이스는 일관성 검증을 위해 유지하며, 기존 운영용 스키마나 삭제한 게임 기능은 복원하지 않습니다. 테스트는 HeroAndMatchTests.cs와 ProtocolTests.cs에서 같은 규칙을 검증합니다.
