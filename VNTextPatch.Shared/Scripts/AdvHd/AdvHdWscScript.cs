using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.AdvHd
{
    public class AdvHdWscScript : IScript
    {
        private static readonly string[] NameControlCodes = { "%LC", "%LF", "%LR", "%L" };

        private byte[] _data;
        private bool _isRotated;
        private readonly List<int> _addressOffsets = new List<int>();
        private readonly List<AdvHdWscRelativeAddress> _relativeAddresses = new List<AdvHdWscRelativeAddress>();
        private readonly List<AdvHdWscStringRange> _textRanges = new List<AdvHdWscStringRange>();

        public string Extension => ".wsc";

        public void Load(ScriptLocation location)
        {
            byte[] fileData = File.ReadAllBytes(location.ToFilePath());

            foreach (bool isRotated in new[] { false, true })
            {
                _data = isRotated ? RotateForReading(fileData) : fileData;
                _isRotated = isRotated;

                if (TryDisassemble(AdvHdWscVersion.V2) || TryDisassemble(AdvHdWscVersion.V1))
                    return;
            }

            throw new InvalidDataException("Failed to read file");
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            foreach (AdvHdWscStringRange range in _textRanges)
            {
                string text = GetStringFromData(range);
                text = RemoveControlCodes(text, range);
                if (!string.IsNullOrWhiteSpace(text))
                    yield return new ScriptString(text, range.Type);
            }
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            using Stream inputStream = new MemoryStream(_data);
            using MemoryStream patchedStream = new MemoryStream();
            BinaryPatcher patcher = new BinaryPatcher(inputStream, patchedStream);

            using IEnumerator<ScriptString> stringEnumerator = strings.GetEnumerator();
            foreach (AdvHdWscStringRange range in _textRanges)
            {
                string origText = GetStringFromData(range);
                if (string.IsNullOrWhiteSpace(RemoveControlCodes(origText, range)))
                    continue;

                if (!stringEnumerator.MoveNext())
                    throw new InvalidDataException("Not enough strings in translation");

                string newText = stringEnumerator.Current.Text;
                if (range.Type == ScriptStringType.Message)
                    newText = MonospaceWordWrapper.Default.Wrap(newText);

                newText = AddControlCodes(origText, newText, range);

                patcher.CopyUpTo(range.Offset);
                patcher.ReplaceZeroTerminatedSjisString(newText);
            }

            if (stringEnumerator.MoveNext())
                throw new InvalidDataException("Too many strings in translation");

            patcher.CopyUpTo((int)inputStream.Length);

            foreach (int offset in _addressOffsets)
            {
                patcher.PatchAddress(offset);
            }

            foreach (AdvHdWscRelativeAddress relativeAddress in _relativeAddresses)
            {
                PatchRelativeAddress(patcher, relativeAddress);
            }

            byte[] outputData = patchedStream.ToArray();
            if (_isRotated)
                outputData = RotateForWriting(outputData);

            File.WriteAllBytes(location.ToFilePath(), outputData);
        }

        private bool TryDisassemble(AdvHdWscVersion version)
        {
            using Stream stream = new MemoryStream(_data);
            AdvHdWscDisassembler disassembler = new AdvHdWscDisassembler(stream, version);

            _addressOffsets.Clear();
            _relativeAddresses.Clear();
            _textRanges.Clear();

            disassembler.AddressEncountered += o => _addressOffsets.Add(o);
            disassembler.RelativeAddressEncountered += o => _relativeAddresses.Add(o);
            disassembler.TextEncountered += r => _textRanges.Add(r);

            try
            {
                disassembler.Disassemble();
                return true;
            }
            catch
            {
                _addressOffsets.Clear();
                _relativeAddresses.Clear();
                _textRanges.Clear();
                return false;
            }
        }

        private string GetStringFromData(AdvHdWscStringRange range)
        {
            return StringUtil.SjisEncoding.GetString(_data, range.Offset, range.Length - 1);
        }

        private static void PatchRelativeAddress(BinaryPatcher patcher, AdvHdWscRelativeAddress relativeAddress)
        {
            int newTargetOffset = patcher.MapOffset(relativeAddress.TargetOffset);
            int newOpEndOffset = patcher.MapOffset(relativeAddress.OpEndOffset);
            patcher.PatchInt32(relativeAddress.Offset, newTargetOffset - newOpEndOffset);
        }

        private static string RemoveControlCodes(string text, AdvHdWscStringRange range)
        {
            switch (range.Type)
            {
                case ScriptStringType.CharacterName:
                    foreach (string controlCode in NameControlCodes)
                    {
                        text = text.Replace(controlCode, "");
                    }
                    break;

                case ScriptStringType.Message:
                    if (range.IsAffixed)
                        text = Regex.Replace(text, @"^(?:%\w{2})+", "");
                    text = Regex.Replace(text, @"(?:%\w{1,2})+$", "");
                    text = text.Replace("\\n", "\r\n");
                    break;
            }
            return text;
        }

        private static string AddControlCodes(string origText, string newText, AdvHdWscStringRange range)
        {
            switch (range.Type)
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
                    if (range.IsAffixed)
                    {
                        Match prefix = Regex.Match(origText, @"^(?:%\w{2})+");
                        if (prefix.Success)
                            newText = prefix.Value + newText;
                    }

                    Match suffix = Regex.Match(origText, @"(?:%\w{1,2})+$");
                    if (suffix.Success)
                        newText += suffix.Value;

                    newText = newText.Replace("\r\n", " \\n");
                    break;
            }
            return newText;
        }

        private static byte[] RotateForReading(byte[] data)
        {
            byte[] result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)((data[i] << 6) | (data[i] >> 2));
            return result;
        }

        private static byte[] RotateForWriting(byte[] data)
        {
            byte[] result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)((data[i] << 2) | (data[i] >> 6));
            return result;
        }
    }
}
