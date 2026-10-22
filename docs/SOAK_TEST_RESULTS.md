# 장시간 부하 테스트 결과

상태: completed

- 시작: 2026-10-09T12:40:26.500044+09:00
- 종료: 2026-10-09T13:43:34.017617+09:00
- Release/net10.0, 동일 PC의 서버·Docker MySQL/Redis·Python 클라이언트, 동시 60개 연결.
- 목표 500건/초, 조회 80%·저장 20%, 3600초 지속 후 저부하 120초·무부하 60초.
- 조회는 메모리 캐시, 저장은 실제 MySQL 트랜잭션. 클라이언트당 미완료 요청은 최대 1개이며 지연으로 놓친 전송 슬롯을 별도 기록한다.
- p95/p99는 1ms 히스토그램 상한값(10000ms 이상은 마지막 버킷). 목표 도착률을 무조건 유지하는 무제한 큐 부하 생성기는 아니다.
- CPU 누적 사용 시간·Working Set·Private Bytes를 주기적으로 기록. GC 및 서버 내부 작업 큐 깊이는 수집하지 않았다.
- 요청 로그는 5MiB 파일 4개 이내로 순환 보관. 동일 PC 자원 경합이 포함되어 운영 최대 성능으로 해석할 수 없다.

| 단계 | 초 | 성공 | 실패 | 실제 요청/초 | p95 ms | p99 ms | 놓친 슬롯 |
|---|---:|---:|---:|---:|---:|---:|---:|
| sustained_load | 3600.06 | 1799156 | 0 | 499.76 | 76 | 129 | 842 |
| low_load_recovery | 120.97 | 7200 | 0 | 59.52 | 49 | 93 | 0 |

**시작 자원:**

```json
{
  "working_set_mb": 66.73,
  "private_mb": 28.73,
  "cpu_seconds": 0.953
}
```

**무부하 후 자원:**

```json
{
  "working_set_mb": 72.87,
  "private_mb": 32.22,
  "cpu_seconds": 779.281
}
```

**정리·종료:**

```json
{
  "test_rows_remaining": "0",
  "fresh_login_after_recovery": true,
  "minimum_ping_count": 347,
  "server_exit_code": 0,
  "graceful_shutdown_log": null,
  "failure": null
}
```

상세 시계열은 TestResults/SoakReview/run-2026-10-09/timeline.jsonl에 보관한다.  
장시간 메모리 증가나 지연 추세는 시계열과 함께 판단해야 하며, 종료 시점 메모리 두 값만으로 누수 유무를 확정하지 않는다.
