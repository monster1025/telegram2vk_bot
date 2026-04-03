using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VkNet;
using VkNet.Exception;
using VkNet.Model;

namespace Telegram2VkBot;

public sealed class VkApiClient : IDisposable
{
    public const string UploadHttpClientName = "VkUpload";
    private const string VkApiBaseUrl = "https://api.vk.com/method/";
    private const int LogBodyMaxLength = 8192;

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

        // Не задавать свою boundary: нестандартная строка (например «-------0988») часто ломает разбор
        // multipart на стороне VK → в ответе photo остаётся пустым.
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(photoBytes);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(bytes, "photo", "photo.jpg");

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

        // VkNet иногда «висит» на SaveWallPhoto (внутренний RestClient). Тот же запрос через наш HttpClient с таймаутом.
        var (ownerId, photoId) = await SaveWallPhotoHttpAsync(result, groupIdUlong, ct).ConfigureAwait(false);
        if (ownerId == null || photoId == null)
            return null;

        var attachment = $"photo{ownerId.Value}_{photoId.Value}";
        _logger.LogInformation("photos.saveWallPhoto → вложение: {Attachment}", attachment);
        return attachment;
    }

    /// <summary>
    /// Прямой вызов photos.saveWallPhoto (без VkNet): корректно передаёт длинное поле photo из ответа upload-сервера.
    /// </summary>
    private async Task<(long? OwnerId, long? Id)> SaveWallPhotoHttpAsync(
        string uploadResponseJson,
        ulong groupId,
        CancellationToken ct)
    {
        using var uploadDoc = JsonDocument.Parse(uploadResponseJson);
        var root = uploadDoc.RootElement;

        if (!root.TryGetProperty("server", out var serverEl) || !root.TryGetProperty("hash", out var hashEl))
        {
            _logger.LogWarning("Ответ upload-сервера без server/hash");
            return (null, null);
        }

        if (!root.TryGetProperty("photo", out var photoEl))
        {
            _logger.LogWarning("Ответ upload-сервера без photo");
            return (null, null);
        }

        var server = serverEl.ValueKind == JsonValueKind.Number
            ? serverEl.GetInt64().ToString(CultureInfo.InvariantCulture)
            : serverEl.GetString() ?? "";

        var hash = hashEl.ValueKind == JsonValueKind.String
            ? hashEl.GetString() ?? ""
            : hashEl.GetRawText();

        var photoParam = photoEl.ValueKind == JsonValueKind.String
            ? photoEl.GetString() ?? ""
            : photoEl.GetRawText();

        if (string.IsNullOrEmpty(photoParam))
        {
            _logger.LogWarning(
                "Ответ upload-сервера: photo пуст — файл не распознан (multipart). Проверьте загрузку на upload_url.");
            return (null, null);
        }

        _logger.LogInformation(
            "photos.saveWallPhoto → group_id={GroupId}, server={Server}, photoLen={PhotoLen}, hashLen={HashLen}",
            groupId,
            server,
            photoParam.Length,
            hash.Length);

        var query = new Dictionary<string, string>
        {
            ["access_token"] = _options.AccessToken,
            ["v"] = _options.ApiVersion,
            ["group_id"] = groupId.ToString(CultureInfo.InvariantCulture),
            ["server"] = server,
            ["photo"] = photoParam,
            ["hash"] = hash,
        };

        using var content = new FormUrlEncodedContent(query);
        var http = _httpFactory.CreateClient(UploadHttpClientName);
        using var resp = await http
            .PostAsync(VkApiBaseUrl + "photos.saveWallPhoto", content, ct)
            .ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "photos.saveWallPhoto ← HTTP {StatusCode}, body: {Body}",
            (int)resp.StatusCode,
            TruncateForLog(body, LogBodyMaxLength));

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"photos.saveWallPhoto HTTP: {(int)resp.StatusCode} {TruncateForLog(body, 2000)}");

        using var outDoc = JsonDocument.Parse(body);
        var outRoot = outDoc.RootElement;

        if (outRoot.TryGetProperty("error", out var err))
        {
            var code = err.TryGetProperty("error_code", out var ec) ? ec.GetInt32().ToString(CultureInfo.InvariantCulture) : "?";
            var msg = err.TryGetProperty("error_msg", out var em) ? em.GetString() : null;
            throw new InvalidOperationException($"photos.saveWallPhoto VK error: code={code} msg={msg}");
        }

        if (!outRoot.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Array || response.GetArrayLength() == 0)
        {
            _logger.LogWarning("photos.saveWallPhoto: пустой или неожиданный response");
            return (null, null);
        }

        var first = response[0];
        var oid = first.GetProperty("owner_id").GetInt64();
        var pid = first.GetProperty("id").GetInt64();
        return (oid, pid);
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
