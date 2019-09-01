using System.Collections.Generic;
using System.IO;

// ReSharper disable once CheckNamespace
namespace psvita_l7ctool
{
    internal class Node
    {
        public Node[] Children { get; } = new Node[2];
        public int Value { get; set; } = -1;
        public bool IsLeaf => Value != -1;
    }

    internal class Tree
    {
        private Node _root;

        public void Build(BitReader br, int valueBitCount)
        {
            _root = new Node();

            ReadNode(br, _root, valueBitCount);
        }

        public int ReadValue(BitReader br)
        {
            var node = _root;
            while (!node.IsLeaf)
                node = node.Children[br.ReadBit()];
            return node.Value;
        }

        private void ReadNode(BitReader br, Node node, int valueBitCount)
        {
            var flag = br.ReadBit();
            if (flag != 0)
            {
                node.Children[0] = new Node();
                ReadNode(br, node.Children[0], valueBitCount);

                node.Children[1] = new Node();
                ReadNode(br, node.Children[1], valueBitCount);
            }
            else
            {
                node.Value = br.ReadBits<int>(valueBitCount);
            }
        }
    }

    internal class TaikoCompression2
    {
        private static readonly int[] Counters =
        {
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 0xa, 0xc, 0xe,
            0x10, 0x12, 0x16, 0x1a,
            0x1e, 0x22, 0x2a, 0x32,
            0x3a, 0x42, 0x52, 0x62,
            0x72, 0x82, 0xa2, 0xc2,
            0xe2, 0x102, 0, 0
        };

        private static readonly int[] CounterBitReads =
        {
            0, 0, 0, 0,
            0, 0, 0, 0,
            0, 1, 1, 1,
            1, 2, 2, 2,
            2, 3, 3, 3,
            3, 4, 4, 4,
            4, 5, 5, 5,
            5, 0, 0, 0
        };

        private static readonly int[] DisplacementRanges =
        {
            1, 2, 3, 4,
            5, 7, 9, 0xd,
            0x11, 0x19, 0x21, 0x31,
            0x41, 0x61, 0x81, 0xc1,
            0x101, 0x181, 0x201, 0x301,
            0x401, 0x601, 0x801, 0xc01,
            0x1001, 0x1801, 0x2001, 0x3001,
            0x4001, 0x6001, 0, 0
        };

        private static readonly int[] DisplacementBitReads =
        {
            0, 0, 0, 0,
            1, 1, 2, 2,
            3, 3, 4, 4,
            5, 5, 6, 6,
            7, 7, 8, 8,
            9, 9, 0xa, 0xa,
            0xb, 0xb, 0xc, 0xc,
            0xd, 0xd, 0, 0
        };

        static readonly byte[] WindowBuffer = new byte[0x8000];
        private static int _windowBufferPosition;

        public static byte[] Decompress(byte[] data, byte[] prev = null)
        {
            var output = new List<byte>();
            var outputPosition = 0;

            if (prev != null)
            {
                output = new List<byte>(prev);
                outputPosition = prev.Length;
            }

            using (var br = new BitReader(new MemoryStream(data), BitOrder.LSBFirst, 1, ByteOrder.LittleEndian))
            {
                var initialByte = br.ReadBits<int>(8);

                // 3 init holders
                var rawValueMapping = new Tree();
                rawValueMapping.Build(br, 8);
                var indexValueMapping = new Tree();
                indexValueMapping.Build(br, 6);
                var displacementIndexMapping = new Tree();
                displacementIndexMapping.Build(br, 5);

                while (true)
                {
                    var index = indexValueMapping.ReadValue(br);

                    if (index == 0)
                    {
                        // Finish decompression
                        if (initialByte < 3)
                            break;

                        var outputLength = output.Count - outputPosition;
                        var iVar4 = initialByte - 2;
                        if (outputLength <= iVar4)
                            break;

                        var length = outputLength - iVar4;
                        var position = 0;
                        do
                        {
                            length--;

                            var byte1 = output[outputPosition+position];
                            var byte2 = output[outputPosition +iVar4+ position];

                            output[outputPosition + iVar4 + position] = (byte)(byte1 + byte2);

                            position++;
                        } while (length != 0);

                        break;
                    }

                    if (index < 0x20)
                    {
                        // Match reading
                        // Max displacement 0x8000; Min displacement 1
                        // Max length 0x102; Min length 1
                        var counter = Counters[index];
                        if (CounterBitReads[index] != 0)
                            counter += br.ReadBits<int>(CounterBitReads[index]);

                        var displacementIndex = displacementIndexMapping.ReadValue(br);

                        var displacement = DisplacementRanges[displacementIndex];
                        if (DisplacementBitReads[displacementIndex] != 0)
                            displacement += br.ReadBits<int>(DisplacementBitReads[displacementIndex]);

                        if (counter == 0)
                            continue;

                        var bufferIndex = _windowBufferPosition + WindowBuffer.Length - displacement;
                        for (int i = 0; i < counter; i++)
                        {
                            var next = WindowBuffer[bufferIndex++ % WindowBuffer.Length];
                            output.Add(next);
                            WindowBuffer[_windowBufferPosition] = next;
                            _windowBufferPosition = (_windowBufferPosition + 1) % WindowBuffer.Length;
                        }
                    }
                    else
                    {
                        // Raw data reading
                        index -= 0x20;

                        var counter = Counters[index];
                        if (CounterBitReads[index] != 0)
                            counter += br.ReadBits<int>(CounterBitReads[index]);

                        if (counter == 0)
                            continue;

                        for (int i = 0; i < counter; i++)
                        {
                            var rawValue = (byte)rawValueMapping.ReadValue(br);

                            output.Add(rawValue);
                            WindowBuffer[_windowBufferPosition] = rawValue;
                            _windowBufferPosition = (_windowBufferPosition + 1) % WindowBuffer.Length;
                        }
                    }
                }
            }

            return output.ToArray();
        }
    }
}
