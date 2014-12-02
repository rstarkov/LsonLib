// This file is part of the LsonLib project, and is subject to the terms
// and conditions of the GPL v3 license, available in 'license.txt'.
// Imported from the RT.Util project.

using System;
using System.Diagnostics;

namespace LsonLib.Private
{
    static class Ut
    {
        /// <summary>
        ///     Throws the specified exception.</summary>
        /// <typeparam name="TResult">
        ///     The type to return.</typeparam>
        /// <param name="exception">
        ///     The exception to throw.</param>
        /// <returns>
        ///     This method never returns a value. It always throws.</returns>
        [DebuggerHidden]
        public static TResult Throw<TResult>(Exception exception)
        {
            throw exception;
        }

        /// <summary>Formats a string in a way compatible with <see cref="string.Format(string, object[])"/>.</summary>
        public static string Fmt(this string formatString, params object[] args)
        {
            return string.Format(formatString, args);
        }

        /// <summary>
        ///     Same as <see cref="string.Substring(int)"/> but does not throw exceptions when the start index falls outside
        ///     the boundaries of the string. Instead the result is truncated as appropriate.</summary>
        public static string SubstringSafe(this string source, int startIndex)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (startIndex >= source.Length)
                return "";
            else if (startIndex < 0)
                return source;
            else
                return source.Substring(startIndex);
        }

        /// <summary>
        ///     Same as <see cref="string.Substring(int, int)"/> but does not throw exceptions when the start index or length
        ///     (or both) fall outside the boundaries of the string. Instead the result is truncated as appropriate.</summary>
        public static string SubstringSafe(this string source, int startIndex, int length)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (startIndex < 0)
            {
                length += startIndex;
                startIndex = 0;
            }
            if (startIndex >= source.Length || length <= 0)
                return "";
            else if (startIndex + length > source.Length)
                return source.Substring(startIndex);
            else
                return source.Substring(startIndex, length);
        }
    }
}
