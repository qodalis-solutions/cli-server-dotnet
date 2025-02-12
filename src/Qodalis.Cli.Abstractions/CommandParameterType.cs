using Qodalis.Cli.Abstractions.Helpers;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Abstractions;

[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum CommandParameterType
{
    String,
    Number,
    Boolean,
    Array,
    Object
}