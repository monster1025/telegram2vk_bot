using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VkNet;
using VkNet.Exception;
using VkNet.Model;

namespace Telegram2VkBot;

public sealed class VkApiClient : IDisposable
{
    public const string UploadHttpClientName = "VkUpload";
    private const int LogParamMaxLength = 240;
    private const int LogBodyMaxLength = 8192;
    private const string MultipartBoundary = "-------0988";

    private readonly VkOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<VkApiClient> _logger;
    private readonly VkApi _api;

    public VkApiClient(
        IOptions<VkOptions> options,
        IHttpClientFactory httpFactory,
        ILogger<VkApiClient> logger)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _logger = logger;
        _api = new VkApi();
        ApplyApiVersion(_options.ApiVersion);
        _api.Authorize(new ApiAuthParams
        {
            AccessToken = _options.AccessToken,
        });
    }

    public void Dispose() => _api.Dispose();

    public async Task<int> WallPostAsync(
        long ownerId,
        string message,
        string? attachments,
        CancellationToken ct)
    {
        var wallParams = new WallPostParams
        {
            OwnerId = ownerId,
            FromGroup = true,
            Message = message ?? string.Empty,
        };

        var media = ParsePhotoAttachments(attachments);
        if (media != null)
            wallParams.Attachments = media;

        _logger.LogInformation(
            "VkNet Wall.Post request: OwnerId={OwnerId}, FromGroup=true, MessageLen={MessageLen}, MessagePreview={MessagePreview}, Attachments={Attachments}",
            ownerId,
            wallParams.Message.Length,
            TruncateForLog(wallParams.Message, 200),
            attachments ?? "(none)");

        try
        {
            var postId = await _api.Wall.PostAsync(wallParams, ct).ConfigureAwait(false);
            _logger.LogInformation("VkNet Wall.Post response: post_id={PostId}", postId);
            return postId > int.MaxValue ? throw new InvalidOperationException($"post_id слишком большой для хранения: {postId}") : (int)postId;
        }
        catch (VkApiException ex)
        {
            _logger.LogError(ex, "VkNet Wall.Post failed (VK code {ErrorCode}): {Message}", ex.ErrorCode, ex.Message);
            throw;
        }
    }

    public async Task WallEditAsync(
        long ownerId,
        int postId,
        string message,
        string? attachments,
        CancellationToken ct)
    {
        var editParams = new WallEditParams
        {
            OwnerId = ownerId,
            PostId = postId,
            Message = message ?? string.Empty,
        };

        var mediaEdit = ParsePhotoAttachments(attachments);
        if (mediaEdit != null)
            editParams.Attachments = mediaEdit;

        _logger.LogInformation(
            "VkNet Wall.Edit request: OwnerId={OwnerId}, PostId={PostId}, MessageLen={MessageLen}, MessagePreview={MessagePreview}, Attachments={Attachments}",
            ownerId,
            postId,
            editParams.Message.Length,
            TruncateForLog(editParams.Message, 200),
            attachments ?? "(none)");

        try
        {
            await _api.Wall.EditAsync(editParams, ct).ConfigureAwait(false);
            _logger.LogInformation("VkNet Wall.Edit completed for post_id={PostId}", postId);
        }
        catch (VkApiException ex)
        {
            _logger.LogError(ex, "VkNet Wall.Edit failed (VK code {ErrorCode}): {Message}", ex.ErrorCode, ex.Message);
            throw;
        }
    }

    public async Task<string?> UploadPhotoAndGetAttachmentAsync(
        byte[] photoBytes,
        CancellationToken ct)
    {
        var groupId = _options.GroupId;
        var groupIdUlong = groupId > 0 ? (ulong)groupId : throw new ArgumentOutOfRangeException(nameof(groupId));

        var uploadServer = await _api.Photo.GetWallUploadServerAsync(groupId, ct).ConfigureAwait(false);
        var uploadUrl = uploadServer.UploadUrl;
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            _logger.LogWarning("VK GetWallUploadServer: UploadUrl пуст");
            return null;
        }

        _logger.LogInformation(
            "VK upload → POST multipart ({FileName}, {Size} bytes) to {UploadUrl}",
            "photo.jpg",
            photoBytes.Length,
            TruncateForLog(uploadUrl, 512));

        using var form = new MultipartFormDataContent(MultipartBoundary);
        var bytes = new ByteArrayContent(photoBytes);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(bytes, "photo", $"{Guid.NewGuid():N}.jpg");

        var http = _httpFactory.CreateClient(UploadHttpClientName);
        using var uploadResp = await http.PostAsync(uploadUrl, form, ct).ConfigureAwait(false);
        var result = await uploadResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "VK upload ← HTTP {StatusCode}, body: {Body}",
            (int)uploadResp.StatusCode,
            TruncateForLog(result, LogBodyMaxLength));

        if (!uploadResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("VK upload failed: {StatusCode} {Body}", uploadResp.StatusCode, TruncateForLog(result, LogBodyMaxLength));
            return null;
        }

        IReadOnlyCollection<Photo> saved;
        try
        {
            saved = await _api.Photo.SaveWallPhotoAsync(
                    response: result,
                    userId: 0,
                    groupId: groupIdUlong,
                    caption: null,
                    token: ct)
                .ConfigureAwait(false);
        }
        catch (VkApiException ex)
        {
            _logger.LogError(ex, "VkNet Photo.SaveWallPhotoAsync failed (VK code {ErrorCode}): {Message}", ex.ErrorCode, ex.Message);
            throw;
        }

        var photo = saved.FirstOrDefault();
        if (photo == null)
        {
            _logger.LogWarning("VkNet SaveWallPhoto: пустой результат");
            return null;
        }

        var attachment = $"photo{photo.OwnerId}_{photo.Id}";
        _logger.LogInformation("VkNet SaveWallPhoto response: {Attachment}", attachment);
        return attachment;
    }

    private static readonly Regex PhotoAttachmentRegex = new(@"^photo(-?\d+)_(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// VkNet ожидает коллекцию <see cref="MediaAttachment"/>; строка из БД вида photo-123_456 парсится в <see cref="Photo"/>.
    /// </summary>
    private static IEnumerable<MediaAttachment>? ParsePhotoAttachments(string? attachments)
    {
        if (string.IsNullOrWhiteSpace(attachments))
            return null;

        var parts = attachments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<MediaAttachment>(parts.Length);
        foreach (var part in parts)
        {
            var m = PhotoAttachmentRegex.Match(part);
            if (!m.Success)
                throw new InvalidOperationException($"Неподдерживаемый формат вложения для VkNet (ожидается photo{{owner}}_{{id}}): {part}");

            list.Add(new Photo
            {
                OwnerId = long.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                Id = long.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
            });
        }

        return list;
    }

    private void ApplyApiVersion(string version)
    {
        var parts = version.Trim().Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, null, out var major)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, null, out var minor))
        {
            _api.VkApiVersion.SetVersion(major, minor);
        }
    }

    private static string TruncateForLog(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Length <= maxLen)
            return text;
        return string.Concat(text.AsSpan(0, maxLen), "…(truncated, totalLen=", text.Length.ToString(), ")");
    }
}
