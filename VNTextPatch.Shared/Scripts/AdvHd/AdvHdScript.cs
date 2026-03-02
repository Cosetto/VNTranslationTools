using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.AdvHd
{
    public enum AdvHdTextEncoding
    {
        ShiftJis,
        Utf16
    }

    public class AdvHdScript : IScript
    {
        public string Extension => ".ws2";

        private static readonly string[] NameControlCodes = { "%LC", "%LF", "%LR", "%L" };

        private byte[] _data;
        private readonly List<int> _addressOffsets = new List<int>();
        private readonly List<Range> _textRanges = new List<Range>();

        public AdvHdTextEncoding TextEncoding { get; set; } = AdvHdTextEncoding.ShiftJis;

        public void Load(ScriptLocation location)
        {
            _data = File.ReadAllBytes(location.ToFilePath());

            if (_data.IndexOf(Encoding.Unicode.GetBytes("char\0")) != -1)
            {
                TextEncoding = AdvHdTextEncoding.Utf16;
            }

            Stream stream = new MemoryStream(_data);
            AdvHdDisassemblerBase[] disassemblers =
                {
                    new AdvHdDisassemblerV1(stream),
                    new AdvHdDisassemblerV2(stream),
                    new AdvHdDisassemblerV3(stream),
                    new AdvHdDisassemblerV4(stream)
                };

            foreach (AdvHdDisassemblerBase disassembler in disassemblers)
            {
                disassembler.TextEncoding = TextEncoding;
                stream.Position = 0;
                _addressOffsets.Clear();
                _textRanges.Clear();

                disassembler.AddressEncountered += o => _addressOffsets.Add(o);
                disassembler.TextEncountered += r => _textRanges.Add(r);
                try
                {
                    disassembler.Disassemble();
                }
                catch
                {
                    continue;
                }
                return;
            }

            throw new InvalidDataException("Failed to read file");
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            foreach (Range range in _textRanges)
            {
                string text = GetStringFromData(range);

                if (TextEncoding == AdvHdTextEncoding.Utf16 && text == "char")
                    continue;

                text = RemoveControlCodes(text, range.Type);
                if (!string.IsNullOrWhiteSpace(text))
                    yield return new ScriptString(text, range.Type);
            }
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            using Stream inputStream = new MemoryStream(_data);
            using Stream outputStream = File.Open(location.ToFilePath(), FileMode.Create, FileAccess.Write);
            BinaryPatcher patcher = new BinaryPatcher(inputStream, outputStream);

            using IEnumerator<ScriptString> stringEnumerator = strings.GetEnumerator();
            foreach (Range range in _textRanges)
            {
                string origText = GetStringFromData(range);

                if (TextEncoding == AdvHdTextEncoding.Utf16 && origText == "char")
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(RemoveControlCodes(origText, range.Type)))
                    continue;

                if (!stringEnumerator.MoveNext())
                    throw new InvalidDataException("Not enough strings in translation");

                string newText = stringEnumerator.Current.Text;
                
                if (range.Type == ScriptStringType.Message && TextEncoding != AdvHdTextEncoding.Utf16)
                    newText = MonospaceWordWrapper.Default.Wrap(newText);

                newText = AddControlCodes(origText, newText, range.Type);

                patcher.CopyUpTo(range.Offset);

                if (TextEncoding == AdvHdTextEncoding.Utf16)
                    patcher.ReplaceZeroTerminatedUtf16String(newText);
                else
                    patcher.ReplaceZeroTerminatedSjisString(newText);
            }

            if (stringEnumerator.MoveNext())
                throw new InvalidDataException("Too many strings in translation");

            patcher.CopyUpTo((int)inputStream.Length);

            foreach (int offset in _addressOffsets)
            {
                patcher.PatchAddress(offset);
            }
        }

        private string GetStringFromData(Range range)
        {
            if (TextEncoding == AdvHdTextEncoding.Utf16)
                return Encoding.Unicode.GetString(_data, range.Offset, range.Length - 2);
            else
                return StringUtil.SjisEncoding.GetString(_data, range.Offset, range.Length - 1);
        }

        private static string RemoveControlCodes(string text, ScriptStringType type)
        {
            switch (type)
            {
                case ScriptStringType.CharacterName:
                    foreach (string controlCode in NameControlCodes)
                    {
                        text = text.Replace(controlCode, "");
                    }
                    break;

                case ScriptStringType.Message:
                    text = Regex.Replace(text, @"(?:%\w+)+$", "");
                    text = text.Replace("\\n", "\r\n");
                    break;
            }
            return text;
        }

        private static string AddControlCodes(string origText, string newText, ScriptStringType type)
        {
            switch (type)
            {
                case ScriptStringType.CharacterName:
                    foreach (string controlCode in NameControlCodes)
                    {
                        if (origText.StartsWith(controlCode))
                        {
                            newText = controlCode + newText;
                            break;
                        }
                    }
                    break;

                case ScriptStringType.Message:
                    Match match = Regex.Match(origText, @"(?:%\w+)+$");
                    if (match.Success)
                        newText += match.Value;

                    newText = newText.Replace("\r\n", " \\n");      
                    break;
            }
            return newText;
        }
    }
}
