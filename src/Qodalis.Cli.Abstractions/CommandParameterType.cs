using Qodalis.Cli.Abstractions.Helpers;
using System.Text.Json.Serialization;

namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Defines the data type of a CLI command parameter.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum CommandParameterType
{
    /// <summary>A text string value.</summary>
    String,
    /// <summary>A numeric value.</summary>
    Number,
    /// <summary>A boolean (true/false) value.</summary>
    Boolean,
    /// <summary>An array of values.</summary>
    Array,
    /// <summary>A structured object value.</summary>
    Object
}