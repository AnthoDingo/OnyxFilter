using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OnyxFilter.Services.QueryLog;

// Persistance SQLite du journal des requêtes DNS dans la base applicative existante (DefaultConnection,
// OnyxFilter.db) : une ligne par requête traitée. Table créée ici (CREATE TABLE IF NOT EXISTS) et
// volontairement hors du modèle EF Core, même approche que SqliteDnsStatisticsRepository. Accès via
// Microsoft.Data.Sqlite, plus léger qu'un DbContext pour ce besoin (pas de suivi de changements, pas de
// matérialisation d'entités).
public sealed class SqliteDnsQueryLogRepository : IDnsQueryLogRepository
{
    private const string TableName = "DnsQueryLogEntries";

    private readonly string connectionString;
    private readonly ILogger<SqliteDnsQueryLogRepository> logger;

    public SqliteDnsQueryLogRepository(IConfiguration configuration, ILogger<SqliteDnsQueryLogRepository> logger)
    {
        string? configuredConnectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrEmpty(configuredConnectionString))
        {
            throw new InvalidOperationException("Chaîne de connexion 'DefaultConnection' introuvable : impossible de persister le journal des requêtes DNS.");
        }

        this.connectionString = configuredConnectionString;
        this.logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand createTableCommand = connection.CreateCommand();
        createTableCommand.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {TableName} (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                TimestampTicks INTEGER NOT NULL,
                ClientKey TEXT NOT NULL,
                Domain TEXT NOT NULL,
                QueryType TEXT NOT NULL,
                Blocked INTEGER NOT NULL,
                UpstreamServer TEXT NULL,
                ProcessingTimeMs INTEGER NOT NULL
            );";
        await createTableCommand.ExecuteNonQueryAsync(cancellationToken);

        // Index sur la date : indispensable pour que la pagination (ORDER BY TimestampTicks DESC) et la
        // purge par rétention (WHERE TimestampTicks < ...) restent rapides même avec un journal volumineux,
        // sans jamais nécessiter de balayage complet de la table.
        await using SqliteCommand createIndexCommand = connection.CreateCommand();
        createIndexCommand.CommandText = $"CREATE INDEX IF NOT EXISTS IX_{TableName}_TimestampTicks ON {TableName} (TimestampTicks);";
        await createIndexCommand.ExecuteNonQueryAsync(cancellationToken);

        // Migration : ajoute la colonne Reason si elle n'existe pas encore (tables créées avant cette version).
        // SQLite ne supporte pas ALTER TABLE ... ADD COLUMN IF NOT EXISTS, donc on vérifie via PRAGMA.
        await using SqliteCommand pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = $"PRAGMA table_info({TableName});";
        bool reasonColumnExists = false;

        await using (SqliteDataReader pragmaReader = await pragmaCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await pragmaReader.ReadAsync(cancellationToken))
            {
                if (string.Equals(pragmaReader.GetString(1), "Reason", StringComparison.OrdinalIgnoreCase))
                {
                    reasonColumnExists = true;
                    break;
                }
            }
        }

        if (!reasonColumnExists)
        {
            await using SqliteCommand alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE {TableName} ADD COLUMN Reason TEXT NOT NULL DEFAULT 'Resolved';";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Migration : ajoute la colonne ReasonDetail si elle n'existe pas encore.
        await using SqliteCommand pragma2Command = connection.CreateCommand();
        pragma2Command.CommandText = $"PRAGMA table_info({TableName});";
        bool reasonDetailColumnExists = false;

        await using (SqliteDataReader pragma2Reader = await pragma2Command.ExecuteReaderAsync(cancellationToken))
        {
            while (await pragma2Reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(pragma2Reader.GetString(1), "ReasonDetail", StringComparison.OrdinalIgnoreCase))
                {
                    reasonDetailColumnExists = true;
                    break;
                }
            }
        }

        if (!reasonDetailColumnExists)
        {
            await using SqliteCommand alterCommand2 = connection.CreateCommand();
            alterCommand2.CommandText = $"ALTER TABLE {TableName} ADD COLUMN ReasonDetail TEXT NULL;";
            await alterCommand2.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task InsertBatchAsync(IReadOnlyList<DnsQueryLogRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $@"
            INSERT INTO {TableName} (TimestampTicks, ClientKey, Domain, QueryType, Blocked, UpstreamServer, ProcessingTimeMs, Reason, ReasonDetail)
            VALUES ($timestampTicks, $clientKey, $domain, $queryType, $blocked, $upstreamServer, $processingTimeMs, $reason, $reasonDetail);";

        SqliteParameter timestampParameter = command.Parameters.Add("$timestampTicks", SqliteType.Integer);
        SqliteParameter clientKeyParameter = command.Parameters.Add("$clientKey", SqliteType.Text);
        SqliteParameter domainParameter = command.Parameters.Add("$domain", SqliteType.Text);
        SqliteParameter queryTypeParameter = command.Parameters.Add("$queryType", SqliteType.Text);
        SqliteParameter blockedParameter = command.Parameters.Add("$blocked", SqliteType.Integer);
        SqliteParameter upstreamParameter = command.Parameters.Add("$upstreamServer", SqliteType.Text);
        SqliteParameter processingTimeParameter = command.Parameters.Add("$processingTimeMs", SqliteType.Integer);
        SqliteParameter reasonParameter = command.Parameters.Add("$reason", SqliteType.Text);
        SqliteParameter reasonDetailParameter = command.Parameters.Add("$reasonDetail", SqliteType.Text);

        foreach (DnsQueryLogRecord record in records)
        {
            timestampParameter.Value = record.TimestampUtc.Ticks;
            clientKeyParameter.Value = record.ClientKey;
            domainParameter.Value = record.Domain;
            queryTypeParameter.Value = record.QueryType;
            blockedParameter.Value = record.Blocked ? 1 : 0;
            upstreamParameter.Value = (object?)record.UpstreamServer ?? DBNull.Value;
            processingTimeParameter.Value = record.ProcessingTimeMs;
            reasonParameter.Value = record.Reason.ToString();
            reasonDetailParameter.Value = (object?)record.ReasonDetail ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DnsQueryLogRecord>> QueryPageAsync(int skip, int take, string? searchText, QueryLogReason? reason, CancellationToken cancellationToken)
    {
        List<DnsQueryLogRecord> results = new List<DnsQueryLogRecord>(take);

        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();

        System.Text.StringBuilder sql = new System.Text.StringBuilder();
        sql.Append($"SELECT Id, TimestampTicks, ClientKey, Domain, QueryType, UpstreamServer, ProcessingTimeMs, Reason, ReasonDetail FROM {TableName} WHERE 1=1");

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            sql.Append(" AND (Domain LIKE $search OR ClientKey LIKE $search)");
            command.Parameters.AddWithValue("$search", "%" + searchText.Trim() + "%");
        }

        if (reason.HasValue)
        {
            sql.Append(" AND Reason = $reason");
            command.Parameters.AddWithValue("$reason", reason.Value.ToString());
        }

        sql.Append(" ORDER BY TimestampTicks DESC, Id DESC LIMIT $take OFFSET $skip;");
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);
        command.CommandText = sql.ToString();

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            string reasonText = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);

            results.Add(new DnsQueryLogRecord
            {
                Id = reader.GetInt64(0),
                TimestampUtc = new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                ClientKey = reader.GetString(2),
                Domain = reader.GetString(3),
                QueryType = reader.GetString(4),
                UpstreamServer = reader.IsDBNull(5) ? null : reader.GetString(5),
                ProcessingTimeMs = reader.GetInt64(6),
                Reason = Enum.TryParse(reasonText, out QueryLogReason parsedReason) ? parsedReason : QueryLogReason.Resolved,
                ReasonDetail = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        return results;
    }

    public async Task DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {TableName} WHERE TimestampTicks < $cutoffTicks;";
            command.Parameters.AddWithValue("$cutoffTicks", cutoffUtc.Ticks);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de purger le journal des requêtes DNS en base.");
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Sondage de la base du journal des requêtes DNS en échec.");
            return false;
        }
    }
}
