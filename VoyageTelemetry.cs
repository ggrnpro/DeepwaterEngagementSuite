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

namespace DeepwaterEngagementSuite;

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
    private readonly Thread _writer;
    private DateTime _lastUnknownFlush = DateTime.MinValue;

    public VoyageTelemetry(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        EventLogPath = Path.Combine(directory, "events.ndjson");
        UnknownModsPath = Path.Combine(directory, "unknown-mods.json");

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
    /// Shallow reflection dump of an object's readable properties. Used to discover what the game
    /// actually exposes without guessing at API shapes; failures on individual properties are
    /// swallowed and reported inline.
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
