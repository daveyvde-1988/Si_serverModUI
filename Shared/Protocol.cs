using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SiSquadHud
{
    // Wire format v2 (server companion 1.x). Shared by the Mono server mod and the Il2Cpp client mod.
    //
    // One native packet: byte PacketType (126), string Marker, string body (body <= MaxBody chars).
    //
    // client -> server (always one packet):
    //   H|nonce|channel|rootHash|id:hash,id:hash,...   hello; rootHash/known list may be empty (= send everything)
    //   C|nonce|seq|base64(utf8 command text)           run one chat command for me, reply with its captured output
    //
    // server -> client (chunked transfer of one UTF-8 JSON document):
    //   T|nonce|id|index|count|flag|base64(bytes)       flag r = raw, g = gzip; reassemble in index order
    //   JSON documents: {"k":"frame",...} {"k":"reply",...} {"k":"event",...} {"k":"rehello"}
    public static class Protocol
    {
        public const byte PacketType = 126;
        public const string Marker = "SiSquadHUD/2";
        public const int MaxBody = 1800, MaxPacket = 2100, ChunkBytes = 1050, MaxChunks = 512, MaxCommandChars = 160, MaxKnownChars = 1400, MaxDocumentBytes = 2 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Marker is ASCII with a single-byte native string length. Inspect raw bytes so another mod's packet 126
        // never triggers native type-error logging on our side.
        public static bool ReadMarker(Func<int> readByte, byte stringTag)
        {
            if (readByte() != stringTag || readByte() != Marker.Length) return false;
            foreach (char c in Marker) if (readByte() != c) return false;
            return true;
        }
        public static bool ValidNonce(string value) => value != null && value.Length == 32 && value.All(IsHex);
        public static bool ValidHash(string value) => value != null && value.Length == 12 && value.All(IsHex);
        private static bool IsHex(char c) => c >= '0' && c <= '9' || c >= 'a' && c <= 'f';
        public static string NewNonce() => Guid.NewGuid().ToString("N");

        public static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static string ShortHash(string text) => Hash(Utf8.GetBytes(text ?? "")).Substring(0, 12);

        // ---- client -> server ----
        public static string Hello(string nonce, int channel, string root, IEnumerable<KeyValuePair<string, string>> known)
        {
            if (!ValidNonce(nonce) || channel < 0 || channel > 255) throw new ArgumentException("Invalid hello.");
            string list = known == null ? "" : string.Join(",", known.Where(p => ValidId(p.Key) && ValidHash(p.Value)).Select(p => p.Key + ":" + p.Value));
            if (list.Length > MaxKnownChars) { list = ""; root = ""; }
            return "H|" + nonce + "|" + channel.ToString(Inv) + "|" + (ValidHash(root) ? root : "") + "|" + list;
        }
        public static bool TryHello(string body, out string nonce, out int channel, out string root, out Dictionary<string, string> known)
        {
            nonce = root = ""; channel = 0; known = null;
            if (body == null || body.Length > MaxBody) return false;
            var p = body.Split('|');
            if (p.Length != 5 || p[0] != "H" || !ValidNonce(p[1]) || !Number(p[2], 0, 255, out channel) || (p[3].Length != 0 && !ValidHash(p[3]))) return false;
            nonce = p[1]; root = p[3]; known = new Dictionary<string, string>();
            if (p[4].Length > MaxKnownChars) return false;
            if (p[4].Length == 0) return true;
            foreach (var pair in p[4].Split(','))
            {
                int at = pair.IndexOf(':'); if (at <= 0) return false;
                string id = pair.Substring(0, at), hash = pair.Substring(at + 1);
                if (!ValidId(id) || !ValidHash(hash)) return false;
                known[id] = hash;
            }
            return true;
        }
        public static string Command(string nonce, long seq, string text)
        {
            if (!ValidNonce(nonce) || seq < 0 || !ValidCommandText(text)) throw new ArgumentException("Invalid command.");
            return "C|" + nonce + "|" + seq.ToString(Inv) + "|" + Convert.ToBase64String(Utf8.GetBytes(text));
        }
        public static bool TryCommand(string body, out string nonce, out long seq, out string text)
        {
            nonce = text = ""; seq = -1;
            if (body == null || body.Length > MaxBody) return false;
            var p = body.Split('|');
            if (p.Length != 4 || p[0] != "C" || !ValidNonce(p[1]) || !long.TryParse(p[2], NumberStyles.None, Inv, out seq) || seq < 0) return false;
            try { text = Utf8.GetString(Convert.FromBase64String(p[3])); } catch (FormatException) { return false; } catch (DecoderFallbackException) { return false; }
            if (!ValidCommandText(text)) return false;
            nonce = p[1]; return true;
        }
        public static bool ValidCommandText(string text) => !string.IsNullOrEmpty(text) && text.Length <= MaxCommandChars && !text.Any(char.IsControl) && text.Trim().Length > 0;
        public static bool ValidId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 48 && id.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-' || c == '.');

        // ---- server -> client transfer ----
        public static string[] Transfer(string nonce, string json)
        {
            if (!ValidNonce(nonce)) throw new ArgumentException("Invalid nonce.");
            byte[] raw = Utf8.GetBytes(json ?? ""), bytes = raw; bool gzip = false;
            if (raw.Length > MaxDocumentBytes) throw new ArgumentException("Document too large.");
            if (raw.Length > 900)
            {
                using (var output = new MemoryStream())
                {
                    using (var zip = new GZipStream(output, CompressionLevel.Optimal, true)) zip.Write(raw, 0, raw.Length);
                    bytes = output.ToArray(); gzip = true;
                }
            }
            int count = Math.Max(1, (bytes.Length + ChunkBytes - 1) / ChunkBytes);
            if (count > MaxChunks) throw new ArgumentException("Transfer too large: " + raw.Length + " bytes.");
            string id = NewNonce();
            return Enumerable.Range(0, count).Select(i => "T|" + nonce + "|" + id + "|" + i.ToString(Inv) + "|" + count.ToString(Inv) + "|" + (gzip ? "g" : "r") + "|" +
                Convert.ToBase64String(bytes, i * ChunkBytes, Math.Min(ChunkBytes, bytes.Length - i * ChunkBytes))).ToArray();
        }
        public static bool TryTransfer(string body, out Chunk chunk)
        {
            chunk = null;
            if (body == null || body.Length > MaxBody) return false;
            var p = body.Split('|');
            if (p.Length != 7 || p[0] != "T" || !ValidNonce(p[1]) || !ValidNonce(p[2]) || !Number(p[3], 0, MaxChunks - 1, out int index) ||
                !Number(p[4], 1, MaxChunks, out int count) || index >= count || (p[5] != "r" && p[5] != "g")) return false;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(p[6]); } catch (FormatException) { return false; }
            if (bytes.Length > ChunkBytes) return false;
            chunk = new Chunk { Nonce = p[1], Id = p[2], Index = index, Count = count, Gzip = p[5] == "g", Bytes = bytes };
            return true;
        }
        public static string Assemble(IList<byte[]> parts, bool gzip)
        {
            if (parts == null || parts.Count < 1 || parts.Count > MaxChunks || parts.Any(p => p == null || p.Length > ChunkBytes)) throw new ArgumentException("Invalid transfer parts.");
            using (var joined = new MemoryStream())
            {
                foreach (var part in parts) joined.Write(part, 0, part.Length);
                byte[] bytes = joined.ToArray();
                if (!gzip) return Utf8.GetString(bytes);
                using (var input = new MemoryStream(bytes)) using (var zip = new GZipStream(input, CompressionMode.Decompress)) using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192]; int read;
                    while ((read = zip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaxDocumentBytes) throw new InvalidDataException("Expanded document too large.");
                        output.Write(buffer, 0, read);
                    }
                    return Utf8.GetString(output.ToArray());
                }
            }
        }
        private static bool Number(string s, int min, int max, out int value) => int.TryParse(s, NumberStyles.None, Inv, out value) && value >= min && value <= max;
    }

    public sealed class Chunk { public string Nonce, Id; public int Index, Count; public bool Gzip; public byte[] Bytes; }
}
