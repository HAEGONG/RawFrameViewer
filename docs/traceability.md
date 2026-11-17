# 요구사항 · 구현 · 검증 추적표

각 요구사항이 **어디에 구현되었고 무엇으로 검증했는지** 한눈에 보기 위한 표입니다.
의료기기 인허가 문서를 흉내 낸 것이 아니라, 이 프로젝트에서 작업을 추적한 방식을 보여 주기 위한 문서입니다.

- 자동 테스트: `tests/RawFrameViewer.Core.Tests` (xUnit, 96개, 모두 통과)
- 실행 검증: 시뮬레이터와 뷰어를 실제 TCP로 연결해 확인한 결과. 수치는 [ai-log.md 측정 메모](ai-log.md#측정-메모)에 기록
- 수동 확인: 사람이 직접 조작해 확인 ([ai-log.md 사람 검토 기록](ai-log.md#사람-검토-기록))

## 1. 프로토콜 (docs/protocol.md)

| ID | 요구사항 | 명세 | 구현 | 자동 테스트 | 실행 검증 | 결과 |
|---|---|---|---|---|---|---|
| R01 | 헤더 36바이트, 필드 오프셋, Little-endian | §2, §3.1 | `FrameHeader.WriteTo/TryParse` | `FrameHeaderTests`: WriteThenParse_RoundTrips, Write_UsesSpecOffsetsAndLittleEndian, Sizes_MatchSpec | — | ✅ |
| R02 | 픽셀은 u16 LE, 행 우선 | §3.2 | `PixelCodec`, `MessageEncoder` | `MessageReaderTests`: Payload_IsLittleEndianRowMajor, Frame_RoundTrips | 10fps 수신 픽셀 범위 확인 | ✅ |
| R03 | 메시지 길이 = 40 + 가로×세로×2 | §3 | `MessageEncoder.GetMessageLength` | EncodedLength_MatchesSpec | 시뮬레이터 로그 8,388,648 bytes | ✅ |
| R04 | CRC-32 (IEEE) 정의 | §4 | `System.IO.Hashing.Crc32` | Crc32_MatchesSpecCheckValue (검증값 0xCBF43926) | — | ✅ |
| R05 | 헤더 검사 순서: Magic → CRC → Version → 필드 | §6.1 ②~⑤ | `FrameHeader.TryParse` | Parse_WrongMagic…, Parse_CorruptedByte… (6), Parse_OtherVersion…, Parse_InvalidField… (10), Parse_NonZeroReserved…, Parse_MaxDimension_IsOk | — | ✅ |
| R06 | 필드 규칙 위반·미지원 버전은 연결 종료 | §6.1 ④⑤ | `MessageReader` → `ProtocolException` | InvalidField_Throws | — | ✅ |
| R07 | 영상 CRC 오류 시 그 프레임만 폐기, 계속 수신 | §6.1 ⑦, §7 F2 | `MessageReader`, `FrameReceiver` | PayloadCorruption_DropsOnlyThatFrame, DetectsAnomalies | F2 주입 → `PayloadCrcError` 1, 다음 프레임 정상 | ✅ |
| R08 | 경계를 잃으면 다음 Magic을 찾아 재동기화 | §6.2, §7 F3 | `MessageReader.FindResyncShift` | HeaderCorruption_ResyncsToNextMessage, LeadingGarbage_IsSkippedExactly (5), PartialMagicInGarbage_IsSkipped, FakeMagic_IsRejectedByHeaderCrc, FindResyncShift_KeepsLastThreeBytes | F3 주입 → 정확히 8,388,648바이트 건너뜀 | ✅ |
| R09 | 재동기화 한도(64MB) 초과 시 연결 종료 | §6.2 ⑤ | `MessageReader` | ResyncLimit_Throws | — | ✅ |
| R10 | 메시지 중간에서 끊기면 오류, 경계에서 끊기면 정상 종료 | §6.1 | `MessageReader.ReadAsync` | EndOfStreamAtBoundary_ReturnsNull, EndOfStreamMidMessage_Throws (3) | — | ✅ |
| R11 | TCP로 잘게 나뉘어 도착해도 조립 | §1 | `MessageReader` (`ReadExactlyAsync`) | FragmentedStream_IsReassembled (1바이트씩) | — | ✅ |
| R12 | FrameId 누락·중복 감지, 연결마다 초기화 | §6.3, §7 F1 | `FrameSequenceTracker` | `ValidationTests` 7개, ServerCloses_ReconnectsAndResetsSequence | F1 주입 → `FramesMissed` 1 | ✅ |
| R13 | CRC 오류 프레임은 누락으로 이중 집계하지 않음 | §6.3 | `FrameReceiver.Handle` | DetectsAnomalies | F2 주입 시 `FramesMissed` 변화 없음 | ✅ |
| R14 | BitsStored 범위 초과 픽셀 경고, 표시는 계속 | §6.4, §7 F6 | `PixelRangeChecker` | PixelRange_* (3) | F6 주입 → 4,096개 픽셀 경고 | ✅ |
| R15 | 3초 수신 없음 → 타임아웃, 재연결 | §6.5, §7 F5 | `FrameReceiver.ReadWithTimeoutAsync` | SilentServer_TimesOutAndReconnects | F5 주입 → 3.0초 후 타임아웃, 1초 후 재연결 | ✅ |
| R16 | 끊김·연결 실패 시 1→2→4→5초 간격 재연결 | §6.5, §7 F4 | `FrameReceiver.RunAsync` | NoServer_KeepsRetrying, ServerCloses_… | F4 주입, 시뮬레이터 강제 종료 후 재시작 | ✅ |
| R17 | Heartbeat 1초 (보낼 프레임이 없을 때) | §5 | `DetectorServer` | Heartbeat_IsReceived (수신 측) | `--fps 0`에서 1.0초 간격 확인 | ✅ |
| R18 | 시뮬레이터는 뷰어 1개만 유지, 새 접속이 교체 | §1 | `DetectorServer` | **없음** | 재접속 2회·교체 시나리오, F5 복구 | ⚠️ 실행 검증만 |

## 2. 뷰어

| ID | 요구사항 | 구현 | 자동 테스트 | 실행·수동 검증 | 결과 |
|---|---|---|---|---|---|
| V01 | 16-bit → 8-bit Window/Level 변환 | `WindowLevel` (LUT) | `WindowLevelTests` 17개 (예시값, 단조성, 반전, Width 0, NaN) | 사용자 조작 확인 | ✅ |
| V02 | 자동 W/L (최소~최대) | `PixelStatistics.MinMax`, `WindowLevel.FromRange` | MinMax_*, FromRange_CoversMinToMax | 사용자 조작 확인 | ✅ |
| V03 | 확대(커서 기준)·이동·화면 맞춤·1:1 | `MainWindow` | 없음 (화면 동작) | 사용자 조작 확인 | ✅ 수동 |
| V04 | 오른쪽 드래그 W/L | `MainViewModel.AdjustWindowLevel` | 없음 | 사용자 조작 확인 | ✅ 수동 |
| V05 | 커서 위치 픽셀 원본 값 표시 | `MainViewModel.UpdateCursor` | 없음 | 사용자 조작 확인 | ✅ 수동 |
| V06 | 거리 측정 (px) | `MainViewModel.SetMeasurement` | 없음 | 화면 644px @36% → 1,794.1px (계산값 1,794) | ✅ 실행 |
| V07 | ROI 통계 (개수·평균·표준편차·최소·최대) | `RoiStatistics` | `MeasurementTests` 5개 (손 계산 값) | 원 내부: 시뮬레이터 이론값과 일치 | ✅ |
| V08 | 히스토그램 + 현재 Window 범위 표시 | `Histogram`, `MainViewModel.UpdateHistogram` | Histogram_* 7개 | 실행 화면 확인 | ✅ |
| V09 | 연결 상태·통계·이벤트 로그 표시 | `MainViewModel`, `FrameReceiver` 이벤트 | `FrameReceiverTests` | 오류 주입 F1~F6 화면 확인 | ✅ |
| V10 | 연결이 끊기면 "멈춘 영상"임을 표시 | `MainViewModel.IsShowingStaleImage` | 없음 | 시뮬레이터 종료 시 경고 띠 표시, 수신 중에는 숨김 | ✅ 실행 |
| V11 | 잘못된 호스트·포트 입력 시 멈추지 않고 알림 | `FrameReceiver` 생성자, `MainViewModel.StartReceiver` | InvalidPort_Throws (4), EmptyHost_Throws (2) | 포트 70000 입력 → 오류 로그, 앱 유지 | ✅ |
| V12 | 화면이 느려도 최신 프레임만 그림 | `MainViewModel.OnFrameReceived` | 없음 | 부하 시험에서 수신 FPS = 표시 FPS | ✅ 실행 |

## 3. 성능 (Release 빌드, 같은 PC)

| ID | 목표 | 측정값 | 결과 |
|---|---|---|---|
| P01 | 2048² @10fps 빠짐없이 표시 | 수신·표시 10/10, 그리기 1.5ms | ✅ |
| P02 | 2048² @30fps 빠짐없이 표시 | 수신·표시 28.5/28.5 (시뮬레이터가 28~29fps만 생성) | ✅ |
| P03 | 3072² @15fps 빠짐없이 표시 | 수신·표시 14~15, 그리기 3.3~4.5ms, 할당 286MB/s | ✅ |

## 알려진 공백

- **시뮬레이터 자동 테스트 없음** (R18, 오류 주입 `FaultInjector.Parse` 등): 실제 실행으로만 확인했다. 시뮬레이터 로직을 라이브러리로 분리하면 테스트할 수 있다.
- **뷰어 화면 동작 자동 테스트 없음** (V03~V06, V10): ViewModel 단위 테스트는 WPF Dispatcher가 필요해 이번 범위에서 제외했다.
- **픽셀 배열 풀링 미적용**: Gen2 GC가 초당 4회 발생 ([ai-log #6](ai-log.md)). 현재 표시 성능에는 문제없음.
