using System.Buffers.Binary;

namespace Erp.Infrastructure.Rag;

/// 向量與 BLOB 的互轉。
///
/// 存 float32 而不是 JSON 文字：768 維是 3072 bytes，JSON 要 10 KB 以上，
/// 而且每次查詢都得重新解析三十段 JSON。代價是用 sqlite3 CLI 看不到內容。
///
/// 位元組順序顯式寫成小端序，不依賴 BitConverter 的平台預設 ——
/// 資料庫檔案會跨機器搬動，平台相依的格式會在另一台機器上靜默讀出垃圾數字。
public static class VectorBlob
{
    private const int BytesPerFloat = sizeof(float);

    public static byte[] ToBlob(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
        {
            throw new ArgumentException("向量不可為空", nameof(vector));
        }

        var blob = new byte[vector.Length * BytesPerFloat];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                blob.AsSpan(i * BytesPerFloat, BytesPerFloat), vector[i]);
        }

        return blob;
    }

    public static float[] FromBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty)
        {
            throw new ArgumentException("BLOB 不可為空", nameof(blob));
        }

        if (blob.Length % BytesPerFloat != 0)
        {
            throw new ArgumentException(
                $"BLOB 長度 {blob.Length} 不是 {BytesPerFloat} 的倍數，不是有效的 float32 向量", nameof(blob));
        }

        var vector = new float[blob.Length / BytesPerFloat];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(
                blob.Slice(i * BytesPerFloat, BytesPerFloat));
        }

        return vector;
    }
}
