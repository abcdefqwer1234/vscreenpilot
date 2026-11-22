using VScreenPilot.Options;

namespace VScreenPilot.Ipc;

/// <summary>
/// 限制单条 MCP 消息（按行）的大小，避免失控客户端用超大 JSON 拖垮 daemon。
/// 读写都转发给底层管道；只统计读方向的“自上次换行以来的字节数”。
/// </summary>
internal sealed class MessageLimiter : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly MessageBudget? _budget;
    private long _currentLineBytes;
    private long _reservedBytes;

    public MessageLimiter(Stream inner, MessageBudget? budget = null)
    {
        _inner = inner;
        _maxBytes = ResolveMaxBytes();
        _budget = budget;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        Count(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Count(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Count(ReadOnlySpan<byte> buffer)
    {
        // 按换行切段、整段预留，避免长消息逐字节 Interlocked CAS。
        while (buffer.Length > 0)
        {
            int newline = buffer.IndexOf((byte)'\n');
            ReadOnlySpan<byte> segment = newline < 0 ? buffer : buffer[..newline];

            if (segment.Length > 0)
            {
                if (_budget is not null && !_budget.TryReserve(segment.Length))
                {
                    ReleaseLineBudget();
                    _currentLineBytes = 0;
                    throw new IOException("E_MESSAGE_TOO_LARGE (total buffer budget exceeded)");
                }

                _reservedBytes += segment.Length;
                _currentLineBytes += segment.Length;

                if (_currentLineBytes > _maxBytes)
                {
                    ReleaseLineBudget();
                    _currentLineBytes = 0;
                    throw new IOException($"E_MESSAGE_TOO_LARGE (>{_maxBytes} bytes)");
                }
            }

            if (newline < 0)
            {
                break;
            }

            ReleaseLineBudget();
            _currentLineBytes = 0;
            buffer = buffer[(newline + 1)..];
        }
    }

    private void ReleaseLineBudget()
    {
        if (_reservedBytes > 0)
        {
            _budget?.Release(_reservedBytes);
            _reservedBytes = 0;
        }
    }

    private static long ResolveMaxBytes() => VspOptions.FromEnvironment().MaxMessageBytes;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseLineBudget();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
