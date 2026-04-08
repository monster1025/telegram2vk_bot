namespace Telegram2VkBot;

public sealed class TelegramOptions
{
    public required string BotToken { get; init; }
    public required long ChannelId { get; init; }
    public bool AddReactionAfterRepost { get; init; } = true;
    public string RepostReactionEmoji { get; init; } = "🤖";
    public bool RepostReactionIsBig { get; init; } = false;
}

public sealed class VkOptions
{
    public required string AccessToken { get; init; }
    public required long GroupId { get; init; }
    public string ApiVersion { get; init; } = "5.199";
}

public sealed class DbOptions
{
    public required string Path { get; init; }
}

