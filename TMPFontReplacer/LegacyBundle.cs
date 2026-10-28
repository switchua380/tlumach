using System.Buffers.Binary;
using System.Text;

namespace TMPFontReplacer;

/// <summary>
/// Old Unity asset bundle container ("UnityWeb" = LZMA, "UnityRaw" = uncompressed), used by Unity 3.x – 5.2.
/// AssetsTools.NET only understands UnityFS, so these are unpacked/repacked here.
/// </summary>
public sealed class LegacyBundle
{
    public string Signature = "UnityRaw";
    public int Format;
    public string PlayerVersion = "", EngineVersion = "";
    public byte[] HashAndCrc = Array.Empty<byte>();
    public readonly List<(string name, byte[] data)> Files = new();

    public static bool IsLegacy(string path)
    {
        using var fs = File.OpenRead(path);
        var b = new byte[8]; int n = fs.Read(b, 0, 8);
        var s = Encoding.ASCII.GetString(b, 0, n);
        return s.StartsWith("UnityWeb") || s.StartsWith("UnityRaw");
    }

    sealed class BeReader
    {
        readonly byte[] d; public int Pos;
        public BeReader(byte[] data, int pos = 0) { d = data; Pos = pos; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(Pos)); Pos += 4; return v; }
        public int I32() => (int)U32();
        public string CStr() { int s = Pos; while (d[Pos] != 0) Pos++; var r = Encoding.UTF8.GetString(d, s, Pos - s); Pos++; return r; }
        public byte[] Bytes(int n) { var r = d.AsSpan(Pos, n).ToArray(); Pos += n; return r; }
    }

    public static LegacyBundle Read(string path)
    {
        var all = File.ReadAllBytes(path);
        var r = new BeReader(all);
        var b = new LegacyBundle();
        b.Signature = r.CStr();
        b.Format = r.I32();
        b.PlayerVersion = r.CStr();
        b.EngineVersion = r.CStr();
        if (b.Format >= 4) b.HashAndCrc = r.Bytes(20);
        r.U32();                       // minimumStreamedBytes
        int headerSize = r.I32();
        r.U32();                       // numberOfLevelsToDownloadBeforeStreaming
        int levelCount = r.I32();
        uint compressed = 0, uncompressed = 0;
        for (int i = 0; i < levelCount; i++) { compressed = r.U32(); uncompressed = r.U32(); }

        byte[] data;
        if (b.Signature == "UnityWeb")
        {
            using var input = new MemoryStream(all, headerSize, all.Length - headerSize);
            var props = new byte[5]; input.Read(props, 0, 5);
            var sizeBytes = new byte[8]; input.Read(sizeBytes, 0, 8);
            long outSize = BitConverter.ToInt64(sizeBytes, 0);
            if (outSize < 0) outSize = uncompressed;
            var dec = new SevenZip.Compression.LZMA.Decoder();
            dec.SetDecoderProperties(props);
            using var output = new MemoryStream();
            dec.Code(input, output, input.Length - input.Position, outSize, null);
            data = output.ToArray();
        }
        else data = all.AsSpan(headerSize).ToArray();

        var dr = new BeReader(data);
        int count = dr.I32();
        for (int i = 0; i < count; i++)
        {
            string name = dr.CStr();
            int off = dr.I32();                 // relative to the start of the (uncompressed) data block
            int size = dr.I32();
            if (off + size > data.Length && off - headerSize >= 0) off -= headerSize; // tolerate header-based offsets
            b.Files.Add((name, data.AsSpan(off, size).ToArray()));
        }
        return b;
    }

    public void Write(string path)
    {
        // ---- header (sizes patched later)
        var hdr = new MemoryStream();
        void CStr(string s) { var bs = Encoding.UTF8.GetBytes(s); hdr.Write(bs); hdr.WriteByte(0); }
        void U32(uint v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(t, v); hdr.Write(t); }
        CStr(Signature); U32((uint)Format); CStr(PlayerVersion); CStr(EngineVersion);
        if (Format >= 4) hdr.Write(HashAndCrc.Length == 20 ? HashAndCrc : new byte[20]);
        long posMinStream = hdr.Position; U32(0);
        long posHeaderSize = hdr.Position; U32(0);
        U32(1);            // levels to download before streaming
        U32(1);            // level count
        long posLevel = hdr.Position; U32(0); U32(0);
        long posComplete = -1, posInfo = -1;
        if (Format >= 2) { posComplete = hdr.Position; U32(0); }
        if (Format >= 3) { posInfo = hdr.Position; U32(0); }
        while (hdr.Length % 4 != 0) hdr.WriteByte(0);
        int headerSize = (int)hdr.Length;

        // ---- directory + file data
        int dirSize = 4 + Files.Sum(f => Encoding.UTF8.GetByteCount(f.name) + 1 + 8);
        int dataStart = (dirSize + 3) & ~3;
        var offsets = new List<int>(); int cur = dataStart;
        foreach (var f in Files) { offsets.Add(cur); cur += f.data.Length; cur = (cur + 3) & ~3; }
        var data = new byte[cur];
        int p = 0;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(p), Files.Count); p += 4;
        for (int i = 0; i < Files.Count; i++)
        {
            var nb = Encoding.UTF8.GetBytes(Files[i].name); nb.CopyTo(data, p); p += nb.Length; data[p++] = 0;
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(p), offsets[i]); p += 4;
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(p), Files[i].data.Length); p += 4;
            Files[i].data.CopyTo(data, offsets[i]);
        }

        byte[] payload;
        if (Signature == "UnityWeb")
        {
            var enc = new SevenZip.Compression.LZMA.Encoder();
            enc.SetCoderProperties(
                new[] { SevenZip.CoderPropID.DictionarySize, SevenZip.CoderPropID.PosStateBits, SevenZip.CoderPropID.LitContextBits,
                        SevenZip.CoderPropID.LitPosBits, SevenZip.CoderPropID.Algorithm, SevenZip.CoderPropID.NumFastBytes,
                        SevenZip.CoderPropID.MatchFinder, SevenZip.CoderPropID.EndMarker },
                new object[] { 1 << 23, 2, 3, 0, 2, 128, "bt4", false });
            using var outS = new MemoryStream();
            enc.WriteCoderProperties(outS);
            outS.Write(BitConverter.GetBytes((long)data.Length));
            using var inS = new MemoryStream(data);
            enc.Code(inS, outS, data.Length, -1, null);
            payload = outS.ToArray();
        }
        else payload = data;

        // ---- patch header numbers
        uint complete = (uint)(headerSize + payload.Length);
        void Patch(long pos, uint v) { var t = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(t, v); hdr.Position = pos; hdr.Write(t); }
        Patch(posMinStream, complete);
        Patch(posHeaderSize, (uint)headerSize);
        Patch(posLevel, (uint)payload.Length); Patch(posLevel + 4, (uint)data.Length);
        if (posComplete >= 0) Patch(posComplete, complete);
        if (posInfo >= 0) Patch(posInfo, (uint)dataStart);

        using var fs = File.Create(path);
        fs.Write(hdr.ToArray());
        fs.Write(payload);
    }
}
