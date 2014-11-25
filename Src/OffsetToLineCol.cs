// This file is part of the RT.Util project, and is subject to the terms
// and conditions of the GPL v3 license, available in 'license.txt'.
// Originally by Roman Starkov and Timwi.

using System;
using System.Collections.Generic;

namespace LsonLib
{
    /// <summary>
    /// Encapsulates a class that offers efficient conversion of a string offset into line/column number. The class
    /// is best suited for multiple lookups on a single fixed string, and is suboptimal for single lookups into many
    /// different strings. All common newline styles are supported.
    /// </summary>
    [Serializable]
    public sealed class OffsetToLineCol
    {
        private int[] _lineStarts;

        /// <summary>Constructor: precomputes certain information to enable efficient lookups.</summary>
        /// <param name="input">The string on which the lookups will be performed.</param>
        public OffsetToLineCol(string input)
        {
            var ls = new List<int> { 0 };
            for (int i = 0; i < input.Length; i++)
                if (input[i] == '\n' || (input[i] == '\r' && (i == input.Length - 1 || input[i + 1] != '\n')))
                    ls.Add(i + 1);
            _lineStarts = ls.ToArray();
        }

        /// <summary>Gets the number of the line containing the character at the specified offset.</summary>
        /// <param name="offset">Offset of the character in question.</param>
        /// <returns>The number of the line containing the specified character (first line is number 1).</returns>
        public int GetLine(int offset)
        {
            return getLineIndex(offset) + 1;
        }

        /// <summary>Gets the number of the column containing the character at the specified offset.</summary>
        /// <param name="offset">Offset of the character in question.</param>
        /// <returns>The number of the column containing the specified character (first column is number 1).</returns>
        public int GetColumn(int offset)
        {
            return offset - _lineStarts[getLineIndex(offset)] + 1;
        }

        /// <summary>Gets the numbers of the line and column containing the character at the specified offset.</summary>
        /// <param name="offset">Offset of the character in question.</param>
        /// <param name="line">The number of the line containing the specified character (first line is number 1).</param>
        /// <param name="column">The number of the column containing the specified character (first column is number 1).</param>
        public void GetLineAndColumn(int offset, out int line, out int column)
        {
            line = getLineIndex(offset) + 1;
            column = offset - _lineStarts[line - 1] + 1;
        }

        private int getLineIndex(int offset)
        {
            int index = Array.BinarySearch(_lineStarts, offset);
            if (index >= 0)
                return index;
            else
                return (~index) - 1;
        }

    }
}
