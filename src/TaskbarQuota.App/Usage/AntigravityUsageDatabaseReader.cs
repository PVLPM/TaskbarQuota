using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TaskbarQuota.Usage
{
    internal readonly record struct AntigravityRecordedUsage(
        DateTimeOffset Timestamp,
        string Model,
        TokenBreakdown Tokens,
        string SessionId,
        string DedupeKey);

    /// <summary>
    /// Reads the token counters Antigravity records in conversation SQLite protobuf blobs.
    /// The wire layout follows the same fields consumed by ccusage and CodexBar. Unknown or
    /// malformed rows are ignored rather than replaced with transcript-derived estimates.
    /// </summary>
    internal static class AntigravityUsageDatabaseReader
    {
        private const int MaxRowsPerTable = 10_000;
        private const int MaxBlobBytes = 16 * 1024 * 1024;
        private const string UnknownModel = "antigravity-unknown";

        private readonly record struct ProtoField(uint Number, ulong? Varint, byte[]? Bytes);

        private sealed class ParsedUsage
        {
            public ulong? ModelId { get; init; }
            public ulong Input { get; init; }
            public ulong TotalOutput { get; init; }
            public ulong CacheWrite { get; init; }
            public ulong CacheRead { get; init; }
            public ulong Reasoning { get; init; }
            public ulong VisibleOutput { get; init; }
            public string? MessageId { get; init; }
            public string? ResponseId { get; init; }
            public string? ProviderMessageId { get; init; }

            public bool HasTokens => Input > 0 || TotalOutput > 0 || CacheWrite > 0
                || CacheRead > 0 || Reasoning > 0 || VisibleOutput > 0;

            public string? Identity => FirstNonEmpty(ResponseId, ProviderMessageId, MessageId);
        }

        private sealed class ParsedRow
        {
            public string? Model { get; set; }
            public ulong? ModelId { get; set; }
            public DateTimeOffset? Timestamp { get; init; }
            public List<ParsedUsage> Usages { get; } = new();
        }

        public static IReadOnlyList<AntigravityRecordedUsage> Read(string path)
        {
            var sessionId = Path.GetFileNameWithoutExtension(path);
            var fallbackTimestamp = ReadTrajectoryTimestamp(path) ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var candidates = new List<AntigravityRecordedUsage>();

            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Private;Pooling=False");
            connection.Open();

            if (TableExists(connection, "steps"))
                ReadRows(connection, "steps", "metadata", sessionId, fallbackTimestamp, ParseStepRow, candidates);
            if (TableExists(connection, "gen_metadata"))
                ReadGenerationRows(connection, sessionId, fallbackTimestamp, candidates);

            // The same provider response may occur in both steps and gen_metadata. Stable response,
            // provider-message, and message identifiers win; rows without an identifier remain
            // distinct because there is no safe evidence that they are duplicates.
            var identified = new Dictionary<string, AntigravityRecordedUsage>(StringComparer.Ordinal);
            var anonymous = new List<AntigravityRecordedUsage>();
            foreach (var item in candidates)
            {
                if (!item.DedupeKey.StartsWith("identity:", StringComparison.Ordinal))
                {
                    anonymous.Add(item);
                    continue;
                }

                if (identified.TryGetValue(item.DedupeKey, out var existing))
                    identified[item.DedupeKey] = Merge(existing, item);
                else
                    identified[item.DedupeKey] = item;
            }
            anonymous.AddRange(identified.Values);
            return anonymous;
        }

        private static void ReadGenerationRows(
            SqliteConnection connection,
            string sessionId,
            DateTimeOffset fallbackTimestamp,
            List<AntigravityRecordedUsage> output)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT idx, data FROM gen_metadata ORDER BY idx ASC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", MaxRowsPerTable + 1);
            using var reader = command.ExecuteReader();
            var count = 0;
            string? currentModel = null;
            while (reader.Read())
            {
                if (++count > MaxRowsPerTable)
                    throw new InvalidDataException("Antigravity generation row limit exceeded.");
                if (reader.IsDBNull(1))
                    continue;
                var blob = (byte[])reader.GetValue(1);
                if (blob.Length == 0 || blob.Length > MaxBlobBytes || !TryParseGeneratorRow(blob, out var parsed))
                    continue;
                currentModel = ResolveModel(parsed.Model, parsed.ModelId, null) ?? currentModel;
                Append(parsed, currentModel, sessionId, fallbackTimestamp, $"gen:{reader.GetInt64(0)}", output);
            }
        }

        private static void ReadRows(
            SqliteConnection connection,
            string table,
            string column,
            string sessionId,
            DateTimeOffset fallbackTimestamp,
            Func<byte[], ParsedRow?> parser,
            List<AntigravityRecordedUsage> output)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT idx, {column} FROM {table} WHERE {column} IS NOT NULL ORDER BY idx ASC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", MaxRowsPerTable + 1);
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                if (++count > MaxRowsPerTable)
                    throw new InvalidDataException($"Antigravity {table} row limit exceeded.");
                var blob = (byte[])reader.GetValue(1);
                if (blob.Length == 0 || blob.Length > MaxBlobBytes)
                    continue;
                var parsed = parser(blob);
                if (parsed is null)
                    continue;
                Append(parsed, ResolveModel(parsed.Model, parsed.ModelId, null), sessionId,
                    fallbackTimestamp, $"{table}:{reader.GetInt64(0)}", output);
            }
        }

        private static void Append(
            ParsedRow row,
            string? rowModel,
            string sessionId,
            DateTimeOffset fallbackTimestamp,
            string rowKey,
            List<AntigravityRecordedUsage> output)
        {
            for (var index = 0; index < row.Usages.Count; index++)
            {
                var usage = row.Usages[index];
                if (!usage.HasTokens)
                    continue;
                var totalOutput = Math.Max(usage.TotalOutput, SaturatingAdd(usage.VisibleOutput, usage.Reasoning));
                var model = ResolveModel(null, usage.ModelId, rowModel) ?? UnknownModel;
                var identity = usage.Identity;
                var key = identity is { Length: > 0 }
                    ? $"identity:{sessionId}:{identity}"
                    : $"row:{sessionId}:{rowKey}:{index}";
                output.Add(new AntigravityRecordedUsage(
                    row.Timestamp ?? fallbackTimestamp,
                    model,
                    new TokenBreakdown
                    {
                        Input = usage.Input,
                        CacheWrite5m = usage.CacheWrite,
                        CacheRead = usage.CacheRead,
                        Output = totalOutput,
                        Reasoning = Math.Min(usage.Reasoning, totalOutput),
                    },
                    sessionId,
                    key));
            }
        }

        private static AntigravityRecordedUsage Merge(AntigravityRecordedUsage left, AntigravityRecordedUsage right)
        {
            var output = Math.Max(left.Tokens.Output, right.Tokens.Output);
            return new AntigravityRecordedUsage(
                left.Timestamp <= right.Timestamp ? left.Timestamp : right.Timestamp,
                left.Model == UnknownModel ? right.Model : left.Model,
                new TokenBreakdown
                {
                    Input = Math.Max(left.Tokens.Input, right.Tokens.Input),
                    CacheWrite5m = Math.Max(left.Tokens.CacheWrite, right.Tokens.CacheWrite),
                    CacheRead = Math.Max(left.Tokens.CacheRead, right.Tokens.CacheRead),
                    Output = output,
                    Reasoning = Math.Min(output, Math.Max(left.Tokens.Reasoning, right.Tokens.Reasoning)),
                },
                left.SessionId,
                left.DedupeKey);
        }

        private static ParsedRow? ParseStepRow(byte[] blob)
        {
            if (!TryDecode(blob, out var fields))
                return null;
            var row = new ParsedRow
            {
                Timestamp = ParseTimestamp(Bytes(fields, 8) ?? Bytes(fields, 1)),
            };
            if (TryDecode(Bytes(fields, 24), out var modelInfo))
            {
                row.Model = Text(modelInfo, 12) ?? Text(modelInfo, 8);
                row.ModelId = Varint(modelInfo, 1);
            }
            AddUsage(row, Bytes(fields, 9), modernLayout: true);
            foreach (var retry in AllBytes(fields, 28))
                if (TryDecode(retry, out var retryFields))
                    AddUsage(row, Bytes(retryFields, 2), modernLayout: true);
            return row;
        }

        private static bool TryParseGeneratorRow(byte[] blob, out ParsedRow row)
        {
            row = new ParsedRow();
            if (!TryDecode(blob, out var root) || !TryDecode(Bytes(root, 1), out var chat))
                return false;
            row = new ParsedRow
            {
                Model = Text(chat, 19) ?? Text(chat, 21),
                ModelId = Varint(chat, 3),
                Timestamp = ParseGenerationTimestamp(Bytes(chat, 9)),
            };
            AddUsage(row, Bytes(chat, 4), modernLayout: false);
            foreach (var retry in AllBytes(chat, 17))
                if (TryDecode(retry, out var retryFields))
                    AddUsage(row, Bytes(retryFields, 2), modernLayout: false);
            return true;
        }

        private static void AddUsage(ParsedRow row, byte[]? blob, bool modernLayout)
        {
            if (!TryDecode(blob, out var fields))
                return;
            // Antigravity currently writes the expanded counters in steps.metadata. Older
            // gen_metadata records use fields 1/2 for system/new input and 9/10 for visible
            // output/reasoning. Presence of modern-only counters safely disambiguates newer
            // generation records without interpreting a system-prompt count as a model id.
            modernLayout = modernLayout || fields.Any(field => field.Number is 3 or 4 or 6 or 12);
            var first = Varint(fields, 1) ?? 0;
            var second = Varint(fields, 2) ?? 0;
            var field9 = Varint(fields, 9) ?? 0;
            var field10 = Varint(fields, 10) ?? 0;
            row.Usages.Add(new ParsedUsage
            {
                ModelId = modernLayout ? first : null,
                Input = modernLayout ? second : SaturatingAdd(first, second),
                TotalOutput = modernLayout ? Varint(fields, 3) ?? 0 : SaturatingAdd(field9, field10),
                CacheWrite = modernLayout ? Varint(fields, 4) ?? 0 : 0,
                CacheRead = Varint(fields, 5) ?? 0,
                Reasoning = modernLayout ? field9 : field10,
                VisibleOutput = modernLayout ? field10 : field9,
                MessageId = Text(fields, 7),
                ResponseId = Text(fields, 11),
                ProviderMessageId = Text(fields, 12),
            });
        }

        private static DateTimeOffset? ReadTrajectoryTimestamp(string path)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Private;Pooling=False");
                connection.Open();
                if (!TableExists(connection, "trajectory_metadata_blob"))
                    return null;
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT data FROM trajectory_metadata_blob ORDER BY rowid ASC LIMIT 1";
                var blob = command.ExecuteScalar() as byte[];
                return TryDecode(blob, out var fields) ? ParseTimestamp(Bytes(fields, 2)) : null;
            }
            catch (SqliteException) { return null; }
        }

        private static DateTimeOffset? ParseGenerationTimestamp(byte[]? blob)
            => TryDecode(blob, out var fields) ? ParseTimestamp(Bytes(fields, 4)) : null;

        private static DateTimeOffset? ParseTimestamp(byte[]? blob)
        {
            if (!TryDecode(blob, out var fields) || Varint(fields, 1) is not { } seconds || seconds == 0)
                return null;
            var nanos = Math.Min(Varint(fields, 2) ?? 0, 999_999_999);
            if (seconds > long.MaxValue / 1000UL)
                return null;
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000UL + nanos / 1_000_000)); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static bool TryDecode(byte[]? blob, out List<ProtoField> fields)
        {
            fields = new List<ProtoField>();
            if (blob is null)
                return false;
            var offset = 0;
            while (offset < blob.Length)
            {
                if (!TryReadVarint(blob, ref offset, out var tag) || tag >> 3 is 0 or > uint.MaxValue)
                    return false;
                var number = (uint)(tag >> 3);
                switch (tag & 7)
                {
                    case 0:
                        if (!TryReadVarint(blob, ref offset, out var value)) return false;
                        fields.Add(new ProtoField(number, value, null));
                        break;
                    case 1:
                        if (!TrySkip(blob, ref offset, 8)) return false;
                        fields.Add(new ProtoField(number, null, null));
                        break;
                    case 2:
                        if (!TryReadVarint(blob, ref offset, out var length) || length > int.MaxValue
                            || !TryTake(blob, ref offset, (int)length, out var bytes)) return false;
                        fields.Add(new ProtoField(number, null, bytes));
                        break;
                    case 5:
                        if (!TrySkip(blob, ref offset, 4)) return false;
                        fields.Add(new ProtoField(number, null, null));
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private static bool TryReadVarint(byte[] blob, ref int offset, out ulong value)
        {
            value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (offset >= blob.Length) return false;
                var current = blob[offset++];
                if (shift == 63 && (current & 0x7f) > 1) return false;
                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0) return true;
            }
            return false;
        }

        private static bool TrySkip(byte[] blob, ref int offset, int length)
        {
            if (length < 0 || offset > blob.Length - length) return false;
            offset += length;
            return true;
        }

        private static bool TryTake(byte[] blob, ref int offset, int length, out byte[] value)
        {
            value = Array.Empty<byte>();
            if (!TrySkip(blob, ref offset, length)) return false;
            value = blob.AsSpan(offset - length, length).ToArray();
            return true;
        }

        private static ulong? Varint(List<ProtoField> fields, uint number)
            => fields.LastOrDefault(field => field.Number == number && field.Varint.HasValue).Varint;

        private static byte[]? Bytes(List<ProtoField> fields, uint number)
            => fields.FirstOrDefault(field => field.Number == number && field.Bytes is not null).Bytes;

        private static IEnumerable<byte[]> AllBytes(List<ProtoField> fields, uint number)
            => fields.Where(field => field.Number == number && field.Bytes is not null).Select(field => field.Bytes!);

        private static string? Text(List<ProtoField> fields, uint number)
        {
            var bytes = fields.LastOrDefault(field => field.Number == number && field.Bytes is not null).Bytes;
            if (bytes is null) return null;
            var text = Encoding.UTF8.GetString(bytes).Trim();
            return text.Length == 0 ? null : text;
        }

        private static bool TableExists(SqliteConnection connection, string name)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
            command.Parameters.AddWithValue("$name", name);
            return command.ExecuteScalar() is not null;
        }

        private static string? ResolveModel(string? text, ulong? id, string? fallback)
        {
            if (id is { } modelId && modelId != 0)
                return NormalizeModel(ModelNameFromId(modelId));
            return NormalizeModel(text) ?? NormalizeModel(fallback);
        }

        private static string ModelNameFromId(ulong id) => id switch
        {
            246 => "gemini-2.5-pro",
            312 => "gemini-2.5-flash",
            313 or 329 => "gemini-2.5-flash-thinking",
            330 => "gemini-2.5-flash-lite",
            281 or 282 => "claude-4-sonnet",
            290 or 291 => "claude-4-opus",
            333 or 334 => "claude-4.5-sonnet",
            340 or 341 => "claude-4.5-haiku",
            342 => "gpt-oss-120b-medium",
            >= 1000 => $"model_placeholder_m{id - 1000}",
            _ => $"antigravity-model-{id}",
        };

        private static string? NormalizeModel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = raw.Trim().ToLowerInvariant();
            var parenthesis = value.IndexOf('(');
            if (parenthesis >= 0) value = value[..parenthesis].Trim();
            return value switch
            {
                "gemini 3.7 flash" or "gemini 3.7 flash thinking" or "gemini-3.7-flash-low"
                    or "gemini-3.7-flash-medium" or "gemini-3.7-flash-high" or "gemini-3.7-flash-tiered"
                    or "model_placeholder_m299" => "gemini-3.7-flash",
                "gemini 3.8 flash" or "gemini 3.8 flash low" or "gemini 3.8 flash medium"
                    or "gemini 3.8 flash high" or "gemini-3.8-flash-low" or "gemini-3.8-flash-medium"
                    or "gemini-3.8-flash-high" or "gemini-3.8-flash-tiered" or "model_placeholder_m318"
                    or "model_placeholder_m319" or "model_placeholder_m320" or "model_placeholder_m322"
                    => "gemini-3.8-flash",
                "gemini 3.7 pro" or "gemini 3.7 pro thinking" => "gemini-3.7-pro",
                "gemini 3.6 flash" or "gemini 3 flash" => "gemini-3.6-flash",
                "gemini 3.6 pro" => "gemini-3.6-pro",
                "gemini 3 pro" or "gemini 3 pro thinking" => "gemini-3-pro",
                "gemini 2.5 flash" => "gemini-2.5-flash",
                "gemini 2.5 pro" => "gemini-2.5-pro",
                "model_placeholder_m26" => "claude-opus-4-6",
                "model_placeholder_m35" => "claude-sonnet-4-6",
                "model_placeholder_m36" or "model_placeholder_m37" or "model_placeholder_m16" => "gemini-3.1-pro",
                "model_placeholder_m18" or "model_placeholder_m84" or "model_placeholder_m47" => "gemini-3-flash-preview",
                "model_placeholder_m132" or "model_placeholder_m133" => "gemini-3.5-flash-high",
                "model_placeholder_m187" => "gemini-3.5-flash-extra-low",
                "model_placeholder_m20" => "gemini-3.5-flash-medium",
                "gemini-pro-default" or "gemini-pro-agent" => "gemini-3.1-pro",
                _ when value.StartsWith("gemini-") || value.StartsWith("claude-") || value.StartsWith("gpt-") => value,
                _ when value.StartsWith("model_placeholder_") => value,
                _ => raw.Trim(),
            };
        }

        private static string? FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static ulong SaturatingAdd(ulong left, ulong right)
            => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    }
}
