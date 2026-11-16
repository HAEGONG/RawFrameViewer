# RawFrameViewer

16-bit 검출기 영상을 TCP로 받아 표시하는 Windows 뷰어와, 실제 장비 대신 영상을 보내는 가상 검출기 시뮬레이터입니다.

> ⚠️ **임상용이 아닙니다.** 이 프로젝트는 영상 뷰어 엔지니어링, 장비 통신, 검증 과정을 보여주기 위한 개인 포트폴리오입니다. 진단이나 의료 목적으로 사용할 수 없습니다.

## 구성

| 프로젝트 | 설명 |
|---|---|
| `src/RawFrameViewer.Core` | 프레임 모델, 프로토콜 직렬화·파싱, CRC 검사, Window/Level 변환 |
| `src/RawFrameViewer.Simulator` | 가상 검출기. 16-bit 프레임을 생성해 TCP로 전송하고, 오류를 주입할 수 있음 |
| `src/RawFrameViewer.Viewer` | WPF(MVVM) 뷰어. 수신, 표시, W/L, 측정, 이상 프레임 감지 |
| `tests/RawFrameViewer.Core.Tests` | Core 단위 테스트 (xUnit) |

## 문서

- [프로토콜 명세](docs/protocol.md)
- [AI 활용 기록](docs/ai-log.md)
- 요구사항·테스트 추적표 (작성 예정)

## 개발 환경

- Windows 10/11
- .NET 10 SDK (`global.json`으로 고정)
- Visual Studio 2026 (.NET 데스크톱 개발 워크로드)

## 빌드와 실행

```powershell
dotnet build
dotnet test

# 시뮬레이터 실행 (기본: 127.0.0.1:5000, 2048×2048, 14-bit, 10 fps)
dotnet run --project src/RawFrameViewer.Simulator -- --fps 10
```

뷰어는 작성 중입니다.
