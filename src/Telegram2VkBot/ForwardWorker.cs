using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Options;
using Telegram.Bot.Polling;

namespace Telegram2VkBot;

public sealed class ForwardWorker : BackgroundService
{
    private static readonly TimeSpan AlbumDebounce = TimeSpan.FromMilliseconds(950);

    private readonly TelegramOptions _telegram;
    private readonly VkOptions _vk;
    private readonly VkApiClient _vkApi;
    private readonly MappingRepository _repo;
    private readonly ILogger<ForwardWorker> _logger;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly ConcurrentDictionary<(long ChatId, string MediaGroupId), AlbumBuffer> _albumBuffers = new();

    private CancellationToken _appStopping;

    public ForwardWorker(
        IOptions<TelegramOptions> telegram,
        IOptions<VkOptions> vk,
        VkApiClient vkApi,
        MappingRepository repo,
        ILogger<ForwardWorker> logger)
    {
        _telegram = telegram.Value;
        _vk = vk.Value;
        _vkApi = vkApi;
        _repo = repo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appStopping = stoppingToken;

        if (string.IsNullOrWhiteSpace(_telegram.BotToken))
            throw new ArgumentException("TELEGRAM__BotToken (или TELEGRAM:BotToken) пуст — проверьте .env / переменные окружения.");
        if (_telegram.ChannelId == 0)
            throw new ArgumentException("TELEGRAM__ChannelId должен быть ненулевым (пример: -1001234567890).");
        if (string.IsNullOrWhiteSpace(_vk.AccessToken))
            throw new ArgumentException("VK__AccessToken пуст — проверьте .env / переменные окружения.");
        if (_vk.GroupId == 0)
            throw new ArgumentException("VK__GroupId должен быть ненулевым (id сообщества без минуса).");

        _logger.LogInformation("Init database");
        await _repo.InitializeAsync(stoppingToken);

        var bot = new TelegramBotClient(_telegram.BotToken);
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[]
            {
                UpdateType.ChannelPost,
                UpdateType.EditedChannelPost
            }
        };

        _logger.LogInformation("Start Telegram polling for channel {ChannelId}", _telegram.ChannelId);

        bot.StartReceiving(
            async (client, update, ct) =>
            {
                await _updateGate.WaitAsync(ct);
                try
                {
                    await HandleUpdateAsync(update, bot, ct);
                }
                finally
                {
                    _updateGate.Release();
                }
            },
            HandleErrorAsync,
            receiverOptions,
            stoppingToken);

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    public override void Dispose()
    {
        _updateGate.Dispose();
        foreach (var kv in _albumBuffers)
        {
            kv.Value.DebounceCts?.Cancel();
            kv.Value.DebounceCts?.Dispose();
        }

        _albumBuffers.Clear();
        base.Dispose();
    }

    private async Task HandleUpdateAsync(Update update, TelegramBotClient bot, CancellationToken ct)
    {
        if (update.EditedChannelPost is { } edited && edited.Chat.Id == _telegram.ChannelId)
        {
            await HandleChannelPostEditAsync(bot, edited, ct);
            return;
        }

        if (update.ChannelPost is { } msg && msg.Chat.Id == _telegram.ChannelId)
        {
            await HandleChannelPostAsync(bot, msg, ct);
            return;
        }
    }

    private async Task HandleChannelPostAsync(TelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.MediaGroupId is { } groupId)
        {
            EnqueueAlbumPartAndScheduleFlush(bot, message, groupId);
            return;
        }

        await PostSingleChannelMessageAsync(bot, message, ct);
    }

    private void EnqueueAlbumPartAndScheduleFlush(TelegramBotClient bot, Message message, string mediaGroupId)
    {
        var key = (message.Chat.Id, mediaGroupId);
        var buffer = _albumBuffers.GetOrAdd(key, _ => new AlbumBuffer());

        lock (buffer.Sync)
        {
            if (buffer.Messages.TrueForAll(m => m.MessageId != message.MessageId))
                buffer.Messages.Add(message);

            buffer.DebounceCts?.Cancel();
            buffer.DebounceCts?.Dispose();
            buffer.DebounceCts = CancellationTokenSource.CreateLinkedTokenSource(_appStopping);
            var debounceToken = buffer.DebounceCts.Token;

            _logger.LogDebug(
                "Альбом media_group_id={MediaGroupId}: частей в буфере={Count}",
                mediaGroupId,
                buffer.Messages.Count);

            _ = FlushAlbumAfterDebounceAsync(bot, key, debounceToken);
        }
    }

    private async Task FlushAlbumAfterDebounceAsync(
        TelegramBotClient bot,
        (long ChatId, string MediaGroupId) key,
        CancellationToken debounceToken)
    {
        try
        {
            await Task.Delay(AlbumDebounce, debounceToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _updateGate.WaitAsync(debounceToken).ConfigureAwait(false);
        try
        {
            await FlushAlbumBufferCoreAsync(bot, key, debounceToken).ConfigureAwait(false);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private async Task FlushAlbumBufferCoreAsync(
        TelegramBotClient bot,
        (long ChatId, string MediaGroupId) key,
        CancellationToken ct)
    {
        if (!_albumBuffers.TryRemove(key, out var buffer))
            return;

        buffer.DebounceCts?.Dispose();
        buffer.DebounceCts = null;

        List<Message> batch;
        lock (buffer.Sync)
        {
            batch = buffer.Messages.OrderBy(m => m.MessageId).ToList();
        }

        if (batch.Count == 0)
            return;

        _logger.LogInformation(
            "Альбом media_group_id={MediaGroupId}: публикация одного поста ВК по {Count} частям Telegram (message ids: {Ids})",
            key.MediaGroupId,
            batch.Count,
            string.Join(",", batch.Select(m => m.MessageId)));

        await PostChannelMessagesAsSingleVkPostAsync(bot, batch, ct);
    }

    private async Task PostSingleChannelMessageAsync(TelegramBotClient bot, Message message, CancellationToken ct)
    {
        var ownerId = -Math.Abs(_vk.GroupId);
        var telegramMessageId = message.MessageId;

        var text = ExtractTelegramText(message);
        var attachments = await ExtractPhotoAttachmentsFromMessagesAsync(bot, new[] { message }, ct);

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(attachments))
        {
            _logger.LogInformation("Skip message {MessageId}: no text/photo", telegramMessageId);
            return;
        }

        _logger.LogInformation(
            "VK wall.post (new): TelegramMessageId={TelegramMessageId}, owner_id={OwnerId}, textLen={TextLen}, textPreview={TextPreview}, attachments={Attachments}",
            telegramMessageId,
            ownerId,
            (text ?? string.Empty).Length,
            TruncateForLog(text, 200),
            attachments ?? "(none)");

        var postId = await _vkApi.WallPostAsync(ownerId, text ?? string.Empty, attachments, ct);
        await _repo.UpsertAsync(
            _telegram.ChannelId,
            telegramMessageId,
            ownerId,
            postId,
            text,
            attachments,
            ct);

        _logger.LogInformation("Posted new VK post {PostId} for Telegram {MessageId}", postId, telegramMessageId);
    }

    private async Task PostChannelMessagesAsSingleVkPostAsync(TelegramBotClient bot, IReadOnlyList<Message> batch, CancellationToken ct)
    {
        var ownerId = -Math.Abs(_vk.GroupId);
        var text = ExtractTelegramTextFromAlbum(batch);
        var attachments = await ExtractPhotoAttachmentsFromMessagesAsync(bot, batch, ct);

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(attachments))
        {
            _logger.LogInformation("Skip album: no text/photo after merge (ids {Ids})", string.Join(",", batch.Select(m => m.MessageId)));
            return;
        }

        _logger.LogInformation(
            "VK wall.post (альбом): owner_id={OwnerId}, частей={Count}, textLen={TextLen}, textPreview={TextPreview}, attachments={Attachments}",
            ownerId,
            batch.Count,
            (text ?? string.Empty).Length,
            TruncateForLog(text, 200),
            attachments ?? "(none)");

        var postId = await _vkApi.WallPostAsync(ownerId, text ?? string.Empty, attachments, ct);

        foreach (var msg in batch)
        {
            await _repo.UpsertAsync(
                _telegram.ChannelId,
                msg.MessageId,
                ownerId,
                postId,
                text,
                attachments,
                ct);
        }

        _logger.LogInformation(
            "Posted single VK post {PostId} for Telegram album media_group_id={Group} (message ids {Ids})",
            postId,
            batch[0].MediaGroupId,
            string.Join(",", batch.Select(m => m.MessageId)));
    }

    private async Task HandleChannelPostEditAsync(TelegramBotClient bot, Message editedMessage, CancellationToken ct)
    {
        var telegramMessageId = editedMessage.MessageId;
        var ownerId = -Math.Abs(_vk.GroupId);

        var existing = await _repo.GetAsync(_telegram.ChannelId, telegramMessageId, ct);

        var extractedText = ExtractTelegramText(editedMessage);

        var extractedAttachments = await ExtractPhotoAttachmentsFromMessagesAsync(bot, new[] { editedMessage }, ct);

        // У апдейта альбома в Telegram приходит одно сообщение с одной фото; не затираем все вложения ВК одним файлом.
        if (editedMessage.MediaGroupId is not null && existing is { VkAttachments: { } prevAtt })
        {
            if (CountAttachmentTokens(prevAtt) > CountAttachmentTokens(extractedAttachments))
                extractedAttachments = prevAtt;
        }

        var attachmentsToSend = !string.IsNullOrWhiteSpace(extractedAttachments)
            ? extractedAttachments
            : existing?.VkAttachments;

        var messageToSend = !string.IsNullOrWhiteSpace(extractedText)
            ? extractedText
            : existing?.VkMessage;

        if (existing == null)
        {
            _logger.LogWarning("Mapping not found for edited Telegram {MessageId}; posting new VK post", telegramMessageId);
            if (string.IsNullOrWhiteSpace(messageToSend) && string.IsNullOrWhiteSpace(attachmentsToSend))
                return;

            _logger.LogInformation(
                "VK wall.post (after edit, no mapping): TelegramMessageId={TelegramMessageId}, owner_id={OwnerId}, textLen={TextLen}, textPreview={TextPreview}, attachments={Attachments}",
                telegramMessageId,
                ownerId,
                (messageToSend ?? string.Empty).Length,
                TruncateForLog(messageToSend, 200),
                attachmentsToSend ?? "(none)");

            var newPostId = await _vkApi.WallPostAsync(ownerId, messageToSend ?? string.Empty, attachmentsToSend, ct);
            await _repo.UpsertAsync(_telegram.ChannelId, telegramMessageId, ownerId, newPostId, messageToSend, attachmentsToSend, ct);
            return;
        }

        if (messageToSend == null) messageToSend = existing.Value.VkMessage;
        if (attachmentsToSend == null) attachmentsToSend = existing.Value.VkAttachments;

        var messageToSendFinal = messageToSend ?? string.Empty;

        _logger.LogInformation(
            "VK wall.edit: TelegramMessageId={TelegramMessageId}, owner_id={OwnerId}, post_id={PostId}, textLen={TextLen}, textPreview={TextPreview}, attachments={Attachments}",
            telegramMessageId,
            existing.Value.VkOwnerId,
            existing.Value.VkPostId,
            messageToSendFinal.Length,
            TruncateForLog(messageToSendFinal, 200),
            attachmentsToSend ?? "(none)");

        await _vkApi.WallEditAsync(existing.Value.VkOwnerId, existing.Value.VkPostId, messageToSendFinal, attachmentsToSend, ct);

        var idsToTouch = await _repo.GetTelegramMessageIdsForVkPostAsync(_telegram.ChannelId, existing.Value.VkPostId, ct);
        if (idsToTouch.Count == 0)
            idsToTouch = new[] { telegramMessageId };

        foreach (var mid in idsToTouch)
        {
            await _repo.UpsertAsync(
                _telegram.ChannelId,
                mid,
                existing.Value.VkOwnerId,
                existing.Value.VkPostId,
                messageToSendFinal,
                attachmentsToSend,
                ct);
        }

        _logger.LogInformation("Edited VK post {PostId} for Telegram {MessageId}", existing.Value.VkPostId, telegramMessageId);
    }

    private static int CountAttachmentTokens(string? vkAttachmentsCsv)
    {
        if (string.IsNullOrWhiteSpace(vkAttachmentsCsv))
            return 0;
        return vkAttachmentsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private static string? ExtractTelegramTextFromAlbum(IReadOnlyList<Message> messages)
    {
        foreach (var message in messages.OrderBy(m => m.MessageId))
        {
            var t = ExtractTelegramText(message);
            if (!string.IsNullOrWhiteSpace(t))
                return t;
        }

        return null;
    }

    private async Task<string?> ExtractPhotoAttachmentsFromMessagesAsync(
        TelegramBotClient bot,
        IReadOnlyList<Message> messages,
        CancellationToken ct)
    {
        var ordered = messages.OrderBy(m => m.MessageId).ToList();
        var parts = new List<string>();

        foreach (var message in ordered)
        {
            if (message.Photo is not { Length: > 0 })
                continue;

            var best = message.Photo[^1];
            await using var ms = new MemoryStream();
            await bot.GetInfoAndDownloadFile(best.FileId, ms, ct).ConfigureAwait(false);
            var att = await _vkApi.UploadPhotoAndGetAttachmentAsync(ms.ToArray(), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(att))
                parts.Add(att);
        }

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static string? ExtractTelegramText(Message message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
            return message.Text;
        if (!string.IsNullOrWhiteSpace(message.Caption))
            return message.Caption;
        return null;
    }

    private static Task HandleErrorAsync(
        ITelegramBotClient client,
        Exception exception,
        HandleErrorSource errorSource,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static string TruncateForLog(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Length <= maxLen)
            return text;
        return string.Concat(text.AsSpan(0, maxLen), "…(len=", text.Length.ToString(), ")");
    }

    private sealed class AlbumBuffer
    {
        public object Sync { get; } = new();
        public List<Message> Messages { get; } = new();
        public CancellationTokenSource? DebounceCts;
    }
}
