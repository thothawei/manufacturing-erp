using Erp.Infrastructure.Rag;

namespace Erp.Infrastructure.Tests;

public class VectorBlobTests
{
    [Fact]
    public void 向量存成BLOB再讀回來數值不變()
    {
        float[] original = [0.125f, -0.5f, 3.75f, 0f, 1e-10f];

        var restored = VectorBlob.FromBlob(VectorBlob.ToBlob(original));

        Assert.Equal(original, restored);
    }

    [Fact]
    public void BLOB長度是維度乘4()
    {
        Assert.Equal(768 * 4, VectorBlob.ToBlob(new float[768]).Length);
    }

    [Fact]
    public void 位元組順序固定為小端序()
    {
        // 資料庫檔案會跨機器搬動。依賴平台預設的話，在大端序機器上會靜默讀出垃圾數字，
        // 而且症狀是「相似度都很低」而不是例外 —— 幾乎不可能追
        var blob = VectorBlob.ToBlob([1f]);

        Assert.Equal<byte[]>([0x00, 0x00, 0x80, 0x3F], blob);
    }

    [Fact]
    public void 長度不是4的倍數時擲例外()
    {
        var ex = Assert.Throws<ArgumentException>(() => VectorBlob.FromBlob(new byte[7]));

        Assert.Contains("不是有效的 float32 向量", ex.Message);
    }

    [Fact]
    public void 空輸入擲例外()
    {
        Assert.Throws<ArgumentException>(() => VectorBlob.ToBlob([]));
        Assert.Throws<ArgumentException>(() => VectorBlob.FromBlob([]));
    }
}
