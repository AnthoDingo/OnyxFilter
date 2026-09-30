using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OnyxFilter.Services.Statistics;

// Persistance SQLite des statistiques DNS dans la base applicative existante (DefaultConnection,
// OnyxFilter.db) : une ligne par tranche horaire, payload sérialisé en JSON. La table est créée ici
// (CREATE TABLE IF NOT EXISTS) et reste volontairement hors du modèle EF Core : les migrations EF ne la
// connaissent pas et ne la modifieront jamais. Accès via Microsoft.Data.Sqlite (référencé transitivement
// par Microsoft.EntityFrameworkCore.Sqlite), plus léger qu'un DbContext pour ce besoin.
public sealed class SqliteDnsStatisticsRepository : IDnsStatisticsRepository
{
    private const string TableName = "DnsStatisticsBuckets";

    private readonly string connectionString;
    private readonly ILogger<SqliteDnsStatisticsRepository> logger;

    public SqliteDnsStatisticsRepository(IConfiguration configuration, ILogger<SqliteDnsStatisticsRepository> logger)
    {
        string? configuredConnectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrEmpty(configuredConnectionString))
        {
            throw new InvalidOperationException("Chaîne de connexion 'DefaultConnection' introuvable : impossible de persister les statistiques DNS.");
        }

        this.connectionString = configuredConnectionString;
        this.logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE IF NOT EXISTS {TableName} (HourKey INTEGER NOT NULL PRIMARY KEY, Payload TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DnsStatisticsBucketRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        List<DnsStatisticsBucketRecord> records = new List<DnsStatisticsBucketRecord>();

        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT HourKey, Payload FROM {TableName};";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            long hourKey = reader.GetInt64(0);
            string payload = reader.GetString(1);

            try
            {
                DnsStatisticsBucketRecord? record = JsonSerializer.Deserialize<DnsStatisticsBucketRecord>(payload);

                if (record is not null)
                {
                    // La colonne HourKey fait foi (clé primaire), pas la copie éventuelle dans le JSON.
                    record.HourKey = hourKey;
                    records.Add(record);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Tranche de statistiques DNS illisible en base (HourKey={HourKey}) : ignorée.", hourKey);
            }
        }

        return records;
    }

    public async Task SaveAsync(IReadOnlyList<DnsStatisticsBucketRecord> records, CancellationToken cancellationToken)
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
        command.CommandText = $"INSERT INTO {TableName} (HourKey, Payload) VALUES ($hourKey, $payload) ON CONFLICT(HourKey) DO UPDATE SET Payload = excluded.Payload;";

        SqliteParameter hourKeyParameter = command.Parameters.Add("$hourKey", SqliteType.Integer);
        SqliteParameter payloadParameter = command.Parameters.Add("$payload", SqliteType.Text);

        foreach (DnsStatisticsBucketRecord record in records)
        {
            hourKeyParameter.Value = record.HourKey;
            payloadParameter.Value = JsonSerializer.Serialize(record);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteOlderThanAsync(long cutoffHourKey, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE HourKey < $cutoffHourKey;";
        command.Parameters.AddWithValue("$cutoffHourKey", cutoffHourKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
