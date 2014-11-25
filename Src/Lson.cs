using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RT.Util.ExtensionMethods;
using RT.Util.Text;

namespace RT.Util.Json
{
    /// <summary>
    ///     Specifies the degree of strictness or leniency when converting a <see cref="JsonValue"/> to a numerical type such
    ///     as <c>int</c> or <c>double</c>.</summary>
    [Flags]
    public enum NumericConversionOptions
    {
        /// <summary>
        ///     The conversion only succeeds if the object is a <see cref="JsonNumber"/> and its value is exactly
        ///     representable by the target type.</summary>
        Strict = 0,
        /// <summary>The conversion succeeds if the object is a <see cref="JsonString"/> with numerical content.</summary>
        AllowConversionFromString = 1 << 0,
        /// <summary>
        ///     Ignored unless <see cref="AllowConversionFromString"/> is also specified. A conversion to an integer type
        ///     succeeds if the string contains a decimal followed by a zero fractional part.</summary>
        AllowZeroFractionToInteger = 1 << 1,
        /// <summary>
        ///     The conversion succeeds if the object is a <see cref="JsonBool"/>, which will convert to 0 if false and 1 if
        ///     true.</summary>
        AllowConversionFromBool = 1 << 2,
        /// <summary>
        ///     Allows conversion of non-integral numbers to integer types by truncation (rounding towards zero). If <see
        ///     cref="AllowConversionFromString"/> is specified, strings containing a decimal part are also converted and
        ///     truncated when converting to an integer type.</summary>
        AllowTruncation = 1 << 3,

        /// <summary>Specifies maximum leniency.</summary>
        Lenient = AllowConversionFromString | AllowZeroFractionToInteger | AllowConversionFromBool | AllowTruncation
    }

    /// <summary>Specifies the degree of strictness or leniency when converting a <see cref="JsonValue"/> to a <c>string</c>.</summary>
    [Flags]
    public enum StringConversionOptions
    {
        /// <summary>The conversion only succeeds if the object is a <see cref="JsonString"/>.</summary>
        Strict = 0,
        /// <summary>The conversion succeeds if the object is a <see cref="JsonNumber"/>.</summary>
        AllowConversionFromNumber = 1 << 0,
        /// <summary>The conversion succeeds if the object is a <see cref="JsonBool"/>.</summary>
        AllowConversionFromBool = 1 << 1,

        /// <summary>Specifies maximum leniency.</summary>
        Lenient = AllowConversionFromNumber | AllowConversionFromBool
    }

    /// <summary>Specifies the degree of strictness or leniency when converting a <see cref="JsonValue"/> to a <c>bool</c>.</summary>
    [Flags]
    public enum BoolConversionOptions
    {
        /// <summary>The conversion only succeeds if the object is a <see cref="JsonBool"/>.</summary>
        Strict = 0,
        /// <summary>
        ///     The conversion succeeds if the object is a <see cref="JsonNumber"/>. 0 (zero) is converted to false, all other
        ///     values to true.</summary>
        AllowConversionFromNumber = 1 << 0,
        /// <summary>
        ///     The conversion succeeds if the object is a <see cref="JsonString"/> with specific content. The set of
        ///     permissible strings is controlled by <see cref="JsonString.True"/>, <see cref="JsonString.False"/> and <see
        ///     cref="JsonString.TrueFalseComparer"/>.</summary>
        AllowConversionFromString = 1 << 1,

        /// <summary>Specifies maximum leniency.</summary>
        Lenient = AllowConversionFromNumber | AllowConversionFromString
    }

    /// <summary>Represents a JSON parsing exception.</summary>
    [Serializable]
    public class JsonParseException : Exception
    {
        private JsonParserState _state;

        internal JsonParseException(JsonParserState ps, string message)
            : base(message)
        {
            _state = ps.Clone();
        }

        /// <summary>Gets the line number at which the parse error occurred.</summary>
        public int Line { get { return _state.OffsetConverter.GetLine(_state.Pos); } }
        /// <summary>Gets the column number at which the parse error occurred.</summary>
        public int Column { get { return _state.OffsetConverter.GetColumn(_state.Pos); } }
        /// <summary>Gets the character index at which the parse error occurred.</summary>
        public int Index { get { return _state.Pos; } }
        /// <summary>A snippet of the JSON string at which the parse error occurred.</summary>
        public string Snippet { get { return _state.Snippet; } }
    }

    /// <summary>Keeps track of the JSON parser state.</summary>
    [Serializable]
    internal class JsonParserState
    {
        public string Json;
        public int Pos;

        private OffsetToLineCol _offsetConverter;
        public OffsetToLineCol OffsetConverter { get { if (_offsetConverter == null) _offsetConverter = new OffsetToLineCol(Json); return _offsetConverter; } }

        private bool _allowJavaScript;

        private JsonParserState() { }

        public JsonParserState(string json, bool allowJavaScript)
        {
            Json = json;
            Pos = 0;
            _allowJavaScript = allowJavaScript;
            ConsumeWhitespace();
        }

        public JsonParserState Clone()
        {
            var result = new JsonParserState();
            result.Json = Json;
            result.Pos = Pos;
            result._offsetConverter = _offsetConverter;
            return result;
        }

        public void ConsumeWhitespace()
        {
            while (Pos < Json.Length)
            {
                var c = Json[Pos];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                    return;
                Pos++;
            }
        }

        public char? Cur { get { return Pos >= Json.Length ? null : (char?) Json[Pos]; } }

        public string Snippet
        {
            get
            {
                int line, col;
                OffsetConverter.GetLineAndColumn(Pos, out line, out col);
                return "Before: {2}   After: {3}   At: {0},{1}".Fmt(line, col, Json.SubstringSafe(Pos - 15, 15), Json.SubstringSafe(Pos, 15));
            }
        }

        public override string ToString()
        {
            return Snippet;
        }

        public JsonValue ParseValue()
        {
            var cn = Cur;
            switch (cn)
            {
                case null: throw new JsonParseException(this, "Unexpected end of input.");
                case '{': return ParseDict();
                case '[': return ParseList();
                case '"': return ParseString();
                default:
                    var c = Cur.Value;
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber();
                    else if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z')
                        return parseWord();
                    else if (_allowJavaScript && cn == '\'')
                        return ParseString();
                    else
                        throw new JsonParseException(this, "Unexpected character.");
            }
        }

        private JsonValue parseWord()
        {
            string word = peekLowercaseAzWord();
            if (word == "true")
            {
                Pos += 4;
                ConsumeWhitespace();
                return (JsonBool) true;
            }
            else if (word == "false")
            {
                Pos += 5;
                ConsumeWhitespace();
                return (JsonBool) false;
            }
            else if (word == "null")
            {
                Pos += 4;
                ConsumeWhitespace();
                return null;
            }
            else
                throw new JsonParseException(this, "Unknown keyword: \"{0}\"".Fmt(word));
        }

        private string peekLowercaseAzWord()
        {
            var index = Pos;
            while (true)
            {
                if (index >= Json.Length)
                    return Json.Substring(Pos);
                var c = Json[index];
                if (c < 'a' || c > 'z')
                    return Json.Substring(Pos, index - Pos);
                index++;
            }
        }

        private JsonString parseDictKey()
        {
            if (Cur == null)
                throw new JsonParseException(this, "Unexpected end of object literal.");

            if (_allowJavaScript)
            {
                char c = Cur.Value;
                if (c == '_' || c == '$' || char.IsLetter(c))
                {
                    int pos = Pos;
                    Pos++;
                    while (Cur != null && (Cur == '_' || Cur == '$' || char.IsLetter(Cur.Value)))
                        Pos++;
                    ConsumeWhitespace();
                    return new JsonString(Json.Substring(pos, Pos - pos));
                }
            }
            return ParseString();
        }

        public JsonString ParseString()
        {
            var sb = new StringBuilder();
            bool isJavaScript = Cur == '\'';
            if (Cur != '"' && (!_allowJavaScript || Cur != '\''))
                throw new JsonParseException(this, "Expected a string literal.");
            Pos++;
            while (true)
            {
                if (_allowJavaScript && isJavaScript && Cur == '\'')
                {
                    Pos++;
                    break;
                    // Note: JavaScript string support is incomplete; it allows only the same escapes as JSON strings.
                }
                switch (Cur)
                {
                    case null: throw new JsonParseException(this, "Unexpected end of string literal.");
                    case '"': Pos++; goto while_break; // break out of the while... argh.
                    case '\\':
                        {
                            Pos++;
                            switch (Cur)
                            {
                                case null: throw new JsonParseException(this, "Unexpected end of string literal.");
                                case '"': sb.Append('"'); break;
                                case '\\': sb.Append('\\'); break;
                                case '/': sb.Append('/'); break;
                                case 'b': sb.Append('\b'); break;
                                case 'f': sb.Append('\f'); break;
                                case 'n': sb.Append('\n'); break;
                                case 'r': sb.Append('\r'); break;
                                case 't': sb.Append('\t'); break;
                                case 'u':
                                    var hex = Json.SubstringSafe(Pos + 1, 4);
                                    if (hex.Length != 4)
                                        throw new JsonParseException(this, "Unexpected end of a \\u escape sequence.");
                                    int code;
                                    if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                        throw new JsonParseException(this, "Expected a four-digit hexadecimal number.");
                                    sb.Append((char) code);
                                    Pos += 4;
                                    break;
                                default:
                                    if (_allowJavaScript && Cur == '\'')
                                    {
                                        sb.Append('\'');
                                        break;
                                    }
                                    throw new JsonParseException(this, "Unknown escape sequence.");
                            }
                        }
                        break;
                    default:
                        sb.Append(Cur.Value);
                        break;
                }
                Pos++;
            }
            while_break: ;
            ConsumeWhitespace();
            return new JsonString(sb.ToString());
        }

        public JsonBool ParseBool()
        {
            var word = peekLowercaseAzWord();
            if (word == "true")
            {
                Pos += 4;
                ConsumeWhitespace();
                return true;
            }
            else if (word == "false")
            {
                Pos += 5;
                ConsumeWhitespace();
                return false;
            }
            else
                throw new JsonParseException(this, "Expected a boolean.");
        }

        public JsonNumber ParseNumber()
        {
            int fromPos = Pos;

            if (Cur == '-') // optional minus
                Pos++;

            if (Cur == '0') // either a single zero...
                Pos++;
            else if (Cur >= '1' && Cur <= '9') // ...or a non-zero followed by any number of digits
                while (Cur >= '0' && Cur <= '9')
                    Pos++;
            else
                throw new JsonParseException(this, "Expected a single zero or a sequence of digits starting with a non-zero.");

            if (Cur == '.') // a decimal point followed by at least one digit
            {
                Pos++;
                if (!(Cur >= '0' && Cur <= '9'))
                    throw new JsonParseException(this, "Expected at least one digit following the decimal point.");
                while (Cur >= '0' && Cur <= '9')
                    Pos++;
            }

            if (Cur == 'e' || Cur == 'E')
            {
                Pos++;
                if (Cur == '+' || Cur == '-') // optional plus/minus
                    Pos++;
                if (!(Cur >= '0' && Cur <= '9'))
                    throw new JsonParseException(this, "Expected at least one digit following the exponent letter.");
                while (Cur >= '0' && Cur <= '9')
                    Pos++;
            }

            JsonNumber result;
            string number = Json.Substring(fromPos, Pos - fromPos);
            long lng;
            if (!long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out lng))
            {
                double dbl;
                if (!double.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out dbl))
                    throw new JsonParseException(this, "Expected a number.");
                result = dbl;
            }
            else
                result = lng;
            ConsumeWhitespace();
            return result;
        }

        public JsonList ParseList()
        {
            var result = new JsonList();
            if (Cur != '[')
                throw new JsonParseException(this, "Expected a list.");
            Pos++;
            ConsumeWhitespace();
            while (true)
            {
                if (Cur == null)
                    throw new JsonParseException(this, "Unexpected end of list.");
                if (Cur == ']')
                    break;
                result.Add(ParseValue());
                if (Cur == null)
                    throw new JsonParseException(this, "Unexpected end of list.");
                if (Cur == ',')
                {
                    Pos++;
                    ConsumeWhitespace();
                    if (Cur == ']')
                        throw new JsonParseException(this, "A list can't end with a comma.");
                }
                else if (Cur != ']')
                    throw new JsonParseException(this, "Expected a comma between list items.");
            }
            Pos++;
            ConsumeWhitespace();
            return result;
        }

        public JsonDict ParseDict()
        {
            var result = new JsonDict();
            if (Cur != '{')
                throw new JsonParseException(this, "Expected an object literal.");
            Pos++;
            ConsumeWhitespace();
            while (true)
            {
                if (Cur == null)
                    throw new JsonParseException(this, "Unexpected end of object literal.");
                if (Cur == '}')
                    break;
                var name = parseDictKey();
                if (Cur != ':')
                    throw new JsonParseException(this, "Expected a colon between object keys and values.");
                Pos++;
                ConsumeWhitespace();
                if (Cur == null)
                    throw new JsonParseException(this, "Unexpected end of object literal.");
                result.Add(name, ParseValue());
                if (Cur == null)
                    throw new JsonParseException(this, "Unexpected end of object literal.");
                if (Cur == ',')
                {
                    Pos++;
                    ConsumeWhitespace();
                    if (Cur == '}')
                        throw new JsonParseException(this, "An object literal can't end with a comma.");
                }
                else if (Cur != '}')
                    throw new JsonParseException(this, "Expected a comma between object properties.");
            }
            Pos++;
            ConsumeWhitespace();
            return result;
        }
    }

    /// <summary>Encapsulates a JSON value (e.g. a boolean, a number, a string, a list, a dictionary, etc.)</summary>
    [Serializable]
    public abstract class JsonValue : DynamicObject, IEquatable<JsonValue>
    {
        /// <summary>
        ///     Parses the specified string into a JSON value.</summary>
        /// <param name="jsonValue">
        ///     A string containing JSON syntax.</param>
        /// <param name="allowJavaScript">
        ///     <para>
        ///         True to allow certain notations that are allowed in JavaScript but not strictly in JSON:</para>
        ///     <list type="bullet">
        ///         <item><description>
        ///             allows keys in dictionaries to be unquoted</description></item>
        ///         <item><description>
        ///             allows strings to be delimited with single-quotes in addition to double-quotes</description></item></list></param>
        /// <returns>
        ///     A <see cref="JsonValue"/> instance representing the value.</returns>
        public static JsonValue Parse(string jsonValue, bool allowJavaScript = false)
        {
            var ps = new JsonParserState(jsonValue, allowJavaScript);
            var result = ps.ParseValue();
            if (ps.Cur != null)
                throw new JsonParseException(ps, "Unexpected characters after end of input.");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a JSON value.</summary>
        /// <param name="jsonValue">
        ///     A string containing JSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="JsonValue"/> representing the value, or null if unsuccessful. (But note that null is
        ///     also a possible valid value in case of success.)</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string jsonValue, out JsonValue result)
        {
            try
            {
                result = Parse(jsonValue);
                return true;
            }
            catch (JsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>Constructs a <see cref="JsonValue"/> from the specified string.</summary>
        public static implicit operator JsonValue(string value) { return value == null ? null : new JsonString(value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified boolean.</summary>
        public static implicit operator JsonValue(bool value) { return new JsonBool(value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified nullable boolean.</summary>
        public static implicit operator JsonValue(bool? value) { return value == null ? null : new JsonBool(value.Value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified double.</summary>
        public static implicit operator JsonValue(double value) { return new JsonNumber(value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified nullable double.</summary>
        public static implicit operator JsonValue(double? value) { return value == null ? null : new JsonNumber(value.Value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified decimal.</summary>
        public static implicit operator JsonValue(decimal value) { return new JsonNumber(value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified nullable decimal.</summary>
        public static implicit operator JsonValue(decimal? value) { return value == null ? null : new JsonNumber(value.Value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified long.</summary>
        public static implicit operator JsonValue(long value) { return new JsonNumber(value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified nullable long.</summary>
        public static implicit operator JsonValue(long? value) { return value == null ? null : new JsonNumber(value.Value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified int.</summary>
        public static implicit operator JsonValue(int value) { return new JsonNumber(value); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified nullable int.</summary>
        public static implicit operator JsonValue(int? value) { return value == null ? null : new JsonNumber(value.Value); }

        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(string[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(bool[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(bool?[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(double[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(double?[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(decimal[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(decimal?[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(long[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(long?[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(int[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(int?[] values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified array.</summary>
        public static implicit operator JsonValue(JsonValue[] values) { return values == null ? null : new JsonList(values); }

        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<string> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<bool> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<bool?> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<double> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<double?> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<decimal> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<decimal?> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<long> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<long?> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<int> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<int?> values) { return values == null ? null : new JsonList(values.Select(value => (JsonValue) value)); }
        /// <summary>Constructs a <see cref="JsonValue"/> from the specified list.</summary>
        public static implicit operator JsonValue(List<JsonValue> values) { return values == null ? null : new JsonList(values); }

        /// <summary>See <see cref="StringConversionOptions.Strict"/>.</summary>
        public static explicit operator string(JsonValue value) { return value == null ? (string) null : value.GetString(); }
        /// <summary>See <see cref="BoolConversionOptions.Strict"/>.</summary>
        public static explicit operator bool(JsonValue value) { return value.GetBool(); }
        /// <summary>See <see cref="BoolConversionOptions.Strict"/>.</summary>
        public static explicit operator bool?(JsonValue value) { return value == null ? (bool?) null : value.GetBool(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator double(JsonValue value) { return value.GetDouble(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator double?(JsonValue value) { return value == null ? (double?) null : value.GetDouble(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator decimal(JsonValue value) { return value.GetDecimal(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator decimal?(JsonValue value) { return value == null ? (decimal?) null : value.GetDecimal(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long(JsonValue value) { return value.GetLong(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long?(JsonValue value) { return value == null ? (long?) null : value.GetLong(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int(JsonValue value) { return value.GetInt(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int?(JsonValue value) { return value == null ? (int?) null : value.GetInt(); }

        /// <summary>See <see cref="StringConversionOptions.Strict"/>.</summary>
        public static explicit operator string[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (string) value).ToArray(); }
        /// <summary>See <see cref="BoolConversionOptions.Strict"/>.</summary>
        public static explicit operator bool[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (bool) value).ToArray(); }
        /// <summary>See <see cref="BoolConversionOptions.Strict"/>.</summary>
        public static explicit operator bool?[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (bool?) value).ToArray(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator double[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (double) value).ToArray(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator double?[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (double?) value).ToArray(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator decimal[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (decimal) value).ToArray(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator decimal?[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (decimal?) value).ToArray(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (long) value).ToArray(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long?[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (long?) value).ToArray(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (int) value).ToArray(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int?[](JsonValue values) { return values == null ? null : values.GetList().Select(value => (int?) value).ToArray(); }

        /// <summary>See <see cref="StringConversionOptions.Strict"/>.</summary>
        public static explicit operator List<string>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (string) value).ToList(); }
        /// <summary>See <see cref="BoolConversionOptions.Strict"/>.</summary>
        public static explicit operator List<bool>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (bool) value).ToList(); }
        /// <summary>See <see cref="BoolConversionOptions.Strict"/>.</summary>
        public static explicit operator List<bool?>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (bool?) value).ToList(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator List<double>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (double) value).ToList(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator List<double?>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (double?) value).ToList(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator List<decimal>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (decimal) value).ToList(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator List<decimal?>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (decimal?) value).ToList(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator List<long>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (long) value).ToList(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator List<long?>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (long?) value).ToList(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator List<int>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (int) value).ToList(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator List<int?>(JsonValue values) { return values == null ? null : values.GetList().Select(value => (int?) value).ToList(); }

        /// <summary>
        ///     Returns an object that allows safe access to the indexers. “Safe” in this context means that the indexers,
        ///     when given an index or key not found in the list or dictionary, do not throw but instead return <see
        ///     cref="JsonNoValue.Instance"/> whose getters (such as <see cref="GetString"/>) return null.</summary>
        public JsonSafeValue Safe { get { return new JsonSafeValue(this); } }

        /// <summary>
        ///     Converts the current value to <see cref="JsonList"/> if it is a <see cref="JsonList"/>; otherwise, throws.</summary>
        public JsonList GetList() { return getList(false); }

        /// <summary>
        ///     Converts the current value to <see cref="JsonList"/> if it is a <see cref="JsonList"/>; otherwise, returns
        ///     null.</summary>
        public JsonList GetListSafe() { return getList(true); }

        /// <summary>
        ///     Converts the current value to <see cref="JsonList"/>.</summary>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected virtual JsonList getList(bool safe) { return safe ? null : Ut.Throw<JsonList>(new InvalidOperationException("Only list values can be converted to list.")); }

        /// <summary>
        ///     Converts the current value to <see cref="JsonDict"/> if it is a <see cref="JsonDict"/>; otherwise, throws.</summary>
        public JsonDict GetDict() { return getDict(false); }

        /// <summary>
        ///     Converts the current value to <see cref="JsonDict"/> if it is a <see cref="JsonDict"/>; otherwise, returns
        ///     null.</summary>
        public JsonDict GetDictSafe() { return getDict(true); }

        /// <summary>
        ///     Converts the current value to <see cref="JsonDict"/>.</summary>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected virtual JsonDict getDict(bool safe) { return safe ? null : Ut.Throw<JsonDict>(new InvalidOperationException("Only dict values can be converted to dict.")); }

        /// <summary>Converts the current value to a <c>string</c>. Throws if the conversion is not valid.</summary>
        public string GetString(StringConversionOptions options = StringConversionOptions.Strict) { return getString(options, false); }
        /// <summary>
        ///     Converts the current value to a <c>string</c> by using the <see cref="StringConversionOptions.Lenient"/>
        ///     option. Throws if the conversion is not valid.</summary>
        public string GetStringLenient() { return getString(StringConversionOptions.Lenient, false); }
        /// <summary>Converts the current value to a <c>string</c>. Returns null if the conversion is not valid.</summary>
        public string GetStringSafe(StringConversionOptions options = StringConversionOptions.Strict) { return getString(options, true); }
        /// <summary>
        ///     Converts the current value to a <c>string</c> by using the <see cref="StringConversionOptions.Lenient"/>
        ///     option. Returns null if the conversion is not valid.</summary>
        public string GetStringLenientSafe() { return getString(StringConversionOptions.Lenient, true); }

        /// <summary>
        ///     Converts the current value to <c>string</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected virtual string getString(StringConversionOptions options, bool safe) { return safe ? null : Ut.Throw<string>(new InvalidOperationException("Only string values can be converted to string.")); }

        /// <summary>Converts the current value to a <c>bool</c>. Throws if the conversion is not valid.</summary>
        public bool GetBool(BoolConversionOptions options = BoolConversionOptions.Strict) { return getBool(options, false).Value; }
        /// <summary>
        ///     Converts the current value to a <c>bool</c> by using the <see cref="BoolConversionOptions.Lenient"/> option.
        ///     Throws if the conversion is not valid.</summary>
        public bool GetBoolLenient() { return getBool(BoolConversionOptions.Lenient, false).Value; }
        /// <summary>Converts the current value to a <c>bool</c>. Returns null if the conversion is not valid.</summary>
        public bool? GetBoolSafe(BoolConversionOptions options = BoolConversionOptions.Strict) { return getBool(options, true); }
        /// <summary>
        ///     Converts the current value to a <c>bool</c> by using the <see cref="BoolConversionOptions.Lenient"/> option.
        ///     Returns null if the conversion is not valid.</summary>
        public bool? GetBoolLenientSafe() { return getBool(BoolConversionOptions.Lenient, true); }

        /// <summary>
        ///     Converts the current value to <c>bool</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected virtual bool? getBool(BoolConversionOptions options, bool safe) { return safe ? null : Ut.Throw<bool?>(new InvalidOperationException("Only bool values can be converted to bool.")); }

        /// <summary>Converts the current value to a <c>double</c>. Throws if the conversion is not valid.</summary>
        public double GetDouble(NumericConversionOptions options = NumericConversionOptions.Strict) { return getDouble(options, false).Value; }
        /// <summary>
        ///     Converts the current value to a <c>double</c> by using the <see cref="NumericConversionOptions.Lenient"/>
        ///     option. Throws if the conversion is not valid.</summary>
        public double GetDoubleLenient() { return getDouble(NumericConversionOptions.Lenient, false).Value; }
        /// <summary>Converts the current value to a <c>double</c>. Returns null if the conversion is not valid.</summary>
        public double? GetDoubleSafe(NumericConversionOptions options = NumericConversionOptions.Strict) { return getDouble(options, true); }
        /// <summary>
        ///     Converts the current value to a <c>double</c> by using the <see cref="NumericConversionOptions.Lenient"/>
        ///     option. Returns null if the conversion is not valid.</summary>
        public double? GetDoubleLenientSafe() { return getDouble(NumericConversionOptions.Lenient, true); }

        /// <summary>
        ///     Converts the current value to <c>double</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected virtual double? getDouble(NumericConversionOptions options, bool safe) { return safe ? null : Ut.Throw<double?>(new InvalidOperationException("Only numeric values can be converted to double.")); }

        /// <summary>Converts the current value to a <c>decimal</c>. Throws if the conversion is not valid.</summary>
        public decimal GetDecimal(NumericConversionOptions options = NumericConversionOptions.Strict) { return getDecimal(options, false).Value; }
        /// <summary>
        ///     Converts the current value to a <c>decimal</c> by using the <see cref="NumericConversionOptions.Lenient"/>
        ///     option. Throws if the conversion is not valid.</summary>
        public decimal GetDecimalLenient() { return getDecimal(NumericConversionOptions.Lenient, false).Value; }
        /// <summary>Converts the current value to a <c>decimal</c>. Returns null if the conversion is not valid.</summary>
        public decimal? GetDecimalSafe(NumericConversionOptions options = NumericConversionOptions.Strict) { return getDecimal(options, true); }
        /// <summary>
        ///     Converts the current value to a <c>decimal</c> by using the <see cref="NumericConversionOptions.Lenient"/>
        ///     option. Returns null if the conversion is not valid.</summary>
        public decimal? GetDecimalLenientSafe() { return getDecimal(NumericConversionOptions.Lenient, true); }

        /// <summary>
        ///     Converts the current value to <c>decimal</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected virtual decimal? getDecimal(NumericConversionOptions options, bool safe) { return safe ? null : Ut.Throw<decimal?>(new InvalidOperationException("Only numeric values can be converted to decimal.")); }

        /// <summary>Converts the current value to a <c>long</c>. Throws if the conversion is not valid.</summary>
        public long GetLong(NumericConversionOptions options = NumericConversionOptions.Strict) { return getLong(options, false).Value; }
        /// <summary>
        ///     Converts the current value to a <c>long</c> by using the <see cref="NumericConversionOptions.Lenient"/>
        ///     option. Throws if the conversion is not valid.</summary>
        public long GetLongLenient() { return getLong(NumericConversionOptions.Lenient, false).Value; }
        /// <summary>Converts the current value to a <c>long</c>. Returns null if the conversion is not valid.</summary>
        public long? GetLongSafe(NumericConversionOptions options = NumericConversionOptions.Strict) { return getLong(options, true); }
        /// <summary>
        ///     Converts the current value to a <c>long</c> by using the <see cref="NumericConversionOptions.Lenient"/>
        ///     option. Returns null if the conversion is not valid.</summary>
        public long? GetLongLenientSafe() { return getLong(NumericConversionOptions.Lenient, true); }

        /// <summary>
        ///     Converts the current value to <c>long</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected virtual long? getLong(NumericConversionOptions options, bool safe) { return safe ? null : Ut.Throw<long?>(new InvalidOperationException("Only numeric values can be converted to long.")); }

        /// <summary>Converts the current value to an <c>int</c>. Throws if the conversion is not valid.</summary>
        public int GetInt(NumericConversionOptions options = NumericConversionOptions.Strict) { return getInt(options, false).Value; }
        /// <summary>
        ///     Converts the current value to an <c>int</c> by using the <see cref="NumericConversionOptions.Lenient"/>
        ///     option. Throws if the conversion is not valid.</summary>
        public int GetIntLenient() { return getInt(NumericConversionOptions.Lenient, false).Value; }
        /// <summary>Converts the current value to an <c>int</c>. Returns null if the conversion is not valid.</summary>
        public int? GetIntSafe(NumericConversionOptions options = NumericConversionOptions.Strict) { return getInt(options, true); }
        /// <summary>
        ///     Converts the current value to an <c>int</c> by using the <see cref="NumericConversionOptions.Lenient"/>
        ///     option. Returns null if the conversion is not valid.</summary>
        public int? GetIntLenientSafe() { return getInt(NumericConversionOptions.Lenient, true); }

        /// <summary>
        ///     Converts the current value to <c>int</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected virtual int? getInt(NumericConversionOptions options, bool safe) { return safe ? null : Ut.Throw<int?>(new InvalidOperationException("Only numeric values can be converted to int.")); }

        #region Both IList and IDictionary

        /// <summary>
        ///     Removes all items from the current value if it is a <see cref="JsonList"/> or <see cref="JsonDict"/>;
        ///     otherwise, throws.</summary>
        public virtual void Clear()
        {
            throw new InvalidOperationException("This method is only supported on dictionary and list values.");
        }

        /// <summary>
        ///     Returns the number of items in the current value if it is a <see cref="JsonList"/> or <see cref="JsonDict"/>;
        ///     otherwise, throws.</summary>
        public virtual int Count
        {
            get
            {
                throw new InvalidOperationException("This method is only supported on dictionary and list values.");
            }
        }

        /// <summary>
        ///     Returns true if this value is a <see cref="JsonDict"/> or a <see cref="JsonList"/>; otherwise, returns false.</summary>
        public virtual bool IsContainer { get { return false; } }

        #endregion

        #region IList

        /// <summary>
        ///     Returns the item at the specified <paramref name="index"/> within the current list if it is a <see
        ///     cref="JsonList"/>; otherwise, throws.</summary>
        public virtual JsonValue this[int index]
        {
            get { throw new InvalidOperationException("This method is only supported on list values."); }
            set { throw new InvalidOperationException("This method is only supported on list values."); }
        }

        /// <summary>
        ///     Add the specified <paramref name="item"/> to the current list if it is a <see cref="JsonList"/>; otherwise,
        ///     throws.</summary>
        public virtual void Add(JsonValue item)
        {
            throw new InvalidOperationException("This method is only supported on list values.");
        }

        /// <summary>
        ///     Add the specified <paramref name="items"/> to the current list if it is a <see cref="JsonList"/>; otherwise,
        ///     throws.</summary>
        public virtual void AddRange(IEnumerable<JsonValue> items)
        {
            throw new InvalidOperationException("This method is only supported on list values.");
        }

        /// <summary>
        ///     Removes the first instance of the specified <paramref name="item"/> from the current list if it is a <see
        ///     cref="JsonList"/>; otherwise, throws.</summary>
        /// <returns>
        ///     True if an item was removed; otherwise, false.</returns>
        public virtual bool Remove(JsonValue item)
        {
            throw new InvalidOperationException("This method is only supported on list values.");
        }

        /// <summary>
        ///     Determines whether the specified <paramref name="item"/> is contained in the current list if it is a <see
        ///     cref="JsonList"/>; otherwise, throws.</summary>
        public virtual bool Contains(JsonValue item)
        {
            throw new InvalidOperationException("This method is only supported on list values.");
        }

        /// <summary>
        ///     Inserts the specified <paramref name="item"/> at the specified <paramref name="index"/> to the current list if
        ///     it is a <see cref="JsonList"/>; otherwise, throws.</summary>
        public virtual void Insert(int index, JsonValue item)
        {
            throw new InvalidOperationException("This method is only supported on list values.");
        }

        /// <summary>
        ///     Removes the item at the specified <paramref name="index"/> from the current list if it is a <see
        ///     cref="JsonList"/>; otherwise, throws.</summary>
        public virtual void RemoveAt(int index)
        {
            throw new InvalidOperationException("This method is only supported on list values.");
        }

        /// <summary>
        ///     Returns the index of the first occurrence of the specified <paramref name="item"/> within the current list if
        ///     it is a <see cref="JsonList"/>; otherwise, throws.</summary>
        /// <returns>
        ///     The index of the item, or -1 if the item is not in the list.</returns>
        public virtual int IndexOf(JsonValue item)
        {
            throw new InvalidOperationException("This method is only supported on list values.");
        }

        /// <summary>
        ///     Copies the entire list to a compatible one-dimensional <paramref name="array"/>, starting at the specified
        ///     <paramref name="arrayIndex"/> of the target array, if this is a <see cref="JsonList"/>; otherwise, throws.</summary>
        /// <param name="array">
        ///     The one-dimensional array that is the destination of the elements copied from the list. The array must have
        ///     zero-based indexing.</param>
        /// <param name="arrayIndex">
        ///     The zero-based index in array at which copying begins.</param>
        public virtual void CopyTo(JsonValue[] array, int arrayIndex)
        {
            throw new InvalidOperationException("This method is only supported on list values.");
        }

        #endregion

        #region IDictionary

        /// <summary>
        ///     Gets or sets the value associated with the specified <paramref name="key"/> if this value is a <see
        ///     cref="JsonDict"/>; otherwise, throws.</summary>
        public virtual JsonValue this[string key]
        {
            get { throw new InvalidOperationException("This method is only supported on dictionary values."); }
            set { throw new InvalidOperationException("This method is only supported on dictionary values."); }
        }

        /// <summary>Returns the keys contained in the dictionary if this is a <see cref="JsonDict"/>; otherwise, throws.</summary>
        public virtual ICollection<string> Keys
        {
            get
            {
                throw new InvalidOperationException("This method is only supported on dictionary values.");
            }
        }

        /// <summary>Returns the values contained in the dictionary if this is a <see cref="JsonDict"/>; otherwise, throws.</summary>
        public virtual ICollection<JsonValue> Values
        {
            get
            {
                throw new InvalidOperationException("This method is only supported on dictionary values.");
            }
        }

        /// <summary>
        ///     Attempts to retrieve the value associated with the specified <paramref name="key"/> if this is a <see
        ///     cref="JsonDict"/>; otherwise, throws.</summary>
        /// <param name="key">
        ///     The key for which to try to retrieve the value.</param>
        /// <param name="value">
        ///     Receives the value associated with the specified <paramref name="key"/>, or null if the key is not in the
        ///     dictionary. (Note that null may also be a valid value in case of success.)</param>
        /// <returns>
        ///     True if the key was in the dictionary; otherwise, false.</returns>
        public virtual bool TryGetValue(string key, out JsonValue value)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        /// <summary>
        ///     Adds the specified key/value pair to the dictionary if this is a <see cref="JsonDict"/>; otherwise, throws.</summary>
        /// <param name="key">
        ///     The key to add.</param>
        /// <param name="value">
        ///     The value to add.</param>
        public virtual void Add(string key, JsonValue value)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        /// <summary>
        ///     Add the specified <paramref name="items"/> to the current dictionary if it is a <see cref="JsonDict"/>;
        ///     otherwise, throws.</summary>
        public virtual void AddRange(IEnumerable<KeyValuePair<string, JsonValue>> items)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        /// <summary>
        ///     Removes the entry with the specified <paramref name="key"/> from the dictionary if this is a <see
        ///     cref="JsonDict"/>; otherwise, throws.</summary>
        /// <param name="key">
        ///     The key that identifies the entry to remove.</param>
        /// <returns>
        ///     True if an entry was removed; false if the key wasn’t in the dictionary.</returns>
        public virtual bool Remove(string key)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        /// <summary>
        ///     Determines whether an entry with the specified <paramref name="key"/> exists in the dictionary if this is a
        ///     <see cref="JsonDict"/>; otherwise, throws.</summary>
        public virtual bool ContainsKey(string key)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        #endregion

        /// <summary>
        ///     Determines whether this value is equal to the <paramref name="other"/> value. (See also remarks in the other
        ///     overload, <see cref="Equals(JsonValue)"/>.)</summary>
        public override bool Equals(object other)
        {
            return other is JsonValue ? Equals((JsonValue) other) : false;
        }

        /// <summary>
        ///     Determines whether this value is equal to the <paramref name="other"/> value. (See also remarks.)</summary>
        /// <remarks>
        ///     Two values are only considered equal if they are of the same type (e.g. a <see cref="JsonString"/> is never
        ///     equal to a <see cref="JsonNumber"/> even if they contain the same number). Lists are equal if they contain the
        ///     same values in the same order. Dictionaries are equal if they contain the same set of key/value pairs.</remarks>
        public abstract bool Equals(JsonValue other);

        /// <summary>Returns a hash code representing this object.</summary>
        public abstract override int GetHashCode();

        /// <summary>Converts the JSON value to a JSON string that parses back to this value. Supports null values.</summary>
        public static string ToString(JsonValue value)
        {
            return value == null ? "null" : value.ToString();
        }

        /// <summary>Converts the current JSON value to a JSON string that parses back to this value.</summary>
        public override string ToString()
        {
            return ToEnumerable().JoinString();
        }

        /// <summary>Converts the JSON value to a JSON string that parses back to this value. Supports null values.</summary>
        public static string ToStringIndented(JsonValue value)
        {
            return value == null ? "null" : value.ToStringIndented();
        }

        /// <summary>Converts the current JSON value to a JSON string that parses back to this value.</summary>
        public string ToStringIndented()
        {
            var sb = new StringBuilder();
            AppendIndented(this, sb);
            return sb.ToString();
        }

        /// <summary>
        ///     Converts the JSON value to a JSON string that parses back to this value and places the string into the
        ///     specified StringBuilder. Supports null values.</summary>
        public static void AppendIndented(JsonValue value, StringBuilder sb, int indentation = 0)
        {
            if (value == null)
                sb.Append("null");
            else
                value.AppendIndented(sb, indentation);
        }

        /// <summary>
        ///     Converts the current JSON value to a JSON void that parses back to this value and places the string into the
        ///     specified StringBuilder.</summary>
        public abstract void AppendIndented(StringBuilder sb, int indentation = 0);

        /// <summary>Lazy-converts the JSON value to a JSON string that parses back to this value. Supports null values.</summary>
        public static IEnumerable<string> ToEnumerable(JsonValue value)
        {
            if (value == null)
            {
                yield return "null";
                yield break;
            }
            foreach (var piece in value.ToEnumerable())
                yield return piece;
        }

        /// <summary>Lazy-converts the current JSON value to a JSON string that parses back to this value.</summary>
        public abstract IEnumerable<string> ToEnumerable();

        private const string _fmt_tokenTemplate = @"
            \{\{(?<placeholder>[^\{\}]*?)\}\}|
            (?<comment>//[^\n]*|/\*.*?\*/)|
            (?<stringliteral>'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*"")|
            (?<return>\breturn\b)|
            (?<identifier>[\p{Ll}\p{Lu}\p{Lt}\p{Lo}\p{Pc}\p{Lm}\$][\w\$]*)|
            (?<regex>{0})|
            (?<number>0x\d+|\d*(?:\d|\.\d+)(?:[Ee][\+\-]\d+)?)|
            (?<operator>[=!]==|[\+\-\*/%<>&\^\|=!]=|<<=|>>=|>>>=|<<|>>>|>>|\+\+|--|&&|\|\||[<>=%\+\-\*/%&\^\|!~\?:\(\)\[\]\{\}\.,;])|
            (?<else>.)
        ";
        private static readonly Regex _fmt_tokenWithoutRegex = new Regex(_fmt_tokenTemplate.Fmt("(?!)"), RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);
        private static readonly Regex _fmt_tokenWithRegex = new Regex(_fmt_tokenTemplate.Fmt(@"/(?:[^/\\]|\\.)*/"), RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

        /// <summary>
        ///     Formats JSON values into a piece of JavaScript code and then removes almost all unnecessary whitespace and
        ///     comments. Values are referenced by names; placeholders for these values are written as {{name}}. Placeholders
        ///     are only replaced outside of JavaScript literal strings and regexes. <see cref="JsonRaw"/> instances are
        ///     inserted unmodified.</summary>
        /// <param name="js">
        ///     JavaScript code with placeholders.</param>
        /// <param name="namevalues">
        ///     Alternating names and associated values, for example ["user", "abc"] specifies one value named "user".</param>
        /// <example>
        ///     <para>
        ///         The following code:</para>
        ///     <code>
        ///         JsonValue.Fmt(@"Foo({{userid}}, {{username}}, {{options}});",
        ///             "userid", userid,
        ///             "username", username,
        ///             "options", null)</code>
        ///     <para>
        ///         might return the following string:</para>
        ///     <code>
        ///         Foo(123, "Matthew Stranger", null);</code></example>
        /// <exception cref="ArgumentException">
        ///     <paramref name="namevalues"/> has an odd number of values. OR <paramref name="js"/> contains a {{placeholder}}
        ///     whose name is not listed in <paramref name="namevalues"/>.</exception>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="js"/> is null. OR <paramref name="namevalues"/> is null.</exception>
        public static string Fmt(string js, params JsonValue[] namevalues)
        {
            if (js == null)
                throw new ArgumentNullException("js");
            if (namevalues == null)
                throw new ArgumentNullException("namevalues");
            if (namevalues.Length % 2 != 0)
                throw new ArgumentException("namevalues must have an even number of values.", "namevalues");

            var tokens = new StringBuilder();
            var nextTokenCanBeRegex = true;
            var lastWasPlus = false;
            var lastWasMinus = false;
            var lastWasNumberOrIdentifierOrKeyword = false;
            var idx = 0;
            while (idx < js.Length)
            {
                var m = (nextTokenCanBeRegex ? _fmt_tokenWithRegex : _fmt_tokenWithoutRegex).Match(js, idx);

                if (!m.Success) // This should never occur.
                    throw new InvalidOperationException("The input string was not in a correct format. (iphcv)");

                if (m.Groups["placeholder"].Success)
                {
                    var name = m.Groups["placeholder"].Value;
                    var index = Enumerable.Range(0, namevalues.Length / 2).IndexOf(i => namevalues[2 * i].GetString() == name);
                    if (index == -1)
                        throw new InvalidOperationException("namevalues does not contain a value named \"{0}\".".Fmt(name));
                    js = JsonValue.ToString(namevalues[2 * index + 1]) + js.Substring(m.Index + m.Length);
                    idx = 0;
                    continue;
                }

                if (m.Groups["comment"].Success || (m.Groups["else"].Success && string.IsNullOrWhiteSpace(m.Groups["else"].Value)))
                {
                    // Do nothing. In particular, don’t change the values of nextTokenCanBeRegex, lastWasPlus, lastWasMinus or lastWasNumberOrIdentifierOrKeyword.
                }
                else if (m.Groups["stringliteral"].Success)
                {
                    tokens.Append(m.Groups["stringliteral"].Value);
                    nextTokenCanBeRegex = lastWasNumberOrIdentifierOrKeyword = lastWasPlus = lastWasMinus = false;
                }
                else if (m.Groups["regex"].Success)
                {
                    tokens.Append(m.Groups["regex"].Value);
                    nextTokenCanBeRegex = lastWasNumberOrIdentifierOrKeyword = lastWasPlus = lastWasMinus = false;
                }
                else if (m.Groups["return"].Success)
                {
                    tokens.Append("return");
                    lastWasPlus = lastWasMinus = false;
                    nextTokenCanBeRegex = lastWasNumberOrIdentifierOrKeyword = true;
                }
                else if (m.Groups["identifier"].Success)
                {
                    if (lastWasNumberOrIdentifierOrKeyword)
                        tokens.Append(' ');
                    tokens.Append(m.Groups["identifier"].Value);
                    nextTokenCanBeRegex = lastWasPlus = lastWasMinus = false;
                    lastWasNumberOrIdentifierOrKeyword = true;
                }
                else if (m.Groups["number"].Success)
                {
                    if (lastWasNumberOrIdentifierOrKeyword)
                        tokens.Append(' ');
                    tokens.Append(m.Groups["number"].Value);
                    nextTokenCanBeRegex = lastWasPlus = lastWasMinus = false;
                    lastWasNumberOrIdentifierOrKeyword = true;
                }
                else if (m.Groups["operator"].Success)
                {
                    var op = m.Groups["operator"].Value;
                    if ((lastWasPlus && op.StartsWith("+")) || (lastWasMinus && op.StartsWith("-")))
                        tokens.Append(' ');
                    tokens.Append(op);
                    nextTokenCanBeRegex = !op.EndsWith(")") && !op.EndsWith("]") && !op.EndsWith("}");
                    lastWasPlus = op.EndsWith('+');
                    lastWasMinus = op.EndsWith('-');
                    lastWasNumberOrIdentifierOrKeyword = false;
                }
                else
                    throw new InvalidOperationException("The input string was in an invalid format. (q0ifmefb)");

                idx += m.Length;
            }

            return tokens.ToString();
        }
    }

    /// <summary>Encapsulates a list of <see cref="JsonValue"/> values.</summary>
    [Serializable]
    public sealed class JsonList : JsonValue, IList<JsonValue>, IEquatable<JsonList>
    {
        internal List<JsonValue> List;

        /// <summary>Constructs an empty list.</summary>
        public JsonList() { List = new List<JsonValue>(); }

        /// <summary>
        ///     Constructs a <see cref="JsonList"/> instance containing a copy of the specified collection of <paramref
        ///     name="items"/>.</summary>
        public JsonList(IEnumerable<JsonValue> items)
        {
            if (items == null)
                throw new ArgumentNullException("items");
            List = new List<JsonValue>(items is ICollection<JsonValue> ? ((ICollection<JsonValue>) items).Count + 2 : 4);
            List.AddRange(items);
        }

        /// <summary>
        ///     Parses the specified JSON as a JSON list. All other types of JSON values result in a <see
        ///     cref="JsonParseException"/>.</summary>
        /// <param name="jsonList">
        ///     JSON syntax to parse.</param>
        /// <param name="allowJavaScript">
        ///     See <see cref="JsonValue.Parse"/>.</param>
        public static new JsonList Parse(string jsonList, bool allowJavaScript = false)
        {
            if (jsonList == null)
                throw new ArgumentNullException("jsonList");
            var ps = new JsonParserState(jsonList, allowJavaScript);
            var result = ps.ParseList();
            if (ps.Cur != null)
                throw new JsonParseException(ps, "expected end of input");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a JSON list.</summary>
        /// <param name="jsonNumber">
        ///     A string containing JSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="JsonList"/> representing the list, or null if unsuccessful.</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string jsonNumber, out JsonList result)
        {
            try
            {
                result = Parse(jsonNumber);
                return true;
            }
            catch (JsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>
        ///     Converts the current value to <see cref="JsonList"/>.</summary>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override JsonList getList(bool safe) { return this; }

        /// <summary>Enumerates the values in this list.</summary>
        public IEnumerator<JsonValue> GetEnumerator()
        {
            return List.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is JsonList ? Equals((JsonList) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(JsonValue other)
        {
            return other is JsonList ? Equals((JsonList) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public bool Equals(JsonList other)
        {
            if (other == null) return false;
            if (this.Count != other.Count) return false;
            return this.Zip(other, (v1, v2) => (v1 == null) == (v2 == null) && (v1 == null || v1.Equals(v2))).All(b => b);
        }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            int result = 977;
            unchecked
            {
                foreach (var item in this)
                    result ^= result * 211 + (item == null ? 1979 : item.GetHashCode());
            }
            return result;
        }

        /// <summary>See <see cref="JsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return "[";
            bool first = true;
            foreach (var value in List)
            {
                if (!first)
                    yield return ",";
                foreach (var piece in JsonValue.ToEnumerable(value))
                    yield return piece;
                first = false;
            }
            yield return "]";
        }

        /// <summary>Converts the JSON value to a JSON string that parses back to this value. Supports null values.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0)
        {
            if (List.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append("[");
            bool first = true;
            foreach (var value in List)
            {
                if (!first)
                    sb.Append(",");
                sb.AppendLine();
                for (int i = 0; i <= indentation; i++)
                    sb.Append("  ");
                JsonValue.AppendIndented(value, sb, indentation + 1);
                first = false;
            }
            sb.AppendLine();
            for (int i = 0; i < indentation; i++)
                sb.Append("  ");
            sb.Append("]");
        }

        /// <summary>Removes all items from the current list.</summary>
        public override void Clear() { List.Clear(); }

        /// <summary>Returns the number of items in the current list.</summary>
        public override int Count { get { return List.Count; } }

        /// <summary>Returns true.</summary>
        public override bool IsContainer { get { return true; } }

        /// <summary>Returns the item at the specified <paramref name="index"/> within the current list.</summary>
        public override JsonValue this[int index]
        {
            get { return List[index]; }
            set { List[index] = value; }
        }

        /// <summary>Adds the specified <paramref name="item"/> to the current list.</summary>
        public override void Add(JsonValue item) { List.Add(item); }

        /// <summary>Adds the specified <paramref name="items"/> to the current list.</summary>
        public override void AddRange(IEnumerable<JsonValue> items) { List.AddRange(items); }

        /// <summary>
        ///     Removes the first instance of the specified <paramref name="item"/> from the current list.</summary>
        /// <returns>
        ///     True if an item was removed; otherwise, false.</returns>
        public override bool Remove(JsonValue item) { return List.Remove(item); }

        /// <summary>Determines whether the specified <paramref name="item"/> is contained in the current list.</summary>
        public override bool Contains(JsonValue item) { return List.Contains(item); }

        /// <summary>
        ///     Inserts the specified <paramref name="item"/> at the specified <paramref name="index"/> to the current list.</summary>
        public override void Insert(int index, JsonValue item) { List.Insert(index, item); }

        /// <summary>Removes the item at the specified <paramref name="index"/> from the current list.</summary>
        public override void RemoveAt(int index) { List.RemoveAt(index); }

        /// <summary>
        ///     Returns the index of the first occurrence of the specified <paramref name="item"/> within the current list.</summary>
        /// <returns>
        ///     The index of the item, or -1 if the item is not in the list.</returns>
        public override int IndexOf(JsonValue item) { return List.IndexOf(item); }

        /// <summary>
        ///     Copies the entire list to a compatible one-dimensional <paramref name="array"/>, starting at the specified
        ///     <paramref name="arrayIndex"/> of the target array.</summary>
        /// <param name="array">
        ///     The one-dimensional array that is the destination of the elements copied from the list. The array must have
        ///     zero-based indexing.</param>
        /// <param name="arrayIndex">
        ///     The zero-based index in array at which copying begins.</param>
        public override void CopyTo(JsonValue[] array, int arrayIndex) { List.CopyTo(array, arrayIndex); }

        bool ICollection<JsonValue>.IsReadOnly { get { return false; } }
    }

    /// <summary>Encapsulates a JSON dictionary (a set of key/value pairs).</summary>
    [Serializable]
    public sealed class JsonDict : JsonValue, IDictionary<string, JsonValue>, IEquatable<JsonDict>
    {
        internal Dictionary<string, JsonValue> Dict;

        /// <summary>Constructs an empty dictionary.</summary>
        public JsonDict() { Dict = new Dictionary<string, JsonValue>(); }

        /// <summary>Constructs a dictionary containing a copy of the specified collection of key/value pairs.</summary>
        public JsonDict(IEnumerable<KeyValuePair<string, JsonValue>> items)
        {
            if (items == null)
                throw new ArgumentNullException("items");
            Dict = new Dictionary<string, JsonValue>(items is ICollection<KeyValuePair<string, JsonValue>> ? ((ICollection<KeyValuePair<string, JsonValue>>) items).Count + 2 : 4);
            foreach (var item in items)
                Dict.Add(item.Key, item.Value);
        }

        /// <summary>
        ///     Parses the specified JSON as a JSON dictionary. All other types of JSON values result in a <see
        ///     cref="JsonParseException"/>.</summary>
        /// <param name="jsonDict">
        ///     JSON syntax to parse.</param>
        /// <param name="allowJavaScript">
        ///     See <see cref="JsonValue.Parse"/>.</param>
        public static new JsonDict Parse(string jsonDict, bool allowJavaScript = false)
        {
            var ps = new JsonParserState(jsonDict, allowJavaScript);
            var result = ps.ParseDict();
            if (ps.Cur != null)
                throw new JsonParseException(ps, "expected end of input");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a JSON dictionary.</summary>
        /// <param name="jsonDict">
        ///     A string containing JSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="JsonDict"/> representing the dictionary, or null if unsuccessful.</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string jsonDict, out JsonDict result)
        {
            try
            {
                result = Parse(jsonDict);
                return true;
            }
            catch (JsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>
        ///     Converts the current value to <see cref="JsonDict"/>.</summary>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override JsonDict getDict(bool safe) { return this; }

        /// <summary>Enumerates the key/value pairs in this dictionary.</summary>
        public IEnumerator<KeyValuePair<string, JsonValue>> GetEnumerator()
        {
            return Dict.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #region IEnumerable<KeyValuePair> methods

        void ICollection<KeyValuePair<string, JsonValue>>.Add(KeyValuePair<string, JsonValue> item)
        {
            ((ICollection<KeyValuePair<string, JsonValue>>) Dict).Add(item);
        }
        bool ICollection<KeyValuePair<string, JsonValue>>.Remove(KeyValuePair<string, JsonValue> item)
        {
            return ((ICollection<KeyValuePair<string, JsonValue>>) Dict).Remove(item);
        }
        bool ICollection<KeyValuePair<string, JsonValue>>.Contains(KeyValuePair<string, JsonValue> item)
        {
            return ((ICollection<KeyValuePair<string, JsonValue>>) Dict).Contains(item);
        }
        void ICollection<KeyValuePair<string, JsonValue>>.CopyTo(KeyValuePair<string, JsonValue>[] array, int arrayIndex)
        {
            ((ICollection<KeyValuePair<string, JsonValue>>) Dict).CopyTo(array, arrayIndex);
        }

        #endregion

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is JsonDict && Equals((JsonDict) other);
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(JsonValue other)
        {
            return other is JsonDict ? Equals((JsonDict) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public bool Equals(JsonDict other)
        {
            if (other == null) return false;
            if (this.Count != other.Count) return false;
            foreach (var kvp in this)
            {
                JsonValue val;
                if (!other.TryGetValue(kvp.Key, out val))
                    return false;
                if ((kvp.Value == null) != (val == null))
                    return false;
                if (kvp.Value != null && !kvp.Value.Equals(val))
                    return false;
            }
            return true;
        }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            int result = 1307;
            unchecked
            {
                foreach (var kvp in this)
                    result ^= result * 647 + (kvp.Value == null ? 1979 : kvp.Value.GetHashCode()) + kvp.Key.GetHashCode();
            }
            return result;
        }

        /// <summary>See <see cref="JsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return "{";
            bool first = true;
            foreach (var kvp in Dict)
            {
                if (!first)
                    yield return ",";
                yield return kvp.Key.JsEscape(JsQuotes.Double);
                yield return ":";
                foreach (var piece in JsonValue.ToEnumerable(kvp.Value))
                    yield return piece;
                first = false;
            }
            yield return "}";
        }

        /// <summary>Converts the JSON value to a JSON string that parses back to this value. Supports null values.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0)
        {
            if (Dict.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append("{");
            bool first = true;
            foreach (var kvp in Dict)
            {
                if (!first)
                    sb.Append(",");
                sb.AppendLine();
                for (int i = 0; i <= indentation; i++)
                    sb.Append("  ");
                sb.Append(kvp.Key.JsEscape(JsQuotes.Double));
                sb.Append(": ");
                JsonValue.AppendIndented(kvp.Value, sb, indentation + 1);
                first = false;
            }
            sb.AppendLine();
            for (int i = 0; i < indentation; i++)
                sb.Append("  ");
            sb.Append("}");
        }

        /// <summary>Removes all items from the current dictionary.</summary>
        public override void Clear() { Dict.Clear(); }

        /// <summary>Returns the number of items in the current dictionary.</summary>
        public override int Count { get { return Dict.Count; } }

        /// <summary>Returns true.</summary>
        public override bool IsContainer { get { return true; } }

        /// <summary>Gets or sets the value associated with the specified <paramref name="key"/>.</summary>
        public override JsonValue this[string key]
        {
            get { return Dict[key]; }
            set { Dict[key] = value; }
        }

        /// <summary>Returns the keys contained in the dictionary.</summary>
        public override ICollection<string> Keys { get { return Dict.Keys; } }

        /// <summary>Returns the values contained in the dictionary.</summary>
        public override ICollection<JsonValue> Values { get { return Dict.Values; } }

        /// <summary>
        ///     Attempts to retrieve the value associated with the specified <paramref name="key"/>.</summary>
        /// <param name="key">
        ///     The key for which to try to retrieve the value.</param>
        /// <param name="value">
        ///     Receives the value associated with the specified <paramref name="key"/>, or null if the key is not in the
        ///     dictionary. (Note that null may also be a valid value in case of success.)</param>
        /// <returns>
        ///     True if the key was in the dictionary; otherwise, false.</returns>
        public override bool TryGetValue(string key, out JsonValue value) { return Dict.TryGetValue(key, out value); }

        /// <summary>
        ///     Adds the specified key/value pair to the dictionary.</summary>
        /// <param name="key">
        ///     The key to add.</param>
        /// <param name="value">
        ///     The value to add.</param>
        public override void Add(string key, JsonValue value) { Dict.Add(key, value); }

        /// <summary>Adds the specified key/value pairs to the dictionary.</summary>
        public override void AddRange(IEnumerable<KeyValuePair<string, JsonValue>> items)
        {
            foreach (var item in items)
                ((ICollection<KeyValuePair<string, JsonValue>>) Dict).Add(item);
        }

        /// <summary>
        ///     Removes the entry with the specified <paramref name="key"/> from the dictionary.</summary>
        /// <param name="key">
        ///     The key that identifies the entry to remove.</param>
        /// <returns>
        ///     True if an entry was removed; false if the key wasn’t in the dictionary.</returns>
        public override bool Remove(string key) { return Dict.Remove(key); }

        /// <summary>Determines whether an entry with the specified <paramref name="key"/> exists in the dictionary.</summary>
        public override bool ContainsKey(string key) { return Dict.ContainsKey(key); }

        bool ICollection<KeyValuePair<string, JsonValue>>.IsReadOnly { get { return false; } }

        /// <summary>
        ///     Implements functionality that allows the keys in this JSON dictionary to be accessed as dynamic members.</summary>
        /// <example>
        ///     <code>
        ///         dynamic dict = JsonDict.Parse(@"{ ""List"": [1, 2, 3] }");
        ///         Console.WriteLine(dict.List.Count);     // outputs 3</code></example>
        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            JsonValue value;
            if (Dict.TryGetValue(binder.Name, out value))
            {
                result = value;
                return true;
            }
            result = null;
            return false;
        }
    }

    /// <summary>Encapsulates a string as a JSON value.</summary>
    [Serializable]
    public sealed class JsonString : JsonValue, IEquatable<JsonString>
    {
        private string _value;

        /// <summary>Constructs a <see cref="JsonString"/> instance from the specified string.</summary>
        public JsonString(string value)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            _value = value;
        }

        /// <summary>
        ///     Parses the specified JSON as a JSON string. All other types of JSON values result in a <see
        ///     cref="JsonParseException"/>.</summary>
        /// <param name="jsonString">
        ///     JSON syntax to parse.</param>
        /// <param name="allowJavaScript">
        ///     See <see cref="JsonValue.Parse"/>.</param>
        public static new JsonString Parse(string jsonString, bool allowJavaScript = false)
        {
            var ps = new JsonParserState(jsonString, allowJavaScript);
            var result = ps.ParseString();
            if (ps.Cur != null)
                throw new JsonParseException(ps, "expected end of input");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a JSON string.</summary>
        /// <param name="jsonString">
        ///     A string containing JSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="JsonString"/> representing the string, or null if unsuccessful.</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string jsonString, out JsonString result)
        {
            try
            {
                result = Parse(jsonString);
                return true;
            }
            catch (JsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>Converts the specified <see cref="JsonString"/> value to an ordinary string.</summary>
        public static implicit operator string(JsonString value) { return value == null ? null : value._value; }
        /// <summary>Converts the specified ordinary string to a <see cref="JsonString"/> value.</summary>
        public static implicit operator JsonString(string value) { return value == null ? null : new JsonString(value); }

        /// <summary>
        ///     Converts the current value to <c>double</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override double? getDouble(NumericConversionOptions options, bool safe)
        {
            if (!options.HasFlag(NumericConversionOptions.AllowConversionFromString))
                return base.getDouble(options, safe);

            double result;
            if (safe)
            {
                if (!double.TryParse(_value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out result))
                    return null;
            }
            else
                result = double.Parse(_value, CultureInfo.InvariantCulture);

            if (double.IsNaN(result) || double.IsInfinity(result))
                return safe ? null : Ut.Throw<double?>(new InvalidOperationException("This string cannot be converted to a double because JSON doesn't support NaNs and infinities."));

            return result;
        }

        /// <summary>
        ///     Converts the current value to <c>decimal</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override decimal? getDecimal(NumericConversionOptions options, bool safe)
        {
            if (!options.HasFlag(NumericConversionOptions.AllowConversionFromString))
                return base.getDecimal(options, safe);

            if (!safe)
                return decimal.Parse(_value, CultureInfo.InvariantCulture);

            decimal result;
            return decimal.TryParse(_value, NumberStyles.Number, CultureInfo.InvariantCulture, out result) ? result : (decimal?) null;
        }

        /// <summary>
        ///     Converts the current value to <c>int</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override int? getInt(NumericConversionOptions options, bool safe)
        {
            if (!options.HasFlag(NumericConversionOptions.AllowConversionFromString))
                return base.getInt(options, safe);

            if (!options.HasFlag(NumericConversionOptions.AllowZeroFractionToInteger))
            {
                if (!safe)
                    return int.Parse(_value, CultureInfo.InvariantCulture);

                int result;
                return int.TryParse(_value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : (int?) null;
            }
            else
            {
                decimal result;
                if (safe)
                {
                    if (!decimal.TryParse(_value, NumberStyles.Number, CultureInfo.InvariantCulture, out result))
                        return null;
                }
                else
                    result = decimal.Parse(_value, CultureInfo.InvariantCulture);

                if (result != decimal.Truncate(result))
                    return safe ? null : Ut.Throw<int?>(new InvalidOperationException("String must represent an integer, but \"{0}\" has a fractional part.".Fmt(_value)));

                if (safe && (result < int.MinValue || result > int.MaxValue))
                    return null;

                return (int) result;
            }
        }

        /// <summary>
        ///     Converts the current value to <c>long</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override long? getLong(NumericConversionOptions options, bool safe)
        {
            if (!options.HasFlag(NumericConversionOptions.AllowConversionFromString))
                return base.getLong(options, safe);

            if (!options.HasFlag(NumericConversionOptions.AllowZeroFractionToInteger))
            {
                if (!safe)
                    return long.Parse(_value, CultureInfo.InvariantCulture);

                long result;
                return long.TryParse(_value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : (long?) null;
            }
            else
            {
                decimal result;
                if (safe)
                {
                    if (!decimal.TryParse(_value, NumberStyles.Number, CultureInfo.InvariantCulture, out result))
                        return null;
                }
                else
                    result = decimal.Parse(_value, CultureInfo.InvariantCulture);

                if (result != decimal.Truncate(result))
                    return safe ? null : Ut.Throw<long?>(new InvalidOperationException("String must represent an integer, but \"{0}\" has a fractional part.".Fmt(_value)));

                if (safe && (result < long.MinValue || result > long.MaxValue))
                    return null;

                return (long) result;
            }
        }

        /// <summary>
        ///     Converts the current value to <c>bool</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override bool? getBool(BoolConversionOptions options, bool safe)
        {
            if (!options.HasFlag(BoolConversionOptions.AllowConversionFromString))
                return base.getBool(options, safe);

            return
                False.Contains(_value, TrueFalseComparer) ? false :
                True.Contains(_value, TrueFalseComparer) ? true :
                (bool?) null;
        }

        /// <summary>
        ///     Controls which string values are converted to <c>false</c> when using <see cref="JsonValue.GetBool"/> with
        ///     <see cref="BoolConversionOptions.AllowConversionFromString"/>.</summary>
        /// <remarks>
        ///     The default is: <c>{ "", "false", "n", "no", "off", "disable", "disabled", "0" }</c>.</remarks>
        public static readonly List<string> False = new List<string> { "", "false", "n", "no", "off", "disable", "disabled", "0" };
        /// <summary>
        ///     Controls which string values are converted to <c>true</c> when using <see cref="JsonValue.GetBool"/> with <see
        ///     cref="BoolConversionOptions.AllowConversionFromString"/>.</summary>
        /// <remarks>
        ///     The default is: <c>{ "true", "y", "yes", "on", "enable", "enabled", "1" }</c>.</remarks>
        public static readonly List<string> True = new List<string> { "true", "y", "yes", "on", "enable", "enabled", "1" };
        /// <summary>
        ///     Controls which string equality comparer is used when comparing strings against elements in <see cref="True"/>
        ///     and <see cref="False"/> during conversion to bool by <see cref="JsonValue.GetBool"/>.</summary>
        /// <remarks>
        ///     The default is <see cref="StringComparer.OrdinalIgnoreCase"/>.</remarks>
        public static readonly IEqualityComparer<string> TrueFalseComparer = StringComparer.OrdinalIgnoreCase;

        /// <summary>
        ///     Converts the current value to <c>string</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override string getString(StringConversionOptions options, bool safe)
        {
            return _value;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is JsonString ? Equals((JsonString) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(JsonValue other)
        {
            return other is JsonString ? Equals((JsonString) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public bool Equals(JsonString other)
        {
            return other != null && _value == other._value;
        }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        /// <summary>See <see cref="JsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return ToStringIndented();
        }

        /// <summary>Converts the current JSON value to a JSON string that parses back to this value.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0)
        {
            sb.AppendJsEscaped(_value, JsQuotes.Double);
        }

        /// <summary>
        ///     Returns a JavaScript-compatible representation of this string.</summary>
        /// <param name="quotes">
        ///     Specifies the style of quotes to use around the string.</param>
        public string ToString(JsQuotes quotes)
        {
            return _value.JsEscape(quotes);
        }
    }

    /// <summary>Encapsulates a boolean value as a <see cref="JsonValue"/>.</summary>
    [Serializable]
    public sealed class JsonBool : JsonValue, IEquatable<JsonBool>
    {
        private bool _value;

        /// <summary>Constructs a <see cref="JsonBool"/> from the specified boolean.</summary>
        public JsonBool(bool value) { _value = value; }

        /// <summary>
        ///     Parses the specified JSON as a JSON boolean. All other types of JSON values result in a <see
        ///     cref="JsonParseException"/>.</summary>
        /// <param name="jsonBool">
        ///     JSON syntax to parse.</param>
        /// <param name="allowJavaScript">
        ///     See <see cref="JsonValue.Parse"/>.</param>
        public static new JsonBool Parse(string jsonBool, bool allowJavaScript = false)
        {
            var ps = new JsonParserState(jsonBool, allowJavaScript);
            var result = ps.ParseBool();
            if (ps.Cur != null)
                throw new JsonParseException(ps, "expected end of input");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a JSON boolean.</summary>
        /// <param name="jsonBool">
        ///     A string containing JSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="JsonBool"/> representing the boolean, or null if unsuccessful.</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string jsonBool, out JsonBool result)
        {
            try
            {
                result = Parse(jsonBool);
                return true;
            }
            catch (JsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>Converts the specified <see cref="JsonBool"/> value to an ordinary boolean.</summary>
        public static explicit operator bool(JsonBool value) { return value._value; }
        /// <summary>Converts the specified <see cref="JsonBool"/> value to a nullable boolean.</summary>
        public static implicit operator bool?(JsonBool value) { return value == null ? (bool?) null : value._value; }
        /// <summary>Converts the specified ordinary boolean to a <see cref="JsonBool"/> value.</summary>
        public static implicit operator JsonBool(bool value) { return new JsonBool(value); }
        /// <summary>Converts the specified nullable boolean to a <see cref="JsonBool"/> value or null.</summary>
        public static implicit operator JsonBool(bool? value) { return value == null ? null : new JsonBool(value.Value); }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is JsonBool ? Equals((JsonBool) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(JsonValue other)
        {
            return other is JsonBool ? Equals((JsonBool) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public bool Equals(JsonBool other)
        {
            return other != null && _value == other._value;
        }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            return _value ? 13259 : 22093;
        }

        /// <summary>See <see cref="JsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return ToStringIndented();
        }

        /// <summary>Converts the current JSON value to a JSON string that parses back to this value.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0)
        {
            sb.Append(_value ? "true" : "false");
        }

        /// <summary>
        ///     Converts the current value to <c>bool</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override bool? getBool(BoolConversionOptions options, bool safe) { return _value; }

        /// <summary>
        ///     Converts the current value to <c>decimal</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override decimal? getDecimal(NumericConversionOptions options, bool safe)
        {
            if (!options.HasFlag(NumericConversionOptions.AllowConversionFromBool))
                return base.getDecimal(options, safe);
            return _value ? 1m : 0m;
        }

        /// <summary>
        ///     Converts the current value to <c>double</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override double? getDouble(NumericConversionOptions options, bool safe)
        {
            if (!options.HasFlag(NumericConversionOptions.AllowConversionFromBool))
                return base.getDouble(options, safe);
            return _value ? 1d : 0d;
        }

        /// <summary>
        ///     Converts the current value to <c>int</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override int? getInt(NumericConversionOptions options, bool safe)
        {
            if (!options.HasFlag(NumericConversionOptions.AllowConversionFromBool))
                return base.getInt(options, safe);
            return _value ? 1 : 0;
        }

        /// <summary>
        ///     Converts the current value to <c>long</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override long? getLong(NumericConversionOptions options, bool safe)
        {
            if (!options.HasFlag(NumericConversionOptions.AllowConversionFromBool))
                return base.getLong(options, safe);
            return _value ? 1L : 0L;
        }

        /// <summary>
        ///     Converts the current value to <c>string</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override string getString(StringConversionOptions options, bool safe)
        {
            if (!options.HasFlag(StringConversionOptions.AllowConversionFromBool))
                return base.getString(options, safe);
            return _value ? "true" : "false";
        }
    }

    /// <summary>
    ///     Encapsulates a number, which may be a floating-point number or an integer, as a <see cref="JsonValue"/>. See
    ///     Remarks.</summary>
    /// <remarks>
    ///     JSON does not define any specific limits for numeric values. This implementation supports integers in the signed
    ///     64-bit range, as well as IEEE 64-bit doubles (except NaNs and infinities). Conversions to/from <c>decimal</c> are
    ///     exact for integers, but can be approximate for non-integers, depending on the exact value.</remarks>
    [Serializable]
    public sealed class JsonNumber : JsonValue, IEquatable<JsonNumber>
    {
        private long _long;
        private double _double = double.NaN;

        /// <summary>Constructs a <see cref="JsonNumber"/> from the specified double-precision floating-point number.</summary>
        public JsonNumber(double value) { _double = value; if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("JSON disallows NaNs and infinities."); }
        /// <summary>Constructs a <see cref="JsonNumber"/> from the specified 64-bit integer.</summary>
        public JsonNumber(long value) { _long = value; }
        /// <summary>Constructs a <see cref="JsonNumber"/> from the specified 32-bit integer.</summary>
        public JsonNumber(int value) { _long = value; }
        /// <summary>
        ///     Constructs a <see cref="JsonNumber"/> from the specified decimal. This operation is slightly lossy; see
        ///     Remarks on <see cref="JsonNumber"/>.</summary>
        public JsonNumber(decimal value)
        {
            if (value == decimal.Truncate(value) && value >= long.MinValue && value <= long.MaxValue)
                _long = (long) value;
            else
                _double = (double) value;
        }

        /// <summary>
        ///     Parses the specified JSON as a JSON number. All other types of JSON values result in a <see
        ///     cref="JsonParseException"/>.</summary>
        /// <param name="jsonNumber">
        ///     JSON syntax to parse.</param>
        /// <param name="allowJavaScript">
        ///     See <see cref="JsonValue.Parse"/>.</param>
        public static new JsonNumber Parse(string jsonNumber, bool allowJavaScript = false)
        {
            var ps = new JsonParserState(jsonNumber, allowJavaScript);
            var result = ps.ParseNumber();
            if (ps.Cur != null)
                throw new JsonParseException(ps, "expected end of input");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a JSON number.</summary>
        /// <param name="jsonNumber">
        ///     A string containing JSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="JsonNumber"/> representing the number, or null if unsuccessful.</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string jsonNumber, out JsonNumber result)
        {
            try
            {
                result = Parse(jsonNumber);
                return true;
            }
            catch (JsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>Converts the specified <see cref="JsonNumber"/> to a double.</summary>
        public static explicit operator double(JsonNumber value) { return double.IsNaN(value._double) ? (double) value._long : value._double; }
        /// <summary>Converts the specified <see cref="JsonNumber"/> to a nullable double.</summary>
        public static implicit operator double?(JsonNumber value) { return value == null ? (double?) null : double.IsNaN(value._double) ? (double) value._long : value._double; }

        /// <summary>
        ///     Converts the specified <see cref="JsonNumber"/> to a decimal. This operator is slightly lossy; see Remarks on
        ///     <see cref="JsonNumber"/>.</summary>
        public static explicit operator decimal(JsonNumber value) { return double.IsNaN(value._double) ? (decimal) value._long : (decimal) value._double; }
        /// <summary>
        ///     Converts the specified <see cref="JsonNumber"/> to a nullable decimal. This operator is slightly lossy; see
        ///     Remarks on <see cref="JsonNumber"/>.</summary>
        public static explicit operator decimal?(JsonNumber value) { return value == null ? (decimal?) null : (decimal) value; }

        /// <summary>
        ///     Converts the specified <see cref="JsonNumber"/> to a 64-bit integer. See <see
        ///     cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long(JsonNumber value)
        {
            if (value == null)
                throw new InvalidCastException("null cannot be cast to long.");
            return value.GetLong(); // use default strict mode
        }
        /// <summary>
        ///     Converts the specified <see cref="JsonNumber"/> to a nullable 64-bit integer. See <see
        ///     cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long?(JsonNumber value) { return value == null ? (long?) null : (long) value; }

        /// <summary>
        ///     Converts the specified <see cref="JsonNumber"/> to a 32-bit integer. See <see
        ///     cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int(JsonNumber value)
        {
            if (value == null)
                throw new InvalidCastException("null cannot be cast to int.");
            return value.GetInt();  // use default strict mode
        }
        /// <summary>
        ///     Converts the specified <see cref="JsonNumber"/> to a nullable 32-bit integer. See <see
        ///     cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int?(JsonNumber value) { return value == null ? (int?) null : (int) value; }

        /// <summary>Converts the specified double to a <see cref="JsonNumber"/> value.</summary>
        public static implicit operator JsonNumber(double value) { return new JsonNumber(value); }
        /// <summary>Converts the specified nullable double to a <see cref="JsonNumber"/> value.</summary>
        public static implicit operator JsonNumber(double? value) { return value == null ? null : new JsonNumber(value.Value); }
        /// <summary>Converts the specified 64-bit integer to a <see cref="JsonNumber"/> value.</summary>
        public static implicit operator JsonNumber(long value) { return new JsonNumber(value); }
        /// <summary>Converts the specified nullable 64-bit integer to a <see cref="JsonNumber"/> value.</summary>
        public static implicit operator JsonNumber(long? value) { return value == null ? null : new JsonNumber(value.Value); }
        /// <summary>Converts the specified 32-bit integer to a <see cref="JsonNumber"/> value.</summary>
        public static implicit operator JsonNumber(int value) { return new JsonNumber(value); }
        /// <summary>Converts the specified nullable 32-bit integer to a <see cref="JsonNumber"/> value.</summary>
        public static implicit operator JsonNumber(int? value) { return value == null ? null : new JsonNumber(value.Value); }
        /// <summary>
        ///     Converts the specified decimal to a <see cref="JsonNumber"/> value. This operator is slightly lossy; see
        ///     Remarks on <see cref="JsonNumber"/>.</summary>
        public static explicit operator JsonNumber(decimal value) { return new JsonNumber(value); }
        /// <summary>
        ///     Converts the specified nullable decimal to a <see cref="JsonNumber"/> value. This operator is slightly lossy;
        ///     see Remarks on <see cref="JsonNumber"/>.</summary>
        public static explicit operator JsonNumber(decimal? value) { return value == null ? null : new JsonNumber(value.Value); }

        /// <summary>
        ///     Converts the current value to <c>double</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override double? getDouble(NumericConversionOptions options, bool safe) { return (double) this; }

        /// <summary>
        ///     Converts the current value to <c>decimal</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override decimal? getDecimal(NumericConversionOptions options, bool safe) { return (decimal) this; }

        /// <summary>
        ///     Converts the current value to <c>int</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override int? getInt(NumericConversionOptions options, bool safe)
        {
            if (double.IsNaN(_double))
            {
                if (_long < int.MinValue || _long > int.MaxValue)
                    return safe ? null : Ut.Throw<int?>(new InvalidCastException("Cannot cast to int because the value exceeds the representable range."));
                return (int) _long;
            }

            if (!options.HasFlag(NumericConversionOptions.AllowTruncation) && _double != Math.Truncate(_double))
                return safe ? null : Ut.Throw<int?>(new InvalidCastException("Only integer values can be converted to int."));

            if (_double < int.MinValue || _double > int.MaxValue)
                return safe ? null : Ut.Throw<int?>(new InvalidCastException("Cannot cast to int because the value exceeds the representable range."));

            return (int) _double;
        }

        /// <summary>
        ///     Converts the current value to <c>long</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override long? getLong(NumericConversionOptions options, bool safe)
        {
            if (double.IsNaN(_double))
                return _long;

            if (!options.HasFlag(NumericConversionOptions.AllowTruncation) && _double != Math.Truncate(_double))
                return safe ? null : Ut.Throw<long?>(new InvalidCastException("Only integer values can be converted to long."));

            if (_double < long.MinValue || _double > long.MaxValue)
                return safe ? null : Ut.Throw<long?>(new InvalidCastException("Cannot cast to long because the value exceeds the representable range."));

            return (long) _double;
        }

        /// <summary>
        ///     Converts the current value to <c>string</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override string getString(StringConversionOptions options, bool safe)
        {
            if (!options.HasFlag(StringConversionOptions.AllowConversionFromNumber))
                return base.getString(options, safe);
            return double.IsNaN(_double) ? _long.ToString() : _double.ToString();
        }

        /// <summary>
        ///     Converts the current value to <c>bool</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override bool? getBool(BoolConversionOptions options, bool safe)
        {
            if (!options.HasFlag(BoolConversionOptions.AllowConversionFromNumber))
                return base.getBool(options, safe);
            return double.IsNaN(_double) ? (_long != 0) : (_double != 0);
        }

        /// <summary>Returns the value of this number as either a <c>double</c> or a <c>long</c>.</summary>
        public object RawValue { get { return double.IsNaN(_double) ? (object) _long : (object) _double; } }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is JsonNumber ? Equals((JsonNumber) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(JsonValue other)
        {
            return other is JsonNumber ? Equals((JsonNumber) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public bool Equals(JsonNumber other)
        {
            if (other == null) return false;
            if (double.IsNaN(this._double) && double.IsNaN(other._double))
                return this._long == other._long;
            else
                return (double) this == (double) other;
        }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            return double.IsNaN(_double) ? _long.GetHashCode() : _double.GetHashCode();
        }

        /// <summary>See <see cref="JsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return ToStringIndented();
        }

        /// <summary>Converts the current JSON value to a JSON string that parses back to this value.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0)
        {
            sb.Append(double.IsNaN(_double) ? _long.ToString() : ExactConvert.ToString(_double));
        }
    }

    /// <summary>
    ///     Represents a non-value when looking up a non-existent index or key in a list or dictionary.</summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item><description>
    ///             This is a singleton class; use <see cref="Instance"/> to access it.</description></item>
    ///         <item><description>
    ///             This class overloads the <c>==</c> operator such that comparing with <c>null</c> returns <c>true</c>.</description></item></list></remarks>
    [Serializable]
    public sealed class JsonNoValue : JsonValue
    {
        private JsonNoValue() { }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is JsonNoValue ? Equals((JsonNoValue) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(JsonValue other)
        {
            return other is JsonNoValue ? Equals((JsonNoValue) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public bool Equals(JsonNoValue other)
        {
            return other != null;
        }

        /// <summary>
        ///     Always returns true.</summary>
        /// <remarks>
        ///     <para>
        ///         This operator can only be invoked in three ways:</para>
        ///     <list type="bullet">
        ///         <item><description>
        ///             <c>JsonNoValue.Instance == JsonNoValue.Instance</c></description></item>
        ///         <item><description>
        ///             <c>JsonNoValue.Instance == null</c></description></item>
        ///         <item><description>
        ///             <c>null == JsonNoValue.Instance</c></description></item></list>
        ///     <para>
        ///         In all three cases, the intended comparison is <c>true</c>.</para></remarks>
        public static bool operator ==(JsonNoValue one, JsonNoValue two) { return true; }

        /// <summary>
        ///     Always returns false.</summary>
        /// <seealso cref="operator=="/>
        public static bool operator !=(JsonNoValue one, JsonNoValue two) { return false; }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode() { return 0; }

        /// <summary>See <see cref="JsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable() { return JsonValue.ToEnumerable(null); }

        /// <summary>Converts the current JSON value to a JSON string that parses back to this value.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0) { JsonValue.AppendIndented(null, sb, indentation); }

        /// <summary>Returns the singleton instance of this type.</summary>
        public static JsonNoValue Instance { get { return _instance; } }
        private static readonly JsonNoValue _instance = new JsonNoValue();

        /// <summary>
        ///     Converts the current value to <c>bool</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override bool? getBool(BoolConversionOptions options, bool safe) { return null; }
        /// <summary>
        ///     Converts the current value to <c>decimal</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override decimal? getDecimal(NumericConversionOptions options, bool safe) { return null; }
        /// <summary>
        ///     Converts the current value to <c>double</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override double? getDouble(NumericConversionOptions options, bool safe) { return null; }
        /// <summary>
        ///     Converts the current value to <c>int</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override int? getInt(NumericConversionOptions options, bool safe) { return null; }
        /// <summary>
        ///     Converts the current value to <c>long</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override long? getLong(NumericConversionOptions options, bool safe) { return null; }
        /// <summary>
        ///     Converts the current value to <c>string</c>.</summary>
        /// <param name="options">
        ///     Specifies options for the conversion.</param>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override string getString(StringConversionOptions options, bool safe) { return null; }
    }

    /// <summary>
    ///     Provides safe access to the indexers of a <see cref="JsonValue"/>. See <see cref="JsonValue.Safe"/> for details.</summary>
    [Serializable]
    public sealed class JsonSafeValue
    {
        /// <summary>Gets the underlying JSON value associated with this object.</summary>
        public JsonValue Value { get; private set; }

        /// <summary>
        ///     Constructor.</summary>
        /// <param name="value">
        ///     Specifies the underlying JSON value to provide safe access to.</param>
        public JsonSafeValue(JsonValue value) { Value = value is JsonNoValue ? null : value; }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            return Value == null ? 1 : Value.GetHashCode() + 1;
        }

        /// <summary>Determines whether the specified instance is equal to this one.</summary>
        public override bool Equals(object obj)
        {
            return obj is JsonSafeValue ? Equals((JsonSafeValue) obj) : false;
        }

        /// <summary>
        ///     Determines whether the specified instance is equal to this one. (See remarks.)</summary>
        /// <remarks>
        ///     Two instances of <see cref="JsonSafeValue"/> are considered equal if the underlying values are equal. See <see
        ///     cref="JsonValue.Equals(JsonValue)"/> for details.</remarks>
        public bool Equals(JsonSafeValue other)
        {
            if (other == null)
                return false;
            if (Value == null)
                return other.Value == null;
            return Value.Equals(other.Value);
        }

        /// <summary>
        ///     If the underlying value is a list, and the specified <paramref name="index"/> exists within the list, returns
        ///     the associated item; otherwise, returns a <see cref="JsonNoValue"/> instance.</summary>
        public JsonValue this[int index]
        {
            get
            {
                var list = Value as JsonList;
                if (list == null || index < 0 || index >= list.Count)
                    return JsonNoValue.Instance;
                return ((JsonList) Value)[index] ?? JsonNoValue.Instance;
            }
        }

        /// <summary>
        ///     If the underlying value is a dictionary, and the specified <paramref name="key"/> exists within the
        ///     dictionary, gets the value associated with that key; otherwise, returns a <see cref="JsonNoValue"/> instance.</summary>
        public JsonValue this[string key]
        {
            get
            {
                var dict = Value as JsonDict;
                JsonValue value;
                if (dict == null || !dict.TryGetValue(key, out value))
                    return JsonNoValue.Instance;
                return value ?? JsonNoValue.Instance;
            }
        }
    }

    /// <summary>
    ///     A special type of value which is never produced as a result of parsing valid JSON. Its sole purpose is to allow
    ///     embedding arbitrary JavaScript code using <see cref="JsonValue.Fmt"/>.</summary>
    [Serializable]
    public class JsonRaw : JsonValue
    {
        /// <summary>Gets the raw JSON.</summary>
        public string Raw { get; private set; }

        /// <summary>Constructs a <see cref="JsonRaw"/> instance from the specified raw JSON.</summary>
        public JsonRaw(string raw)
        {
            Raw = raw;
        }

        /// <summary>Generates a <see cref="JsonRaw"/> instance from the specified date/time stamp.</summary>
        public static JsonRaw FromDate(DateTime datetime)
        {
            return new JsonRaw
            (
                datetime.TimeOfDay == TimeSpan.Zero
                    ? "new Date({0}, {1}, {2})".Fmt(datetime.Year, datetime.Month - 1, datetime.Day)
                    : "new Date({0}, {1}, {2}, {3}, {4}, {5}, {6})".Fmt(datetime.Year, datetime.Month - 1, datetime.Day, datetime.Hour, datetime.Minute, datetime.Second, datetime.Millisecond)
            );
        }

        /// <summary>See <see cref="JsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return Raw;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is JsonRaw ? Equals((JsonRaw) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public override bool Equals(JsonValue other)
        {
            return other is JsonRaw ? Equals((JsonRaw) other) : false;
        }

        /// <summary>See <see cref="JsonValue.Equals(JsonValue)"/>.</summary>
        public bool Equals(JsonRaw other)
        {
            return other != null && Raw == other.Raw;
        }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            return Raw.GetHashCode();
        }

        /// <summary>Converts the current JSON value to a JSON string that parses back to this value.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0) { sb.Append(Raw); }
    }

    /// <summary>Provides extension methods for the JSON types.</summary>
    public static class JsonExtensions
    {
        /// <summary>
        ///     Creates a <see cref="JsonDict"/> from an input collection.</summary>
        /// <typeparam name="T">
        ///     Type of the input collection.</typeparam>
        /// <param name="source">
        ///     Input collection.</param>
        /// <param name="keySelector">
        ///     Function to map each input element to a key for the resulting dictionary.</param>
        /// <param name="valueSelector">
        ///     Function to map each input element to a value for the resulting dictionary.</param>
        /// <returns>
        ///     The constructed <see cref="JsonDict"/>.</returns>
        public static JsonDict ToJsonDict<T>(this IEnumerable<T> source, Func<T, string> keySelector, Func<T, JsonValue> valueSelector)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (keySelector == null)
                throw new ArgumentNullException("keySelector");
            if (valueSelector == null)
                throw new ArgumentNullException("valueSelector");

            var newDict = new JsonDict();
            foreach (var elem in source)
                newDict.Add(keySelector(elem), valueSelector(elem));
            return newDict;
        }

        /// <summary>
        ///     Creates a <see cref="JsonList"/> from an input collection.</summary>
        /// <typeparam name="T">
        ///     Type of the input collection.</typeparam>
        /// <param name="source">
        ///     Input collection.</param>
        /// <param name="elementSelector">
        ///     Function to map each input element to a <see cref="JsonValue"/> for the resulting list.</param>
        /// <returns>
        ///     The constructed <see cref="JsonList"/>.</returns>
        public static JsonList ToJsonList<T>(this IEnumerable<T> source, Func<T, JsonValue> elementSelector)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (elementSelector == null)
                throw new ArgumentNullException("elementSelector");

            var newList = new JsonList();
            foreach (var elem in source)
                newList.Add(elementSelector(elem));
            return newList;
        }

        /// <summary>
        ///     Creates a <see cref="JsonList"/> from an input collection.</summary>
        /// <param name="source">
        ///     Input collection.</param>
        /// <returns>
        ///     The constructed <see cref="JsonList"/>.</returns>
        public static JsonList ToJsonList(this IEnumerable<JsonValue> source)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            return new JsonList(source);
        }

        /// <summary>
        ///     Creates a <see cref="JsonList"/> from an input collection.</summary>
        /// <param name="source">
        ///     Input collection.</param>
        /// <returns>
        ///     The constructed <see cref="JsonList"/>.</returns>
        public static JsonList ToJsonList(this IEnumerable<string> source)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            return new JsonList(source.Select(str => (JsonValue) str));
        }
    }
}
