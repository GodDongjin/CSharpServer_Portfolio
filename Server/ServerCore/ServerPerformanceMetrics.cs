using System.Diagnostics;
using System.Diagnostics.Metrics;
using MySqlConnector;

namespace ServerCore.Diagnostics;

public static class ServerPerformanceMetrics
{
    public const string MeterName = "CSharp.ServerCore";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Histogram<double> SessionQueueWait = Meter.CreateHistogram<double>("csharp.session.job_queue.wait", "ms");
    private static readonly Histogram<double> HandlerDuration = Meter.CreateHistogram<double>("csharp.packet.handler.duration", "ms");
    private static readonly Histogram<double> SendEnqueueDuration = Meter.CreateHistogram<double>("csharp.packet.send_enqueue.duration", "ms");
    private static readonly Histogram<double> RequestToAckEnqueue = Meter.CreateHistogram<double>("csharp.packet.request_to_ack_enqueue.duration", "ms");
    private static readonly Histogram<double> DbQueueWait = Meter.CreateHistogram<double>("csharp.db.job_queue.wait", "ms");
    private static readonly Histogram<double> DbConnectionAcquire = Meter.CreateHistogram<double>("csharp.db.connection.acquire.duration", "ms");
    private static readonly Histogram<double> DbOperationDuration = Meter.CreateHistogram<double>("csharp.db.operation.duration", "ms");
    private static readonly Histogram<double> DbCommandExecuteDuration = Meter.CreateHistogram<double>("csharp.db.command.execute.duration", "ms");
    private static readonly Histogram<double> DbResultProcessingDuration = Meter.CreateHistogram<double>("csharp.db.result_processing.duration", "ms");

    private static readonly AsyncLocal<PacketTimingContext?> PacketContext = new();
    private static readonly AsyncLocal<DbTimingContext?> DatabaseContext = new();

    public static PacketTimingScope BeginPacket(ushort packetId, long requestStartTimestamp, long queueEnqueuedTimestamp)
    {
        PacketTimingContext? previous = PacketContext.Value;
        var context = new PacketTimingContext(packetId, requestStartTimestamp);
        PacketContext.Value = context;

        double queueWaitMs = Stopwatch.GetElapsedTime(queueEnqueuedTimestamp).TotalMilliseconds;
        RecordPacket(SessionQueueWait, queueWaitMs, packetId, "started");
        return new PacketTimingScope(context, previous, queueWaitMs);
    }

    public static void MarkAckSendEnqueued(long sendStartTimestamp)
    {
        PacketTimingContext? context = PacketContext.Value;
        if (context is null)
            return;

        long completedTimestamp = Stopwatch.GetTimestamp();
        context.AckEnqueueTimestamp = completedTimestamp;
        context.SendEnqueueMs = Stopwatch.GetElapsedTime(sendStartTimestamp, completedTimestamp).TotalMilliseconds;
    }

    internal static DbTimingScope BeginDbOperation(int workerId, string jobName)
    {
        DbTimingContext? previous = DatabaseContext.Value;
        var context = new DbTimingContext(workerId, jobName);
        DatabaseContext.Value = context;
        return new DbTimingScope(previous);
    }

    public static ResultProcessingScope MeasureDbResultProcessing() => new(DatabaseContext.Value);

    public static async Task<MySqlDataReader> ExecuteReaderAsync(
        MySqlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        DbTimingContext? context = DatabaseContext.Value;
        long startTimestamp = Stopwatch.GetTimestamp();
        string outcome = "success";

        try
        {
            return await command.ExecuteReaderAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        catch
        {
            outcome = "error";
            throw;
        }
        finally
        {
            if (context is not null)
            {
                DbCommandExecuteDuration.Record(
                    Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    CreateDbTags(context.WorkerId, context.JobName, outcome));
            }
        }
    }

    internal static void RecordDbJob(int workerId, string jobName, string outcome,
        double queueWaitMs, double connectionAcquireMs, double operationMs)
    {
        TagList tags = CreateDbTags(workerId, jobName, outcome);
        DbQueueWait.Record(queueWaitMs, tags);
        DbConnectionAcquire.Record(connectionAcquireMs, tags);
        DbOperationDuration.Record(operationMs, tags);
    }

    private static void RecordPacket(Histogram<double> instrument, double value, ushort packetId, string outcome)
    {
        TagList tags = default;
        tags.Add("packet.id", packetId);
        tags.Add("outcome", outcome);
        instrument.Record(value, tags);
    }

    private static TagList CreateDbTags(int workerId, string jobName, string outcome)
    {
        TagList tags = default;
        tags.Add("db.worker.id", workerId);
        tags.Add("db.job", jobName);
        tags.Add("outcome", outcome);
        return tags;
    }

    internal sealed class PacketTimingContext
    {
        public PacketTimingContext(ushort packetId, long requestStartTimestamp)
        {
            PacketId = packetId;
            RequestStartTimestamp = requestStartTimestamp;
        }

        public ushort PacketId { get; }
        public long RequestStartTimestamp { get; }
        public long? AckEnqueueTimestamp { get; set; }
        public double SendEnqueueMs { get; set; }
    }

    internal sealed class DbTimingContext
    {
        public DbTimingContext(int workerId, string jobName)
        {
            WorkerId = workerId;
            JobName = jobName;
        }

        public int WorkerId { get; }
        public string JobName { get; }
    }

    public sealed class PacketTimingScope : IDisposable
    {
        private readonly PacketTimingContext _context;
        private readonly PacketTimingContext? _previous;
        private readonly long _handlerStartTimestamp = Stopwatch.GetTimestamp();
        private bool _disposed;

        internal PacketTimingScope(PacketTimingContext context, PacketTimingContext? previous, double queueWaitMs)
        {
            _context = context;
            _previous = previous;
            QueueWaitMs = queueWaitMs;
        }

        public double QueueWaitMs { get; }
        public double HandlerMs { get; private set; }
        public double? RequestToAckMs => _context.AckEnqueueTimestamp is long timestamp
            ? Stopwatch.GetElapsedTime(_context.RequestStartTimestamp, timestamp).TotalMilliseconds
            : null;
        public double? SendEnqueueMs => _context.AckEnqueueTimestamp.HasValue ? _context.SendEnqueueMs : null;
        public string Outcome { get; set; } = "success";

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            HandlerMs = Stopwatch.GetElapsedTime(_handlerStartTimestamp).TotalMilliseconds;
            RecordPacket(HandlerDuration, HandlerMs, _context.PacketId, Outcome);

            if (_context.AckEnqueueTimestamp is long ackTimestamp)
            {
                RecordPacket(SendEnqueueDuration, _context.SendEnqueueMs, _context.PacketId, Outcome);
                RecordPacket(RequestToAckEnqueue,
                    Stopwatch.GetElapsedTime(_context.RequestStartTimestamp, ackTimestamp).TotalMilliseconds,
                    _context.PacketId, Outcome);
            }

            PacketContext.Value = _previous;
        }
    }

    internal sealed class DbTimingScope : IDisposable
    {
        private readonly DbTimingContext? _previous;
        internal DbTimingScope(DbTimingContext? previous) => _previous = previous;
        public void Dispose() => DatabaseContext.Value = _previous;
    }

    public readonly struct ResultProcessingScope : IDisposable
    {
        private readonly DbTimingContext? _context;
        private readonly long _startTimestamp;

        internal ResultProcessingScope(DbTimingContext? context)
        {
            _context = context;
            _startTimestamp = context is null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_context is null)
                return;

            DbResultProcessingDuration.Record(
                Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds,
                CreateDbTags(_context.WorkerId, _context.JobName, "observed"));
        }
    }
}
