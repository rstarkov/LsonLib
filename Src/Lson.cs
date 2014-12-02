// This file is part of the LsonLib project, and is subject to the terms
// and conditions of the GPL v3 license, available in 'license.txt'.
// Largely based on RT.Util.JsonValue, by Roman Starkov and Timwi.

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LsonLib
{
    using Private;

    /// <summary>
    ///     Provides methods for handling data in the "LSON" format, consisting of variable assignments in the Lua syntax.
    ///     Actual Lua code is not supported.</summary>
    public static class LsonVars
    {
        /// <summary>Parses an LSON string and returns a dictionary of variable definitions present in the input.</summary>
        public static Dictionary<string, LsonValue> Parse(string vars)
        {
            var ps = new LsonParserState(vars);
            var result = ps.ParseVars();
            if (ps.Cur != null)
                throw new LsonParseException(ps, "Unexpected characters after end of input.");
            return result;
        }

        /// <summary>Converts a dictionary of variable definitions back to the LSON format, reverting <see cref="Parse"/>.</summary>
        public static string ToString(Dictionary<string, LsonValue> vars)
        {
            var sb = new StringBuilder();
            foreach (var v in vars)
            {
                sb.Append("\r\n");
                sb.Append(v.Key);
                sb.Append(" = ");
                LsonValue.AppendIndented(v.Value, sb, 0);
            }
            sb.Append("\r\n");
            return sb.ToString();
        }
    }

    /// <summary>
    ///     Specifies the degree of strictness or leniency when converting a <see cref="LsonValue"/> to a numerical type such
    ///     as <c>int</c> or <c>double</c>.</summary>
    [Flags]
    public enum NumericConversionOptions
    {
        /// <summary>
        ///     The conversion only succeeds if the object is a <see cref="LsonNumber"/> and its value is exactly
        ///     representable by the target type.</summary>
        Strict = 0,
        /// <summary>The conversion succeeds if the object is a <see cref="LsonString"/> with numerical content.</summary>
        AllowConversionFromString = 1 << 0,
        /// <summary>
        ///     Ignored unless <see cref="AllowConversionFromString"/> is also specified. A conversion to an integer type
        ///     succeeds if the string contains a decimal followed by a zero fractional part.</summary>
        AllowZeroFractionToInteger = 1 << 1,
        /// <summary>
        ///     The conversion succeeds if the object is a <see cref="LsonBool"/>, which will convert to 0 if false and 1 if
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

    /// <summary>Specifies the degree of strictness or leniency when converting a <see cref="LsonValue"/> to a <c>string</c>.</summary>
    [Flags]
    public enum StringConversionOptions
    {
        /// <summary>The conversion only succeeds if the object is a <see cref="LsonString"/>.</summary>
        Strict = 0,
        /// <summary>The conversion succeeds if the object is a <see cref="LsonNumber"/>.</summary>
        AllowConversionFromNumber = 1 << 0,
        /// <summary>The conversion succeeds if the object is a <see cref="LsonBool"/>.</summary>
        AllowConversionFromBool = 1 << 1,

        /// <summary>Specifies maximum leniency.</summary>
        Lenient = AllowConversionFromNumber | AllowConversionFromBool
    }

    /// <summary>Specifies the degree of strictness or leniency when converting a <see cref="LsonValue"/> to a <c>bool</c>.</summary>
    [Flags]
    public enum BoolConversionOptions
    {
        /// <summary>The conversion only succeeds if the object is a <see cref="LsonBool"/>.</summary>
        Strict = 0,
        /// <summary>
        ///     The conversion succeeds if the object is a <see cref="LsonNumber"/>. 0 (zero) is converted to false, all other
        ///     values to true.</summary>
        AllowConversionFromNumber = 1 << 0,
        /// <summary>
        ///     The conversion succeeds if the object is a <see cref="LsonString"/> with specific content. The set of
        ///     permissible strings is controlled by <see cref="LsonString.True"/>, <see cref="LsonString.False"/> and <see
        ///     cref="LsonString.TrueFalseComparer"/>.</summary>
        AllowConversionFromString = 1 << 1,

        /// <summary>Specifies maximum leniency.</summary>
        Lenient = AllowConversionFromNumber | AllowConversionFromString
    }

    /// <summary>Represents a LSON parsing exception.</summary>
    [Serializable]
    public class LsonParseException : Exception
    {
        private LsonParserState _state;

        internal LsonParseException(LsonParserState ps, string message)
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
        /// <summary>A snippet of the LSON string at which the parse error occurred.</summary>
        public string Snippet { get { return _state.Snippet; } }
    }

    /// <summary>Keeps track of the LSON parser state.</summary>
    [Serializable]
    class LsonParserState
    {
        public string Lson;
        public int Pos;

        private OffsetToLineCol _offsetConverter;
        public OffsetToLineCol OffsetConverter { get { if (_offsetConverter == null) _offsetConverter = new OffsetToLineCol(Lson); return _offsetConverter; } }

        private LsonParserState() { }

        public LsonParserState(string lson)
        {
            Lson = lson;
            Pos = 0;
            ConsumeWhitespace();
        }

        public LsonParserState Clone()
        {
            var result = new LsonParserState();
            result.Lson = Lson;
            result.Pos = Pos;
            result._offsetConverter = _offsetConverter;
            return result;
        }

        public void ConsumeWhitespace()
        {
            while (Pos < Lson.Length)
            {
                var c = Lson[Pos];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                {
                    if (c == '-' && Pos + 1 < Lson.Length && Lson[Pos + 1] == '-')
                    {
                        ConsumeLineComment();
                        continue;
                    }
                    return;
                }
                Pos++;
            }
        }

        public void ConsumeLineComment()
        {
            while (Pos < Lson.Length)
            {
                var c = Lson[Pos];
                if (c == '\r' || c == '\n')
                    return;
                Pos++;
            }
        }

        public char? Cur { get { return Pos >= Lson.Length ? null : (char?) Lson[Pos]; } }

        public string Snippet
        {
            get
            {
                int line, col;
                OffsetConverter.GetLineAndColumn(Pos, out line, out col);
                return "Before: {2}   After: {3}   At: {0},{1}".Fmt(line, col, Lson.SubstringSafe(Pos - 15, 15), Lson.SubstringSafe(Pos, 15));
            }
        }

        public override string ToString()
        {
            return Snippet;
        }

        public LsonValue ParseValue()
        {
            var cn = Cur;
            switch (cn)
            {
                case null: throw new LsonParseException(this, "Unexpected end of input.");
                case '{': return ParseDict();
                case '\'':
                case '"': return ParseString();
                default:
                    var c = Cur.Value;
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber();
                    else if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z')
                        return parseWord();
                    else
                        throw new LsonParseException(this, "Unexpected character.");
            }
        }

        private LsonValue parseWord()
        {
            string word = peekLowercaseAzWord();
            if (word == "true")
            {
                Pos += word.Length;
                ConsumeWhitespace();
                return (LsonBool) true;
            }
            else if (word == "false")
            {
                Pos += word.Length;
                ConsumeWhitespace();
                return (LsonBool) false;
            }
            else if (word == "nil")
            {
                Pos += word.Length;
                ConsumeWhitespace();
                return null;
            }
            else
                throw new LsonParseException(this, "Unknown keyword: \"{0}\"".Fmt(word));
        }

        private string peekLowercaseAzWord()
        {
            var index = Pos;
            while (true)
            {
                if (index >= Lson.Length)
                    return Lson.Substring(Pos);
                var c = Lson[index];
                if (c < 'a' || c > 'z')
                    return Lson.Substring(Pos, index - Pos);
                index++;
            }
        }

        private LsonValue parseDictKey(LsonDict dict)
        {
            if (Cur == null)
                throw new LsonParseException(this, "Unexpected end of object literal.");

            if (Cur == '[')
            {
                Pos++;
                ConsumeWhitespace();
                var result = ParseValue();
                if (Cur != ']')
                    throw new LsonParseException(this, "Expected a ] at the end of a table key.");
                Pos++;
                ConsumeWhitespace();
                if (Cur != '=')
                    throw new LsonParseException(this, "Expected a = between table key and value.");
                Pos++;
                ConsumeWhitespace();
                return result;
            }
            else
            {
                return dict.Count + 1;
            }
        }

        public LsonString ParseString()
        {
            var sb = new StringBuilder();
            if (Cur != '"' && Cur != '\'')
                throw new LsonParseException(this, "Expected a string literal.");
            char openingQuote = Cur.Value;
            Pos++;
            while (true)
            {
                switch (Cur)
                {
                    case null: throw new LsonParseException(this, "Unexpected end of string literal.");
                    case '\\':
                        {
                            Pos++;
                            switch (Cur)
                            {
                                case null: throw new LsonParseException(this, "Unexpected end of string literal.");
                                case '"': sb.Append('"'); break;
                                case '\'': sb.Append('\''); break;
                                case '[': sb.Append('['); break;
                                case ']': sb.Append(']'); break;
                                case '\\': sb.Append('\\'); break;
                                case 'a': sb.Append('\a'); break;
                                case 'b': sb.Append('\b'); break;
                                case 'f': sb.Append('\f'); break;
                                case 'n': sb.Append('\n'); break;
                                case 'r': sb.Append('\r'); break;
                                case 't': sb.Append('\t'); break;
                                case 'v': sb.Append('\v'); break;
                                default:
                                    if (Cur >= '0' && Cur <= '9')
                                        throw new LsonParseException(this, "String escapes like \\d - \\ddd are not yet implemented.");
                                    throw new LsonParseException(this, "Unknown escape sequence.");
                            }
                        }
                        break;
                    default:
                        if (Cur == openingQuote)
                        {
                            Pos++;
                            goto while_break; // break out of the while... argh.
                        }
                        sb.Append(Cur.Value);
                        break;
                }
                Pos++;
            }
            while_break: ;
            ConsumeWhitespace();
            return new LsonString(sb.ToString());
        }

        public LsonBool ParseBool()
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
                throw new LsonParseException(this, "Expected a boolean.");
        }

        public LsonNumber ParseNumber()
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
                throw new LsonParseException(this, "Expected a single zero or a sequence of digits starting with a non-zero.");

            if (Cur == '.') // a decimal point followed by at least one digit
            {
                Pos++;
                if (!(Cur >= '0' && Cur <= '9'))
                    throw new LsonParseException(this, "Expected at least one digit following the decimal point.");
                while (Cur >= '0' && Cur <= '9')
                    Pos++;
            }

            if (Cur == 'e' || Cur == 'E')
            {
                Pos++;
                if (Cur == '+' || Cur == '-') // optional plus/minus
                    Pos++;
                if (!(Cur >= '0' && Cur <= '9'))
                    throw new LsonParseException(this, "Expected at least one digit following the exponent letter.");
                while (Cur >= '0' && Cur <= '9')
                    Pos++;
            }

            LsonNumber result;
            string number = Lson.Substring(fromPos, Pos - fromPos);
            long lng;
            if (!long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out lng))
            {
                double dbl;
                if (!double.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out dbl))
                    throw new LsonParseException(this, "Expected a number.");
                result = dbl;
            }
            else
                result = lng;
            ConsumeWhitespace();
            return result;
        }

        public LsonDict ParseDict()
        {
            var result = new LsonDict();
            if (Cur != '{')
                throw new LsonParseException(this, "Expected an object literal.");
            Pos++;
            ConsumeWhitespace();
            while (true)
            {
                if (Cur == null)
                    throw new LsonParseException(this, "Unexpected end of object literal.");
                if (Cur == '}')
                    break;
                var key = parseDictKey(result);
                if (Cur == null)
                    throw new LsonParseException(this, "Unexpected end of object literal.");
                result.Add(key, ParseValue());
                if (Cur == null)
                    throw new LsonParseException(this, "Unexpected end of object literal.");
                if (Cur == ',')
                {
                    Pos++;
                    ConsumeWhitespace();
                }
                else if (Cur != '}')
                    throw new LsonParseException(this, "Expected a comma between object properties.");
            }
            Pos++;
            ConsumeWhitespace();
            return result;
        }

        public Dictionary<string, LsonValue> ParseVars()
        {
            var result = new Dictionary<string, LsonValue>();
            ConsumeWhitespace();
            while (true)
            {
                if (Cur == null)
                    break;

                var index = Pos;
                while (true)
                {
                    if (index >= Lson.Length) break;
                    var c = Lson[index];
                    if ((c < 'a' || c > 'z') && (c < 'A' || c > 'Z') && (c < '0' || c > '9') && (c != '_')) break;
                    index++;
                }
                var word = Lson.Substring(Pos, index - Pos);
                if (word.Length == 0)
                    throw new LsonParseException(this, "Expected a variable name.");
                Pos += word.Length;
                ConsumeWhitespace();
                if (Cur != '=')
                    throw new LsonParseException(this, "Expected an = after variable name.");
                Pos++;
                ConsumeWhitespace();

                result.Add(word, ParseValue());
            }
            return result;
        }
    }

    /// <summary>Encapsulates a LSON value (e.g. a boolean, a number, a string, a dictionary, etc.)</summary>
    [Serializable]
    public abstract class LsonValue : DynamicObject, IEquatable<LsonValue>
    {
        /// <summary>
        ///     Parses the specified string into a LSON value.</summary>
        /// <param name="lsonValue">
        ///     A string containing LSON syntax.</param>
        /// <returns>
        ///     A <see cref="LsonValue"/> instance representing the value.</returns>
        public static LsonValue Parse(string lsonValue)
        {
            var ps = new LsonParserState(lsonValue);
            var result = ps.ParseValue();
            if (ps.Cur != null)
                throw new LsonParseException(ps, "Unexpected characters after end of input.");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a LSON value.</summary>
        /// <param name="lsonValue">
        ///     A string containing LSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="LsonValue"/> representing the value, or null if unsuccessful. (But note that null is
        ///     also a possible valid value in case of success.)</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string lsonValue, out LsonValue result)
        {
            try
            {
                result = Parse(lsonValue);
                return true;
            }
            catch (LsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>Constructs a <see cref="LsonValue"/> from the specified string.</summary>
        public static implicit operator LsonValue(string value) { return value == null ? null : new LsonString(value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified boolean.</summary>
        public static implicit operator LsonValue(bool value) { return new LsonBool(value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified nullable boolean.</summary>
        public static implicit operator LsonValue(bool? value) { return value == null ? null : new LsonBool(value.Value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified double.</summary>
        public static implicit operator LsonValue(double value) { return new LsonNumber(value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified nullable double.</summary>
        public static implicit operator LsonValue(double? value) { return value == null ? null : new LsonNumber(value.Value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified decimal.</summary>
        public static implicit operator LsonValue(decimal value) { return new LsonNumber(value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified nullable decimal.</summary>
        public static implicit operator LsonValue(decimal? value) { return value == null ? null : new LsonNumber(value.Value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified long.</summary>
        public static implicit operator LsonValue(long value) { return new LsonNumber(value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified nullable long.</summary>
        public static implicit operator LsonValue(long? value) { return value == null ? null : new LsonNumber(value.Value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified int.</summary>
        public static implicit operator LsonValue(int value) { return new LsonNumber(value); }
        /// <summary>Constructs a <see cref="LsonValue"/> from the specified nullable int.</summary>
        public static implicit operator LsonValue(int? value) { return value == null ? null : new LsonNumber(value.Value); }

        /// <summary>See <see cref="StringConversionOptions.Strict"/>.</summary>
        public static explicit operator string(LsonValue value) { return value == null ? (string) null : value.GetString(); }
        /// <summary>See <see cref="BoolConversionOptions.Strict"/>.</summary>
        public static explicit operator bool(LsonValue value) { return value.GetBool(); }
        /// <summary>See <see cref="BoolConversionOptions.Strict"/>.</summary>
        public static explicit operator bool?(LsonValue value) { return value == null ? (bool?) null : value.GetBool(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator double(LsonValue value) { return value.GetDouble(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator double?(LsonValue value) { return value == null ? (double?) null : value.GetDouble(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator decimal(LsonValue value) { return value.GetDecimal(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator decimal?(LsonValue value) { return value == null ? (decimal?) null : value.GetDecimal(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long(LsonValue value) { return value.GetLong(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long?(LsonValue value) { return value == null ? (long?) null : value.GetLong(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int(LsonValue value) { return value.GetInt(); }
        /// <summary>See <see cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int?(LsonValue value) { return value == null ? (int?) null : value.GetInt(); }

        /// <summary>
        ///     Returns an object that allows safe access to the indexers. “Safe” in this context means that the indexers,
        ///     when given an index or key not found in the dictionary, do not throw but instead return <see
        ///     cref="LsonNoValue.Instance"/> whose getters (such as <see cref="GetString"/>) return null.</summary>
        public LsonSafeValue Safe { get { return new LsonSafeValue(this); } }

        /// <summary>
        ///     Converts the current value to <see cref="LsonDict"/> if it is a <see cref="LsonDict"/>; otherwise, throws.</summary>
        public LsonDict GetDict() { return getDict(false); }

        /// <summary>
        ///     Converts the current value to <see cref="LsonDict"/> if it is a <see cref="LsonDict"/>; otherwise, returns
        ///     null.</summary>
        public LsonDict GetDictSafe() { return getDict(true); }

        /// <summary>
        ///     Converts the current value to <see cref="LsonDict"/>.</summary>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected virtual LsonDict getDict(bool safe) { return safe ? null : Ut.Throw<LsonDict>(new InvalidOperationException("Only dict values can be converted to dict.")); }

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

        #region IDictionary

        /// <summary>Removes all items from the current value if it is an <see cref="LsonDict"/>; otherwise, throws.</summary>
        public virtual void Clear()
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        /// <summary>Returns the number of items in the current value if it is an <see cref="LsonDict"/>; otherwise, throws.</summary>
        public virtual int Count
        {
            get
            {
                throw new InvalidOperationException("This method is only supported on dictionary values.");
            }
        }

        /// <summary>Returns true if this value is an <see cref="LsonDict"/>; otherwise, returns false.</summary>
        public virtual bool IsContainer { get { return false; } }

        /// <summary>
        ///     Gets or sets the value associated with the specified <paramref name="key"/> if this value is a <see
        ///     cref="LsonDict"/>; otherwise, throws.</summary>
        public virtual LsonValue this[LsonValue key]
        {
            get { throw new InvalidOperationException("This method is only supported on dictionary values."); }
            set { throw new InvalidOperationException("This method is only supported on dictionary values."); }
        }

        /// <summary>Returns the keys contained in the dictionary if this is a <see cref="LsonDict"/>; otherwise, throws.</summary>
        public virtual ICollection<LsonValue> Keys
        {
            get
            {
                throw new InvalidOperationException("This method is only supported on dictionary values.");
            }
        }

        /// <summary>Returns the values contained in the dictionary if this is a <see cref="LsonDict"/>; otherwise, throws.</summary>
        public virtual ICollection<LsonValue> Values
        {
            get
            {
                throw new InvalidOperationException("This method is only supported on dictionary values.");
            }
        }

        /// <summary>
        ///     Attempts to retrieve the value associated with the specified <paramref name="key"/> if this is a <see
        ///     cref="LsonDict"/>; otherwise, throws.</summary>
        /// <param name="key">
        ///     The key for which to try to retrieve the value.</param>
        /// <param name="value">
        ///     Receives the value associated with the specified <paramref name="key"/>, or null if the key is not in the
        ///     dictionary. (Note that null may also be a valid value in case of success.)</param>
        /// <returns>
        ///     True if the key was in the dictionary; otherwise, false.</returns>
        public virtual bool TryGetValue(LsonValue key, out LsonValue value)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        /// <summary>
        ///     Adds the specified key/value pair to the dictionary if this is a <see cref="LsonDict"/>; otherwise, throws.</summary>
        /// <param name="key">
        ///     The key to add.</param>
        /// <param name="value">
        ///     The value to add.</param>
        public virtual void Add(LsonValue key, LsonValue value)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        /// <summary>
        ///     Add the specified <paramref name="items"/> to the current dictionary if it is a <see cref="LsonDict"/>;
        ///     otherwise, throws.</summary>
        public virtual void AddRange(IEnumerable<KeyValuePair<LsonValue, LsonValue>> items)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        /// <summary>
        ///     Removes the entry with the specified <paramref name="key"/> from the dictionary if this is a <see
        ///     cref="LsonDict"/>; otherwise, throws.</summary>
        /// <param name="key">
        ///     The key that identifies the entry to remove.</param>
        /// <returns>
        ///     True if an entry was removed; false if the key wasn’t in the dictionary.</returns>
        public virtual bool Remove(LsonValue key)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        /// <summary>
        ///     Determines whether an entry with the specified <paramref name="key"/> exists in the dictionary if this is a
        ///     <see cref="LsonDict"/>; otherwise, throws.</summary>
        public virtual bool ContainsKey(LsonValue key)
        {
            throw new InvalidOperationException("This method is only supported on dictionary values.");
        }

        #endregion

        /// <summary>
        ///     Determines whether this value is equal to the <paramref name="other"/> value. (See also remarks in the other
        ///     overload, <see cref="Equals(LsonValue)"/>.)</summary>
        public override bool Equals(object other)
        {
            return other is LsonValue ? Equals((LsonValue) other) : false;
        }

        /// <summary>
        ///     Determines whether this value is equal to the <paramref name="other"/> value. (See also remarks.)</summary>
        /// <remarks>
        ///     Two values are only considered equal if they are of the same type (e.g. a <see cref="LsonString"/> is never
        ///     equal to a <see cref="LsonNumber"/> even if they contain the same number). Dictionaries are equal if they
        ///     contain the same set of key/value pairs.</remarks>
        public abstract bool Equals(LsonValue other);

        /// <summary>Returns a hash code representing this object.</summary>
        public abstract override int GetHashCode();

        /// <summary>Converts the LSON value to a LSON string that parses back to this value. Supports null values.</summary>
        public static string ToString(LsonValue value)
        {
            return value == null ? "nil" : value.ToString();
        }

        /// <summary>Converts the current LSON value to a LSON string that parses back to this value.</summary>
        public override string ToString()
        {
            return string.Join("", ToEnumerable());
        }

        /// <summary>Converts the LSON value to a LSON string that parses back to this value. Supports null values.</summary>
        public static string ToStringIndented(LsonValue value)
        {
            return value == null ? "nil" : value.ToStringIndented();
        }

        /// <summary>Converts the current LSON value to a LSON string that parses back to this value.</summary>
        public string ToStringIndented()
        {
            var sb = new StringBuilder();
            AppendIndented(this, sb);
            return sb.ToString();
        }

        /// <summary>
        ///     Converts the LSON value to a LSON string that parses back to this value and places the string into the
        ///     specified StringBuilder. Supports null values.</summary>
        public static void AppendIndented(LsonValue value, StringBuilder sb, int indentation = 0)
        {
            if (value == null)
                sb.Append("nil");
            else
                value.AppendIndented(sb, indentation);
        }

        /// <summary>
        ///     Converts the current LSON value to a LSON void that parses back to this value and places the string into the
        ///     specified StringBuilder.</summary>
        public abstract void AppendIndented(StringBuilder sb, int indentation = 0);

        /// <summary>Lazy-converts the LSON value to a LSON string that parses back to this value. Supports null values.</summary>
        public static IEnumerable<string> ToEnumerable(LsonValue value)
        {
            if (value == null)
            {
                yield return "nil";
                yield break;
            }
            foreach (var piece in value.ToEnumerable())
                yield return piece;
        }

        /// <summary>Lazy-converts the current LSON value to a LSON string that parses back to this value.</summary>
        public abstract IEnumerable<string> ToEnumerable();
    }

    /// <summary>Encapsulates a LSON dictionary (a set of key/value pairs).</summary>
    [Serializable]
    public sealed class LsonDict : LsonValue, IDictionary<LsonValue, LsonValue>, IEquatable<LsonDict>
    {
        internal Dictionary<LsonValue, LsonValue> Dict;

        /// <summary>Constructs an empty dictionary.</summary>
        public LsonDict() { Dict = new Dictionary<LsonValue, LsonValue>(); }

        /// <summary>Constructs a dictionary containing a copy of the specified collection of key/value pairs.</summary>
        public LsonDict(IEnumerable<KeyValuePair<LsonValue, LsonValue>> items)
        {
            if (items == null)
                throw new ArgumentNullException("items");
            Dict = new Dictionary<LsonValue, LsonValue>(items is ICollection<KeyValuePair<LsonValue, LsonValue>> ? ((ICollection<KeyValuePair<LsonValue, LsonValue>>) items).Count + 2 : 4);
            foreach (var item in items)
                Dict.Add(item.Key, item.Value);
        }

        /// <summary>
        ///     Parses the specified LSON as a LSON dictionary. All other types of LSON values result in a <see
        ///     cref="LsonParseException"/>.</summary>
        /// <param name="lsonDict">
        ///     LSON syntax to parse.</param>
        public static new LsonDict Parse(string lsonDict)
        {
            var ps = new LsonParserState(lsonDict);
            var result = ps.ParseDict();
            if (ps.Cur != null)
                throw new LsonParseException(ps, "expected end of input");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a LSON dictionary.</summary>
        /// <param name="lsonDict">
        ///     A string containing LSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="LsonDict"/> representing the dictionary, or null if unsuccessful.</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string lsonDict, out LsonDict result)
        {
            try
            {
                result = Parse(lsonDict);
                return true;
            }
            catch (LsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>
        ///     Converts the current value to <see cref="LsonDict"/>.</summary>
        /// <param name="safe">
        ///     Controls the behavior in case of conversion failure. If true, returns null; if false, throws.</param>
        protected override LsonDict getDict(bool safe) { return this; }

        /// <summary>Enumerates the key/value pairs in this dictionary.</summary>
        public IEnumerator<KeyValuePair<LsonValue, LsonValue>> GetEnumerator()
        {
            return Dict.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #region IEnumerable<KeyValuePair> methods

        void ICollection<KeyValuePair<LsonValue, LsonValue>>.Add(KeyValuePair<LsonValue, LsonValue> item)
        {
            ((ICollection<KeyValuePair<LsonValue, LsonValue>>) Dict).Add(item);
        }
        bool ICollection<KeyValuePair<LsonValue, LsonValue>>.Remove(KeyValuePair<LsonValue, LsonValue> item)
        {
            return ((ICollection<KeyValuePair<LsonValue, LsonValue>>) Dict).Remove(item);
        }
        bool ICollection<KeyValuePair<LsonValue, LsonValue>>.Contains(KeyValuePair<LsonValue, LsonValue> item)
        {
            return ((ICollection<KeyValuePair<LsonValue, LsonValue>>) Dict).Contains(item);
        }
        void ICollection<KeyValuePair<LsonValue, LsonValue>>.CopyTo(KeyValuePair<LsonValue, LsonValue>[] array, int arrayIndex)
        {
            ((ICollection<KeyValuePair<LsonValue, LsonValue>>) Dict).CopyTo(array, arrayIndex);
        }

        #endregion

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is LsonDict && Equals((LsonDict) other);
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(LsonValue other)
        {
            return other is LsonDict ? Equals((LsonDict) other) : false;
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public bool Equals(LsonDict other)
        {
            if (other == null) return false;
            if (this.Count != other.Count) return false;
            foreach (var kvp in this)
            {
                LsonValue val;
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

        /// <summary>See <see cref="LsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return "{";
            bool first = true;
            foreach (var kvp in Dict)
            {
                if (!first)
                    yield return ",";
                foreach (var piece in LsonValue.ToEnumerable(kvp.Key))
                    yield return piece;
                yield return ":";
                foreach (var piece in LsonValue.ToEnumerable(kvp.Value))
                    yield return piece;
                first = false;
            }
            yield return "}";
        }

        /// <summary>Converts the LSON value to a LSON string that parses back to this value. Supports null values.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0)
        {
            if (Dict.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            int implicitIndex = 0;
            foreach (var kvp in Dict)
            {
                if (implicitIndex >= 0)
                    implicitIndex++;
                sb.AppendLine();
                for (int i = 0; i <= indentation; i++)
                    sb.Append('\t');
                if (kvp.Key.Equals((LsonNumber) implicitIndex))
                {
                    LsonValue.AppendIndented(kvp.Value, sb, indentation + 1);
                    sb.Append(',');
                    sb.Append(" -- [" + implicitIndex + "]");
                }
                else
                {
                    implicitIndex = -1;
                    sb.Append('[');
                    LsonValue.AppendIndented(kvp.Key, sb, indentation + 1);
                    sb.Append("] = ");
                    LsonValue.AppendIndented(kvp.Value, sb, indentation + 1);
                    sb.Append(',');
                }
            }
            sb.AppendLine();
            for (int i = 0; i < indentation; i++)
                sb.Append('\t');
            sb.Append("}");
        }

        /// <summary>Removes all items from the current dictionary.</summary>
        public override void Clear() { Dict.Clear(); }

        /// <summary>Returns the number of items in the current dictionary.</summary>
        public override int Count { get { return Dict.Count; } }

        /// <summary>Returns true.</summary>
        public override bool IsContainer { get { return true; } }

        /// <summary>Gets or sets the value associated with the specified <paramref name="key"/>.</summary>
        public override LsonValue this[LsonValue key]
        {
            get { return Dict[key]; }
            set { Dict[key] = value; }
        }

        /// <summary>Returns the keys contained in the dictionary.</summary>
        public override ICollection<LsonValue> Keys { get { return Dict.Keys; } }

        /// <summary>Returns the values contained in the dictionary.</summary>
        public override ICollection<LsonValue> Values { get { return Dict.Values; } }

        /// <summary>
        ///     Attempts to retrieve the value associated with the specified <paramref name="key"/>.</summary>
        /// <param name="key">
        ///     The key for which to try to retrieve the value.</param>
        /// <param name="value">
        ///     Receives the value associated with the specified <paramref name="key"/>, or null if the key is not in the
        ///     dictionary. (Note that null may also be a valid value in case of success.)</param>
        /// <returns>
        ///     True if the key was in the dictionary; otherwise, false.</returns>
        public override bool TryGetValue(LsonValue key, out LsonValue value) { return Dict.TryGetValue(key, out value); }

        /// <summary>
        ///     Adds the specified key/value pair to the dictionary.</summary>
        /// <param name="key">
        ///     The key to add.</param>
        /// <param name="value">
        ///     The value to add.</param>
        public override void Add(LsonValue key, LsonValue value) { Dict.Add(key, value); }

        /// <summary>Adds the specified key/value pairs to the dictionary.</summary>
        public override void AddRange(IEnumerable<KeyValuePair<LsonValue, LsonValue>> items)
        {
            foreach (var item in items)
                ((ICollection<KeyValuePair<LsonValue, LsonValue>>) Dict).Add(item);
        }

        /// <summary>
        ///     Removes the entry with the specified <paramref name="key"/> from the dictionary.</summary>
        /// <param name="key">
        ///     The key that identifies the entry to remove.</param>
        /// <returns>
        ///     True if an entry was removed; false if the key wasn’t in the dictionary.</returns>
        public override bool Remove(LsonValue key) { return Dict.Remove(key); }

        /// <summary>Determines whether an entry with the specified <paramref name="key"/> exists in the dictionary.</summary>
        public override bool ContainsKey(LsonValue key) { return Dict.ContainsKey(key); }

        bool ICollection<KeyValuePair<LsonValue, LsonValue>>.IsReadOnly { get { return false; } }

        /// <summary>
        ///     Implements functionality that allows the keys in this LSON dictionary to be accessed as dynamic members.</summary>
        /// <example>
        ///     <code>
        ///         dynamic dict = LsonDict.Parse(@"{ [""Foo""] = "abc" }");
        ///         Console.WriteLine(dict.Foo);     // outputs "abc"</code></example>
        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            LsonValue value;
            if (Dict.TryGetValue(binder.Name, out value))
            {
                result = value;
                return true;
            }
            result = null;
            return false;
        }
    }

    /// <summary>Encapsulates a string as a LSON value.</summary>
    [Serializable]
    public sealed class LsonString : LsonValue, IEquatable<LsonString>
    {
        private string _value;

        /// <summary>Constructs a <see cref="LsonString"/> instance from the specified string.</summary>
        public LsonString(string value)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            _value = value;
        }

        /// <summary>
        ///     Parses the specified LSON as a LSON string. All other types of LSON values result in a <see
        ///     cref="LsonParseException"/>.</summary>
        /// <param name="lsonString">
        ///     LSON syntax to parse.</param>
        public static new LsonString Parse(string lsonString)
        {
            var ps = new LsonParserState(lsonString);
            var result = ps.ParseString();
            if (ps.Cur != null)
                throw new LsonParseException(ps, "expected end of input");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a LSON string.</summary>
        /// <param name="lsonString">
        ///     A string containing LSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="LsonString"/> representing the string, or null if unsuccessful.</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string lsonString, out LsonString result)
        {
            try
            {
                result = Parse(lsonString);
                return true;
            }
            catch (LsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>Converts the specified <see cref="LsonString"/> value to an ordinary string.</summary>
        public static implicit operator string(LsonString value) { return value == null ? null : value._value; }
        /// <summary>Converts the specified ordinary string to a <see cref="LsonString"/> value.</summary>
        public static implicit operator LsonString(string value) { return value == null ? null : new LsonString(value); }

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
                return safe ? null : Ut.Throw<double?>(new InvalidOperationException("This string cannot be converted to a double because LSON doesn't support NaNs and infinities."));

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
        ///     Controls which string values are converted to <c>false</c> when using <see cref="LsonValue.GetBool"/> with
        ///     <see cref="BoolConversionOptions.AllowConversionFromString"/>.</summary>
        /// <remarks>
        ///     The default is: <c>{ "", "false", "n", "no", "off", "disable", "disabled", "0" }</c>.</remarks>
        public static readonly List<string> False = new List<string> { "", "false", "n", "no", "off", "disable", "disabled", "0" };
        /// <summary>
        ///     Controls which string values are converted to <c>true</c> when using <see cref="LsonValue.GetBool"/> with <see
        ///     cref="BoolConversionOptions.AllowConversionFromString"/>.</summary>
        /// <remarks>
        ///     The default is: <c>{ "true", "y", "yes", "on", "enable", "enabled", "1" }</c>.</remarks>
        public static readonly List<string> True = new List<string> { "true", "y", "yes", "on", "enable", "enabled", "1" };
        /// <summary>
        ///     Controls which string equality comparer is used when comparing strings against elements in <see cref="True"/>
        ///     and <see cref="False"/> during conversion to bool by <see cref="LsonValue.GetBool"/>.</summary>
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

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is LsonString ? Equals((LsonString) other) : false;
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(LsonValue other)
        {
            return other is LsonString ? Equals((LsonString) other) : false;
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public bool Equals(LsonString other)
        {
            return other != null && _value == other._value;
        }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        /// <summary>See <see cref="LsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return ToStringIndented();
        }

        /// <summary>Converts the current LSON value to a LSON string that parses back to this value.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0)
        {
            if (sb == null)
                throw new ArgumentNullException("sb");

            sb.Append('"');
            foreach (var c in _value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\a': sb.Append("\\a"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c <= 31)
                        {
                            sb.Append('\\');
                            sb.Append(((int) c).ToString());
                        }
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>
        ///     Returns a Lua-compatible representation of this string.</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            AppendIndented(sb);
            return sb.ToString();
        }
    }

    /// <summary>Encapsulates a boolean value as a <see cref="LsonValue"/>.</summary>
    [Serializable]
    public sealed class LsonBool : LsonValue, IEquatable<LsonBool>
    {
        private bool _value;

        /// <summary>Constructs a <see cref="LsonBool"/> from the specified boolean.</summary>
        public LsonBool(bool value) { _value = value; }

        /// <summary>
        ///     Parses the specified LSON as a LSON boolean. All other types of LSON values result in a <see
        ///     cref="LsonParseException"/>.</summary>
        /// <param name="lsonBool">
        ///     LSON syntax to parse.</param>
        public static new LsonBool Parse(string lsonBool)
        {
            var ps = new LsonParserState(lsonBool);
            var result = ps.ParseBool();
            if (ps.Cur != null)
                throw new LsonParseException(ps, "expected end of input");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a LSON boolean.</summary>
        /// <param name="lsonBool">
        ///     A string containing LSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="LsonBool"/> representing the boolean, or null if unsuccessful.</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string lsonBool, out LsonBool result)
        {
            try
            {
                result = Parse(lsonBool);
                return true;
            }
            catch (LsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>Converts the specified <see cref="LsonBool"/> value to an ordinary boolean.</summary>
        public static explicit operator bool(LsonBool value) { return value._value; }
        /// <summary>Converts the specified <see cref="LsonBool"/> value to a nullable boolean.</summary>
        public static implicit operator bool?(LsonBool value) { return value == null ? (bool?) null : value._value; }
        /// <summary>Converts the specified ordinary boolean to a <see cref="LsonBool"/> value.</summary>
        public static implicit operator LsonBool(bool value) { return new LsonBool(value); }
        /// <summary>Converts the specified nullable boolean to a <see cref="LsonBool"/> value or null.</summary>
        public static implicit operator LsonBool(bool? value) { return value == null ? null : new LsonBool(value.Value); }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is LsonBool ? Equals((LsonBool) other) : false;
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(LsonValue other)
        {
            return other is LsonBool ? Equals((LsonBool) other) : false;
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public bool Equals(LsonBool other)
        {
            return other != null && _value == other._value;
        }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            return _value ? 13259 : 22093;
        }

        /// <summary>See <see cref="LsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return ToStringIndented();
        }

        /// <summary>Converts the current LSON value to a LSON string that parses back to this value.</summary>
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
    ///     Encapsulates a number, which may be a floating-point number or an integer, as a <see cref="LsonValue"/>. See
    ///     Remarks.</summary>
    /// <remarks>
    ///     LSON does not define any specific limits for numeric values. This implementation supports integers in the signed
    ///     64-bit range, as well as IEEE 64-bit doubles (except NaNs and infinities). Conversions to/from <c>decimal</c> are
    ///     exact for integers, but can be approximate for non-integers, depending on the exact value.</remarks>
    [Serializable]
    public sealed class LsonNumber : LsonValue, IEquatable<LsonNumber>
    {
        private long _long;
        private double _double = double.NaN;

        /// <summary>Constructs a <see cref="LsonNumber"/> from the specified double-precision floating-point number.</summary>
        public LsonNumber(double value) { _double = value; if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("LSON disallows NaNs and infinities."); }
        /// <summary>Constructs a <see cref="LsonNumber"/> from the specified 64-bit integer.</summary>
        public LsonNumber(long value) { _long = value; }
        /// <summary>Constructs a <see cref="LsonNumber"/> from the specified 32-bit integer.</summary>
        public LsonNumber(int value) { _long = value; }
        /// <summary>
        ///     Constructs a <see cref="LsonNumber"/> from the specified decimal. This operation is slightly lossy; see
        ///     Remarks on <see cref="LsonNumber"/>.</summary>
        public LsonNumber(decimal value)
        {
            if (value == decimal.Truncate(value) && value >= long.MinValue && value <= long.MaxValue)
                _long = (long) value;
            else
                _double = (double) value;
        }

        /// <summary>
        ///     Parses the specified LSON as a LSON number. All other types of LSON values result in a <see
        ///     cref="LsonParseException"/>.</summary>
        /// <param name="lsonNumber">
        ///     LSON syntax to parse.</param>
        public static new LsonNumber Parse(string lsonNumber)
        {
            var ps = new LsonParserState(lsonNumber);
            var result = ps.ParseNumber();
            if (ps.Cur != null)
                throw new LsonParseException(ps, "expected end of input");
            return result;
        }

        /// <summary>
        ///     Attempts to parse the specified string into a LSON number.</summary>
        /// <param name="lsonNumber">
        ///     A string containing LSON syntax.</param>
        /// <param name="result">
        ///     Receives the <see cref="LsonNumber"/> representing the number, or null if unsuccessful.</param>
        /// <returns>
        ///     True if parsing was successful; otherwise, false.</returns>
        public static bool TryParse(string lsonNumber, out LsonNumber result)
        {
            try
            {
                result = Parse(lsonNumber);
                return true;
            }
            catch (LsonParseException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>Converts the specified <see cref="LsonNumber"/> to a double.</summary>
        public static explicit operator double(LsonNumber value) { return double.IsNaN(value._double) ? (double) value._long : value._double; }
        /// <summary>Converts the specified <see cref="LsonNumber"/> to a nullable double.</summary>
        public static implicit operator double?(LsonNumber value) { return value == null ? (double?) null : double.IsNaN(value._double) ? (double) value._long : value._double; }

        /// <summary>
        ///     Converts the specified <see cref="LsonNumber"/> to a decimal. This operator is slightly lossy; see Remarks on
        ///     <see cref="LsonNumber"/>.</summary>
        public static explicit operator decimal(LsonNumber value) { return double.IsNaN(value._double) ? (decimal) value._long : (decimal) value._double; }
        /// <summary>
        ///     Converts the specified <see cref="LsonNumber"/> to a nullable decimal. This operator is slightly lossy; see
        ///     Remarks on <see cref="LsonNumber"/>.</summary>
        public static explicit operator decimal?(LsonNumber value) { return value == null ? (decimal?) null : (decimal) value; }

        /// <summary>
        ///     Converts the specified <see cref="LsonNumber"/> to a 64-bit integer. See <see
        ///     cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long(LsonNumber value)
        {
            if (value == null)
                throw new InvalidCastException("null cannot be cast to long.");
            return value.GetLong(); // use default strict mode
        }
        /// <summary>
        ///     Converts the specified <see cref="LsonNumber"/> to a nullable 64-bit integer. See <see
        ///     cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator long?(LsonNumber value) { return value == null ? (long?) null : (long) value; }

        /// <summary>
        ///     Converts the specified <see cref="LsonNumber"/> to a 32-bit integer. See <see
        ///     cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int(LsonNumber value)
        {
            if (value == null)
                throw new InvalidCastException("null cannot be cast to int.");
            return value.GetInt();  // use default strict mode
        }
        /// <summary>
        ///     Converts the specified <see cref="LsonNumber"/> to a nullable 32-bit integer. See <see
        ///     cref="NumericConversionOptions.Strict"/>.</summary>
        public static explicit operator int?(LsonNumber value) { return value == null ? (int?) null : (int) value; }

        /// <summary>Converts the specified double to a <see cref="LsonNumber"/> value.</summary>
        public static implicit operator LsonNumber(double value) { return new LsonNumber(value); }
        /// <summary>Converts the specified nullable double to a <see cref="LsonNumber"/> value.</summary>
        public static implicit operator LsonNumber(double? value) { return value == null ? null : new LsonNumber(value.Value); }
        /// <summary>Converts the specified 64-bit integer to a <see cref="LsonNumber"/> value.</summary>
        public static implicit operator LsonNumber(long value) { return new LsonNumber(value); }
        /// <summary>Converts the specified nullable 64-bit integer to a <see cref="LsonNumber"/> value.</summary>
        public static implicit operator LsonNumber(long? value) { return value == null ? null : new LsonNumber(value.Value); }
        /// <summary>Converts the specified 32-bit integer to a <see cref="LsonNumber"/> value.</summary>
        public static implicit operator LsonNumber(int value) { return new LsonNumber(value); }
        /// <summary>Converts the specified nullable 32-bit integer to a <see cref="LsonNumber"/> value.</summary>
        public static implicit operator LsonNumber(int? value) { return value == null ? null : new LsonNumber(value.Value); }
        /// <summary>
        ///     Converts the specified decimal to a <see cref="LsonNumber"/> value. This operator is slightly lossy; see
        ///     Remarks on <see cref="LsonNumber"/>.</summary>
        public static explicit operator LsonNumber(decimal value) { return new LsonNumber(value); }
        /// <summary>
        ///     Converts the specified nullable decimal to a <see cref="LsonNumber"/> value. This operator is slightly lossy;
        ///     see Remarks on <see cref="LsonNumber"/>.</summary>
        public static explicit operator LsonNumber(decimal? value) { return value == null ? null : new LsonNumber(value.Value); }

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

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is LsonNumber ? Equals((LsonNumber) other) : false;
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(LsonValue other)
        {
            return other is LsonNumber ? Equals((LsonNumber) other) : false;
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public bool Equals(LsonNumber other)
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

        /// <summary>See <see cref="LsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable()
        {
            yield return ToStringIndented();
        }

        /// <summary>Converts the current LSON value to a LSON string that parses back to this value.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0)
        {
            sb.Append(double.IsNaN(_double) ? _long.ToString() : _double.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    ///     Represents a non-value when looking up a non-existent index or key in a dictionary.</summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item><description>
    ///             This is a singleton class; use <see cref="Instance"/> to access it.</description></item>
    ///         <item><description>
    ///             This class overloads the <c>==</c> operator such that comparing with <c>null</c> returns <c>true</c>.</description></item></list></remarks>
    [Serializable]
    public sealed class LsonNoValue : LsonValue
    {
        private LsonNoValue() { }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(object other)
        {
            return other is LsonNoValue ? Equals((LsonNoValue) other) : false;
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public override bool Equals(LsonValue other)
        {
            return other is LsonNoValue ? Equals((LsonNoValue) other) : false;
        }

        /// <summary>See <see cref="LsonValue.Equals(LsonValue)"/>.</summary>
        public bool Equals(LsonNoValue other)
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
        ///             <c>LsonNoValue.Instance == LsonNoValue.Instance</c></description></item>
        ///         <item><description>
        ///             <c>LsonNoValue.Instance == null</c></description></item>
        ///         <item><description>
        ///             <c>null == LsonNoValue.Instance</c></description></item></list>
        ///     <para>
        ///         In all three cases, the intended comparison is <c>true</c>.</para></remarks>
        public static bool operator ==(LsonNoValue one, LsonNoValue two) { return true; }

        /// <summary>
        ///     Always returns false.</summary>
        /// <seealso cref="operator=="/>
        public static bool operator !=(LsonNoValue one, LsonNoValue two) { return false; }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode() { return 0; }

        /// <summary>See <see cref="LsonValue.ToEnumerable()"/>.</summary>
        public override IEnumerable<string> ToEnumerable() { return LsonValue.ToEnumerable(null); }

        /// <summary>Converts the current LSON value to a LSON string that parses back to this value.</summary>
        public override void AppendIndented(StringBuilder sb, int indentation = 0) { LsonValue.AppendIndented(null, sb, indentation); }

        /// <summary>Returns the singleton instance of this type.</summary>
        public static LsonNoValue Instance { get { return _instance; } }
        private static readonly LsonNoValue _instance = new LsonNoValue();

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
    ///     Provides safe access to the indexers of a <see cref="LsonValue"/>. See <see cref="LsonValue.Safe"/> for details.</summary>
    [Serializable]
    public sealed class LsonSafeValue
    {
        /// <summary>Gets the underlying LSON value associated with this object.</summary>
        public LsonValue Value { get; private set; }

        /// <summary>
        ///     Constructor.</summary>
        /// <param name="value">
        ///     Specifies the underlying LSON value to provide safe access to.</param>
        public LsonSafeValue(LsonValue value) { Value = value is LsonNoValue ? null : value; }

        /// <summary>Returns a hash code representing this object.</summary>
        public override int GetHashCode()
        {
            return Value == null ? 1 : Value.GetHashCode() + 1;
        }

        /// <summary>Determines whether the specified instance is equal to this one.</summary>
        public override bool Equals(object obj)
        {
            return obj is LsonSafeValue ? Equals((LsonSafeValue) obj) : false;
        }

        /// <summary>
        ///     Determines whether the specified instance is equal to this one. (See remarks.)</summary>
        /// <remarks>
        ///     Two instances of <see cref="LsonSafeValue"/> are considered equal if the underlying values are equal. See <see
        ///     cref="LsonValue.Equals(LsonValue)"/> for details.</remarks>
        public bool Equals(LsonSafeValue other)
        {
            if (other == null)
                return false;
            if (Value == null)
                return other.Value == null;
            return Value.Equals(other.Value);
        }

        /// <summary>
        ///     If the underlying value is a dictionary, and the specified <paramref name="key"/> exists within the
        ///     dictionary, gets the value associated with that key; otherwise, returns a <see cref="LsonNoValue"/> instance.</summary>
        public LsonValue this[string key]
        {
            get
            {
                var dict = Value as LsonDict;
                LsonValue value;
                if (dict == null || !dict.TryGetValue(key, out value))
                    return LsonNoValue.Instance;
                return value ?? LsonNoValue.Instance;
            }
        }
    }

    /// <summary>Provides extension methods for the LSON types.</summary>
    public static class LsonExtensions
    {
        /// <summary>
        ///     Creates a <see cref="LsonDict"/> from an input collection.</summary>
        /// <typeparam name="T">
        ///     Type of the input collection.</typeparam>
        /// <param name="source">
        ///     Input collection.</param>
        /// <param name="keySelector">
        ///     Function to map each input element to a key for the resulting dictionary.</param>
        /// <param name="valueSelector">
        ///     Function to map each input element to a value for the resulting dictionary.</param>
        /// <returns>
        ///     The constructed <see cref="LsonDict"/>.</returns>
        public static LsonDict ToLsonDict<T>(this IEnumerable<T> source, Func<T, string> keySelector, Func<T, LsonValue> valueSelector)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (keySelector == null)
                throw new ArgumentNullException("keySelector");
            if (valueSelector == null)
                throw new ArgumentNullException("valueSelector");

            var newDict = new LsonDict();
            foreach (var elem in source)
                newDict.Add(keySelector(elem), valueSelector(elem));
            return newDict;
        }
    }
}
