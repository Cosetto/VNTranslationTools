using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Softpal
{
    public class SoftpalDisassembler
    {
        public const int CodeOffset = 0xC;

        private readonly Stream _stream;
        private readonly List<int> _labelOffsets;
        private readonly BinaryReader _reader;
        private readonly StreamWriter _writer;
        private readonly byte[] _textData;
        private readonly Dictionary<short, Action<Instruction>> _opcodeHandlers;

        private readonly Dictionary<int, UserMessageFunction> _userMessageFuncs = new Dictionary<int, UserMessageFunction>();
        private readonly Dictionary<int, Operand> _variables = new Dictionary<int, Operand>();
        private readonly Stack<Operand> _stack = new Stack<Operand>();

        public SoftpalDisassembler(Stream stream, List<int> labelOffsets, StreamWriter writer = null, byte[] textData = null)
        {
            _stream = stream;
            _labelOffsets = labelOffsets;
            _reader = new BinaryReader(stream);
            _writer = writer;
            _textData = textData;
            _opcodeHandlers = new Dictionary<short, Action<Instruction>>
                              { 
                                  { SoftpalOpcodes.Mov,             HandleMovInstruction },
                                  { SoftpalOpcodes.Push,            HandlePushInstruction },
                                  { SoftpalOpcodes.Call,            HandleCallInstruction },
                                  { SoftpalOpcodes.Syscall,         HandleSyscallInstruction },
                                  { SoftpalOpcodes.SelectAddChoice, HandleSelectChoiceInstruction }
                              };

            if (Encoding.ASCII.GetString(_reader.ReadBytes(4)) != "Sv20")
                throw new InvalidDataException("Invalid Softpal script magic");
        }

        public void Disassemble()
        {
            FindUserMessageFunctions();

            _stream.Position = CodeOffset;
            while (_stream.Position < _stream.Length)
            {
                Instruction instr = ReadInstruction();
                if (_writer != null)
                    WriteInstruction(instr);

                if (IsMessageInstruction(instr))
                {
                    HandleMessageInstruction();
                }
                else if (_opcodeHandlers.TryGetValue(instr.Opcode, out Action<Instruction> handler))
                {
                    handler(instr);
                }
                else
                {
                    // Unhandled instructions might modify the stack, so clear it.
                    // DO NOT clear variables here. Variables are local registers and persist across math/logic ops.
                    _stack.Clear();
                    // _variables.Clear();
                }
            }
        }

        public event Action<int, ScriptStringType> TextAddressEncountered;

        private void FindUserMessageFunctions()
        {
            int currentFuncOffset = -1;
            int currentFuncNumArgs = -1;

            _stream.Position = CodeOffset;
            while (_stream.Position < _stream.Length)
            {
                Instruction instr = ReadInstruction();

                if (IsMessageInstruction(instr))
                {
                    if (currentFuncOffset >= 0 && _stack.Count >= 4)
                    {
                        Operand number = _stack.Pop();
                        Operand name = _stack.Pop();
                        Operand message = _stack.Pop();
                        if (name.Type == OperandType.Argument && message.Type == OperandType.Argument)
                        {
                            _userMessageFuncs[currentFuncOffset] = new UserMessageFunction(currentFuncNumArgs, name.Value - 1, message.Value - 1);
                            currentFuncOffset = -1;
                            currentFuncNumArgs = -1;
                        }
                    }
                    _stack.Clear();
                    // _variables.Clear();
                    continue;
                }

                switch (instr.Opcode)
                {
                    case SoftpalOpcodes.Enter:
                        currentFuncOffset = instr.Offset;
                        currentFuncNumArgs = instr.Operands[0].Value;
                        _stack.Clear();
                        _variables.Clear(); // Safe to clear here, entering a new function frame
                        break;

                    case SoftpalOpcodes.Mov:
                        HandleMovInstruction(instr);
                        break;

                    case SoftpalOpcodes.Push:
                        HandlePushInstruction(instr);
                        break;

                    case SoftpalOpcodes.Ret:
                        currentFuncOffset = -1;
                        currentFuncNumArgs = -1;
                        _stack.Clear();
                        _variables.Clear(); // Safe to clear here, leaving function frame
                        break;

                    default:
                        _stack.Clear();
                        // _variables.Clear();
                        break;
                }
            }
        }

        private void HandleMovInstruction(Instruction instr)
        {
            if (instr.Operands[0].Type == OperandType.Variable)
            {
                Operand rhs = instr.Operands[1];
                // If moving a variable to a variable, resolve the stored literal to carry it forward
                if (rhs.Type == OperandType.Variable && _variables.TryGetValue(rhs.Value, out Operand resolved))
                {
                    rhs = resolved;
                }
                _variables[instr.Operands[0].Value] = rhs;
            }
        }

        private void HandlePushInstruction(Instruction instr)
        {
            Operand op = instr.Operands[0];
            if (op.Type == OperandType.Variable && _variables.TryGetValue(op.Value, out Operand resolved))
            {
                _stack.Push(resolved);
            }
            else
            {
                _stack.Push(op);
            }
        }

        private void HandleCallInstruction(Instruction instr)
        {
            try
            {
                if (_labelOffsets == null || instr.Operands[0].Type != OperandType.Literal)
                    return;

                int targetOffset = _labelOffsets[instr.Operands[0].Value - 1];
                UserMessageFunction messageFunc = _userMessageFuncs.GetOrDefault(targetOffset);
                if (messageFunc == null || _stack.Count < messageFunc.NumArgs)
                    return;

                List<Operand> args = new List<Operand>();
                for (int i = 0; i < messageFunc.NumArgs; i++)
                {
                    args.Add(_stack.Pop());
                }
                args.Reverse();

                Operand name = args[messageFunc.NameArgIndex];
                Operand message = args[messageFunc.MessageArgIndex];

                if (message.Type == OperandType.Literal && message.Value >= 0)
                {
                    if (name.Type == OperandType.Literal && name.Value >= 0)
                        TextAddressEncountered?.Invoke(name.Offset, ScriptStringType.CharacterName);

                    TextAddressEncountered?.Invoke(message.Offset, ScriptStringType.Message);
                }
            }
            finally
            {
                _stack.Clear();
                // _variables.Clear();
            }
        }

        private void HandleSyscallInstruction(Instruction instr)
        {
            switch (instr.Operands[0].RawValue)
            {
                case 0x60002:
                    HandleSelectChoiceInstruction(instr);
                    break;

                default:
                    _stack.Clear();
                    break;
            }
        }

        private void HandleMessageInstruction()
        {
            try
            {
                if (_stack.Count < 4)
                    return;

                Operand number = _stack.Pop();
                Operand name = _stack.Pop();
                Operand message = _stack.Pop();
                if (name.Type != OperandType.Literal || message.Type != OperandType.Literal)
                    return;

                if (name.Value >= 0)
                    TextAddressEncountered?.Invoke(name.Offset, ScriptStringType.CharacterName);

                if (message.Value >= 0)
                    TextAddressEncountered?.Invoke(message.Offset, ScriptStringType.Message);
            }
            finally
            {
                _stack.Clear();
                // _variables.Clear();
            }
        }

        private void HandleSelectChoiceInstruction(Instruction instr)
        {
            try
            {
                if (_stack.Count < 1)
                    return;

                Operand choice = _stack.Pop();
                if (choice.Type == OperandType.Literal && choice.Value >= 0)
                {
                    TextAddressEncountered?.Invoke(choice.Offset, ScriptStringType.Message);
                }
            }
            finally
            {
                _stack.Clear();
                // _variables.Clear();
            }
        }

        private static bool IsMessageInstruction(Instruction instr)
        {
            switch (instr.Opcode)
            {
                case SoftpalOpcodes.Text:
                case SoftpalOpcodes.TextW:
                case SoftpalOpcodes.TextA:
                case SoftpalOpcodes.TextWA:
                case SoftpalOpcodes.TextN:
                case SoftpalOpcodes.TextCat:
                    return true;

                case SoftpalOpcodes.Syscall:
                    switch (instr.Operands[0].RawValue)
                    {
                        case 0x20002:
                        case 0x2000F:
                        case 0x20010:
                        case 0x20011:
                        case 0x20012:
                        case 0x20013:
                            return true;

                        default:
                            return false;
                    }

                default:
                    return false;
            }
        }

        private Instruction ReadInstruction()
        {
            int offset = (int)_stream.Position;
            int opcode = _reader.ReadInt32();
            if ((opcode >> 16) != 1)
                throw new InvalidDataException();

            Instruction instr = new Instruction(offset, (short)opcode);

            (_, string operandTypes) = SoftpalOpcodes.Descriptions[(short)opcode];
            foreach (char _ in operandTypes)
            {
                offset = (int)_stream.Position;
                int value = _reader.ReadInt32();
                instr.Operands.Add(new Operand(offset, value));
            }

            return instr;
        }

        private string TryResolveText(int rawValue)
        {
            if (_textData == null) return null;

            int type = (rawValue >> 28) & 0xF;
            if (type != 0) return null;

            int addr = (rawValue << 4) >> 4;
            int targetPos = addr + 4; 
            
            if (addr <= 0 || targetPos < 0 || targetPos >= _textData.Length) return null;

            try
            {
                int endPos = Array.IndexOf(_textData, (byte)0, targetPos);
                if (endPos == -1) endPos = _textData.Length;
                
                int len = endPos - targetPos;
                if (len <= 0 || len > 2000) return null; 

                string text = StringUtil.SjisEncoding.GetString(_textData, targetPos, len);
                
                if (string.IsNullOrWhiteSpace(text)) return null;
                
                return text.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("<br>", "\\n");
            }
            catch
            {
                return null;
            }
        }

        private void WriteInstruction(Instruction instr)
        {
            (string opcodeName, string operandTypes) = SoftpalOpcodes.Descriptions[instr.Opcode];
            opcodeName ??= instr.Opcode.ToString("X04");
            _writer.Write($"{instr.Offset:X08} {opcodeName}");

            List<string> comments = new List<string>();

            for (int i = 0; i < instr.Operands.Count; i++)
            {
                _writer.Write(i == 0 ? " " : ", ");

                int value = instr.Operands[i].Value;
                char type = operandTypes[i];
                
                if (type == 'l')
                {
                    if (instr.Operands[i].Type == OperandType.Literal)
                        _writer.Write($"#{_labelOffsets[value - 1]:X08}");
                    else
                        type = 'p';
                }

                if (type == 'p')
                {
                    _writer.Write(
                        instr.Operands[i].Type switch
                        {
                            OperandType.Literal => $"0x{value:X08}",
                            OperandType.Variable => $"var_{value}",
                            OperandType.Argument => $"arg_{value}",
                            _ => $"{instr.Operands[i].Type}:[0x{value:X08}]"
                        }
                    );
                }
                else if (type == 'i')
                {
                    _writer.Write($"0x{value:X}");
                }

                if (instr.Operands[i].Type == OperandType.Literal)
                {
                    string resolvedText = TryResolveText(instr.Operands[i].RawValue);
                    if (resolvedText != null)
                    {
                        comments.Add($"\"{resolvedText}\"");
                    }
                }
            }

            if (comments.Count > 0)
            {
                _writer.Write(" ; " + string.Join(", ", comments));
            }

            _writer.WriteLine();
            if (instr.Opcode == SoftpalOpcodes.Ret)
                _writer.WriteLine();
        }

        private class UserMessageFunction
        {
            public UserMessageFunction(int numArgs, int nameArgIndex, int messageArgIndex)
            {
                NumArgs = numArgs;
                NameArgIndex = nameArgIndex;
                MessageArgIndex = messageArgIndex;
            }

            public int NumArgs { get; }
            public int NameArgIndex { get; }
            public int MessageArgIndex { get; }
        }

        private class Instruction
        {
            public Instruction(int offset, short opcode)
            {
                Offset = offset;
                Opcode = opcode;
                Operands = new List<Operand>();
            }

            public int Offset { get; }
            public short Opcode { get; }
            public List<Operand> Operands { get; }
        }

        private readonly struct Operand
        {
            public Operand(int offset, int rawValue)
            {
                Offset = offset;
                RawValue = rawValue;
            }

            public readonly int Offset;
            public readonly int RawValue;

            public OperandType Type => (OperandType)((RawValue >> 28) & 0xF);
            public int Value => (RawValue << 4) >> 4;

            public override string ToString() => RawValue.ToString("X08");
        }

        private enum OperandType
        {
            Literal = 0,
            Variable = 4,
            Argument = 8
        }
    }
}
