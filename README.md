<<<<<<< HEAD
# RECON

**Find · Organize · Restore**

RECON은 Windows용 데스크톱 파일 정리 프로그램입니다. 선택한 폴더를 스캔하고 파일을 분류한 뒤, 이동 위험도를 검사하고 사용자가 검토·승인한 정리 계획을 실행할 수 있도록 설계되었습니다.

> **현재 상태:** 첫 공개 릴리즈 준비 단계 (`v1.0.0`). 초기 버전이므로 중요한 파일은 별도로 백업한 뒤 사용해 주세요.

## 주요 기능

- 선택한 폴더의 파일 스캔
- 파일 확장자와 파일명 규칙을 이용한 파일 분류
- 분류 신뢰도, 이동 위험도 및 권장 폴더 표시
- 실제 작업 전 정리 계획 검토 및 승인
- 파일 이동 전 사전 검사와 최종 드라이런
- Safe Commit 기록 및 SHA-256 해시 검증을 통한 작업 확인
- 분석 기록과 작업 저널 저장
- 지원되는 환경에서 바탕화면 아이콘 상태 저장 및 검증

RECON은 클라우드 서비스가 아닙니다. 파일 스캔과 정리 작업은 사용자의 Windows 컴퓨터에서 수행됩니다.

## 다운로드

최신 실행 파일은 [GitHub Releases](../../releases)에서 **`RECON.exe`**를 다운로드하세요.

릴리즈 파일은 **64비트 Windows**용 자체 포함(Self-contained) 단일 실행 파일입니다. 별도의 .NET 런타임 설치 없이 실행하도록 빌드됩니다.

## 시스템 요구 사항

- Windows 10 또는 Windows 11 64비트
- x64 환경

## 소스 코드에서 빌드하기

1. .NET 10 SDK를 설치합니다.
2. 이 저장소를 Clone 합니다.
3. 저장소 루트에서 PowerShell 또는 터미널을 열고 다음 명령을 실행합니다.

```powershell
dotnet restore .\RECON.sln
dotnet build .\RECON.sln -c Release
```

Windows x64용 자체 포함 단일 실행 파일을 만들려면 다음 명령을 사용합니다.

```powershell
dotnet publish .\src\Recon.App\Recon.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o .\artifacts\publish
```

`v`로 시작하는 태그(예: `v1.0.0`)를 Push 하면 GitHub Actions가 Windows 환경에서 빌드하고 `RECON.exe`를 GitHub Release에 첨부합니다.

## 안전하게 사용하기

- 파일 작업을 승인하기 전에 제안된 정리 계획을 반드시 확인하세요.
- 중요한 데이터는 별도 저장 장치 등에 백업하세요. Safe Commit과 검증 기능은 안전장치이며 백업을 대체하지 않습니다.
- 중요한 파일의 유일한 사본을 대상으로 이동 기능을 시험하지 마세요.
- 파일 분류는 규칙 기반이므로 제안된 분류나 목적지가 항상 사용자의 의도와 일치하지는 않습니다.
- 바탕화면 아이콘 관련 동작은 Windows 버전, 디스플레이 구성 및 데스크톱 환경에 따라 달라질 수 있습니다.
- 첫 공개 릴리즈는 독립적인 보안 감사를 거치지 않았습니다.

## 버그 제보 및 제안

문제나 개선 제안은 [GitHub Issues](../../issues)에 남겨 주세요. 재현 절차, Windows 버전, 오류 메시지를 함께 제공하면 문제를 파악하는 데 도움이 됩니다. 로그나 스크린샷을 공유하기 전에는 개인 파일 경로 등 민감한 정보를 가려 주세요.

## 라이선스

현재 이 저장소에는 오픈소스 라이선스가 지정되어 있지 않습니다. 소스 코드를 공개하는 것만으로 수정본 재배포나 다른 프로젝트에서의 사용 권한이 자동으로 부여되지는 않습니다. 외부 기여를 받거나 재사용을 허용하기 전에 저작권자가 라이선스를 선택해 추가해야 합니다.
=======
# RECON
>>>>>>> bc5082cea51c910c988658d9d849711d969e5e18
