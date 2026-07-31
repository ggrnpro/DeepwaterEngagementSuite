using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;

namespace DeepwaterEngagementSuiteGGRN;

/// <summary>
/// Debug telemetry for the voyage planner.
///
/// Everything is written under the plugin's config directory, which survives plugin updates:
///   events.ndjson      one JSON object per line, appended as things happen
///   snapshot-*.json    full board dumps written on demand
///   unknown-mods.json  every chart/border modifier id seen in game but missing from the profile
///
/// Writing happens on a background thread so the render loop never blocks on disk.
/// </summary>
public sealed class VoyageTelemetry : IDisposable
{
    private static readonly JsonSerializerSettings LineSettings = new()
    {
        Formatting = Formatting.None,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
    };

    private static readonly JsonSerializerSettings SnapshotSettings = new()
    {
        Formatting = Formatting.Indented,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
    };

    private readonly BlockingCollection<(string Path, string Content, bool Append)> _queue =
        new(new ConcurrentQueue<(string, string, bool)>(), 512);

    private readonly ConcurrentDictionary<string, UnknownMod> _unknown = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _modRecords = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _entityPaths = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastEntityFlush = DateTime.MinValue;
    private readonly Thread _writer;
    private DateTime _lastUnknownFlush = DateTime.MinValue;

    public VoyageTelemetry(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        EventLogPath = Path.Combine(directory, "events.ndjson");
        UnknownModsPath = Path.Combine(directory, "unknown-mods.json");
        ModRecordsPath = Path.Combine(directory, "mod-records.json");
        EntityCensusPath = Path.Combine(directory, "voyage-entities.json");

        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "DWS telemetry writer",
        };
        _writer.Start();
    }

    public string Directory { get; }
    public string EventLogPath { get; }
    public string UnknownModsPath { get; }
    public string ModRecordsPath { get; }
    public string EntityCensusPath { get; }

    /// <summary>Number of events dropped because the writer could not keep up (should stay 0).</summary>
    public int DroppedEvents { get; private set; }

    /// <summary>Appends one event line to <see cref="EventLogPath"/>.</summary>
    public void Log(string type, object payload)
    {
        var line = JsonConvert.SerializeObject(
            new { ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), type, data = payload },
            LineSettings);
        Enqueue(EventLogPath, line + Environment.NewLine, append: true);
    }

    /// <summary>Writes a full, pretty-printed snapshot and returns the file path.</summary>
    public string WriteSnapshot(string label, object payload)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var safeLabel = string.Concat((label ?? "snapshot").Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-'));
        var path = Path.Combine(Directory, $"snapshot-{safeLabel}-{stamp}.json");
        Enqueue(path, JsonConvert.SerializeObject(payload, SnapshotSettings), append: false);
        return path;
    }

    /// <summary>
    /// Records a modifier id that the game showed but the active profile does not know about.
    /// These accumulate into unknown-mods.json, which is the ground truth for filling profile gaps.
    /// </summary>
    public void NoteUnknown(string kind, string id, string sampleText = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        var key = $"{kind}|{id}";
        _unknown.AddOrUpdate(
            key,
            _ => new UnknownMod { Kind = kind, Id = id, Count = 1, Sample = sampleText },
            (_, existing) =>
            {
                existing.Count++;
                existing.Sample ??= sampleText;
                return existing;
            });

        // Throttled: this is called from the render loop for every mod on every frame.
        if (DateTime.UtcNow - _lastUnknownFlush < TimeSpan.FromSeconds(10))
            return;

        _lastUnknownFlush = DateTime.UtcNow;
        FlushUnknown();
    }

    /// <summary>
    /// Records a modifier's game data (its stat names and value ranges) the first time that id is
    /// seen. This is what maps a mod's raw values onto stats like item quantity or sulphur found,
    /// which the item's own stat block does not expose.
    /// </summary>
    public void NoteModRecord(string id, Func<object> describe)
    {
        if (string.IsNullOrWhiteSpace(id) || _modRecords.ContainsKey(id))
            return;

        object described;
        try
        {
            described = describe();
        }
        catch (Exception ex)
        {
            described = $"<error: {ex.GetBaseException().Message}>";
        }

        if (!_modRecords.TryAdd(id, described))
            return;

        Enqueue(ModRecordsPath, JsonConvert.SerializeObject(_modRecords, SnapshotSettings), append: false);
    }

    /// <summary>
    /// Counts the metadata paths of entities seen inside a voyage. Which path a Golden Lantern uses
    /// is not documented anywhere, and the pointer cannot aim at what it cannot name.
    /// </summary>
    public void NoteEntity(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _entityPaths.AddOrUpdate(path, 1, (_, count) => count + 1);

        if (DateTime.UtcNow - _lastEntityFlush < TimeSpan.FromSeconds(15))
            return;

        _lastEntityFlush = DateTime.UtcNow;
        FlushEntities();
    }

    public void FlushEntities()
    {
        if (_entityPaths.IsEmpty)
            return;

        var ordered = _entityPaths
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Value);
        Enqueue(EntityCensusPath, JsonConvert.SerializeObject(ordered, SnapshotSettings), append: false);
    }

    public void FlushUnknown()
    {
        if (_unknown.IsEmpty)
            return;

        var ordered = _unknown.Values
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Enqueue(UnknownModsPath, JsonConvert.SerializeObject(ordered, SnapshotSettings), append: false);
    }

    /// <summary>
    /// Shallow reflection dump of an object's readable members. Used to discover what the game
    /// actually exposes without guessing at API shapes; failures on individual members are
    /// swallowed and reported inline.
    ///
    /// Fields are walked as well as properties: some ExileCore types (ItemStats among them) expose
    /// their contents as fields, and a property-only dump makes them look empty.
    /// </summary>
    public static Dictionary<string, object> Describe(object obj, int depth = 1)
    {
        if (obj == null)
            return null;

        var result = new Dictionary<string, object>();
        var type = obj.GetType();
        result["__type"] = type.FullName;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0)
                continue;

            object value;
            try
            {
                value = prop.GetValue(obj);
            }
            catch (Exception ex)
            {
                result[prop.Name] = $"<error: {ex.GetBaseException().Message}>";
                continue;
            }

            result[prop.Name] = Simplify(value, depth);
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (result.ContainsKey(field.Name))
                continue;

            try
            {
                result[field.Name] = Simplify(field.GetValue(obj), depth);
            }
            catch (Exception ex)
            {
                result[field.Name] = $"<error: {ex.GetBaseException().Message}>";
            }
        }

        return result;
    }

    private static object Simplify(object value, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case string or bool or decimal:
                return value;
            case Enum e:
                return e.ToString();
            case IConvertible when value.GetType().IsPrimitive:
                return value;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var items = new List<object>();
            try
            {
                foreach (var item in enumerable)
                {
                    items.Add(depth > 0 ? Simplify(item, depth - 1) : item?.ToString());
                    if (items.Count >= 32)
                    {
                        items.Add("<truncated>");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                items.Add($"<error: {ex.GetBaseException().Message}>");
            }

            return items;
        }

        if (depth > 0)
            return Describe(value, depth - 1);

        try
        {
            return value.ToString();
        }
        catch (Exception ex)
        {
            return $"<error: {ex.GetBaseException().Message}>";
        }
    }

    private void Enqueue(string path, string content, bool append)
    {
        if (!_queue.TryAdd((path, content, append)))
            DroppedEvents++;
    }

    private void WriterLoop()
    {
        foreach (var (path, content, append) in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (append)
                    File.AppendAllText(path, content);
                else
                    File.WriteAllText(path, content);
            }
            catch
            {
                // Telemetry must never take the plugin down; a failed write is simply lost.
            }
        }
    }

    public void Dispose()
    {
        FlushUnknown();
        FlushEntities();
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class UnknownMod
    {
        public string Kind { get; set; }
        public string Id { get; set; }
        public int Count { get; set; }
        public string Sample { get; set; }
    }
}
