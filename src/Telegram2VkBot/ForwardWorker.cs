using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Options;
using Telegram.Bot.Polling;

namespace Telegram2VkBot;

public sealed class ForwardWorker : BackgroundService
{
    private readonly TelegramOptions _telegram;
    private readonly VkOptions _vk;
    private readonly VkApiClient _vkApi;
    private readonly MappingRepository _repo;
    private readonly ILogger<ForwardWorker> _logger;

    public ForwardWorker(
        IOptions<TelegramOptions> telegram,
        IOptions<VkOptions> vk,
        IOptions<DbOptions> db,
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
        if (string.IsNullOrWhiteSpace(_telegram.BotToken))
            throw new ArgumentException("TELEGRAM__BOT_TOKEN is empty");
        if (_telegram.ChannelId == 0)
            throw new ArgumentException("TELEGRAM__CHANNEL_ID must be non-zero (example: -1001234567890)");
        if (string.IsNullOrWhiteSpace(_vk.AccessToken))
            throw new ArgumentException("VK__ACCESS_TOKEN is empty");
        if (_vk.GroupId == 0)
            throw new ArgumentException("VK__GROUP_ID must be non-zero (community id without minus)");

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

        using var semaphore = new SemaphoreSlim(1, 1);

        bot.StartReceiving(
            async (client, update, ct) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    await HandleUpdateAsync(update, bot, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            },
            HandleErrorAsync,
            receiverOptions,
            stoppingToken);

        // StartReceiving не блокирует, поэтому держим сервис живым.
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
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
        var ownerId = -Math.Abs(_vk.GroupId);
        var telegramMessageId = message.MessageId;

        var text = ExtractTelegramText(message);
        var attachments = await ExtractPhotoAttachmentIfAnyAsync(bot, message, ct);

        // Если это не текст и не фото, просто пропускаем (сложно маппить видео/документы в VK без доп. логики).
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(attachments))
        {
            _logger.LogInformation("Skip message {MessageId}: no text/photo", telegramMessageId);
            return;
        }

        var postId = await _vkApi.WallPostAsync(ownerId, text ?? string.Empty, attachments, ct);
        await _repo.UpsertAsync(
            telegramChatId: _telegram.ChannelId,
            telegramMessageId: telegramMessageId,
            vkOwnerId: ownerId,
            vkPostId: postId,
            vkMessage: text,
            vkAttachments: attachments,
            ct);

        _logger.LogInformation("Posted new VK post {PostId} for Telegram {MessageId}", postId, telegramMessageId);
    }

    private async Task HandleChannelPostEditAsync(TelegramBotClient bot, Message editedMessage, CancellationToken ct)
    {
        var telegramMessageId = editedMessage.MessageId;
        var ownerId = -Math.Abs(_vk.GroupId);

        var existing = await _repo.GetAsync(_telegram.ChannelId, telegramMessageId, ct);

        var extractedText = ExtractTelegramText(editedMessage);

        // Если фото в апдейте отсутствует, то оставляем старые вложения, чтобы не "стереть" медиа на стороне VK.
        string? extractedAttachments = await ExtractPhotoAttachmentIfAnyAsync(bot, editedMessage, ct);
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

            var newPostId = await _vkApi.WallPostAsync(ownerId, messageToSend ?? string.Empty, attachmentsToSend, ct);
            await _repo.UpsertAsync(_telegram.ChannelId, telegramMessageId, ownerId, newPostId, messageToSend, attachmentsToSend, ct);
            return;
        }

        if (messageToSend == null) messageToSend = existing.Value.VkMessage;
        if (attachmentsToSend == null) attachmentsToSend = existing.Value.VkAttachments;

        var messageToSendFinal = messageToSend ?? string.Empty;

        await _vkApi.WallEditAsync(existing.Value.VkOwnerId, existing.Value.VkPostId, messageToSendFinal, attachmentsToSend, ct);
        await _repo.UpsertAsync(_telegram.ChannelId, telegramMessageId, existing.Value.VkOwnerId, existing.Value.VkPostId, messageToSendFinal, attachmentsToSend, ct);

        _logger.LogInformation("Edited VK post {PostId} for Telegram {MessageId}", existing.Value.VkPostId, telegramMessageId);
    }

    private static string? ExtractTelegramText(Message message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
            return message.Text;
        if (!string.IsNullOrWhiteSpace(message.Caption))
            return message.Caption;
        return null;
    }

    private async Task<string?> ExtractPhotoAttachmentIfAnyAsync(TelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.Photo == null || message.Photo.Length == 0)
            return null;

        // Telegram предоставляет несколько размеров одного фото. Берем самый большой.
        var best = message.Photo[^1];

        await using var ms = new MemoryStream();
        // В Telegram.Bot v22 методы называются без Async-суффикса.
        await bot.GetInfoAndDownloadFile(best.FileId, ms, ct);

        return await _vkApi.UploadPhotoAndGetAttachmentAsync(ms.ToArray(), ct);
    }

    private static Task HandleErrorAsync(
        ITelegramBotClient client,
        Exception exception,
        HandleErrorSource errorSource,
        CancellationToken cancellationToken)
    {
        // Логирование делаем в Worker через исключения, но чтобы не терять ошибки polling'а — просто возвращаем CompletedTask.
        return Task.CompletedTask;
    }
}

