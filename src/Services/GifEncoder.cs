using screengrab.Models;

namespace screengrab.Services;

/// <summary>
/// Writes a picture as a GIF (Skia, which writes the PNGs and JPGs, has no GIF encoder). A GIF
/// holds at most 256 colours: a screenshot with no more than that (common for plain windows and
/// menus) keeps its exact colours; anything else gets a median-cut palette of the 256 that best
/// cover it.
/// </summary>
public static class GifEncoder
{
    private const int MaxColors = 256;

    public static void Write(CaptureImage image, Stream output)
    {
        var (palette, indices) = ExactPalette(image) ?? MedianCutPalette(image);

        using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("GIF89a"u8);

        // Logical screen: the picture's size, with a 256-entry global colour table.
        writer.Write((ushort)image.Width);
        writer.Write((ushort)image.Height);
        writer.Write((byte)0xF7);
        writer.Write((byte)0); // background colour index
        writer.Write((byte)0); // pixel aspect ratio: square
        writer.Write(palette);
        writer.Write(new byte[MaxColors * 3 - palette.Length]);

        // One image covering the whole screen, no local colour table, not interlaced.
        writer.Write((byte)0x2C);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)image.Width);
        writer.Write((ushort)image.Height);
        writer.Write((byte)0);

        const int minimumCodeSize = 8;
        writer.Write((byte)minimumCodeSize);
        WriteLzw(indices, minimumCodeSize, writer);

        writer.Write((byte)0x3B); // trailer
    }

    /// <summary>The picture's own colours, when there are no more than 256 of them.</summary>
    private static (byte[] Palette, byte[] Indices)? ExactPalette(CaptureImage image)
    {
        var pixels = image.Pixels;
        var colors = new Dictionary<int, byte>();
        var indices = new byte[image.Width * image.Height];
        for (var i = 0; i < indices.Length; i++)
        {
            var p = i * 4;
            var rgb = pixels[p + 2] << 16 | pixels[p + 1] << 8 | pixels[p];
            if (!colors.TryGetValue(rgb, out var index))
            {
                if (colors.Count == MaxColors)
                    return null;
                index = (byte)colors.Count;
                colors[rgb] = index;
            }
            indices[i] = index;
        }

        var palette = new byte[colors.Count * 3];
        foreach (var (rgb, index) in colors)
        {
            palette[index * 3] = (byte)(rgb >> 16);
            palette[index * 3 + 1] = (byte)(rgb >> 8);
            palette[index * 3 + 2] = (byte)rgb;
        }
        return (palette, indices);
    }

    /// <summary>
    /// A 256-colour palette by median cut. Colours are first reduced to 5 bits a channel (32,768
    /// possible), counted, and those counts are split into 256 boxes, each time halving the box
    /// with the widest spread along that spread's channel. Each box's colour is the average of
    /// the pixels in it.
    /// </summary>
    private static (byte[] Palette, byte[] Indices) MedianCutPalette(CaptureImage image)
    {
        var pixels = image.Pixels;
        var count = image.Width * image.Height;
        var histogram = new int[32768];
        for (var i = 0; i < count; i++)
            histogram[Key(pixels, i * 4)]++;

        var boxes = new List<Box> { Box.Of(Enumerable.Range(0, histogram.Length).Where(k => histogram[k] > 0).ToArray()) };
        while (boxes.Count < MaxColors)
        {
            // The box with the widest spread in any channel; stop when none can be split.
            var best = -1;
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Keys.Length > 1 && (best < 0 || boxes[b].Range > boxes[best].Range))
                    best = b;
            }
            if (best < 0)
                break;

            // Split at the pixel-weighted median, keeping at least one colour on each side.
            var channel = boxes[best].Channel;
            var box = boxes[best].Keys.OrderBy(k => Channel(k, channel)).ToArray();
            var half = box.Sum(k => (long)histogram[k]) / 2;
            long running = 0;
            var split = 1;
            for (; split < box.Length - 1; split++)
            {
                running += histogram[box[split - 1]];
                if (running >= half)
                    break;
            }
            boxes[best] = Box.Of(box[..split]);
            boxes.Add(Box.Of(box[split..]));
        }

        var palette = new byte[boxes.Count * 3];
        var lookup = new byte[histogram.Length];
        for (var b = 0; b < boxes.Count; b++)
        {
            long r = 0, g = 0, bl = 0, total = 0;
            foreach (var key in boxes[b].Keys)
            {
                var weight = histogram[key];
                r += Expand(Channel(key, 0)) * (long)weight;
                g += Expand(Channel(key, 1)) * (long)weight;
                bl += Expand(Channel(key, 2)) * (long)weight;
                total += weight;
                lookup[key] = (byte)b;
            }
            palette[b * 3] = (byte)(r / total);
            palette[b * 3 + 1] = (byte)(g / total);
            palette[b * 3 + 2] = (byte)(bl / total);
        }

        var indices = new byte[count];
        for (var i = 0; i < count; i++)
            indices[i] = lookup[Key(pixels, i * 4)];
        return (palette, indices);
    }

    /// <summary>Some colours (5-bit keys), with the channel they spread most along and by how much.</summary>
    private sealed record Box(int[] Keys, int Channel, int Range)
    {
        public static Box Of(int[] keys)
        {
            var (channel, range) = (0, -1);
            for (var c = 0; c < 3; c++)
            {
                int min = 31, max = 0;
                foreach (var key in keys)
                {
                    var value = GifEncoder.Channel(key, c);
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                }
                if (max - min > range)
                    (channel, range) = (c, max - min);
            }
            return new Box(keys, channel, range);
        }
    }

    /// <summary>A BGRA pixel reduced to 5 bits a channel: rrrrrgggggbbbbb.</summary>
    private static int Key(byte[] pixels, int p) =>
        (pixels[p + 2] >> 3) << 10 | (pixels[p + 1] >> 3) << 5 | (pixels[p] >> 3);

    /// <summary>Channel 0 (red), 1 (green) or 2 (blue) of a key, 0-31.</summary>
    private static int Channel(int key, int channel) => (key >> (10 - channel * 5)) & 31;

    private static int Expand(int fiveBits) => fiveBits << 3 | fiveBits >> 2;

    /// <summary>GIF's variable-length LZW, in sub-blocks of up to 255 bytes.</summary>
    private static void WriteLzw(byte[] indices, int minimumCodeSize, BinaryWriter writer)
    {
        const int maxCodes = 4096;
        var clearCode = 1 << minimumCodeSize;
        var endCode = clearCode + 1;
        var codeSize = minimumCodeSize + 1;
        var nextCode = endCode + 1;
        var table = new Dictionary<int, int>();
        var bits = new BitWriter(writer);

        bits.Write(clearCode, codeSize);
        int prefix = indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            int index = indices[i];
            var key = prefix << 8 | index;
            if (table.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }

            bits.Write(prefix, codeSize);
            if (nextCode < maxCodes)
            {
                table[key] = nextCode++;
                // The decoder adds each code one step later, so it widens once a code past the size exists.
                if (nextCode > 1 << codeSize && codeSize < 12)
                    codeSize++;
            }
            else
            {
                // The table is full: start a new one.
                bits.Write(clearCode, codeSize);
                table.Clear();
                codeSize = minimumCodeSize + 1;
                nextCode = endCode + 1;
            }
            prefix = index;
        }

        bits.Write(prefix, codeSize);
        bits.Write(endCode, codeSize);
        bits.Flush();
        writer.Write((byte)0); // block terminator
    }

    /// <summary>Packs codes least significant bit first, into length-prefixed sub-blocks.</summary>
    private sealed class BitWriter(BinaryWriter writer)
    {
        private readonly byte[] _block = new byte[255];
        private int _blockLength;
        private int _buffer;
        private int _bufferBits;

        public void Write(int code, int size)
        {
            _buffer |= code << _bufferBits;
            _bufferBits += size;
            while (_bufferBits >= 8)
            {
                AddByte((byte)_buffer);
                _buffer >>= 8;
                _bufferBits -= 8;
            }
        }

        public void Flush()
        {
            if (_bufferBits > 0)
                AddByte((byte)_buffer);
            _buffer = 0;
            _bufferBits = 0;
            if (_blockLength > 0)
                WriteBlock();
        }

        private void AddByte(byte value)
        {
            _block[_blockLength++] = value;
            if (_blockLength == _block.Length)
                WriteBlock();
        }

        private void WriteBlock()
        {
            writer.Write((byte)_blockLength);
            writer.Write(_block, 0, _blockLength);
            _blockLength = 0;
        }
    }
}
