using System.Text.Json;
using System.Text.Json.Serialization;

namespace NameTool.Services.N115;

/// <summary>
/// 115 网页接口里同一个字段有时返回字符串、有时返回数字（例如 cid / s / m），
/// 统一按字符串读取，避免反序列化整包失败。
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var value)
                    ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
            case JsonTokenType.True:
                return "1";
            case JsonTokenType.False:
                return "0";
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

// ---------------- 扫码登录（qrcodeapi / passportapi） ----------------

public sealed class N115QrSession
{
    [JsonPropertyName("uid")] public string? Uid { get; set; }
    [JsonPropertyName("time")] public long Time { get; set; }
    [JsonPropertyName("sign")] public string? Sign { get; set; }
    [JsonPropertyName("qrcode")] public string? QrCode { get; set; }
}

public sealed class N115QrTokenResponse
{
    [JsonPropertyName("state")] public int State { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("errno")] public int Errno { get; set; }
    [JsonPropertyName("data")] public N115QrSession? Data { get; set; }
}

public sealed class N115QrStatus
{
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public sealed class N115QrStatusResponse
{
    [JsonPropertyName("state")] public int State { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("errno")] public int Errno { get; set; }
    [JsonPropertyName("data")] public N115QrStatus? Data { get; set; }
}

public sealed class N115CookiePayload
{
    [JsonPropertyName("UID")] public string? UID { get; set; }
    [JsonPropertyName("CID")] public string? CID { get; set; }
    [JsonPropertyName("SEID")] public string? SEID { get; set; }
    [JsonPropertyName("KID")] public string? KID { get; set; }
}

public sealed class N115QrLoginData
{
    [JsonPropertyName("cookie")] public N115CookiePayload? Cookie { get; set; }
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
}

public sealed class N115QrLoginResponse
{
    [JsonPropertyName("state")] public int State { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("errno")] public int Errno { get; set; }
    [JsonPropertyName("data")] public N115QrLoginData? Data { get; set; }
}

// ---------------- OpenAPI（开放平台 OAuth2.0 设备码授权） ----------------

/// <summary>authDeviceCode 返回的设备码：uid 既是轮询键也是二维码内容。</summary>
public sealed class N115OpenDeviceCode
{
    [JsonPropertyName("uid")] public string? Uid { get; set; }
    [JsonPropertyName("time")] public long Time { get; set; }
    [JsonPropertyName("sign")] public string? Sign { get; set; }
    /// <summary>开放平台返回的二维码内容（需自行编码成二维码图）。</summary>
    [JsonPropertyName("qrcode")] public string? QrCode { get; set; }
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
}

public sealed class N115OpenDeviceCodeResponse
{
    [JsonPropertyName("state")] public int State { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("errno")] public int Errno { get; set; }
    [JsonPropertyName("data")] public N115OpenDeviceCode? Data { get; set; }
}

/// <summary>OpenAPI 令牌对：access 短期、refresh 长期（续期用）。</summary>
public sealed class N115OpenTokens
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
}

public sealed class N115OpenTokenResponse
{
    [JsonPropertyName("state")] public int State { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("errno")] public int Errno { get; set; }
    [JsonPropertyName("data")] public N115OpenTokens? Data { get; set; }
}

// ---------------- 通用返回（webapi / my.115.com） ----------------

public class N115BasicResponse
{
    [JsonPropertyName("state")] public bool State { get; set; }

    [JsonPropertyName("errno")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Errno { get; set; }

    [JsonPropertyName("errNo")] public int ErrNo { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("errtype")] public string? ErrType { get; set; }
}

public sealed class N115FileListResponse : N115BasicResponse
{
    [JsonPropertyName("cid")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Cid { get; set; }

    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("order")] public string? Order { get; set; }
    [JsonPropertyName("data")] public List<N115FileInfo>? Data { get; set; }
}

/// <summary>
/// 115 目录条目。注意：文件夹条目的 fid 为空、目录 ID 放在 cid 里，
/// 文件条目才是 fid 有值 —— 两者要结合 fc 字段一起判断。
/// </summary>
public sealed class N115FileInfo
{
    [JsonPropertyName("fid")] public string? FileId { get; set; }
    [JsonPropertyName("pid")] public string? ParentId { get; set; }

    [JsonPropertyName("cid")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryId { get; set; }

    [JsonPropertyName("aid")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? AreaId { get; set; }

    [JsonPropertyName("n")] public string? Name { get; set; }
    [JsonPropertyName("ico")] public string? Ico { get; set; }

    [JsonPropertyName("s")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Size { get; set; }

    [JsonPropertyName("sha")] public string? Sha1 { get; set; }
    [JsonPropertyName("pc")] public string? PickCode { get; set; }

    [JsonPropertyName("m")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? IsStar { get; set; }

    [JsonPropertyName("fc")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Category { get; set; }

    /// <summary>
    /// 修改时间，Unix **秒**（115 网页里就是这两个字段：t = 修改时间，tp = 创建时间）。
    /// 用 FlexibleStringConverter 是因为同一字段有时回字符串（"1759..."）、有时回数字，
    /// 声明成 string 又收到数字会让整个响应反序列化失败。
    /// </summary>
    [JsonPropertyName("t")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? UpdateTime { get; set; }

    [JsonPropertyName("tp")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CreateTime { get; set; }
    [JsonPropertyName("u")] public string? ThumbUrl { get; set; }
}

public sealed class N115UserInfoData
{
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("vip")] public int Vip { get; set; }
}

public sealed class N115UserInfoResponse : N115BasicResponse
{
    [JsonPropertyName("data")] public N115UserInfoData? Data { get; set; }
}
