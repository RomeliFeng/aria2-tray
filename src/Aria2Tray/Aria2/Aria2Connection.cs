using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Aria2Tray.Aria2;

/// <summary>aria2 回的错误（JSON-RPC 的 error）。密钥不对时 aria2 回 "Unauthorized"。</summary>
internal sealed class Aria2Exception(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    public bool Unauthorized => Message == "Unauthorized";
}

/// <summary>
/// 一条 aria2 的 WebSocket JSON-RPC 连接：调用按 id 配对，服务器推来的通知（aria2.onDownloadComplete 等）经 <see cref="Notified"/> 报出去。
/// 每个调用的第一个参数是 "token:密钥"（aria2 没设密钥就不带）。不管重连，重连由 <see cref="Aria2Session"/> 做。
/// </summary>
internal sealed class Aria2Connection : IAsyncDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _ws = new();
    private readonly string? _token;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private Task? _receive;

    private Aria2Connection(string secret)
    {
        _token = secret.Length > 0 ? "token:" + secret : null;
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    /// <summary>通知：方法名（如 aria2.onDownloadComplete）和任务的 gid。在后台线程上触发。</summary>
    public event Action<string, string>? Notified;

    /// <summary>连接断了（对方关闭、网络断开、出错）。在后台线程上触发，只触发一次。</summary>
    public event Action<string?>? Closed;

    /// <summary>
    /// 设置里填的地址换成 WebSocket 地址：http → ws，https → wss；ws、wss 原样；没写路径时补 /jsonrpc。不是这几种就是 null。
    /// </summary>
    public static Uri? ToWebSocketUri(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u))
        {
            return null;
        }

        var scheme = u.Scheme switch
        {
            "http" or "ws" => "ws",
            "https" or "wss" => "wss",
            _ => null,
        };
        if (scheme is null)
        {
            return null;
        }

        var b = new UriBuilder(u) { Scheme = scheme, Port = u.IsDefaultPort ? -1 : u.Port };
        if (b.Path is "" or "/")
        {
            b.Path = "/jsonrpc";
        }

        return b.Uri;
    }

    public static async Task<Aria2Connection> ConnectAsync(Uri uri, string secret, CancellationToken ct)
    {
        var c = new Aria2Connection(secret);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await c._ws.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        c._receive = Task.Run(c.ReceiveLoop);
        return c;
    }

    /// <summary>连一次、调一次 aria2.getVersion，用来在保存设置前核对地址和密钥。成功返回 null，否则返回给用户看的原因。</summary>
    public static async Task<string?> TestAsync(Uri uri, string secret)
    {
        try
        {
            await using var c = await ConnectAsync(uri, secret, CancellationToken.None).ConfigureAwait(false);
            await c.CallAsync("aria2.getVersion").ConfigureAwait(false);
            return null;
        }
        catch (Aria2Exception ex) when (ex.Unauthorized)
        {
            return L.T("密钥不对。", "The secret is wrong.");
        }
        catch (Aria2Exception ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException)
        {
            return L.T("连不上 aria2：", "Can't reach aria2: ") + Describe(ex);
        }
    }

    /// <summary>连接出错的原因，给用户看。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => L.T("连接超时", "Timed out"),
        WebSocketException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };

    /// <summary>调一个方法，参数里不用带密钥。</summary>
    public async Task<JsonElement> CallAsync(string method, params object?[] args)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("jsonrpc", "2.0");
                w.WriteString("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteString("method", method);
                w.WritePropertyName("params");
                WriteParams(w, args);
                w.WriteEndObject();
            }

            await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            return await tcs.Task.WaitAsync(CallTimeout).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// system.multicall：一次往返调几个方法。每项的结果要么是返回值，要么是 aria2 的错误（<see cref="Aria2Exception"/>）。
    /// </summary>
    public async Task<IReadOnlyList<(JsonElement? Result, Aria2Exception? Error)>> MulticallAsync(IReadOnlyList<(string Method, object?[] Args)> calls)
    {
        var list = calls.Select(c => new MultiCall(c.Method, c.Args)).ToArray();
        var result = await CallRawAsync("system.multicall", list).ConfigureAwait(false);
        var outcome = new List<(JsonElement?, Aria2Exception?)>();
        foreach (var item in result.EnumerateArray())
        {
            // 成功的一项是只有一个元素的数组，失败的一项是 {code, message}。
            outcome.Add(item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0
                ? (item[0], null)
                : (null, ToException(item)));
        }

        return outcome;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, t.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        _ws.Dispose();
        if (_receive is not null)
        {
            try
            {
                await _receive.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
        }

        _cts.Dispose();
        _sendLock.Dispose();
    }

    private static Aria2Exception ToException(JsonElement error) => new(
        error.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) ? c : 0,
        error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "");

    /// <summary>不加密钥（system.multicall 的密钥加在每一项里）。</summary>
    private Task<JsonElement> CallRawAsync(string method, MultiCall[] calls) => CallAsync(method, new RawParams(calls));

    private void WriteParams(Utf8JsonWriter w, object?[] args)
    {
        if (args is [RawParams raw])
        {
            // system.multicall 的参数：[[{methodName, params}, …]]，密钥在每一项的 params 里。
            w.WriteStartArray();
            w.WriteStartArray();
            foreach (var call in raw.Calls)
            {
                w.WriteStartObject();
                w.WriteString("methodName", call.Method);
                w.WritePropertyName("params");
                WriteParams(w, call.Args);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndArray();
            return;
        }

        w.WriteStartArray();
        if (_token is not null)
        {
            w.WriteStringValue(_token);
        }

        foreach (var a in args)
        {
            WriteValue(w, a);
        }

        w.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case IEnumerable<string> list:
                w.WriteStartArray();
                foreach (var s in list)
                {
                    w.WriteStringValue(s);
                }

                w.WriteEndArray();
                break;
            case IReadOnlyDictionary<string, string> map:
                w.WriteStartObject();
                foreach (var (k, val) in map)
                {
                    w.WriteString(k, val);
                }

                w.WriteEndObject();
                break;
            default:
                throw new ArgumentException("不支持的参数类型：" + v.GetType().Name);
        }
    }

    private async Task ReceiveLoop()
    {
        string? reason = null;
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    reason = L.T("aria2 关闭了连接", "aria2 closed the connection");
                    break;
                }

                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage)
                {
                    continue;
                }

                Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            reason = Describe(ex);
        }
        finally
        {
            foreach (var p in _pending.Values)
            {
                p.TrySetException(new IOException(reason ?? L.T("连接已断开", "Disconnected")));
            }

            if (!_cts.IsCancellationRequested)
            {
                Closed?.Invoke(reason);
            }
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> json)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        // 通知：{"method":"aria2.onDownloadStart","params":[{"gid":"…"}]}，没有 id。
        if (root.TryGetProperty("method", out var method) && !root.TryGetProperty("id", out _))
        {
            if (root.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in ps.EnumerateArray())
                {
                    if (p.TryGetProperty("gid", out var gid) && gid.GetString() is { } g)
                    {
                        Notified?.Invoke(method.GetString() ?? "", g);
                    }
                }
            }

            return;
        }

        if (!root.TryGetProperty("id", out var idEl) || !long.TryParse(idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText(), out var id))
        {
            return;
        }

        if (!_pending.TryGetValue(id, out var tcs))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            tcs.TrySetException(ToException(error));
        }
        else if (root.TryGetProperty("result", out var result))
        {
            tcs.TrySetResult(result);
        }
    }

    private sealed record MultiCall(string Method, object?[] Args);

    private sealed record RawParams(MultiCall[] Calls);
}
