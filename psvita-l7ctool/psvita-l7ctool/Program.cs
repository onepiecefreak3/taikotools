using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

// ReSharper disable once CheckNamespace
namespace psvita_l7ctool
{
    // ReSharper disable once InconsistentNaming
    internal class L7CAHeader
    {
        public uint magic = 0x4143374c; // L7CA
        public uint unk = 0x00010000; // Version? Must be 0x00010000
        public int archiveSize;
        public int metadataOffset;
        public int metadataSize;
        public uint unk2 = 0x00010000; // Chunk max size?
        public int filesystemEntries;
        public int folders;
        public int files;
        public int chunks;
        public int stringTableSize;
        public int unk4 = 5; // Number of sections??
    }

    // ReSharper disable once InconsistentNaming
    internal class L7CAFilesystemEntry
    {
        public int id;
        public uint hash; // Hash of lower case filesystem entry name
        public int folderOffset;
        public int filenameOffset;
        public long timestamp;
        public string filename;
    }

    // ReSharper disable once InconsistentNaming
    internal class L7CAFileEntry
    {
        public int compressedFilesize;
        public int rawFilesize;
        public int chunkIdx;
        public int chunkCount;
        public int offset;
        public uint crc32;
    }

    // ReSharper disable once InconsistentNaming
    internal class L7CAChunkEntry
    {
        public int chunkSize;
        public ushort unk;
        public ushort chunkId;
    }

    class Program
    {
        private static bool _debugDecompressionCode = false;
        private const int MaxChunkSize = 0x10000;
        private const int PaddingBoundary = 0x200;

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("usage:");
                Console.WriteLine("Extraction:");
                Console.WriteLine("\t{0} x input.l7z", AppDomain.CurrentDomain.FriendlyName);
                Console.WriteLine();
                Console.WriteLine("Creation:");
                Console.WriteLine("\t{0} c input_folderName output.l7z", AppDomain.CurrentDomain.FriendlyName);
                Console.WriteLine();
                Console.WriteLine("Decompress individual file with mode 0x80:");
                Console.WriteLine("\t{0} d input.bin output.bin", AppDomain.CurrentDomain.FriendlyName);
                Console.WriteLine();
                Console.WriteLine("Compress individual file with mode 0x80:");
                Console.WriteLine("\t{0} e input.bin output.bin", AppDomain.CurrentDomain.FriendlyName);
                Environment.Exit(1);
            }

            if (args[0] == "c")
            {
                PackArchive(args[1], args[2]);
            }
            else if (args[0] == "x")
            {
                UnpackArchive(args[1]);
            }
            else if (args[0] == "d")
            {
                var input = args[1];
                var output = input + ".out";

                if (args.Length >= 2)
                {
                    output = args[2];
                }

                byte[] data;
                using (BinaryReader reader = new BinaryReader(File.OpenRead(input)))
                {
                    var expectedFileSize = reader.ReadUInt32();

                    if (expectedFileSize == 0x19) // Blank
                    {
                        expectedFileSize = reader.ReadUInt32();
                    }
                    else
                    {
                        expectedFileSize = (expectedFileSize & 0xffffff00) >> 8;
                    }

                    data = TaikoCompression.Decompress(reader.ReadBytes((int)reader.BaseStream.Length - 4));
                    if (data.Length != expectedFileSize)
                    {
                        Console.WriteLine("File size didn't match expected output file size. Maybe bad decompression? ({0:x8} != {1:x8})", data.Length, expectedFileSize);
                    }
                }

                File.WriteAllBytes(output, data);
            }
            else if (args[0] == "e")
            {
                var input = args[1];
                var output = input + ".out";

                if (args.Length >= 2)
                {
                    output = args[2];
                }

                var data = File.ReadAllBytes(input);
                using (var writer = new BinaryWriter(File.OpenWrite(output)))
                {
                    var expectedFileSize = data.Length;

                    if (expectedFileSize > 0xffffff)
                    {
                        writer.Write(0x00000019);
                        writer.Write(expectedFileSize);
                    }
                    else
                    {
                        writer.Write((expectedFileSize << 8) | 0x19);
                    }

                    writer.Write(TaikoCompression.Compress(data));
                }
            }
        }

        static void PackArchive(string inputFolderName, string outputFileName)
        {
            var filesystemSection = new MemoryStream();
            var fileEntriesSection = new MemoryStream();
            var chunksSection = new MemoryStream();
            var stringSection = new MemoryStream();

            // Build folders and paths
            var paths = new List<string>();
            var fullpaths = new List<string>();
            foreach (var file in Directory.EnumerateFiles(inputFolderName, "*.*", SearchOption.AllDirectories))
            {
                var curPaths = new List<string>();

                var path = file.Replace('\\', '/');
                while (!string.IsNullOrWhiteSpace((path = Path.GetDirectoryName(path))))
                {
                    path = path.Replace('\\', '/');

                    if (!paths.Contains(path))
                        curPaths.Add(path);
                }

                curPaths.Reverse();

                fullpaths.AddRange(curPaths);
                fullpaths.Add(file);

                var filename = Path.GetFileName(file);
                if (!paths.Contains(filename))
                {
                    curPaths.Add(filename);
                }

                paths.AddRange(curPaths);
            }

            // Build string table
            var stringTableMapping = new Dictionary<string, int>();
            using (var writer = new BinaryWriter(stringSection, Encoding.ASCII, true))
            {
                foreach (var path in paths)
                {
                    var temp = Encoding.ASCII.GetBytes(path);
                    var offset = (int)writer.BaseStream.Position;

                    writer.Write((byte)temp.Length);
                    writer.Write(temp);

                    stringTableMapping.Add(path, offset);
                }
            }

            // Build filesystem table entries
            var filesystemEntries = new List<L7CAFilesystemEntry>();
            var folders = new List<string>();
            var files = new List<string>();

            var filesystemId = 0;
            foreach (var path in fullpaths)
            {
                var entry = new L7CAFilesystemEntry();

                if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                {
                    entry.id = -1;
                    entry.folderOffset = stringTableMapping[path.Replace("\\", "/")];
                    folders.Add(path);
                }
                else
                {
                    entry.id = filesystemId++;
                    entry.folderOffset = stringTableMapping[Path.GetDirectoryName(path).Replace("\\", "/")];
                    entry.filenameOffset = stringTableMapping[Path.GetFileName(path)];
                    files.Add(path);
                }

                entry.hash = Crc32.CalculateNamco(path.Replace("\\", "/"));
                entry.timestamp = new FileInfo(path).LastWriteTime.ToFileTime();
                entry.filename = path;

                filesystemEntries.Add(entry);
            }

            using (var writer = new BinaryWriter(filesystemSection, Encoding.ASCII, true))
            {
                foreach (var entry in filesystemEntries)
                {
                    writer.Write(entry.id);
                    writer.Write(entry.hash);
                    writer.Write(entry.folderOffset);
                    writer.Write(entry.filenameOffset);
                    writer.Write(entry.timestamp);
                }
            }

            using (var archiveWriter = new BinaryWriter(File.OpenWrite(outputFileName)))
            {
                // Write heading padding
                archiveWriter.Write(new byte[PaddingBoundary]);

                // Build file entries
                var fileEntries = new List<L7CAFileEntry>();
                var chunkEntries = new List<L7CAChunkEntry>();
                foreach (var fs in filesystemEntries)
                {
                    if (fs.id == -1)
                        continue;

                    Console.WriteLine("Reading {0}...", fs.filename);

                    var entry = new L7CAFileEntry
                    {
                        chunkIdx = chunkEntries.Count
                    };

                    var data = File.ReadAllBytes(fs.filename);
                    ushort chunks = 0;
                    for (var size = data.Length; size > 0; size -= MaxChunkSize)
                    {
                        var chunkEntry = new L7CAChunkEntry
                        {
                            chunkId = chunks++,
                            chunkSize = size > MaxChunkSize ? MaxChunkSize : size
                        };
                        chunkEntries.Add(chunkEntry);
                    }

                    entry.chunkCount = chunks;
                    entry.compressedFilesize = data.Length;
                    entry.rawFilesize = data.Length;
                    entry.crc32 = Crc32.Calculate(data);
                    entry.offset = (int)archiveWriter.BaseStream.Position;

                    // Write data to data table
                    archiveWriter.Write(data);

                    // Write padding
                    archiveWriter.Write(new byte[(int)(PaddingBoundary - (archiveWriter.BaseStream.Length - PaddingBoundary) % PaddingBoundary)]);

                    fileEntries.Add(entry);
                }

                // Write file entries section data
                using (var writer = new BinaryWriter(fileEntriesSection, Encoding.ASCII, true))
                {
                    foreach (var entry in fileEntries)
                    {
                        writer.Write(entry.compressedFilesize);
                        writer.Write(entry.rawFilesize);
                        writer.Write(entry.chunkIdx);
                        writer.Write(entry.chunkCount);
                        writer.Write(entry.offset);
                        writer.Write(entry.crc32);
                    }
                }

                // Write chunk section data
                using (var writer = new BinaryWriter(chunksSection, Encoding.ASCII, true))
                {
                    foreach (var entry in chunkEntries)
                    {
                        writer.Write(entry.chunkSize);
                        writer.Write(entry.unk);
                        writer.Write(entry.chunkId);
                    }
                }

                Console.WriteLine("Writing {0}...", outputFileName);

                var header = new L7CAHeader
                {
                    metadataOffset = (int) archiveWriter.BaseStream.Position
                };
                header.archiveSize = (int)(header.metadataOffset + filesystemSection.Length + fileEntriesSection.Length + chunksSection.Length + stringSection.Length);
                header.metadataSize = (int)(filesystemSection.Length + fileEntriesSection.Length + chunksSection.Length + stringSection.Length);
                header.filesystemEntries = filesystemEntries.Count;
                header.folders = folders.Count;
                header.files = files.Count;
                header.chunks = chunkEntries.Count;
                header.stringTableSize = (int)stringSection.Length;

                archiveWriter.BaseStream.Seek(0, SeekOrigin.Begin);
                archiveWriter.Write(header.magic);
                archiveWriter.Write(header.unk);
                archiveWriter.Write(header.archiveSize);
                archiveWriter.Write(header.metadataOffset);
                archiveWriter.Write(header.metadataSize);
                archiveWriter.Write(header.unk2);
                archiveWriter.Write(header.filesystemEntries);
                archiveWriter.Write(header.folders);
                archiveWriter.Write(header.files);
                archiveWriter.Write(header.chunks);
                archiveWriter.Write(header.stringTableSize);
                archiveWriter.Write(header.unk4);

                archiveWriter.BaseStream.Seek(0, SeekOrigin.End);

                // Write file info sections
                archiveWriter.Write(filesystemSection.GetBuffer(), 0, (int)filesystemSection.Length);
                archiveWriter.Write(fileEntriesSection.GetBuffer(), 0, (int)fileEntriesSection.Length);
                archiveWriter.Write(chunksSection.GetBuffer(), 0, (int)chunksSection.Length);
                archiveWriter.Write(stringSection.GetBuffer(), 0, (int)stringSection.Length);

            }
        }

        static void UnpackArchive(string inputFilename)
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(inputFilename)))
            {
                if (reader.ReadUInt32() != 0x4143374c)
                {
                    Console.WriteLine("Not a L7CA archive");
                    Environment.Exit(1);
                }

                var header = new L7CAHeader
                {
                    unk = reader.ReadUInt32(),
                    archiveSize = reader.ReadInt32(),
                    metadataOffset = reader.ReadInt32(),
                    metadataSize = reader.ReadInt32(),
                    unk2 = reader.ReadUInt32(),
                    filesystemEntries = reader.ReadInt32(),
                    folders = reader.ReadInt32(),
                    files = reader.ReadInt32(),
                    chunks = reader.ReadInt32(),
                    stringTableSize = reader.ReadInt32(),
                    unk4 = reader.ReadInt32()
                };

                if (header.unk4 != 0x05)
                {
                    Console.WriteLine("This archive type is unsupported and most likely won't unpack properly.");
                }

                // Read strings
                var baseOffset = reader.BaseStream.Length - header.stringTableSize;
                reader.BaseStream.Seek(baseOffset, SeekOrigin.Begin);

                var strings = new Dictionary<int, string>();
                while (reader.PeekChar() != -1)
                {
                    var offset = (int)(reader.BaseStream.Position - baseOffset);
                    int len = reader.ReadByte();
                    var str = Encoding.UTF8.GetString(reader.ReadBytes(len));

                    strings.Add(offset, str);
                }


                // Read filesystem entries
                var entries = new Dictionary<int, L7CAFilesystemEntry>();
                reader.BaseStream.Seek(header.metadataOffset, SeekOrigin.Begin);
                for (var i = 0; i < header.filesystemEntries; i++)
                {
                    var entry = new L7CAFilesystemEntry
                    {
                        id = reader.ReadInt32(),
                        hash = reader.ReadUInt32(),
                        folderOffset = reader.ReadInt32(),
                        filenameOffset = reader.ReadInt32(),
                        timestamp = reader.ReadInt64()
                    };

                    entry.filename = entry.id == -1 ? 
                        $"{strings[entry.folderOffset]}" : 
                        $"{strings[entry.folderOffset]}/{strings[entry.filenameOffset]}";

                    //Console.WriteLine("{0:x8} {1:x8} {2:x8} {3:x8} {4:x16}", entry.id, entry.hash, entry.folderOffset, entry.filenameOffset, entry.timestamp);

                    if (Crc32.CalculateNamco(entry.filename) != entry.hash)
                    {
                        Console.WriteLine("{0} did not match expected hash", entry.filename);
                    }

                    if (entry.id != -1)
                    {
                        entries.Add(entry.id, entry);
                    }
                    else
                    {
                        // -1 is a folder.
                        // Only create a folder and move on to next entry.
                        // This step probably isn't needed,  but just for the sake of completeness I added it.
                        // There might be some game out there that has blank folders but no actual data in it, so those will be accounted for as well.

                        if (!Directory.Exists(entry.filename))
                            Directory.CreateDirectory(entry.filename);
                    }
                }

                // Read file information
                var files = new List<L7CAFileEntry>();
                for (var i = 0; i < header.files; i++)
                {
                    var entry = new L7CAFileEntry
                    {
                        compressedFilesize = reader.ReadInt32(),
                        rawFilesize = reader.ReadInt32(),
                        chunkIdx = reader.ReadInt32(),
                        chunkCount = reader.ReadInt32(),
                        offset = reader.ReadInt32(),
                        crc32 = reader.ReadUInt32()
                    };

                    //var filename = entries[(uint)i].filename;
                    //Console.WriteLine("{2}\noffset[{0:x8}] fileSize[{1:x8}]\nreal_crc32[{3:x8}] crc32[{4:x8}] crc32[{5:x8}]\n", entry.offset, entry.compressedFileSize, filename, entries[(uint)i].hash, crc32.Value, entry.crc32);

                    files.Add(entry);
                }

                // Read chunk information
                var chunks = new List<L7CAChunkEntry>();
                for (var i = 0; i < header.chunks; i++)
                {
                    var entry = new L7CAChunkEntry
                    {
                        chunkSize = reader.ReadInt32(),
                        unk = reader.ReadUInt16(),
                        chunkId = reader.ReadUInt16()
                    };

                    //Console.WriteLine("{3:x8} {0:x8} {1:x4} {2:x4}", entry.chunkSize, entry.unk, entry.chunkNum, i);

                    chunks.Add(entry);
                }

                for (var i = 0; i < header.files; i++)
                {
                    var file = files[i];
                    var entry = entries[i];

                    Console.WriteLine("Extracting {0}...", entry.filename);
                    //Console.WriteLine("{0:x1} {1:x8} {2:x8} {3:x8} {4:x8} {5:x8}", file.chunkIdx, file.chunkCount, file.offset, file.compressedFileSize, file.rawFileSize, file.crc32);

                    //var output = Path.Combine("output", entry.filename);
                    var output = entry.filename;
                    Directory.CreateDirectory(Path.GetDirectoryName(output));

                    reader.BaseStream.Seek(file.offset, SeekOrigin.Begin);
                    var origData = reader.ReadBytes(file.compressedFilesize);

                    var data = new List<byte>();

                    using (var dataStream = new BinaryReader(new MemoryStream(origData)))
                    {
                        if (_debugDecompressionCode)
                        {
                            foreach (var f in Directory.EnumerateFiles(".", "output-chunk-*.bin"))
                                File.Delete(f);
                        }

                        for (int x = 0; x < file.chunkCount; x++)
                        {
                            var compMode = (chunks[file.chunkIdx + x].chunkSize >> 24) & 0xff;
                            var len = chunks[file.chunkIdx + x].chunkSize & 0xffffff;
                            var isCompressed = (chunks[file.chunkIdx + x].chunkSize & 0x80000000) != 0;
                            if (isCompressed)
                                Console.WriteLine("Chunk is compressed.");

                            if (_debugDecompressionCode)
                                Console.WriteLine("{0:x8} {1}", len, isCompressed);

                            var d = dataStream.ReadBytes(len);

                            if (_debugDecompressionCode)
                                File.WriteAllBytes($"output-chunk-{x}.bin", d);

                            if (isCompressed)
                            {
                                try
                                {
                                    // Decompress chunk
                                    switch (compMode)
                                    {
                                        case 0x80:
                                            d = TaikoCompression.Decompress(d, data.ToArray());
                                            data = new List<byte>(d);
                                            break;
                                        case 0x81:
                                            d = TaikoCompression2.Decompress(d, data.ToArray());
                                            data = new List<byte>(d);
                                            break;
                                    }
                                }
                                catch
                                {
                                    // Save compressed data
                                    Console.WriteLine("Could not decompress file.");
                                    data.AddRange(d);
                                }
                            }
                            else
                            {
                                data.AddRange(d);
                            }

                            //Console.WriteLine(" {0:x8}", d.Length);

                            if (_debugDecompressionCode)
                                File.WriteAllBytes($"output-chunk-{x}-decomp.bin", d);
                        }
                    }

                    var crc32 = Crc32.Calculate(data.ToArray());
                    if (crc32 != file.crc32)
                    {
                        Console.WriteLine("Invalid CRC32: {0:x8} vs {1:x8}", crc32, file.crc32);

                        //File.WriteAllBytes("invalid.bin", data.ToArray());
                        //Environment.Exit(1);
                    }

                    File.WriteAllBytes(output, data.ToArray());

                    if (data.Count != file.rawFilesize)
                    {
                        Console.WriteLine("Invalid file size: {0:x8} vs {1:x8}", data.Count, file.rawFilesize);
                        //Environment.Exit(1);
                    }

                    Console.WriteLine();
                }
            }
        }
    }
}