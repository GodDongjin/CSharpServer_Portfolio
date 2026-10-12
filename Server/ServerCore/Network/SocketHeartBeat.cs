using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/*
 * ==========================================
 *          class SocketHeartBeat
 *  - 소켓 연결 상태 확인 클래스
 *  - 주기적으로 핑을 보내 소켓이 정상 연결중인지 확인하는 기능.
 * ==========================================
 */

namespace ServerCore.Network
{
    public class SocketHeartBeat
    {
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    // Session 참조와 등록 당시 connectionId를 함께 가져온다.
                    foreach (KeyValuePair<Int64, Session> kvp in SessionManager.GetSessionAndConnection())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            kvp.Value.CheckHeartbeat(kvp.Key);
                        }
                        catch (Exception exception)
                        {
                            Console.Error.WriteLine($"Heartbeat 검사 실패: sessionId={kvp.Key}, {exception}");
                            kvp.Value.Disconnect(kvp.Key);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                // 서버 종료로 검사 작업을 중단한다.
            }
        }
    }
}
