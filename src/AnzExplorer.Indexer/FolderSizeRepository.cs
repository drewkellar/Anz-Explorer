using System.Globalization;
using AnzExplorer.Contracts;
using Microsoft.Data.Sqlite;

namespace AnzExplorer.Indexer;

internal sealed class FolderSizeRepository
{
    private readonly string _connectionString;

    public FolderSizeRepository(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS folder_sizes (
                path TEXT PRIMARY KEY COLLATE NOCASE,
                logical_bytes INTEGER NULL,
                state INTEGER NOT NULL,
                updated_utc TEXT NULL,
                error_summary TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FolderSizeResult?> GetAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT logical_bytes, state, updated_utc, error_summary
            FROM folder_sizes WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new FolderSizeResult(
            path,
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            (FolderSizeState)reader.GetInt32(1),
            reader.IsDBNull(2)
                ? null
                : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public async Task PutAsync(FolderSizeResult result, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO folder_sizes(path, logical_bytes, state, updated_utc, error_summary)
            VALUES($path, $bytes, $state, $updated, $error)
            ON CONFLICT(path) DO UPDATE SET
                logical_bytes = excluded.logical_bytes,
                state = excluded.state,
                updated_utc = excluded.updated_utc,
                error_summary = excluded.error_summary;
            """;
        command.Parameters.AddWithValue("$path", result.Path);
        command.Parameters.AddWithValue("$bytes", result.LogicalBytes is null ? DBNull.Value : result.LogicalBytes.Value);
        command.Parameters.AddWithValue("$state", (int)result.State);
        command.Parameters.AddWithValue("$updated", result.LastUpdated?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", result.ErrorSummary ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

