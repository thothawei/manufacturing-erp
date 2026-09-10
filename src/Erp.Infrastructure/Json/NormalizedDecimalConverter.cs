using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erp.Infrastructure.Json;

/// 數量欄位輸出成最精簡的表示法：30 而不是 30.0。
///
/// SQLite 把 decimal 存成 TEXT，讀回來會保留原本的小數位數，
/// 於是 30m 存進去再取出就變成 30.0。這對兩邊都不好：
/// LLM 可能照抄成「短少 120.0 件」，而且每個數字都多花 token。
/// 小數本身會保留（120.5 仍是 120.5），只是去掉沒有意義的尾隨零。
public sealed class NormalizedDecimalConverter : JsonConverter<decimal>
{
    private const string Format = "0.############################";

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteRawValue(value.ToString(Format, CultureInfo.InvariantCulture));
}
