namespace VScreenPilot.Session;

/// <summary>4 连通域扫描的 BFS 填充；由 ConnectedComponents 复用队列缓冲区。</summary>
internal sealed class ColorComponentScanner
{
    private readonly byte[] _mask;
    private readonly int _width;
    private readonly int _height;
    private readonly int[] _queue;

    public ColorComponentScanner(byte[] mask, int width, int height, int[] queue)
    {
        _mask = mask;
        _width = width;
        _height = height;
        _queue = queue;
    }

    public VisualComponentsOps.ColorComponent Fill(int start, byte[] visited)
    {
        int head = 0;
        int tail = 0;
        _queue[tail++] = start;
        visited[start] = 1;

        int minX = _width, minY = _height, maxX = -1, maxY = -1, pixels = 0;

        while (head < tail)
        {
            int index = _queue[head++];
            int px = index % _width;
            int py = index / _width;
            pixels++;

            if (px < minX)
            {
                minX = px;
            }

            if (px > maxX)
            {
                maxX = px;
            }

            if (py < minY)
            {
                minY = py;
            }

            if (py > maxY)
            {
                maxY = py;
            }

            if (px > 0)
            {
                TryEnqueue(index - 1, visited, ref tail);
            }

            if (px < _width - 1)
            {
                TryEnqueue(index + 1, visited, ref tail);
            }

            if (py > 0)
            {
                TryEnqueue(index - _width, visited, ref tail);
            }

            if (py < _height - 1)
            {
                TryEnqueue(index + _width, visited, ref tail);
            }
        }

        return new VisualComponentsOps.ColorComponent(minX, minY, maxX, maxY, pixels);
    }

    private void TryEnqueue(int next, byte[] visited, ref int tail)
    {
        if (_mask[next] == 0 || visited[next] != 0)
        {
            return;
        }

        visited[next] = 1;
        _queue[tail++] = next;
    }
}
