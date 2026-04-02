using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Telegram2VkBot;

public sealed class VkApiClient
{
    private const string VkApiBaseUrl = "https://api.vk.com/method/";
    private const int LogParamMaxLength = 240;
    private const int LogBodyMaxLength = 8192;

    private readonly VkOptions _options;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<VkApiClient> _logger;

    public VkApiClient(IOptions<VkOptions> options, HttpClient http, ILogger<VkApiClient> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
        _json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public async Task<int> WallPostAsync(
        long ownerId,
        string message,
        string? attachments,
        CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["message"] = message ?? string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(attachments))
        {
            parameters["attachments"] = attachments;
        }

        var response = await CallMethodAsync("wall.post", parameters, ct);
        // response: { "post_id": 123 }
        return response.GetProperty("post_id").GetInt32();
    }

    public async Task WallEditAsync(
        long ownerId,
        int postId,
        string message,
        string? attachments,
        CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["post_id"] = postId.ToString(),
            ["message"] = message ?? string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(attachments))
        {
            parameters["attachments"] = attachments;
        }

        // response: 1
        await CallMethodAsync("wall.edit", parameters, ct);
    }

    public async Task<string?> UploadPhotoAndGetAttachmentAsync(
        byte[] photoBytes,
        CancellationToken ct)
    {
        // 1) photos.getUploadServer
        var uploadServerResponse = await CallMethodAsync(
            "photos.getUploadServer",
            new Dictionary<string, string>
            {
                ["group_id"] = _options.GroupId.ToString(),
            },
            ct);

        var uploadUrl = uploadServerResponse.GetProperty("upload_url").GetString();
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            _logger.LogWarning("VK upload_url is empty");
            return null;
        }

        // 2) Upload bytes to upload_url (VK expects multipart form with field name 'photo')
        using var form = new MultipartFormDataContent();
        var byteContent = new ByteArrayContent(photoBytes);
        byteContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(byteContent, "photo", "photo.jpg");

        _logger.LogInformation(
            "VK upload → POST multipart (photo.jpg, {Size} bytes) to {UploadUrl}",
            photoBytes.Length,
            TruncateForLog(uploadUrl, 512));

        using var uploadResp = await _http.PostAsync(uploadUrl, form, ct);
        var uploadRespText = await uploadResp.Content.ReadAsStringAsync(ct);
        _logger.LogInformation(
            "VK upload ← HTTP {StatusCode}, body: {Body}",
            (int)uploadResp.StatusCode,
            TruncateForLog(uploadRespText, LogBodyMaxLength));

        if (!uploadResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("VK upload failed: {StatusCode} {Body}", uploadResp.StatusCode, uploadRespText);
            return null;
        }

        using var uploadDoc = JsonDocument.Parse(uploadRespText);
        // upload response: { server, photo, hash, ... }
        var server = uploadDoc.RootElement.GetProperty("server").GetString() ?? "";
        var photo = uploadDoc.RootElement.GetProperty("photo").GetString() ?? "";
        var hash = uploadDoc.RootElement.GetProperty("hash").GetString() ?? "";

        // 3) photos.saveWallPhoto
        var saved = await CallMethodAsync(
            "photos.saveWallPhoto",
            new Dictionary<string, string>
            {
                ["group_id"] = _options.GroupId.ToString(),
                ["server"] = server,
                ["photo"] = photo,
                ["hash"] = hash,
            },
            ct);

        // saved response usually: { "photos": [ { "id": ..., "owner_id": ... } ] }
        if (saved.TryGetProperty("photos", out var photos) && photos.GetArrayLength() > 0)
        {
            var first = photos[0];
            var ownerId = first.GetProperty("owner_id").GetInt32();
            var id = first.GetProperty("id").GetInt32();
            return $"photo{ownerId}_{id}";
        }

        _logger.LogWarning("VK photos.saveWallPhoto response has no photos array");
        return null;
    }

    private async Task<JsonElement> CallMethodAsync(
        string method,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var query = new Dictionary<string, string>(parameters)
        {
            ["access_token"] = _options.AccessToken,
            ["v"] = _options.ApiVersion,
        };

        var requestUrl = VkApiBaseUrl + method;
        _logger.LogInformation(
            "VK API → POST {Url}, form: {Form}",
            requestUrl,
            FormatFormForLog(query));

        using var content = new FormUrlEncodedContent(query);
        using var resp = await _http.PostAsync(requestUrl, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var bodyForLog = TruncateForLog(body, LogBodyMaxLength);

        _logger.LogInformation(
            "VK API ← {Method}: HTTP {StatusCode}, body: {Body}",
            method,
            (int)resp.StatusCode,
            bodyForLog);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError(
                "VK API HTTP error for {Method}: {StatusCode}, body: {Body}",
                method,
                (int)resp.StatusCode,
                bodyForLog);
            throw new InvalidOperationException($"VK API HTTP error: {(int)resp.StatusCode} {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("error_msg", out var em) ? em.GetString() : null;
            var code = error.TryGetProperty("error_code", out var ec) ? ec.ToString() : null;
            _logger.LogError(
                "VK API error body for {Method}: {Body}",
                method,
                bodyForLog);
            throw new InvalidOperationException($"VK API error: code={code} msg={message}");
        }

        if (!root.TryGetProperty("response", out var response))
        {
            throw new InvalidOperationException($"VK API unexpected response: {body}");
        }

        var responsePayload = response.GetRawText();
        _logger.LogDebug("VK API response.extracted for {Method}: {Payload}", method, TruncateForLog(responsePayload, LogBodyMaxLength));

        // Return detached element by cloning to avoid disposing doc
        return JsonDocument.Parse(responsePayload).RootElement.Clone();
    }

    private string FormatFormForLog(IReadOnlyDictionary<string, string> form)
    {
        var sb = new StringBuilder(capacity: Math.Min(512, form.Count * 32));
        var first = true;
        foreach (var (key, value) in form.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (!first)
                sb.Append("; ");
            first = false;
            sb.Append(key);
            sb.Append('=');
            sb.Append(key.Equals("access_token", StringComparison.OrdinalIgnoreCase)
                ? "***"
                : SanitizeParamForLog(key, value));
        }

        return sb.ToString();
    }

    private string SanitizeParamForLog(string key, string value)
    {
        if (value.Length <= LogParamMaxLength)
            return value;

        // VK передаёт поле photo как длинную строка; message тоже может быть огромным.
        var previewLen = Math.Min(120, LogParamMaxLength);
        var head = value[..previewLen];
        return $"{head}…(truncated, len={value.Length}, key={key})";
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

