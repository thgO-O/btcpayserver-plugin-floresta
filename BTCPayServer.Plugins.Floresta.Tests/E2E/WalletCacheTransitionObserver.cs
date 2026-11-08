using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests.E2E;

internal sealed class WalletCacheTransitionObserver(NpgsqlConnection connection, string schema) : IAsyncDisposable
{
    public static async Task<WalletCacheTransitionObserver> StartAsync(string connectionString, string walletId, string[] txIds)
    {
        var connection = new NpgsqlConnection(connectionString);
        var schema = $"e2e_cache_{Guid.NewGuid():N}";
        try
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand($"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.watched (wallet_id text NOT NULL, txid text NOT NULL);
                CREATE TABLE {schema}.transitions (source text NOT NULL, txid text NOT NULL);
                """, connection, transaction))
                await command.ExecuteNonQueryAsync();

            await using (var command = new NpgsqlCommand($"""
                INSERT INTO {schema}.watched SELECT @walletId, unnest(@txIds)
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("walletId", walletId);
                command.Parameters.AddWithValue("txIds", txIds);
                await command.ExecuteNonQueryAsync();
            }

            // Capture persisted transitions, including ones that are repaired
            // between two polling queries. Everything lives in this test DB.
            await using (var command = new NpgsqlCommand($"""
                CREATE FUNCTION {schema}.record_transition() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE deactivated boolean := false;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        deactivated := true;
                    ELSIF TG_TABLE_NAME = 'transactions' THEN
                        deactivated := OLD."RemovedAt" IS NULL AND NEW."RemovedAt" IS NOT NULL;
                    ELSE
                        deactivated := NOT OLD."IsSpent" AND NEW."IsSpent";
                    END IF;
                    IF deactivated AND EXISTS (
                        SELECT 1 FROM {schema}.watched
                        WHERE wallet_id = OLD."WalletId" AND txid = OLD."Txid") THEN
                        INSERT INTO {schema}.transitions VALUES (TG_TABLE_NAME || ':' || TG_OP, OLD."Txid");
                    END IF;
                    RETURN NULL;
                END;
                $$;
                CREATE TRIGGER {schema}_transactions
                    AFTER UPDATE OF "RemovedAt" OR DELETE ON floresta.transactions
                    FOR EACH ROW EXECUTE FUNCTION {schema}.record_transition();
                CREATE TRIGGER {schema}_utxos
                    AFTER UPDATE OF "IsSpent" OR DELETE ON floresta.utxos
                    FOR EACH ROW EXECUTE FUNCTION {schema}.record_transition();
                """, connection, transaction))
                await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return new WalletCacheTransitionObserver(connection, schema);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task AssertNoTransitionsAsync()
    {
        var transitions = new List<string>();
        await using var transaction = await connection.BeginTransactionAsync();
        // Dropping the triggers waits for preceding writes. Read the complete
        // observation window while their table locks are still held.
        await using (var command = new NpgsqlCommand($"""
            DROP TRIGGER {schema}_transactions ON floresta.transactions;
            DROP TRIGGER {schema}_utxos ON floresta.utxos;
            """, connection, transaction))
            await command.ExecuteNonQueryAsync();
        await using (var command = new NpgsqlCommand($"SELECT source, txid FROM {schema}.transitions", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                transitions.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        await transaction.CommitAsync();
        Assert.True(transitions.Count == 0,
            $"Cached payments changed during node data reset: {string.Join(", ", transitions)}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = new NpgsqlCommand($"""
                DROP TRIGGER IF EXISTS {schema}_transactions ON floresta.transactions;
                DROP TRIGGER IF EXISTS {schema}_utxos ON floresta.utxos;
                DROP SCHEMA {schema} CASCADE;
                """, connection);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
