using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SharpDX;
using WoWEditor6.Editing;
using WoWEditor6.IO.Files.Models;
using WoWEditor6.Scene;
using WoWEditor6.Scene.Texture;

namespace WoWEditor6.IO.Files.Terrain.Wotlk
{
    struct ChunkInfo
    {
        public int Offset;
        public int Size;
    }

    class MapArea : Terrain.MapArea
    {
        private List<ChunkInfo> mChunkInfos = new List<ChunkInfo>();
        private List<MapChunk> mChunks = new List<MapChunk>();
        private List<LoadedModel> mWmoInstances = new List<LoadedModel>();

        private Mhdr mHeader;
        private Dictionary<uint, DataChunk> mSaveChunks = new Dictionary<uint, DataChunk>();
        private Mcin[] mChunkOffsets = new Mcin[0];
        private Mddf[] mDoodadDefs = new Mddf[0];
        private int[] mDoodadNameIds = new int[0];
        private readonly List<string> mDoodadNames = new List<string>();

        private bool mWasChanged;

        public MapArea(string continent, int ix, int iy)
        {
            Continent = continent;
            IndexX = ix;
            IndexY = iy;
        }

        public override void AddDoodadInstance(int uuid, string modelName, BoundingBox box, Vector3 position, Vector3 rotation, float scale)
        {
            var mmidValue = 0;
            var nameFound = false;
            foreach (var s in mDoodadNames)
            {
                if (string.Equals(s, modelName, StringComparison.InvariantCultureIgnoreCase))
                {
                    nameFound = true;
                    break;
                }

                mmidValue += s.Length + 1;
            }

            int mmidIndex;
            if (nameFound == false)
            {
                mmidValue = mDoodadNames.Sum(s => s.Length + 1);
                mmidIndex = mDoodadNameIds.Length;
                Array.Resize(ref mDoodadNameIds, mDoodadNameIds.Length + 1);
                mDoodadNameIds[mDoodadNameIds.Length - 1] = mmidValue;
                mDoodadNames.Add(modelName);
            }
            else
            {
                mmidIndex = -1;
                for (var i = 0; i < mDoodadNameIds.Length; ++i)
                {
                    if (mDoodadNameIds[i] == mmidValue)
                    {
                        mmidIndex = i;
                        break;
                    }
                }

                if (mmidIndex < 0)
                {
                    mmidIndex = mDoodadNameIds.Length;
                    Array.Resize(ref mDoodadNameIds, mDoodadNameIds.Length + 1);
                    mDoodadNameIds[mDoodadNameIds.Length - 1] = mmidValue;
                }
            }

            var mcrfValue = mDoodadDefs.Length;
            Array.Resize(ref mDoodadDefs, mDoodadDefs.Length + 1);
            mDoodadDefs[mDoodadDefs.Length - 1] = new Mddf
            {
                Position = new Vector3(position.X, position.Z, position.Y),
                Mmid = mmidIndex,
                Flags = 0,
                Scale = (ushort)(scale * 1024),
                UniqueId = uuid,
                Rotation = new Vector3(-rotation.X, 90 - rotation.Z, -rotation.Y)
            };

            var instance = WorldFrame.Instance.M2Manager.AddInstance(modelName, uuid, position, rotation,
                new Vector3(scale));

            DoodadInstances.Add(new M2Instance
            {
                Hash = modelName.ToUpperInvariant().GetHashCode(),
                Uuid = uuid,
                BoundingBox = (instance != null ? instance.BoundingBox : new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue))),
                RenderInstance = instance,
                MddfIndex = mDoodadDefs.Length - 1
            });

            foreach (var chunk in mChunks)
                if (chunk.BoundingBox.Contains(position) == ContainmentType.Contains)
                    chunk.AddDoodad(mcrfValue, box);

            mWasChanged = true;
        }

        public override void OnUpdateModelPositions(TerrainChangeParameters parameters)
        {
            var center = new Vector2(parameters.Center.X, parameters.Center.Y);
            foreach(var inst in DoodadInstances)
            {
                if (inst == null || inst.RenderInstance == null)
                    continue;

                var pos = mDoodadDefs[inst.MddfIndex].Position;
                var old_pos = pos;
                var dist = (new Vector2(pos.X, pos.Z) - center).Length();
                if (dist > parameters.OuterRadius)
                    continue;

                if(WorldFrame.Instance.MapManager.GetLandHeight(pos.X, pos.Z, out pos.Y))
                {
                    mDoodadDefs[inst.MddfIndex].Position = pos;
                    inst.RenderInstance.UpdatePosition(new Vector3(0, 0, pos.Y - old_pos.Y));
                }
            }
        }

        public void UpdateBoundingBox(BoundingBox chunkBox)
        {
            var minPos = chunkBox.Minimum;
            var maxPos = chunkBox.Maximum;

            var omin = BoundingBox.Minimum;
            var omax = BoundingBox.Maximum;

            omin.X = Math.Min(omin.X, minPos.X);
            omin.Y = Math.Min(omin.Y, minPos.Y);
            omin.Z = Math.Min(omin.Z, minPos.Z);
            omax.X = Math.Max(omax.X, maxPos.X);
            omax.Y = Math.Max(omax.Y, maxPos.Y);
            omax.Z = Math.Max(omax.Z, maxPos.Z);

            BoundingBox = new BoundingBox(omin, omax);
        }

        public void UpdateModelBox(BoundingBox chunkBox)
        {
            var minPos = chunkBox.Minimum;
            var maxPos = chunkBox.Maximum;

            var omin = ModelBox.Minimum;
            var omax = ModelBox.Maximum;

            omin.X = Math.Min(omin.X, minPos.X);
            omin.Y = Math.Min(omin.Y, minPos.Y);
            omin.Z = Math.Min(omin.Z, minPos.Z);
            omax.X = Math.Max(omax.X, maxPos.X);
            omax.Y = Math.Max(omax.Y, maxPos.Y);
            omax.Z = Math.Max(omax.Z, maxPos.Z);

            ModelBox = new BoundingBox(omin, omax);
        }

        public void UpdateVertices(MapChunk chunk)
        {
            if (chunk == null)
                return;

            var ix = chunk.IndexX;
            var iy = chunk.IndexY;

            var index = (ix + iy * 16) * 145;
            for (var i = 0; i < 145; ++i)
                FullVertices[i + index] = chunk.Vertices[i];
        }

        public override void Save()
        {
            if (mWasChanged == false)
                return;

            var hasMccv = mChunks.Any(c => c != null && c.HasMccv);
            if(hasMccv)
            {
                var wdt = WorldFrame.Instance.MapManager.CurrentWdt;
                if((wdt.Flags & 2) == 0)
                {
                    wdt.Flags |= 2;
                    wdt.Save(WorldFrame.Instance.MapManager.Continent);
                }
            }

            using (var strm = FileManager.Instance.GetOutputStream(string.Format(@"World\Maps\{0}\{0}_{1}_{2}.adt", Continent, IndexX, IndexY)))
            {
                var writer = new BinaryWriter(strm);
                writer.Write(0x4D564552); // MVER
                writer.Write(4);
                writer.Write(18);
                writer.Write(0x4D484452);
                writer.Write(SizeCache<Mhdr>.Size);

                var headerStart = writer.BaseStream.Position;
                writer.Write(mHeader);
                var header = mHeader;

                var chunkInfos = mChunkOffsets.ToArray();
                writer.Write(0x4D43494E);
                writer.Write(256 * SizeCache<Mcin>.Size);
                var mcinStart = writer.BaseStream.Position;
                writer.WriteArray(chunkInfos);

                header.ofsMcin = (int)(mcinStart - 28);

                header.ofsMtex = (int) (writer.BaseStream.Position - 20);
                writer.Write(0x4D544558);
                var textureData = new List<byte>();
                writer.Write(TextureNames.Sum(t =>
                {
                    var data = Encoding.ASCII.GetBytes(t);
                    textureData.AddRange(data);
                    textureData.Add(0);
                    return data.Length + 1;
                }));
                writer.Write(textureData.ToArray());

                header.ofsMmdx = (int) (writer.BaseStream.Position - 20);
                var m2NameData = new List<byte>();
                writer.Write(0x4D4D4458);
                writer.Write(mDoodadNames.Sum(t =>
                {
                    var data = Encoding.ASCII.GetBytes(t);
                    m2NameData.AddRange(data);
                    m2NameData.Add(0);
                    return data.Length + 1;
                }));
                writer.Write(m2NameData.ToArray());

                header.ofsMmid = (int) (writer.BaseStream.Position - 20);
                writer.Write(0x4D4D4944);
                writer.Write(mDoodadNameIds.Length * 4);
                writer.WriteArray(mDoodadNameIds);

                SaveChunk(0x4D574D4F, writer, out header.ofsMwmo);
                SaveChunk(0x4D574944, writer, out header.ofsMwid);

                if (mDoodadDefs != null && mDoodadDefs.Length > 0)
                {
                    header.ofsMddf = (int)(writer.BaseStream.Position - 20);
                    writer.Write(0x4D444446);
                    writer.Write(mDoodadDefs.Length * SizeCache<Mddf>.Size);
                    writer.WriteArray(mDoodadDefs);
                }
                else
                    header.ofsMddf = 0;

                SaveChunk(0x4D4F4446, writer, out header.ofsModf);
                SaveChunk(0x4D48324F, writer, out header.ofsMh2o);

                for (var i = 0; i < mChunks.Count; ++i)
                {
                    var startPos = writer.BaseStream.Position;
                    mChunks[i].SaveChunk(writer);
                    var endPos = writer.BaseStream.Position;
                    chunkInfos[i].OfsMcnk = (int)startPos;
                    chunkInfos[i].SizeMcnk = (int)(endPos - startPos);
                }

                SaveChunk(0x4D545846, writer, out header.ofsMtxf);
                SaveChunk(0x4D46424F, writer, out header.ofsMfbo);

                writer.BaseStream.Position = headerStart;
                writer.Write(header);
                writer.BaseStream.Position = mcinStart;
                writer.WriteArray(chunkInfos);
            }
        }

        public override void AsyncLoad()
        {
            using (var file = FileManager.Instance.Provider.OpenFile(string.Format(@"World\Maps\{0}\{0}_{1}_{2}.adt", Continent, IndexX, IndexY)))
            {
                if (file == null)
                {
                    IsValid = false;
                    return;
                }

                var reader = new BinaryReader(file);
                reader.BaseStream.Position = 0;
                while(reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    var signature = reader.ReadUInt32();
                    var size = reader.ReadInt32();
                    if (reader.BaseStream.Position + size > reader.BaseStream.Length)
                        break;

                    var bytes = reader.ReadBytes(size);
                    if (mSaveChunks.ContainsKey(signature))
                        continue;

                    mSaveChunks.Add(signature, new DataChunk {Data = bytes, Signature = signature, Size = size});
                }

                reader.BaseStream.Position = 0;
                if (SeekChunk(reader, 0x4D484452) == false)
                    throw new InvalidOperationException("ADT has no header chunk");

                reader.ReadInt32();
                mHeader = reader.Read<Mhdr>();
                InitChunkInfos(reader);
                InitTextures(reader);
                InitM2Models(reader);
                InitWmoModels(reader);
                InitChunks(reader);
            }
        }

        public override Terrain.MapChunk GetChunk(int index)
        {
            if (index >= mChunks.Count)
                throw new IndexOutOfRangeException();

            return mChunks[index];
        }

        public override bool Intersect(ref Ray ray, out Terrain.MapChunk chunk, out float distance)
        {
            distance = float.MaxValue;
            chunk = null;

            var mindistance = float.MaxValue;
            if (BoundingBox.Intersects(ref ray) == false)
                return false;

            Terrain.MapChunk chunkHit = null;
            var hasHit = false;
            foreach (var cnk in mChunks)
            {
                float dist;
                if (cnk.Intersect(ref ray, out dist) == false)
                    continue;

                hasHit = true;
                if (dist >= mindistance) continue;

                mindistance = dist;
                chunkHit = cnk;
            }

            chunk = chunkHit;
            distance = mindistance;
            return hasHit;
        }

        public override bool OnChangeTerrain(TerrainChangeParameters parameters)
        {
            var changed = false;
            foreach (var chunk in mChunks)
            {
                if (chunk == null)
                    continue;

                if (chunk.OnTerrainChange(parameters))
                    changed = true;
            }

            if (changed)
                mWasChanged = true;

            return changed;
        }

        public override void UpdateNormals()
        {
            foreach (var chunk in mChunks)
            {
                if (chunk == null) continue;

                chunk.UpdateNormals();
            }
        }

        public override bool OnTextureTerrain(TextureChangeParameters parameters)
        {
            var changed = false;
            foreach (var chunk in mChunks)
            {
                if (chunk == null) continue;

                if (chunk.OnTextureTerrain(parameters))
                    changed = true;
            }

            if (changed)
                mWasChanged = true;

            return changed;
        }

        private void InitChunkInfos(BinaryReader reader)
        {
            if (SeekChunk(reader, 0x4D43494E))
            {
                reader.ReadInt32();
                mChunkOffsets = reader.ReadArray<Mcin>(256);
            }

            reader.BaseStream.Position = 0;
            for (var i = 0; i < 256; ++i)
            {
                if (SeekNextMcnk(reader) == false)
                    throw new InvalidOperationException("Area is missing chunks");

                mChunkInfos.Add(new ChunkInfo
                {
                    Offset = (int)(reader.BaseStream.Position),
                    Size = reader.ReadInt32()
                });

                reader.ReadBytes(mChunkInfos.Last().Size);
            }
        }

        private void InitTextures(BinaryReader reader)
        {
            if (SeekChunk(reader, 0x4D544558) == false)
                return;

            var size = reader.ReadInt32();
            var bytes = reader.ReadBytes(size);
            var fullString = Encoding.ASCII.GetString(bytes);
            TextureNames.AddRange(fullString.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));
            for (var i = 0; i < TextureNames.Count; ++i)
            {
                mTextures.Add(TextureManager.Instance.GetTexture(TextureNames[i]));
                TextureNames[i] = TextureNames[i].ToLowerInvariant();
            }

            LoadSpecularTextures();
        }

        private void InitChunks(BinaryReader reader)
        {
            var minPos = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var maxPos = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            var modelMin = new Vector3(float.MaxValue);
            var modelMax = new Vector3(float.MinValue);

            for (var i = 0; i < 256; ++i)
            {
                var chunk = new MapChunk(i % 16, i / 16, new WeakReference<MapArea>(this));
                if (chunk.AsyncLoad(reader, mChunkInfos[i]) == false)
                    throw new InvalidOperationException("Unable to load chunk");

                var bbmin = chunk.BoundingBox.Minimum;
                var bbmax = chunk.BoundingBox.Maximum;
                if (bbmin.X < minPos.X)
                    minPos.X = bbmin.X;
                if (bbmax.X > maxPos.X)
                    maxPos.X = bbmax.X;
                if (bbmin.Y < minPos.Y)
                    minPos.Y = bbmin.Y;
                if (bbmax.Y > maxPos.Y)
                    maxPos.Y = bbmax.Y;
                if (bbmin.Z < minPos.Z)
                    minPos.Z = bbmin.Z;
                if (bbmax.Z > maxPos.Z)
                    maxPos.Z = bbmax.Z;

                bbmin = chunk.ModelBox.Minimum;
                bbmax = chunk.ModelBox.Maximum;
                if (bbmin.X < modelMin.X)
                    modelMin.X = bbmin.X;
                if (bbmax.X > modelMax.X)
                    modelMax.X = bbmax.X;
                if (bbmin.Y < modelMin.Y)
                    modelMin.Y = bbmin.Y;
                if (bbmax.Y > modelMax.Y)
                    modelMax.Y = bbmax.Y;
                if (bbmin.Z < modelMin.Z)
                    modelMin.Z = bbmin.Z;
                if (bbmax.Z > modelMax.Z)
                    modelMax.Z = bbmax.Z;

                mChunks.Add(chunk);
                Array.Copy(chunk.Vertices, 0, FullVertices, i * 145, 145);
            }

            BoundingBox = new BoundingBox(minPos, maxPos);
            ModelBox = new BoundingBox(modelMin, modelMax);
        }

        private void InitM2Models(BinaryReader reader)
        {
            if (SeekChunk(reader, 0x4D4D4458) == false)
                return;

            var size = reader.ReadInt32();
            var bytes = reader.ReadBytes(size);
            var fullString = Encoding.ASCII.GetString(bytes);
            var modelNames = fullString.Split('\0');
            mDoodadNames.AddRange(modelNames.ToList());
            var modelNameLookup = new Dictionary<int, string>();
            var curOffset = 0;
            foreach (var name in modelNames)
            {
                modelNameLookup.Add(curOffset, name);
                curOffset += name.Length + 1;
            }

            if (SeekChunk(reader, 0x4D4D4944) == false)
                return;

            size = reader.ReadInt32();
            mDoodadNameIds = reader.ReadArray<int>(size / 4);

            if (SeekChunk(reader, 0x4D444446) == false)
                return;

            size = reader.ReadInt32();
            mDoodadDefs = reader.ReadArray<Mddf>(size / SizeCache<Mddf>.Size);

            var index = -1;
            foreach (var entry in mDoodadDefs)
            {
                ++index;
                if (entry.Mmid >= mDoodadNameIds.Length)
                    continue;

                var nameId = mDoodadNameIds[entry.Mmid];
                string modelName;
                if (modelNameLookup.TryGetValue(nameId, out modelName) == false)
                    continue;

                var position = new Vector3(entry.Position.X, entry.Position.Z, entry.Position.Y);
                var rotation = new Vector3(-entry.Rotation.X, -entry.Rotation.Z, 90 - entry.Rotation.Y);
                var scale = entry.Scale / 1024.0f;

                var instance = WorldFrame.Instance.M2Manager.AddInstance(modelName, entry.UniqueId, position, rotation,
                    new Vector3(scale));

                DoodadInstances.Add(new M2Instance
                {
                    Hash = modelName.ToUpperInvariant().GetHashCode(),
                    Uuid = entry.UniqueId,
                    BoundingBox = (instance != null ? instance.BoundingBox : new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue))),
                    RenderInstance = instance,
                    MddfIndex = index
                });
            }
        }

        private void InitWmoModels(BinaryReader reader)
        {
            if (SeekChunk(reader, 0x4D574D4F) == false)
                return;

            var size = reader.ReadInt32();
            var bytes = reader.ReadBytes(size);
            var modelNameLookup = new Dictionary<int, string>();
            var curOffset = 0;
            var curBytes = new List<byte>();

            for (var i = 0; i < bytes.Length; ++i)
            {
                if (bytes[i] == 0)
                {
                    if (curBytes.Count > 0)
                        modelNameLookup.Add(curOffset, Encoding.ASCII.GetString(curBytes.ToArray()));

                    curOffset = i + 1;
                    curBytes.Clear();
                }
                else
                    curBytes.Add(bytes[i]);
            }

            if (SeekChunk(reader, 0x4D574944) == false)
                return;

            size = reader.ReadInt32();
            var modelNameIds = reader.ReadArray<int>(size / 4);

            if (SeekChunk(reader, 0x4D4F4446) == false)
                return;

            size = reader.ReadInt32();
            var modf = reader.ReadArray<Modf>(size / SizeCache<Modf>.Size);

            foreach (var entry in modf)
            {
                if (entry.Mwid >= modelNameIds.Length)
                    continue;

                var nameId = modelNameIds[entry.Mwid];
                string modelName;
                if (modelNameLookup.TryGetValue(nameId, out modelName) == false)
                    continue;


                var position = new Vector3(entry.Position.X, entry.Position.Z, entry.Position.Y);
                var rotation = new Vector3(entry.Rotation.Z, -entry.Rotation.X, 90 - entry.Rotation.Y);

                WorldFrame.Instance.WmoManager.AddInstance(modelName, entry.UniqueId, position, rotation);
                mWmoInstances.Add(new LoadedModel(modelName, entry.UniqueId));
            }
        }

        private void SaveChunk(uint signature, BinaryWriter writer, out int offsetField)
        {
            if(mSaveChunks.ContainsKey(signature) == false)
            {
                offsetField = 0;
                return;
            }

            offsetField = (int)writer.BaseStream.Position - (12 + 8); // MVER signature + size + version = 12 bytes, MHDR signature + size = 8 bytes

            var chunk = mSaveChunks[signature];
            writer.Write(signature);
            writer.Write(chunk.Size);
            writer.Write(chunk.Data);
        }

        private static bool SeekNextMcnk(BinaryReader reader) { return SeekChunk(reader, 0x4D434E4B, false); }

        private static bool SeekChunk(BinaryReader reader, uint signature, bool begin = true)
        {
            if (begin)
                reader.BaseStream.Position = 0;

            try
            {
                var sig = reader.ReadUInt32();
                while (sig != signature)
                {
                    var size = reader.ReadInt32();
                    reader.ReadBytes(size);
                    sig = reader.ReadUInt32();
                }

                return sig == signature;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (mChunks != null)
            {
                foreach (var chunk in mChunks)
                    chunk.Dispose();

                mChunks.Clear();
                mChunks = null;
            }

            if (mWmoInstances != null)
            {
                foreach (var instance in mWmoInstances)
                    WorldFrame.Instance.WmoManager.RemoveInstance(instance.FileName, instance.Uuid,false);

                mWmoInstances.Clear();
                mWmoInstances = null;
            }

            if (mChunkInfos != null)
            {
                mChunkInfos.Clear();
                mChunkInfos = null;
            }

            if (mSaveChunks != null)
            {
                mSaveChunks.Clear();
                mSaveChunks = null;
            }

            mChunkOffsets = null;
            mDoodadDefs = null;

            base.Dispose(disposing);
        }

        public override void SetChanged()
        {
            mWasChanged = true;
        }
    }
}
