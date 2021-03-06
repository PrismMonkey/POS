#region License

// Copyright (c) 2007 James Newton-King
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Lib.JSON.Utilities;

namespace Lib.JSON
{
    /// <summary>
    /// Represents a reader that provides fast, non-cached, forward-only access to serialized Json data.
    /// </summary>
    public class JsonTextReader : JsonReader, IJsonLineInfo
    {
        private enum ReadType
        {
            Read,
            ReadAsInt32,
            ReadAsBytes,
            ReadAsDecimal,
            #if !NET20
            ReadAsDateTimeOffset
            #endif
        }
        
        private ReadType _readType;
        
        private readonly TextReader _reader;
        
        private char[] _chars;
        private int _charsUsed;
        private int _charPos;
        private int _lineStartPos;
        private int _lineNumber;
        private bool _isEndOfFile;
        private StringBuffer _buffer;
        private StringReference _stringReference;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonReader"/> class with the specified <see cref="TextReader"/>.
        /// </summary>
        /// <param name="reader">The <c>TextReader</c> containing the XML data to read.</param>
        public JsonTextReader(TextReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }

            this._reader = reader;
            this._lineNumber = 1;

            this._chars = new char[4097];
        }
        
        internal void SetCharBuffer(char[] chars)
        {
            this._chars = chars;
        }
        
        private StringBuffer GetBuffer()
        {
            if (this._buffer == null)
            {
                this._buffer = new StringBuffer(4096);
            }
            else
            {
                this._buffer.Position = 0;
            }

            return this._buffer;
        }
        
        private void OnNewLine(int pos)
        {
            this._lineNumber++;
            this._lineStartPos = pos - 1;
        }
        
        private void ParseString(char quote)
        {
            this._charPos++;

            this.ShiftBufferIfNeeded();
            this.ReadStringIntoBuffer(quote);
            
            if (this._readType == ReadType.ReadAsBytes)
            {
                byte[] data;
                if (this._stringReference.Length == 0)
                {
                    data = new byte[0];
                }
                else
                {
                    data = Convert.FromBase64CharArray(this._stringReference.Chars, this._stringReference.StartIndex, this._stringReference.Length);
                }
                
                this.SetToken(JsonToken.Bytes, data);
            }
            else
            {
                string text = this._stringReference.ToString();
                
                if (text.StartsWith("/Date(", StringComparison.Ordinal) && text.EndsWith(")/", StringComparison.Ordinal))
                {
                    this.ParseDate(text);
                }
                else
                {
                    this.SetToken(JsonToken.String, text);
                    this.QuoteChar = quote;
                }
            }
        }
        
        private static void BlockCopyChars(char[] src, int srcOffset, char[] dst, int dstOffset, int count)
        {
            const int charByteCount = 2;

            Buffer.BlockCopy(src, srcOffset * charByteCount, dst, dstOffset * charByteCount, count * charByteCount);
        }
        
        private void ShiftBufferIfNeeded()
        {
            // once in the last 10% of the buffer shift the remainling content to the start to avoid
            // unnessesarly increasing the buffer size when reading numbers/strings
            int length = this._chars.Length;
            if (length - this._charPos <= length * 0.1)
            {
                int count = this._charsUsed - this._charPos;
                if (count > 0)
                {
                    BlockCopyChars(this._chars, this._charPos, this._chars, 0, count);
                }
                
                this._lineStartPos -= this._charPos;
                this._charPos = 0;
                this._charsUsed = count;
                this._chars[this._charsUsed] = '\0';
            }
        }
        
        private int ReadData(bool append)
        {
            return this.ReadData(append, 0);
        }
        
        private int ReadData(bool append, int charsRequired)
        {
            if (this._isEndOfFile)
            {
                return 0;
            }
            
            // char buffer is full
            if (this._charsUsed + charsRequired >= this._chars.Length - 1)
            {
                if (append)
                {
                    // copy to new array either double the size of the current or big enough to fit required content
                    int newArrayLength = Math.Max(this._chars.Length * 2, this._charsUsed + charsRequired + 1);

                    // increase the size of the buffer
                    char[] dst = new char[newArrayLength];
                    
                    BlockCopyChars(this._chars, 0, dst, 0, this._chars.Length);
                    
                    this._chars = dst;
                }
                else
                {
                    int remainingCharCount = this._charsUsed - this._charPos;
                    
                    if (remainingCharCount + charsRequired + 1 >= this._chars.Length)
                    {
                        // the remaining count plus the required is bigger than the current buffer size
                        char[] dst = new char[remainingCharCount + charsRequired + 1];
                        
                        if (remainingCharCount > 0)
                        {
                            BlockCopyChars(this._chars, this._charPos, dst, 0, remainingCharCount);
                        }
                        
                        this._chars = dst;
                    }
                    else
                    {
                        // copy any remaining data to the beginning of the buffer if needed and reset positions
                        if (remainingCharCount > 0)
                        {
                            BlockCopyChars(this._chars, this._charPos, this._chars, 0, remainingCharCount);
                        }
                    }
                    
                    this._lineStartPos -= this._charPos;
                    this._charPos = 0;
                    this._charsUsed = remainingCharCount;
                }
            }
            
            int attemptCharReadCount = this._chars.Length - this._charsUsed - 1;

            int charsRead = this._reader.Read(this._chars, this._charsUsed, attemptCharReadCount);
            this._charsUsed += charsRead;
            
            if (charsRead == 0)
            {
                this._isEndOfFile = true;
            }
            
            this._chars[this._charsUsed] = '\0';
            return charsRead;
        }
        
        private bool EnsureChars(int relativePosition, bool append)
        {
            if (this._charPos + relativePosition >= this._charsUsed)
            {
                return this.ReadChars(relativePosition, append);
            }

            return true;
        }
        
        private bool ReadChars(int relativePosition, bool append)
        {
            if (this._isEndOfFile)
            {
                return false;
            }
            
            int charsRequired = this._charPos + relativePosition - this._charsUsed + 1;
            
            int charsRead = this.ReadData(append, charsRequired);
            
            if (charsRead < charsRequired)
            {
                return false;
            }
            return true;
        }
        
        private static TimeSpan ReadOffset(string offsetText)
        {
            bool negative = (offsetText[0] == '-');
            
            int hours = int.Parse(offsetText.Substring(1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture);
            int minutes = 0;
            if (offsetText.Length >= 5)
            {
                minutes = int.Parse(offsetText.Substring(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            
            TimeSpan offset = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
            if (negative)
            {
                offset = offset.Negate();
            }

            return offset;
        }
        
        private void ParseDate(string text)
        {
            string value = text.Substring(6, text.Length - 8);
            DateTimeKind kind = DateTimeKind.Utc;
            
            int index = value.IndexOf('+', 1);
            
            if (index == -1)
            {
                index = value.IndexOf('-', 1);
            }
            
            TimeSpan offset = TimeSpan.Zero;
            
            if (index != -1)
            {
                kind = DateTimeKind.Local;
                offset = ReadOffset(value.Substring(index));
                value = value.Substring(0, index);
            }
            
            long javaScriptTicks = long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            
            DateTime utcDateTime = JsonConvert.ConvertJavaScriptTicksToDateTime(javaScriptTicks);
            
            #if !NET20
            if (this._readType == ReadType.ReadAsDateTimeOffset)
            {
                this.SetToken(JsonToken.Date, new DateTimeOffset(utcDateTime.Add(offset).Ticks, offset));
            }
            else
            #endif
            {
                DateTime dateTime;
                
                switch (kind)
                {
                    case DateTimeKind.Unspecified:
                        dateTime = DateTime.SpecifyKind(utcDateTime.ToLocalTime(), DateTimeKind.Unspecified);
                        break;
                    case DateTimeKind.Local:
                        dateTime = utcDateTime.ToLocalTime();
                        break;
                    default:
                        dateTime = utcDateTime;
                        break;
                }
                
                this.SetToken(JsonToken.Date, dateTime);
            }
        }
        
        /// <summary>
        /// Reads the next JSON token from the stream.
        /// </summary>
        /// <returns>
        /// true if the next token was read successfully; false if there are no more tokens to read.
        /// </returns>
        [DebuggerStepThrough]
        public override bool Read()
        {
            this._readType = ReadType.Read;
            return this.ReadInternal();
        }
        
        private bool IsWrappedInTypeObject()
        {
            this._readType = ReadType.Read;
            
            if (this.TokenType == JsonToken.StartObject)
            {
                if (!this.ReadInternal())
                {
                    throw this.CreateReaderException(this, "Unexpected end when reading bytes.");
                }
                
                if (this.Value.ToString() == "$type")
                {
                    this.ReadInternal();
                    if (this.Value != null && this.Value.ToString().StartsWith("System.Byte[]"))
                    {
                        this.ReadInternal();
                        if (this.Value.ToString() == "$value")
                        {
                            return true;
                        }
                    }
                }

                throw this.CreateReaderException(this, "Unexpected token when reading bytes: {0}.".FormatWith(CultureInfo.InvariantCulture, JsonToken.StartObject));
            }

            return false;
        }
        
        /// <summary>
        /// Reads the next JSON token from the stream as a <see cref="T:Byte[]"/>.
        /// </summary>
        /// <returns>
        /// A <see cref="T:Byte[]"/> or a null reference if the next JSON token is null. This method will return <c>null</c> at the end of an array.
        /// </returns>
        public override byte[] ReadAsBytes()
        {
            this._readType = ReadType.ReadAsBytes;
            
            do
            {
                if (!this.ReadInternal())
                {
                    throw this.CreateReaderException(this, "Unexpected end when reading bytes.");
                }
            }
            while (this.TokenType == JsonToken.Comment);
            
            if (this.IsWrappedInTypeObject())
            {
                byte[] data = this.ReadAsBytes();
                this.ReadInternal();
                this.SetToken(JsonToken.Bytes, data);
                return data;
            }
            
            if (this.TokenType == JsonToken.Null)
            {
                return null;
            }
            
            if (this.TokenType == JsonToken.Bytes)
            {
                return (byte[])this.Value;
            }
            
            if (this.TokenType == JsonToken.StartArray)
            {
                List<byte> data = new List<byte>();
                
                while (this.ReadInternal())
                {
                    switch (this.TokenType)
                    {
                        case JsonToken.Integer:
                            data.Add(Convert.ToByte(this.Value, CultureInfo.InvariantCulture));
                            break;
                        case JsonToken.EndArray:
                            byte[] d = data.ToArray();
                            this.SetToken(JsonToken.Bytes, d);
                            return d;
                        case JsonToken.Comment:
                            // skip
                            break;
                        default:
                            throw this.CreateReaderException(this, "Unexpected token when reading bytes: {0}.".FormatWith(CultureInfo.InvariantCulture, TokenType));
                    }
                }

                throw this.CreateReaderException(this, "Unexpected end when reading bytes.");
            }
            
            if (this.TokenType == JsonToken.EndArray)
            {
                return null;
            }

            throw this.CreateReaderException(this, "Unexpected token when reading bytes: {0}.".FormatWith(CultureInfo.InvariantCulture, TokenType));
        }
        
        /// <summary>
        /// Reads the next JSON token from the stream as a <see cref="Nullable{Decimal}"/>.
        /// </summary>
        /// <returns>A <see cref="Nullable{Decimal}"/>. This method will return <c>null</c> at the end of an array.</returns>
        public override decimal? ReadAsDecimal()
        {
            this._readType = ReadType.ReadAsDecimal;
            
            do
            {
                if (!this.ReadInternal())
                {
                    throw this.CreateReaderException(this, "Unexpected end when reading decimal.");
                }
            }
            while (this.TokenType == JsonToken.Comment);
            
            if (this.TokenType == JsonToken.Float)
            {
                return (decimal?)this.Value;
            }
            
            if (this.TokenType == JsonToken.Null)
            {
                return null;
            }
            
            decimal d;
            if (this.TokenType == JsonToken.String)
            {
                if (decimal.TryParse((string)this.Value, NumberStyles.Number, this.Culture, out d))
                {
                    this.SetToken(JsonToken.Float, d);
                    return d;
                }
                else
                {
                    throw this.CreateReaderException(this, "Could not convert string to decimal: {0}.".FormatWith(CultureInfo.InvariantCulture, Value));
                }
            }
            
            if (this.TokenType == JsonToken.EndArray)
            {
                return null;
            }

            throw this.CreateReaderException(this, "Unexpected token when reading decimal: {0}.".FormatWith(CultureInfo.InvariantCulture, TokenType));
        }
        
        /// <summary>
        /// Reads the next JSON token from the stream as a <see cref="Nullable{Int32}"/>.
        /// </summary>
        /// <returns>A <see cref="Nullable{Int32}"/>. This method will return <c>null</c> at the end of an array.</returns>
        public override int? ReadAsInt32()
        {
            this._readType = ReadType.ReadAsInt32;
            
            do
            {
                if (!this.ReadInternal())
                {
                    throw this.CreateReaderException(this, "Unexpected end when reading integer.");
                }
            }
            while (this.TokenType == JsonToken.Comment);
            
            if (this.TokenType == JsonToken.Integer)
            {
                return (int?)this.Value;
            }
            
            if (this.TokenType == JsonToken.Null)
            {
                return null;
            }
            
            int i;
            if (this.TokenType == JsonToken.String)
            {
                if (int.TryParse((string)this.Value, NumberStyles.Integer, this.Culture, out i))
                {
                    this.SetToken(JsonToken.Integer, i);
                    return i;
                }
                else
                {
                    throw this.CreateReaderException(this, "Could not convert string to integer: {0}.".FormatWith(CultureInfo.InvariantCulture, Value));
                }
            }
            
            if (this.TokenType == JsonToken.EndArray)
            {
                return null;
            }

            throw this.CreateReaderException(this, "Unexpected token when reading integer: {0}.".FormatWith(CultureInfo.InvariantCulture, TokenType));
        }
        
        #if !NET20
        /// <summary>
        /// Reads the next JSON token from the stream as a <see cref="Nullable{DateTimeOffset}"/>.
        /// </summary>
        /// <returns>A <see cref="DateTimeOffset"/>. This method will return <c>null</c> at the end of an array.</returns>
        public override DateTimeOffset? ReadAsDateTimeOffset()
        {
            this._readType = ReadType.ReadAsDateTimeOffset;
            
            do
            {
                if (!this.ReadInternal())
                {
                    throw this.CreateReaderException(this, "Unexpected end when reading date.");
                }
            }
            while (this.TokenType == JsonToken.Comment);
            
            if (this.TokenType == JsonToken.Date)
            {
                return (DateTimeOffset)this.Value;
            }
            
            if (this.TokenType == JsonToken.Null)
            {
                return null;
            }
            
            DateTimeOffset dt;
            if (this.TokenType == JsonToken.String)
            {
                if (DateTimeOffset.TryParse((string)this.Value, this.Culture, DateTimeStyles.None, out dt))
                {
                    this.SetToken(JsonToken.Date, dt);
                    return dt;
                }
                else
                {
                    throw this.CreateReaderException(this, "Could not convert string to DateTimeOffset: {0}.".FormatWith(CultureInfo.InvariantCulture, Value));
                }
            }
            
            if (this.TokenType == JsonToken.EndArray)
            {
                return null;
            }

            throw this.CreateReaderException(this, "Unexpected token when reading date: {0}.".FormatWith(CultureInfo.InvariantCulture, TokenType));
        }
        
        #endif
        
        private bool ReadInternal()
        {
            while (true)
            {
                switch (this._currentState)
                {
                    case State.Start:
                    case State.Property:
                    case State.Array:
                    case State.ArrayStart:
                    case State.Constructor:
                    case State.ConstructorStart:
                        return this.ParseValue();
                    case State.Complete:
                        break;
                    case State.Object:
                    case State.ObjectStart:
                        return this.ParseObject();
                    case State.PostValue:
                        // returns true if it hits
                        // end of object or array
                        if (this.ParsePostValue())
                        {
                            return true;
                        }
                        break;
                    case State.Finished:
                        if (this.EnsureChars(0, false))
                        {
                            this.EatWhitespace(false);
                            if (this._isEndOfFile)
                            {
                                return false;
                            }
                            if (this._chars[this._charPos] == '/')
                            {
                                this.ParseComment();
                                return true;
                            }
                            else
                            {
                                throw this.CreateReaderException(this, "Additional text encountered after finished reading JSON content: {0}.".FormatWith(CultureInfo.InvariantCulture, _chars[_charPos]));
                            }
                        }
                        return false;
                    case State.Closed:
                        break;
                    case State.Error:
                        break;
                    default:
                        throw this.CreateReaderException(this, "Unexpected state: {0}.".FormatWith(CultureInfo.InvariantCulture, CurrentState));
                }
            }
        }
        
        private void ReadStringIntoBuffer(char quote)
        {
            int charPos = this._charPos;
            int initialPosition = this._charPos;
            int lastWritePosition = this._charPos;
            StringBuffer buffer = null;
            
            while (true)
            {
                switch (this._chars[charPos++])
                {
                    case '\0':
                        if (this._charsUsed == charPos - 1)
                        {
                            charPos--;
                            
                            if (this.ReadData(true) == 0)
                            {
                                this._charPos = charPos;
                                throw this.CreateReaderException(this, "Unterminated string. Expected delimiter: {0}.".FormatWith(CultureInfo.InvariantCulture, quote));
                            }
                        }
                        break;
                    case '\\':
                        this._charPos = charPos;
                        if (!this.EnsureChars(0, true))
                        {
                            this._charPos = charPos;
                            throw this.CreateReaderException(this, "Unterminated string. Expected delimiter: {0}.".FormatWith(CultureInfo.InvariantCulture, quote));
                        }

                        // start of escape sequence
                        int escapeStartPos = charPos - 1;
                        
                        char currentChar = this._chars[charPos];
                        
                        char writeChar;
                        
                        switch (currentChar)
                        {
                            case 'b':
                                charPos++;
                                writeChar = '\b';
                                break;
                            case 't':
                                charPos++;
                                writeChar = '\t';
                                break;
                            case 'n':
                                charPos++;
                                writeChar = '\n';
                                break;
                            case 'f':
                                charPos++;
                                writeChar = '\f';
                                break;
                            case 'r':
                                charPos++;
                                writeChar = '\r';
                                break;
                            case '\\':
                                charPos++;
                                writeChar = '\\';
                                break;
                            case '"':
                            case '\'':
                            case '/':
                                writeChar = currentChar;
                                charPos++;
                                break;
                            case 'u':
                                charPos++;
                                this._charPos = charPos;
                                if (this.EnsureChars(4, true))
                                {
                                    string hexValues = new string(this._chars, charPos, 4);
                                    char hexChar = Convert.ToChar(int.Parse(hexValues, NumberStyles.HexNumber, NumberFormatInfo.InvariantInfo));
                                    writeChar = hexChar;
                                    
                                    charPos += 4;
                                }
                                else
                                {
                                    this._charPos = charPos;
                                    throw this.CreateReaderException(this, "Unexpected end while parsing unicode character.");
                                }
                                break;
                            default:
                                charPos++;
                                this._charPos = charPos;
                                throw this.CreateReaderException(this, "Bad JSON escape sequence: {0}.".FormatWith(CultureInfo.InvariantCulture, string.Format("{0}{1}", @"\", currentChar)));
                        }
                        
                        if (buffer == null)
                        {
                            buffer = this.GetBuffer();
                        }
                        
                        if (escapeStartPos > lastWritePosition)
                        {
                            buffer.Append(this._chars, lastWritePosition, escapeStartPos - lastWritePosition);
                        }
                        
                        buffer.Append(writeChar);
                        
                        lastWritePosition = charPos;
                        break;
                    case StringUtils.CarriageReturn:
                        this._charPos = charPos - 1;
                        this.ProcessCarriageReturn(true);
                        charPos = this._charPos;
                        break;
                    case StringUtils.LineFeed:
                        this._charPos = charPos - 1;
                        this.ProcessLineFeed();
                        charPos = this._charPos;
                        break;
                    case '"':
                    case '\'':
                        if (this._chars[charPos - 1] == quote)
                        {
                            charPos--;
                            
                            if (initialPosition == lastWritePosition)
                            {
                                this._stringReference = new StringReference(this._chars, initialPosition, charPos - initialPosition);
                            }
                            else
                            {
                                if (buffer == null)
                                {
                                    buffer = this.GetBuffer();
                                }
                                
                                if (charPos > lastWritePosition)
                                {
                                    buffer.Append(this._chars, lastWritePosition, charPos - lastWritePosition);
                                }

                                this._stringReference = new StringReference(buffer.GetInternalBuffer(), 0, buffer.Position);
                            }
                            
                            charPos++;
                            this._charPos = charPos;
                            return;
                        }
                        break;
                }
            }
        }
        
        private void ReadNumberIntoBuffer()
        {
            int charPos = this._charPos;
            
            while (true)
            {
                switch (this._chars[charPos++])
                {
                    case '\0':
                        if (this._charsUsed == charPos - 1)
                        {
                            charPos--;
                            this._charPos = charPos;
                            if (this.ReadData(true) == 0)
                            {
                                return;
                            }
                        }
                        break;
                    case '-':
                    case '+':
                    case 'a':
                    case 'A':
                    case 'b':
                    case 'B':
                    case 'c':
                    case 'C':
                    case 'd':
                    case 'D':
                    case 'e':
                    case 'E':
                    case 'f':
                    case 'F':
                    case 'x':
                    case 'X':
                    case '.':
                    case '0':
                    case '1':
                    case '2':
                    case '3':
                    case '4':
                    case '5':
                    case '6':
                    case '7':
                    case '8':
                    case '9':
                        break;
                    default:
                        this._charPos = charPos - 1;
                        return;
                }
            }
        }
        
        private void ClearRecentString()
        {
            if (this._buffer != null)
            {
                this._buffer.Position = 0;
            }

            this._stringReference = new StringReference();
        }
        
        private bool ParsePostValue()
        {
            while (true)
            {
                char currentChar = this._chars[this._charPos];
                
                switch (currentChar)
                {
                    case '\0':
                        if (this._charsUsed == this._charPos)
                        {
                            if (this.ReadData(false) == 0)
                            {
                                this._currentState = State.Finished;
                                return false;
                            }
                        }
                        else
                        {
                            this._charPos++;
                        }
                        break;
                    case '}':
                        this._charPos++;
                        this.SetToken(JsonToken.EndObject);
                        return true;
                    case ']':
                        this._charPos++;
                        this.SetToken(JsonToken.EndArray);
                        return true;
                    case ')':
                        this._charPos++;
                        this.SetToken(JsonToken.EndConstructor);
                        return true;
                    case '/':
                        this.ParseComment();
                        return true;
                    case ',':
                        this._charPos++;
                        
                        // finished parsing
                        this.SetStateBasedOnCurrent();
                        return false;
                    case ' ':
                    case StringUtils.Tab:
                        // eat
                        this._charPos++;
                        break;
                    case StringUtils.CarriageReturn:
                        this.ProcessCarriageReturn(false);
                        break;
                    case StringUtils.LineFeed:
                        this.ProcessLineFeed();
                        break;
                    default:
                        if (char.IsWhiteSpace(currentChar))
                        {
                            // eat
                            this._charPos++;
                        }
                        else
                        {
                            throw this.CreateReaderException(this, "After parsing a value an unexpected character was encountered: {0}.".FormatWith(CultureInfo.InvariantCulture, currentChar));
                        }
                        break;
                }
            }
        }
        
        private bool ParseObject()
        {
            while (true)
            {
                char currentChar = this._chars[this._charPos];
                
                switch (currentChar)
                {
                    case '\0':
                        if (this._charsUsed == this._charPos)
                        {
                            if (this.ReadData(false) == 0)
                            {
                                return false;
                            }
                        }
                        else
                        {
                            this._charPos++;
                        }
                        break;
                    case '}':
                        this.SetToken(JsonToken.EndObject);
                        this._charPos++;
                        return true;
                    case '/':
                        this.ParseComment();
                        return true;
                    case StringUtils.CarriageReturn:
                        this.ProcessCarriageReturn(false);
                        break;
                    case StringUtils.LineFeed:
                        this.ProcessLineFeed();
                        break;
                    case ' ':
                    case StringUtils.Tab:
                        // eat
                        this._charPos++;
                        break;
                    default:
                        if (char.IsWhiteSpace(currentChar))
                        {
                            // eat
                            this._charPos++;
                        }
                        else
                        {
                            return this.ParseProperty();
                        }
                        break;
                }
            }
        }
        
        private bool ParseProperty()
        {
            char firstChar = this._chars[this._charPos];
            char quoteChar;
            
            if (firstChar == '"' || firstChar == '\'')
            {
                this._charPos++;
                quoteChar = firstChar;
                this.ShiftBufferIfNeeded();
                this.ReadStringIntoBuffer(quoteChar);
            }
            else if (this.ValidIdentifierChar(firstChar))
            {
                quoteChar = '\0';
                this.ShiftBufferIfNeeded();
                this.ParseUnquotedProperty();
            }
            else
            {
                throw this.CreateReaderException(this, "Invalid property identifier character: {0}.".FormatWith(CultureInfo.InvariantCulture, _chars[_charPos]));
            }
            
            string propertyName = this._stringReference.ToString();
            
            this.EatWhitespace(false);
            
            if (this._chars[this._charPos] != ':')
            {
                throw this.CreateReaderException(this, "Invalid character after parsing property name. Expected ':' but got: {0}.".FormatWith(CultureInfo.InvariantCulture, _chars[_charPos]));
            }
            
            this._charPos++;
            
            this.SetToken(JsonToken.PropertyName, propertyName);
            this.QuoteChar = quoteChar;
            this.ClearRecentString();

            return true;
        }
        
        private bool ValidIdentifierChar(char value)
        {
            return (char.IsLetterOrDigit(value) || value == '_' || value == '$');
        }
        
        private void ParseUnquotedProperty()
        {
            int initialPosition = this._charPos;
            
            // parse unquoted property name until whitespace or colon
            while (true)
            {
                switch (this._chars[this._charPos])
                {
                    case '\0':
                        if (this._charsUsed == this._charPos)
                        {
                            if (this.ReadData(true) == 0)
                            {
                                throw this.CreateReaderException(this, "Unexpected end while parsing unquoted property name.");
                            }

                            break;
                        }
                        
                        this._stringReference = new StringReference(this._chars, initialPosition, this._charPos - initialPosition);
                        return;
                    default:
                        char currentChar = this._chars[this._charPos];
                        
                        if (this.ValidIdentifierChar(currentChar))
                        {
                            this._charPos++;
                            break;
                        }
                        else if (char.IsWhiteSpace(currentChar) || currentChar == ':')
                        {
                            this._stringReference = new StringReference(this._chars, initialPosition, this._charPos - initialPosition);
                            return;
                        }
                        
                        throw this.CreateReaderException(this, "Invalid JavaScript property identifier character: {0}.".FormatWith(CultureInfo.InvariantCulture, currentChar));
                }
            }
        }
        
        private bool ParseValue()
        {
            while (true)
            {
                char currentChar = this._chars[this._charPos];
                
                switch (currentChar)
                {
                    case '\0':
                        if (this._charsUsed == this._charPos)
                        {
                            if (this.ReadData(false) == 0)
                            {
                                return false;
                            }
                        }
                        else
                        {
                            this._charPos++;
                        }
                        break;
                    case '"':
                    case '\'':
                        this.ParseString(currentChar);
                        return true;
                    case 't':
                        this.ParseTrue();
                        return true;
                    case 'f':
                        this.ParseFalse();
                        return true;
                    case 'n':
                        if (this.EnsureChars(1, true))
                        {
                            char next = this._chars[this._charPos + 1];
                            
                            if (next == 'u')
                            {
                                this.ParseNull();
                            }
                            else if (next == 'e')
                            {
                                this.ParseConstructor();
                            }
                            else
                            {
                                throw this.CreateReaderException(this, "Unexpected character encountered while parsing value: {0}.".FormatWith(CultureInfo.InvariantCulture, _chars[_charPos]));
                            }
                        }
                        else
                        {
                            throw this.CreateReaderException(this, "Unexpected end.");
                        }
                        return true;
                    case 'N':
                        this.ParseNumberNaN();
                        return true;
                    case 'I':
                        this.ParseNumberPositiveInfinity();
                        return true;
                    case '-':
                        if (this.EnsureChars(1, true) && this._chars[this._charPos + 1] == 'I')
                        {
                            this.ParseNumberNegativeInfinity();
                        }
                        else
                        {
                            this.ParseNumber();
                        }
                        return true;
                    case '/':
                        this.ParseComment();
                        return true;
                    case 'u':
                        this.ParseUndefined();
                        return true;
                    case '{':
                        this._charPos++;
                        this.SetToken(JsonToken.StartObject);
                        return true;
                    case '[':
                        this._charPos++;
                        this.SetToken(JsonToken.StartArray);
                        return true;
                    case ']':
                        this._charPos++;
                        this.SetToken(JsonToken.EndArray);
                        return true;
                    case ',':
                        // don't increment position, the next call to read will handle comma
                        // this is done to handle multiple empty comma values
                        this.SetToken(JsonToken.Undefined);
                        return true;
                    case ')':
                        this._charPos++;
                        this.SetToken(JsonToken.EndConstructor);
                        return true;
                    case StringUtils.CarriageReturn:
                        this.ProcessCarriageReturn(false);
                        break;
                    case StringUtils.LineFeed:
                        this.ProcessLineFeed();
                        break;
                    case ' ':
                    case StringUtils.Tab:
                        // eat
                        this._charPos++;
                        break;
                    default:
                        if (char.IsWhiteSpace(currentChar))
                        {
                            // eat
                            this._charPos++;
                            break;
                        }
                        else if (char.IsNumber(currentChar) || currentChar == '-' || currentChar == '.')
                        {
                            this.ParseNumber();
                            return true;
                        }
                        else
                        {
                            throw this.CreateReaderException(this, "Unexpected character encountered while parsing value: {0}.".FormatWith(CultureInfo.InvariantCulture, currentChar));
                        }
                }
            }
        }
        
        private void ProcessLineFeed()
        {
            this._charPos++;
            this.OnNewLine(_charPos);
        }
        
        private void ProcessCarriageReturn(bool append)
        {
            this._charPos++;
            
            if (this.EnsureChars(1, append) && this._chars[this._charPos] == StringUtils.LineFeed)
            {
                this._charPos++;
            }

            this.OnNewLine(_charPos);
        }
        
        private bool EatWhitespace(bool oneOrMore)
        {
            bool finished = false;
            bool ateWhitespace = false;
            while (!finished)
            {
                char currentChar = this._chars[this._charPos];
                
                switch (currentChar)
                {
                    case '\0':
                        if (this._charsUsed == this._charPos)
                        {
                            if (this.ReadData(false) == 0)
                            {
                                finished = true;
                            }
                        }
                        else
                        {
                            this._charPos++;
                        }
                        break;
                    case StringUtils.CarriageReturn:
                        this.ProcessCarriageReturn(false);
                        break;
                    case StringUtils.LineFeed:
                        this.ProcessLineFeed();
                        break;
                    default:
                        if (currentChar == ' ' || char.IsWhiteSpace(currentChar))
                        {
                            ateWhitespace = true;
                            this._charPos++;
                        }
                        else
                        {
                            finished = true;
                        }
                        break;
                }
            }

            return (!oneOrMore || ateWhitespace);
        }
        
        private void ParseConstructor()
        {
            if (this.MatchValueWithTrailingSeperator("new"))
            {
                this.EatWhitespace(false);

                int initialPosition = this._charPos;
                int endPosition;
                
                while (true)
                {
                    char currentChar = this._chars[this._charPos];
                    if (currentChar == '\0')
                    {
                        if (this._charsUsed == this._charPos)
                        {
                            if (this.ReadData(true) == 0)
                            {
                                throw this.CreateReaderException(this, "Unexpected end while parsing constructor.");
                            }
                        }
                        else
                        {
                            endPosition = this._charPos;
                            this._charPos++;
                            break;
                        }
                    }
                    else if (char.IsLetterOrDigit(currentChar))
                    {
                        this._charPos++;
                    }
                    else if (currentChar == StringUtils.CarriageReturn)
                    {
                        endPosition = this._charPos;
                        this.ProcessCarriageReturn(true);
                        break;
                    }
                    else if (currentChar == StringUtils.LineFeed)
                    {
                        endPosition = this._charPos;
                        this.ProcessLineFeed();
                        break;
                    }
                    else if (char.IsWhiteSpace(currentChar))
                    {
                        endPosition = this._charPos;
                        this._charPos++;
                        break;
                    }
                    else if (currentChar == '(')
                    {
                        endPosition = this._charPos;
                        break;
                    }
                    else
                    {
                        throw this.CreateReaderException(this, "Unexpected character while parsing constructor: {0}.".FormatWith(CultureInfo.InvariantCulture, currentChar));
                    }
                }

                this._stringReference = new StringReference(this._chars, initialPosition, endPosition - initialPosition);
                string constructorName = this._stringReference.ToString();
                
                this.EatWhitespace(false);
                
                if (this._chars[this._charPos] != '(')
                {
                    throw this.CreateReaderException(this, "Unexpected character while parsing constructor: {0}.".FormatWith(CultureInfo.InvariantCulture, _chars[_charPos]));
                }
                
                this._charPos++;
                
                this.ClearRecentString();
                
                this.SetToken(JsonToken.StartConstructor, constructorName);
            }
        }
        
        private void ParseNumber()
        {
            this.ShiftBufferIfNeeded();

            char firstChar = this._chars[this._charPos];
            int initialPosition = this._charPos;
            
            this.ReadNumberIntoBuffer();
            
            this._stringReference = new StringReference(this._chars, initialPosition, this._charPos - initialPosition);

            object numberValue;
            JsonToken numberType;
            
            bool singleDigit = (char.IsDigit(firstChar) && this._stringReference.Length == 1);
            bool nonBase10 = (firstChar == '0' && this._stringReference.Length > 1 &&
                              this._stringReference.Chars[this._stringReference.StartIndex + 1] != '.' &&
                              this._stringReference.Chars[this._stringReference.StartIndex + 1] != 'e' &&
                              this._stringReference.Chars[this._stringReference.StartIndex + 1] != 'E');
            
            if (this._readType == ReadType.ReadAsInt32)
            {
                if (singleDigit)
                {
                    // digit char values start at 48
                    numberValue = firstChar - 48;
                }
                else if (nonBase10)
                {
                    string number = this._stringReference.ToString();
                    
                    // decimal.Parse doesn't support parsing hexadecimal values
                    int integer = number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                  ? Convert.ToInt32(number, 16)
                                  : Convert.ToInt32(number, 8);
                    
                    numberValue = integer;
                }
                else
                {
                    string number = this._stringReference.ToString();

                    numberValue = Convert.ToInt32(number, CultureInfo.InvariantCulture);
                }
                
                numberType = JsonToken.Integer;
            }
            else if (this._readType == ReadType.ReadAsDecimal)
            {
                if (singleDigit)
                {
                    // digit char values start at 48
                    numberValue = (decimal)firstChar - 48;
                }
                else if (nonBase10)
                {
                    string number = this._stringReference.ToString();
                    
                    // decimal.Parse doesn't support parsing hexadecimal values
                    long integer = number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                   ? Convert.ToInt64(number, 16)
                                   : Convert.ToInt64(number, 8);
                    
                    numberValue = Convert.ToDecimal(integer);
                }
                else
                {
                    string number = this._stringReference.ToString();

                    numberValue = decimal.Parse(number, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
                }
                
                numberType = JsonToken.Float;
            }
            else
            {
                if (singleDigit)
                {
                    // digit char values start at 48
                    numberValue = (long)firstChar - 48;
                    numberType = JsonToken.Integer;
                }
                else if (nonBase10)
                {
                    string number = this._stringReference.ToString();
                    
                    numberValue = number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                  ? Convert.ToInt64(number, 16)
                                  : Convert.ToInt64(number, 8);
                    numberType = JsonToken.Integer;
                }
                else
                {
                    string number = this._stringReference.ToString();
                    
                    // it's faster to do 3 indexof with single characters than an indexofany
                    if (number.IndexOf('.') != -1 || number.IndexOf('E') != -1 || number.IndexOf('e') != -1)
                    {
                        numberValue = Convert.ToDouble(number, CultureInfo.InvariantCulture);
                        numberType = JsonToken.Float;
                    }
                    else
                    {
                        try
                        {
                            numberValue = Convert.ToInt64(number, CultureInfo.InvariantCulture);
                        }
                        catch (OverflowException ex)
                        {
                            throw new JsonReaderException("JSON integer {0} is too large or small for an Int64.".FormatWith(CultureInfo.InvariantCulture, number), ex);
                        }
                        
                        numberType = JsonToken.Integer;
                    }
                }
            }
            
            this.ClearRecentString();

            this.SetToken(numberType, numberValue);
        }
        
        private void ParseComment()
        {
            // should have already parsed / character before reaching this method
            this._charPos++;
            
            if (!this.EnsureChars(1, false) || this._chars[this._charPos] != '*')
            {
                throw this.CreateReaderException(this, "Error parsing comment. Expected: *, got {0}.".FormatWith(CultureInfo.InvariantCulture, _chars[_charPos]));
            }
            else
            {
                this._charPos++;
            }
            
            int initialPosition = this._charPos;
            
            bool commentFinished = false;
            
            while (!commentFinished)
            {
                switch (this._chars[this._charPos])
                {
                    case '\0':
                        if (this._charsUsed == this._charPos)
                        {
                            if (this.ReadData(true) == 0)
                            {
                                throw this.CreateReaderException(this, "Unexpected end while parsing comment.");
                            }
                        }
                        else
                        {
                            this._charPos++;
                        }
                        break;
                    case '*':
                        this._charPos++;
                        
                        if (this.EnsureChars(0, true))
                        {
                            if (this._chars[this._charPos] == '/')
                            {
                                this._stringReference = new StringReference(this._chars, initialPosition, this._charPos - initialPosition - 1);
                                
                                this._charPos++;
                                commentFinished = true;
                            }
                        }
                        break;
                    case StringUtils.CarriageReturn:
                        this.ProcessCarriageReturn(true);
                        break;
                    case StringUtils.LineFeed:
                        this.ProcessLineFeed();
                        break;
                    default:
                        this._charPos++;
                        break;
                }
            }
            
            this.SetToken(JsonToken.Comment, _stringReference.ToString());

            this.ClearRecentString();
        }
        
        private bool MatchValue(string value)
        {
            if (!this.EnsureChars(value.Length - 1, true))
            {
                return false;
            }
            
            for (int i = 0; i < value.Length; i++)
            {
                if (this._chars[this._charPos + i] != value[i])
                {
                    return false;
                }
            }
            
            this._charPos += value.Length;

            return true;
        }
        
        private bool MatchValueWithTrailingSeperator(string value)
        {
            // will match value and then move to the next character, checking that it is a seperator character
            bool match = this.MatchValue(value);
            
            if (!match)
            {
                return false;
            }
            
            if (!this.EnsureChars(0, false))
            {
                return true;
            }

            return this.IsSeperator(_chars[_charPos]) || this._chars[this._charPos] == '\0';
        }
        
        private bool IsSeperator(char c)
        {
            switch (c)
            {
                case '}':
                case ']':
                case ',':
                    return true;
                case '/':
                    // check next character to see if start of a comment
                    if (!this.EnsureChars(1, false))
                    {
                        return false;
                    }
                    
                    return (this._chars[this._charPos + 1] == '*');
                case ')':
                    if (this.CurrentState == State.Constructor || this.CurrentState == State.ConstructorStart)
                    {
                        return true;
                    }
                    break;
                case ' ':
                case StringUtils.Tab:
                case StringUtils.LineFeed:
                case StringUtils.CarriageReturn:
                    return true;
                default:
                    if (char.IsWhiteSpace(c))
                    {
                        return true;
                    }
                    break;
            }

            return false;
        }
        
        private void ParseTrue()
        {
            // check characters equal 'true'
            // and that it is followed by either a seperator character
            // or the text ends
            if (this.MatchValueWithTrailingSeperator(JsonConvert.True))
            {
                this.SetToken(JsonToken.Boolean, true);
            }
            else
            {
                throw this.CreateReaderException(this, "Error parsing boolean value.");
            }
        }
        
        private void ParseNull()
        {
            if (this.MatchValueWithTrailingSeperator(JsonConvert.Null))
            {
                this.SetToken(JsonToken.Null);
            }
            else
            {
                throw this.CreateReaderException(this, "Error parsing null value.");
            }
        }
        
        private void ParseUndefined()
        {
            if (this.MatchValueWithTrailingSeperator(JsonConvert.Undefined))
            {
                this.SetToken(JsonToken.Undefined);
            }
            else
            {
                throw this.CreateReaderException(this, "Error parsing undefined value.");
            }
        }
        
        private void ParseFalse()
        {
            if (this.MatchValueWithTrailingSeperator(JsonConvert.False))
            {
                this.SetToken(JsonToken.Boolean, false);
            }
            else
            {
                throw this.CreateReaderException(this, "Error parsing boolean value.");
            }
        }
        
        private void ParseNumberNegativeInfinity()
        {
            if (this.MatchValueWithTrailingSeperator(JsonConvert.NegativeInfinity))
            {
                this.SetToken(JsonToken.Float, double.NegativeInfinity);
            }
            else
            {
                throw this.CreateReaderException(this, "Error parsing negative infinity value.");
            }
        }
        
        private void ParseNumberPositiveInfinity()
        {
            if (this.MatchValueWithTrailingSeperator(JsonConvert.PositiveInfinity))
            {
                this.SetToken(JsonToken.Float, double.PositiveInfinity);
            }
            else
            {
                throw this.CreateReaderException(this, "Error parsing positive infinity value.");
            }
        }
        
        private void ParseNumberNaN()
        {
            if (this.MatchValueWithTrailingSeperator(JsonConvert.NaN))
            {
                this.SetToken(JsonToken.Float, double.NaN);
            }
            else
            {
                throw this.CreateReaderException(this, "Error parsing NaN value.");
            }
        }
        
        /// <summary>
        /// Changes the state to closed. 
        /// </summary>
        public override void Close()
        {
            base.Close();
            
            if (this.CloseInput && this._reader != null)
            {
                this._reader.Close();
            }
            
            if (this._buffer != null)
            {
                this._buffer.Clear();
            }
        }
        
        /// <summary>
        /// Gets a value indicating whether the class can return line information.
        /// </summary>
        /// <returns>
        /// 	<c>true</c> if LineNumber and LinePosition can be provided; otherwise, <c>false</c>.
        /// </returns>
        public bool HasLineInfo()
        {
            return true;
        }
        
        /// <summary>
        /// Gets the current line number.
        /// </summary>
        /// <value>
        /// The current line number or 0 if no line information is available (for example, HasLineInfo returns false).
        /// </value>
        public int LineNumber
        {
            get
            {
                if (this.CurrentState == State.Start && this.LinePosition == 0)
                {
                    return 0;
                }
                
                return this._lineNumber;
            }
        }
        
        /// <summary>
        /// Gets the current line position.
        /// </summary>
        /// <value>
        /// The current line position or 0 if no line information is available (for example, HasLineInfo returns false).
        /// </value>
        public int LinePosition
        {
            get
            {
                return this._charPos - this._lineStartPos;
            }
        }
    }
}