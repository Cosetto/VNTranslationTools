using System;
using System.Collections.Generic;
using System.IO;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.AdvHd
{
    internal enum AdvHdWscVersion
    {
        V1,
        V2
    }

    internal struct AdvHdWscRelativeAddress
    {
        public AdvHdWscRelativeAddress(int offset, int opEndOffset, int targetOffset)
        {
            Offset = offset;
            OpEndOffset = opEndOffset;
            TargetOffset = targetOffset;
        }

        public int Offset { get; }
        public int OpEndOffset { get; }
        public int TargetOffset { get; }
    }

    internal struct AdvHdWscStringRange
    {
        public AdvHdWscStringRange(int offset, int length, ScriptStringType type, bool isAffixed)
        {
            Offset = offset;
            Length = length;
            Type = type;
            IsAffixed = isAffixed;
        }

        public int Offset { get; }
        public int Length { get; }
        public ScriptStringType Type { get; }
        public bool IsAffixed { get; }
    }

    internal class AdvHdWscDisassembler
    {
        private static readonly Dictionary<byte, string> V1OperandTemplates =
            new Dictionary<byte, string>
            {
                { 0x01, "bhhrb" },
                { 0x02, "D" },
                { 0x03, "bhbhb" },
                { 0x04, "" },
                { 0x05, "b" },
                { 0x06, "ab" },
                { 0x07, "s" },
                { 0x08, "bb" },
                { 0x09, "s" },
                { 0x0A, "b" },
                { 0x0B, "bb" },
                { 0x0C, "hb" },
                { 0x21, "bhs" },
                { 0x22, "bhb" },
                { 0x23, "bhhbbs" },
                { 0x24, "b" },
                { 0x25, "bbbbhbbs" },
                { 0x26, "bb" },
                { 0x27, "bbs" },
                { 0x28, "bbb" },
                { 0x41, "bbbt" },
                { 0x42, "hbbtt" },
                { 0x43, "bhhbs" },
                { 0x44, "bbbb" },
                { 0x45, "bbbb" },
                { 0x46, "hhibs" },
                { 0x47, "bb" },
                { 0x48, "bhhibs" },
                { 0x49, "bbb" },
                { 0x4A, "bhb" },
                { 0x4B, "bhhihb" },
                { 0x4C, "b" },
                { 0x4D, "bbhb" },
                { 0x4E, "bbbb" },
                { 0x4F, "bbbb" },
                { 0x50, "s" },
                { 0x51, "hhb" },
                { 0x52, "bb" },
                { 0x53, "bbbbbs" },
                { 0x54, "s" },
                { 0x55, "b" },
                { 0x56, "b" },
                { 0x57, "hhib" },
                { 0x58, "bbbhhb" },
                { 0x59, "s" },
                { 0x60, "b" },
                { 0x61, "bs" },
                { 0x62, "b" },
                { 0x63, "bbb" },
                { 0x64, "bhb" },
                { 0x65, "hhb" },
                { 0x66, "bhib" },
                { 0x67, "b" },
                { 0x68, "hhhb" },
                { 0x69, "bb" },
                { 0x70, "b" },
                { 0x71, "s" },
                { 0x72, "b" },
                { 0x73, "hhibs" },
                { 0x74, "bb" },
                { 0x75, "hhhhb" },
                { 0x76, "hhib" },
                { 0x77, "hhib" },
                { 0x78, "bbbib" },
                { 0x79, "b" },
                { 0x81, "bb" },
                { 0x82, "hb" },
                { 0x83, "b" },
                { 0x84, "b" },
                { 0x85, "bb" },
                { 0x86, "bb" },
                { 0x87, "hb" },
                { 0x88, "bb" },
                { 0x89, "b" },
                { 0x8A, "bb" },
                { 0x8B, "b" },
                { 0x8C, "hb" },
                { 0x8D, "b" },
                { 0x8E, "b" },
                { 0xB1, "hhb" },
                { 0xB2, "bbs" },
                { 0xB3, "bb" },
                { 0xB4, "bbbhhib" },
                { 0xB5, "bb" },
                { 0xB6, "hs" },
                { 0xB7, "bhhs" },
                { 0xB8, "bbb" },
                { 0xB9, "bbb" },
                { 0xBA, "bbbbs" },
                { 0xBB, "b" },
                { 0xBC, "bbbb" },
                { 0xE0, "s" },
                { 0xE1, "b" },
                { 0xE2, "b" },
                { 0xE3, "b" },
                { 0xE4, "bb" },
                { 0xE5, "b" },
                { 0xFF, "" }
            };

        private static readonly Dictionary<byte, string> V2OperandTemplates =
            new Dictionary<byte, string>
            {
                { 0x01, "bhhrb" },
                { 0x02, "D" },
                { 0x03, "bhbhb" },
                { 0x04, "" },
                { 0x05, "bb" },
                { 0x06, "ab" },
                { 0x07, "s" },
                { 0x08, "b" },
                { 0x09, "s" },
                { 0x0A, "b" },
                { 0x0B, "bb" },
                { 0x0C, "hb" },
                { 0x0D, "hhhb" },
                { 0x0E, "bb" },
                { 0x21, "bhbhhhs" },
                { 0x22, "bhb" },
                { 0x23, "bhhhbbs" },
                { 0x24, "b" },
                { 0x25, "bbbbhbbbbbs" },
                { 0x26, "bb" },
                { 0x27, "bhhhbbs" },
                { 0x28, "bbbbb" },
                { 0x29, "bhb" },
                { 0x30, "bbbb" },
                { 0x31, "bb" },
                { 0x32, "bb" },
                { 0x33, "hhhb" },
                { 0x41, "hbbt" },
                { 0x42, "hbbbtt" },
                { 0x43, "bhhbs" },
                { 0x44, "bbbb" },
                { 0x45, "bbbb" },
                { 0x46, "hhhhbs" },
                { 0x47, "bb" },
                { 0x48, "bhhibbs" },
                { 0x49, "bbb" },
                { 0x4A, "bhhb" },
                { 0x4B, "bhhihiib" },
                { 0x4C, "bbbib" },
                { 0x4D, "bbhhhhhb" },
                { 0x4E, "bbbb" },
                { 0x4F, "bbbb" },
                { 0x50, "s" },
                { 0x51, "hhb" },
                { 0x52, "bb" },
                { 0x53, "bhhs" },
                { 0x54, "s" },
                { 0x55, "b" },
                { 0x56, "b" },
                { 0x57, "hhib" },
                { 0x58, "bbbhhb" },
                { 0x59, "s" },
                { 0x60, "b" },
                { 0x61, "bs" },
                { 0x62, "b" },
                { 0x63, "bbb" },
                { 0x64, "bhhhb" },
                { 0x65, "hhb" },
                { 0x66, "bhhbhihii" },
                { 0x67, "bbbib" },
                { 0x68, "hhhhb" },
                { 0x69, "bb" },
                { 0x70, "bbbib" },
                { 0x71, "s" },
                { 0x72, "b" },
                { 0x73, "hhibs" },
                { 0x74, "bb" },
                { 0x75, "hhhhb" },
                { 0x76, "hhihhib" },
                { 0x77, "hhib" },
                { 0x78, "bbbib" },
                { 0x79, "b" },
                { 0x81, "bb" },
                { 0x82, "hb" },
                { 0x83, "b" },
                { 0x84, "b" },
                { 0x85, "bb" },
                { 0x86, "bb" },
                { 0x87, "hb" },
                { 0x88, "bbb" },
                { 0x89, "b" },
                { 0x8A, "b" },
                { 0x8B, "b" },
                { 0x8C, "hb" },
                { 0x8D, "b" },
                { 0x8E, "b" },
                { 0xA0, "hhh" },
                { 0xA1, "bhhbb" },
                { 0xA2, "bhhb" },
                { 0xA3, "bhhb" },
                { 0xA4, "bhhb" },
                { 0xA5, "bb" },
                { 0xA6, "b" },
                { 0xA7, "b" },
                { 0xA8, "bbbbbbbhhhhb" },
                { 0xA9, "b" },
                { 0xAA, "bbb" },
                { 0xAB, "b" },
                { 0xAC, "b" },
                { 0xAD, "bbbbbbbbbb" },
                { 0xAE, "b" },
                { 0xB1, "hhb" },
                { 0xB2, "bs" },
                { 0xB3, "bb" },
                { 0xB4, "bbhhibb" },
                { 0xB5, "bbbbbbb" },
                { 0xB6, "hs" },
                { 0xB7, "bhhs" },
                { 0xB8, "bbb" },
                { 0xB9, "bbb" },
                { 0xBA, "hhbbbbbhs" },
                { 0xBB, "b" },
                { 0xBC, "bbbb" },
                { 0xBD, "bb" },
                { 0xBE, "bbb" },
                { 0xBF, "bbbib" },
                { 0xE0, "s" },
                { 0xE2, "b" },
                { 0xE3, "b" },
                { 0xE4, "bb" },
                { 0xE5, "b" },
                { 0xE6, "bb" },
                { 0xE7, "hb" },
                { 0xE8, "s" },
                { 0xE9, "b" },
                { 0xEA, "bs" },
                { 0xEB, "b" },
                { 0xFF, "bbbbbbbb" }
            };

        private readonly Stream _stream;
        private readonly BinaryReader _reader;
        private readonly Dictionary<byte, string> _operandTemplates;

        public AdvHdWscDisassembler(Stream stream, AdvHdWscVersion version)
        {
            _stream = stream;
            _reader = new BinaryReader(stream);
            _operandTemplates = version == AdvHdWscVersion.V2 ? V2OperandTemplates : V1OperandTemplates;
        }

        public event Action<int> AddressEncountered;
        public event Action<AdvHdWscRelativeAddress> RelativeAddressEncountered;
        public event Action<AdvHdWscStringRange> TextEncountered;

        public void Disassemble()
        {
            while (_stream.Position < _stream.Length)
            {
                ReadInstruction();
            }
        }

        private void ReadInstruction()
        {
            long instructionOffset = _stream.Position;
            byte opcode = _reader.ReadByte();
            string operandTemplate = _operandTemplates.GetOrDefault(opcode);
            if (operandTemplate == null)
                throw new InvalidDataException($"Invalid opcode encountered: {opcode:X02}");

            ReadOperands(opcode, operandTemplate, instructionOffset);
        }

        private void ReadOperands(byte opcode, string template, long instructionOffset)
        {
            int affixedStringIndex = 0;
            foreach (char type in template)
            {
                switch (type)
                {
                    case 'a':
                        ReadAddress();
                        break;

                    case 'r':
                        ReadRelativeAddress(instructionOffset, template);
                        break;

                    case 'b':
                        _reader.Skip(1);
                        break;

                    case 'h':
                        _reader.Skip(2);
                        break;

                    case 'i':
                    case 'f':
                        _reader.Skip(4);
                        break;

                    case 's':
                        _reader.SkipZeroTerminatedSjisString();
                        break;

                    case 't':
                        ReadAffixedText(opcode, affixedStringIndex++);
                        break;

                    case 'H':
                        ReadUInt16Array();
                        break;

                    case 'D':
                        ReadChoices();
                        break;

                    default:
                        throw new ArgumentException();
                }
            }
        }

        private void ReadAddress()
        {
            int offset = (int)_stream.Position;
            int address = _reader.ReadInt32();
            if (address > 0 && address < _stream.Length)
                AddressEncountered?.Invoke(offset);
        }

        private void ReadRelativeAddress(long instructionOffset, string template)
        {
            int offset = (int)_stream.Position;
            int address = _reader.ReadInt32();
            int opEndOffset = checked((int)instructionOffset + 1 + GetTemplateSize(template));
            int targetOffset = opEndOffset + address;

            if (targetOffset >= 0 && targetOffset <= _stream.Length)
                RelativeAddressEncountered?.Invoke(new AdvHdWscRelativeAddress(offset, opEndOffset, targetOffset));
        }

        private void ReadUInt16Array()
        {
            int count = _reader.ReadByte();
            _reader.Skip(count * 2);
        }

        private void ReadChoices()
        {
            int count = _reader.ReadUInt16();
            if (count > 100)
                throw new InvalidDataException($"Invalid choice count: {count}");

            for (int i = 0; i < count; i++)
            {
                _reader.Skip(2);
                ReadText(ScriptStringType.Message, false);
                _reader.Skip(3);
                ReadInstruction();
            }
        }

        private void ReadAffixedText(byte opcode, int index)
        {
            ScriptStringType type = ScriptStringType.Internal;
            if (opcode == 0x41)
            {
                type = ScriptStringType.Message;
            }
            else if (opcode == 0x42)
            {
                type = index == 0 ? ScriptStringType.CharacterName : ScriptStringType.Message;
            }

            ReadText(type, true);
        }

        private void ReadText(ScriptStringType type, bool isAffixed)
        {
            int offset = (int)_stream.Position;
            int length = _reader.SkipZeroTerminatedSjisString();
            if (length > 1 && type != ScriptStringType.Internal)
                TextEncountered?.Invoke(new AdvHdWscStringRange(offset, length, type, isAffixed));
        }

        private static int GetTemplateSize(string template)
        {
            int result = 0;
            foreach (char type in template)
            {
                switch (type)
                {
                    case 'a':
                    case 'r':
                    case 'i':
                    case 'f':
                        result += 4;
                        break;

                    case 'b':
                        result += 1;
                        break;

                    case 'h':
                        result += 2;
                        break;

                    default:
                        throw new ArgumentException("Relative addresses are only supported in fixed-size instructions.");
                }
            }

            return result;
        }
    }
}
