# 로컬 통합 스모크 클라이언트

이 프로젝트는 통합 스모크 테스트 클라이언트입니다. GameServer/Protocol의 생성 C# 파일을 링크하여 로컬 서버의 로그인, Hero 갱신/복구, 매칭 취소/성립/연결 종료를 확인합니다.

저장소 루트에서 로컬 저장소와 GameServer를 실행한 후:

```powershell
dotnet run --project ServerSmokeTester -- --smoke
```

옵션 없이 실행하면 안내만 출력합니다. 접속 주소는 `127.0.0.1:7777`이며 `demo-a`, `demo-b` 시드 사용자를 사용합니다. 상세 실행 순서와 제한은 루트 README를 참고하세요.
