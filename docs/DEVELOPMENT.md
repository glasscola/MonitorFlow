# 개발 안내

## 구성

| 경로 | 역할 |
| --- | --- |
| `MonitorFlow/MainWindow.xaml` | 한국어 WPF 배치 편집 화면 |
| `MonitorFlow/MainWindow.xaml.cs` | 초안·적용 상태, 배치 변경 확인, 복원, 트레이 |
| `MonitorFlow/Core/LayoutEngine.cs` | 인접 관계, 스냅·크기 재배치, Windows 픽셀 배치 변환 |
| `MonitorFlow/Core/CursorMapper.cs` | 실제 크기 비율에 따른 순수 경계 전환 계산 |
| `MonitorFlow/Services/DisplayService.cs` | 화면 조회와 위치만 변경하는 Win32 트랜잭션 |
| `MonitorFlow/Services/MouseRouter.cs` | 전용 STA 메시지 스레드, 마우스 훅, Raw Input, 비상 중지 키 감지 |
| `MonitorFlow/Services/ProfileStore.cs` | 로컬 JSON 설정과 복구 스냅샷 |
| `MonitorFlow.Tests/Program.cs` | 외부 테스트 프레임워크 없는 계산 검증 |

## 상태와 적용

배치 편집은 초안에만 반영됩니다. 마우스 보정 엔진은 복제한 마지막 적용 프로필과 네이티브 화면 스냅샷을 사용하므로, 편집 중인 변경이 실행 중인 보정으로 바로 흘러가지 않습니다.

Windows 위치 변경은 현재 모드에서 `DM_POSITION`만 지정해 모든 화면의 위치를 준비하고 한 번에 적용합니다. 해상도·배율·주 모니터를 변경하지 않습니다. 오류 시 이전 스냅샷으로 복원을 시도하고, 적용 확인 전에는 복구 파일을 유지합니다.

네이티브 픽셀 사각형으로 표현할 수 없는 일부 혼합 해상도 격자는 명시적으로 거부합니다. 사용자의 화면 연결 관계를 조용히 바꾸는 대체 배치는 만들지 않습니다.

## 확인 방법

```powershell
dotnet restore MonitorFlow/MonitorFlow.csproj
dotnet build MonitorFlow/MonitorFlow.csproj -c Release --no-restore
dotnet run --project MonitorFlow.Tests/MonitorFlow.Tests.csproj -c Release
```

`--demo`는 샘플 3화면을 표시하고 실제 Windows 위치나 포인터를 변경하지 않습니다. `--diagnostics <절대경로.json>`은 연결된 화면을 읽고 보정 입력 등록·해제를 확인합니다. 진단 파일에는 화면 장치 식별자가 포함되므로 공개 저장소에 커밋하지 않습니다.

자동화 입력은 마우스 보정을 통과하지 않습니다. 따라서 계산 테스트와 입력 훅 등록 테스트 외에 실제 마우스로 화면 경계를 넘는 확인이 필요합니다.

## CI와 릴리스

- `ci.yml`: 모든 브랜치의 push와 pull request에서 Windows 빌드와 계산 테스트를 실행합니다.
- `release.yml`: `v*` 태그를 push하면 테스트 후 Windows x64 단일 EXE, PDF, 휴대용 ZIP, SHA-256 목록을 GitHub Release에 업로드합니다.
- SDK는 `10.0.402`, 외부 Actions는 검증한 릴리스의 커밋 SHA에 고정했습니다.

릴리스 전에 프로젝트 버전과 `RELEASE_NOTES.md`, PDF 설명서를 갱신하세요. 예를 들어 버전 1.0.0 배포는 다음과 같습니다.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

GitHub의 소스 ZIP/TAR 아카이브는 태그에 연결된 커밋에서 생성됩니다. 실행 파일은 Git 이력에 넣지 않고 Release 자산으로 배포합니다.
