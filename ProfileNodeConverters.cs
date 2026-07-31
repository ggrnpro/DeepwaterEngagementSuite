using System;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DeepwaterEngagementSuiteGGRN;

public class TextNodeConverter : CustomCreationConverter<TextNode>
{
    public override bool CanWrite => false;
    public override bool CanRead => true;

    public override TextNode Create(Type objectType) => new();

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.String)
        {
            return new TextNode((string)reader.Value);
        }

        return JsonSerializationHelper.DeserializeDefaultValue(reader, objectType, existingValue as TextNode, serializer);
    }
}

public class RangeNodeFloatConverter : CustomCreationConverter<RangeNode<float>>
{
    public override bool CanWrite => false;
    public override bool CanRead => true;

    public override RangeNode<float> Create(Type objectType) => new();

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Float || reader.TokenType == JsonToken.Integer)
        {
            return new RangeNode<float>(serializer.Deserialize<float>(reader), 0f, 100f);
        }

        return JsonSerializationHelper.DeserializeDefaultValue(reader, objectType, existingValue as RangeNode<float>, serializer);
    }
}
