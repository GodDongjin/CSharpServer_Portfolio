# 프로토콜 생성

원본은 `../../Library/protoc-35.1-win64/bin/`의 Enum.proto, Struct.proto, Protocol.proto입니다. 이 폴더는 생성 결과를 보관합니다. 생성된 Enum.cs, Struct.cs, Protocol.cs와 PacketIds.cs도 Git에 포함합니다. GameProtocol 프로젝트는 사용하지 않습니다.

## 실행

저장소 루트에서:

```powershell
.\Server\Library\protoc-35.1-win64\bin\GenPackets.bat
```

Windows PowerShell과 protoc.exe가 필요합니다. protoc 실행 파일은 GenPackets.bat 옆에 두며, 현재 로컬에 설치된 파일을 사용합니다. 파일이 없으면 생성기는 오류로 종료합니다. protoc.exe는 Git 및 공개 ZIP에 포함되지 않습니다. 생성 결과가 이미 포함되어 있으므로 일반 dotnet 빌드에는 이 도구가 필요하지 않습니다.

## 새 패킷 추가

1. bin 폴더의 proto 원본에 메시지 필드와 필드 번호를 작성합니다. 기존 필드 번호를 다른 의미로 재사용하지 않습니다.
2. 같은 bin 폴더의 PacketRoutes.json에 고유 Id, Name, Message, Constant를 추가합니다. 일반 요청에는 Handler 이름도 작성하고 응답은 빈 문자열로 둡니다.
3. 생성 도구를 실행하고 GSPacket에 추가된 핸들러를 구현합니다. 자동 스텁의 return true만으로 요청 처리가 완성되는 것은 아닙니다.
4. 솔루션 빌드 및 테스트를 실행합니다.

선언 순서 대신 명시적 ID를 사용합니다. 기존 ID와 핸들러 이름을 유지하면서 하나의 ACK_MATCH 메시지를 서로 다른 응답 ID에 대응시키기 위한 구성입니다. PACKET_ID enum과 클라이언트용 PacketIds 상수는 같은 대응표에서 생성합니다.

핸들러 등록은 AUTO-GENERATED-BEGIN/END 사이만 갱신하고 기존 메서드는 보존합니다. 없는 메서드만 추가하며, 제거한 요청의 옛 메서드는 자동으로 삭제하지 않습니다. Heartbeat Pong은 Handler를 비워 두고 RegisterHeartbeatPong에서 직접 처리합니다. 일반 요청 큐에 넣지 않습니다.

## 검증 기록

2026-10-12: 기존 자동 테스트 80개 통과 및 스모크 클라이언트 포함 Release 빌드 성공. 생성 재실행 시 파일 불변, 대응표 재정렬 후 ID 유지, 기존 구현 보존, 새 핸들러 한 번만 추가, 중복 ID 거절 시 핸들러 파일 불변을 별도 임시 파일로 검증했습니다. 실제 DB/Redis 스모크는 이번 변경에서 재실행하지 않았습니다.

현재 작업 폴더는 전체 백업한 뒤 검증 ZIP으로 복구했습니다. 다시 들어온 이전 콘텐츠와 병합 충돌은 공개 소스 트리에 포함하지 않습니다. 백업 위치는 로컬 TestResults/restore-backup-path.txt에 기록했습니다.
