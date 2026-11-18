using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using MoonSharp.Interpreter;

namespace Stellaris.AI
{
    /// <summary>
    /// Lua 表 ↔ JSON 的桥。
    ///
    /// 为什么需要它：RimLua 的 CallScript 只回字符串（ToPrintString 对表只会给出
    /// "table: 0x..."），所以脚本用 <c>rim.aist.json_encode(tbl)</c> 把结果编码成 JSON 字符串，
    /// C# 侧再解析。反方向（C# → Lua）直接把 Dictionary/List 树交给 LuaValueConverter 即可。
    /// </summary>
    public static class LuaJson
    {
        /// <summary>Lua 表 → JSON 字符串。</summary>
        public static string Encode(Table table)
        {
            if (table == null) return "null";

            using var stream = new MemoryStream(512);
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteTable(writer, table);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteTable(Utf8JsonWriter writer, Table table)
        {
            int length = table.Length;

            bool isArray = length > 0;
            if (isArray)
            {
                foreach (TablePair pair in table.Pairs)
                {
                    if (pair.Key.Type != DataType.Number)
                    {
                        isArray = false;
                        break;
                    }
                }
            }

            if (isArray)
            {
                writer.WriteStartArray();
                for (int i = 1; i <= length; i++)
                {
                    WriteValue(writer, table.Get(i));
                }

                writer.WriteEndArray();
                return;
            }

            writer.WriteStartObject();
            foreach (TablePair pair in table.Pairs)
            {
                string key = KeyToString(pair.Key);
                if (string.IsNullOrEmpty(key)) continue;

                writer.WritePropertyName(key);
                WriteValue(writer, pair.Value);
            }

            writer.WriteEndObject();
        }

        private static void WriteValue(Utf8JsonWriter writer, DynValue value)
        {
            if (value == null || value.IsNil())
            {
                writer.WriteNullValue();
                return;
            }

            switch (value.Type)
            {
                case DataType.Boolean:
                    writer.WriteBooleanValue(value.Boolean);
                    return;

                case DataType.Number:
                {
                    double number = value.Number;
                    if (Math.Abs(number - Math.Round(number)) < 1e-9 && Math.Abs(number) < 1e15)
                    {
                        writer.WriteNumberValue((long)Math.Round(number));
                    }
                    else
                    {
                        writer.WriteNumberValue(number);
                    }

                    return;
                }

                case DataType.String:
                    writer.WriteStringValue(value.String);
                    return;

                case DataType.Table:
                    WriteTable(writer, value.Table);
                    return;

                default:
                    // 函数/协程/userdata 一律不进 JSON，写成字符串方便排查。
                    writer.WriteStringValue(value.ToPrintString());
                    return;
            }
        }

        private static string KeyToString(DynValue key)
        {
            if (key == null || key.IsNil()) return null;

            switch (key.Type)
            {
                case DataType.String: return key.String;
                case DataType.Number: return ((long)Math.Round(key.Number)).ToString();
                default: return key.ToPrintString();
            }
        }

        /// <summary>JSON 字符串 → Dictionary/List 纯数据树（供 LuaValueConverter 转成 Lua 表）。</summary>
        public static object Decode(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();

            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                return ToPlain(doc.RootElement);
            }
            catch (JsonException e)
            {
                Verse.Log.Warning(AINarratorMod.LogPrefix + "state json decode failed: " + e.Message);
                return new Dictionary<string, object>();
            }
        }

        private static object ToPlain(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var dict = new Dictionary<string, object>();
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        dict[property.Name] = ToPlain(property.Value);
                    }

                    return dict;
                }

                case JsonValueKind.Array:
                {
                    var list = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        list.Add(ToPlain(item));
                    }

                    return list;
                }

                case JsonValueKind.String:
                    return element.GetString();

                case JsonValueKind.Number:
                    if (element.TryGetInt64(out long l)) return l;
                    return element.GetDouble();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                default:
                    return null;
            }
        }
    }
}
