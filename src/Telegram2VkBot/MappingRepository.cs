using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Telegram2VkBot;

public sealed class MappingRepository
{
    private readonly DbOptions _dbOptions;
    private readonly ILogger<MappingRepository> _logger;

    public MappingRepository(IOptions<DbOptions> dbOptions, ILogger<MappingRepository> logger)
    {
        _dbOptions = dbOptions.Value;
        _logger = logger;
    }

    private string ConnectionString => $"Data Source={_dbOptions.Path};Cache=Shared";

    public async Task InitializeAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_dbOptions.Path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var con = new SqliteConnection(ConnectionString);
        await con.OpenAsync(ct);

        var sql = @"
CREATE TABLE IF NOT EXISTS vk_mappings (
    telegram_chat_id INTEGER NOT NULL,
    telegram_message_id INTEGER NOT NULL,
    vk_owner_id INTEGER NOT NULL,
    vk_post_id INTEGER NOT NULL,
    vk_message TEXT NULL,
    vk_attachments TEXT NULL,
    PRIMARY KEY (telegram_chat_id, telegram_message_id)
);";

        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(long VkOwnerId, int VkPostId, string? VkMessage, string? VkAttachments)?> GetAsync(
        long telegramChatId,
        int telegramMessageId,
        CancellationToken ct)
    {
        await using var con = new SqliteConnection(ConnectionString);
        await con.OpenAsync(ct);

        var sql = @"
SELECT vk_owner_id, vk_post_id, vk_message, vk_attachments
FROM vk_mappings
WHERE telegram_chat_id = $telegram_chat_id AND telegram_message_id = $telegram_message_id
LIMIT 1;";

        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$telegram_chat_id", telegramChatId);
        cmd.Parameters.AddWithValue("$telegram_message_id", telegramMessageId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var vkOwnerId = reader.GetInt64(0);
            var vkPostId = reader.GetInt32(1);
            var vkMessage = reader.IsDBNull(2) ? null : reader.GetString(2);
            var vkAttachments = reader.IsDBNull(3) ? null : reader.GetString(3);
            return (vkOwnerId, vkPostId, vkMessage, vkAttachments);
        }

        return null;
    }

    public async Task UpsertAsync(
        long telegramChatId,
        int telegramMessageId,
        long vkOwnerId,
        int vkPostId,
        string? vkMessage,
        string? vkAttachments,
        CancellationToken ct)
    {
        await using var con = new SqliteConnection(ConnectionString);
        await con.OpenAsync(ct);

        var sql = @"
INSERT INTO vk_mappings (
    telegram_chat_id,
    telegram_message_id,
    vk_owner_id,
    vk_post_id,
    vk_message,
    vk_attachments
)
VALUES (
    $telegram_chat_id,
    $telegram_message_id,
    $vk_owner_id,
    $vk_post_id,
    $vk_message,
    $vk_attachments
)
ON CONFLICT (telegram_chat_id, telegram_message_id) DO UPDATE SET
    vk_owner_id = excluded.vk_owner_id,
    vk_post_id = excluded.vk_post_id,
    vk_message = excluded.vk_message,
    vk_attachments = excluded.vk_attachments;";

        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$telegram_chat_id", telegramChatId);
        cmd.Parameters.AddWithValue("$telegram_message_id", telegramMessageId);
        cmd.Parameters.AddWithValue("$vk_owner_id", vkOwnerId);
        cmd.Parameters.AddWithValue("$vk_post_id", vkPostId);
        cmd.Parameters.AddWithValue("$vk_message", (object?)vkMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$vk_attachments", (object?)vkAttachments ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

