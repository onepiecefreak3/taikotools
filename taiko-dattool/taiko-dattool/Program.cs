using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;

namespace taiko_dattool
{
    class Program
    {
        static void Main(string[] args)
        {
            if(args.Length < 2)
            {
                Console.WriteLine("usage: x/c {0} input.dat/input.json (optional: output.dat/output.json");
                Environment.Exit(1);
            }

            string inputFilename = args[1];
            string outputFilename = null;
            if(args.Length == 3)
            {
                outputFilename = args[2];
            }

            Dictionary<string, Dictionary<string, string>> strings = new Dictionary<string, Dictionary<string, string>>();
            if (args[0] == "x")
            {
                if(outputFilename == null)
                    outputFilename = Path.Combine(Path.GetDirectoryName(inputFilename), Path.GetFileNameWithoutExtension(inputFilename) + ".json");

                using (BinaryReader reader = new BinaryReader(File.OpenRead(inputFilename)))
                {
                    var entries = reader.ReadInt32();
                    var unk = reader.ReadInt32();
                    var tableOffset = reader.ReadInt32();
                    var unk2 = reader.ReadInt32();

                    string mainKey = Encoding.UTF8.GetString(reader.ReadBytes(0x10)).Trim('\0');
                    strings[mainKey] = new Dictionary<string, string>();

                    for (int i = 0; i < entries; i++)
                    {
                        var keyOffset = reader.ReadInt32();
                        var stringOffset = reader.ReadInt32();
                        var curOffset = reader.BaseStream.Position;
                        byte c = 0;

                        List<byte> keyBytes = new List<byte>();
                        reader.BaseStream.Seek(keyOffset, SeekOrigin.Begin);
                        while ((c = reader.ReadByte()) != 0)
                            keyBytes.Add(c);

                        List<byte> stringBytes = new List<byte>();
                        reader.BaseStream.Seek(stringOffset, SeekOrigin.Begin);
                        while ((c = reader.ReadByte()) != 0)
                            stringBytes.Add(c);

                        reader.BaseStream.Seek(curOffset, SeekOrigin.Begin);

                        strings[mainKey][Encoding.UTF8.GetString(keyBytes.ToArray())] = Encoding.UTF8.GetString(stringBytes.ToArray());
                    }
                }

                File.WriteAllText(outputFilename, JsonConvert.SerializeObject(strings, Formatting.Indented));
            }
            else if(args[0] == "c")
            {
                if (outputFilename == null)
                    outputFilename = Path.Combine(Path.GetDirectoryName(inputFilename), Path.GetFileNameWithoutExtension(inputFilename) + ".dat");

                strings = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(inputFilename));


                var mainKey = strings.Keys.ToArray()[0];
                var entries = strings[mainKey].Count;
                var idx = 0x20 + entries * 8;
                var tablePadding = calculatePadding(idx, 0x10);
                idx += tablePadding;
                
                using(BinaryWriter writer = new BinaryWriter(File.OpenWrite(outputFilename)))
                {
                    writer.Write(entries);
                    writer.Write(0x10);
                    writer.Write(0x20);
                    writer.Write(0);

                    var mainKeyBytes = Encoding.UTF8.GetBytes(mainKey).ToList();
                    while (mainKeyBytes.Count < 0x10)
                        mainKeyBytes.Add(0);
                    writer.Write(mainKeyBytes.Take(0x10).ToArray());

                    foreach(var k in strings[mainKey])
                    {
                        writer.Write(idx);
                        var keyLen = Encoding.UTF8.GetByteCount(k.Key);
                        idx += keyLen;
                        idx += calculatePadding(idx, 0x10);

                        writer.Write(idx);
                        var strLen = Encoding.UTF8.GetByteCount(k.Value);
                        idx += strLen;
                        idx += calculatePadding(idx, 0x10);
                    }


                    while (tablePadding-- > 0)
                        writer.Write((byte)0);



                    foreach (var k in strings[mainKey])
                    {
                        writer.Write(Encoding.UTF8.GetBytes(k.Key));
                        var padding = calculatePadding((int)writer.BaseStream.Position, 0x10);
                        while(padding-- > 0)
                            writer.Write((byte)0);


                        writer.Write(Encoding.UTF8.GetBytes(k.Value));
                        padding = calculatePadding((int)writer.BaseStream.Position, 0x10);
                        while (padding-- > 0)
                            writer.Write((byte)0);
                    }
                }
            }
            else
            {
                Console.WriteLine("Invalid mode flag");
            }
        }

        static int calculatePadding(int value, int boundary)
        {
            var padding = boundary - (value % boundary);

            if (padding == 0)
                padding = boundary;

            return padding;
        }
    }
}
