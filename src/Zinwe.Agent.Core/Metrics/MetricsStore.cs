using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Zinwe.Agent.Core.Metrics;

/// <summary>
/// One rolled-up metric: within an hour <see cref="BucketStart"/>, under a <see cref="Category"/>
/// (e.g. "web.domain", "app.foreground"), the activity for one <see cref="Key"/> (a domain, an app).
/// </summary>
/// <param name="Count">How many times it happened (samples, visits, copies).</param>
/// <param name="Bytes">Bytes involved, when the metric has a size (network, file copies); else 0.</param>
/// <param name="Seconds">Seconds of activity, when the metric is duration-based (foreground time); else 0.</param>
public sealed record MetricRow(
    DateTimeOffset BucketStart, string Category, string Key, long Count, long Bytes, long Seconds);

/// <summary>
/// Local rolled-up usage counters (DataDirectory\metrics.db, SQLite). Feature collectors (internet usage,
/// data transfer...) add into the current hour bucket; a later uploader reads buckets and ships them, exactly
/// as <see cref="Events.EventQueue"/> does for one-off events. Metrics are aggregates, never raw activity logs.
///
/// Bounded by rows (hours x categories x keys): past <see cref="Capacity"/> the oldest buckets are dropped.
/// Thread-safe: every call opens its own connection under one in-process lock.
/// </summary>
public sealed class MetricsStore
{
    public const int DefaultCapacity = 50_000;

    /// <summary>Longest key we store; keeps a runaway domain/app name from bloating the row.</summary>
    public const int MaxKeyLength = 255;

    private readonly string _connectionString;
    private readonly Lock _lock = new();
    private readonly Func<bool>? _recordingAllowed;

    /// <param name="recordingAllowed">
    /// Asked before each sample is stored; false drops it (nothing is recorded until the member has agreed to
    /// monitoring — see <see cref="Work.ConsentGate"/>). Null = always record.
    /// </param>
    public MetricsStore(string dataDirectory, int capacity = DefaultCapacity, Func<bool>? recordingAllowed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _recordingAllowed = recordingAllowed;
        Directory.CreateDirectory(dataDirectory);
        FilePath = Path.Combine(dataDirectory, "metrics.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS metrics (
                bucket_start TEXT    NOT NULL,
                category     TEXT    NOT NULL,
                key          TEXT    NOT NULL,
                count        INTEGER NOT NULL DEFAULT 0,
                bytes        INTEGER NOT NULL DEFAULT 0,
                seconds      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (bucket_start, category, key)
            );
            """);
    }

    public string FilePath { get; }

    public int Capacity { get; }

    /// <summary>The hour a timestamp belongs to (its own UTC offset, minutes/seconds zeroed).</summary>
    public static DateTimeOffset HourBucket(DateTimeOffset at) =>
        new(at.Year, at.Month, at.Day, at.Hour, 0, 0, at.Offset);

    /// <summary>
    /// Adds to the counters for (hour of <paramref name="at"/>, <paramref name="category"/>, <paramref name="key"/>),
    /// creating the row on first use. Returns how many old buckets were dropped to stay within <see cref="Capacity"/>.
    /// </summary>
    public int Add(string category, string key, long count = 1, long bytes = 0, long seconds = 0, DateTimeOffset? at = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > MaxKeyLength)
        {
            key = key[..MaxKeyLength];
        }
        if (_recordingAllowed is { } allowed && !allowed())
        {
            return 0; // recording paused: the member hasn't agreed to monitoring yet
        }
        var bucket = HourBucket(at ?? DateTimeOffset.Now).ToString("O", CultureInfo.InvariantCulture);

        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO metrics (bucket_start, category, key, count, bytes, seconds)
                    VALUES ($bucket, $category, $key, $count, $bytes, $seconds)
                    ON CONFLICT (bucket_start, category, key) DO UPDATE SET
                        count = count + excluded.count,
                        bytes = bytes + excluded.bytes,
                        seconds = seconds + excluded.seconds;
                    """;
                upsert.Parameters.AddWithValue("$bucket", bucket);
                upsert.Parameters.AddWithValue("$category", category);
                upsert.Parameters.AddWithValue("$key", key);
                upsert.Parameters.AddWithValue("$count", count);
                upsert.Parameters.AddWithValue("$bytes", bytes);
                upsert.Parameters.AddWithValue("$seconds", seconds);
                upsert.ExecuteNonQuery();
            }

            int dropped;
            using (var trim = connection.CreateCommand())
            {
                trim.Transaction = tx;
                // Evict whole oldest buckets first, so a half-reported hour never lingers.
                trim.CommandText = """
                    DELETE FROM metrics WHERE bucket_start IN (
                        SELECT bucket_start FROM (
                            SELECT bucket_start FROM metrics
                            GROUP BY bucket_start ORDER BY bucket_start DESC LIMIT -1 OFFSET $capacity
                        )
                    );
                    """;
                trim.Parameters.AddWithValue("$capacity", Capacity);
                dropped = trim.ExecuteNonQuery();
            }

            tx.Commit();
            return dropped;
        }
    }

    /// <summary>All stored rows, oldest bucket first. Nothing is removed.</summary>
    public IReadOnlyList<MetricRow> Snapshot()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT bucket_start, category, key, count, bytes, seconds
                FROM metrics ORDER BY bucket_start, category, key;
                """;

            var rows = new List<MetricRow>();
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MetricRow(
                    DateTimeOffset.ParseExact(reader.GetString(0), "O", CultureInfo.InvariantCulture),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5)));
            }
            return rows;
        }
    }

    /// <summary>Removes every row in buckets at or before <paramref name="through"/> (after a successful upload).</summary>
    public int DeleteThrough(DateTimeOffset through)
    {
        var bucket = HourBucket(through).ToString("O", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            using var connection = Open();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM metrics WHERE bucket_start <= $through;";
            delete.Parameters.AddWithValue("$through", bucket);
            return delete.ExecuteNonQuery();
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM metrics;";
            return Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
