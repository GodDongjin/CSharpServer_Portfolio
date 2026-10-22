# 공개 ZIP 제작·업로드 안내

공개용 ZIP 제작과 GitHub 업로드 절차를 정리한 문서입니다.  
서버 실행과 테스트 방법은 [README](../README.md#실행-방법)를 참고하세요.

## 공개 ZIP 제작

PowerShell 7에서 저장소 루트를 기준으로 실행합니다.

```powershell
pwsh -File ./export-portfolio.ps1
```

결과 파일은 `artifacts/CSharpServer.zip`입니다.  
ZIP 안에는 `CSharpServer` 폴더가 생성됩니다.

## 포함 범위

공개 소스·문서·테스트·로컬 실행 자료를 포함합니다.  
기존 Git 이력·로컬 설정·빌드 산출물·로그·`TestResults`는 제외합니다.

상세 포함·제외 규칙은 [export-portfolio.ps1](../export-portfolio.ps1)에 있습니다.

## GitHub 업로드

1. ZIP을 작업 폴더 밖의 새 폴더에 풉니다.
2. ZIP 안의 `CSharpServer` 폴더 내용을 저장소 루트로 사용합니다.  
   `README.md`, `CSharpServer.slnx`, `Server`, `docs`와 숨김 파일인 `.gitignore`를 포함합니다.
3. 기존 작업 폴더의 `.git`과 로컬 백업을 함께 복사하지 않습니다.

공개 패키지의 빌드·테스트·포함 파일 검증 기록은 [검토 결과](PORTFOLIO_REVIEW.md#공개-패키지-검증-2026-10-12)를 참고하세요.

[README로 돌아가기](../README.md)
