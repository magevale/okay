using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SharpDX;
using WoWEditor6.Scene.Models.M2;

namespace WoWEditor6.IO.Files.Models.Wotlk
{
    class M2File : Models.M2File
    {
        private string mModelName;
        private readonly string mFileName;

        private M2Header mHeader;
        private Graphics.Texture[] mTextures = new Graphics.Texture[0];
        private bool mRemapBlend;
        private ushort[] mBlendMap = new ushort[0];
        private M2SkinFile mSkin;
        private string[] mDirectoryParts;

        public M2AnimationBone[] Bones { get; private set; }
        public M2UVAnimation[] UvAnimations { get; private set; }
        public M2TexColorAnimation[] ColorAnimations { get; private set; }
        public M2AlphaAnimation[] Transparencies { get; private set; }

        public uint[] GlobalSequences { get; private set; }
        public AnimationEntry[] Animations { get; private set; }

        public override string ModelName { get { return mModelName; } }

        public M2File(string fileName) : base(fileName)
        {
            Bones = new M2AnimationBone[0];
            UvAnimations = new M2UVAnimation[0];
            ColorAnimations = new M2TexColorAnimation[0];
            Transparencies = new M2AlphaAnimation[0];
            GlobalSequences = new uint[0];
            Animations = new AnimationEntry[0];
            AnimationLookup = new short[0];
            mModelName = string.Empty;
            mFileName = fileName;
            mDirectoryParts = Path.GetDirectoryName(fileName).Split(Path.DirectorySeparatorChar);
        }

        public override bool Load()
        {
            using (var strm = FileManager.Instance.Provider.OpenFile(mFileName))
            {
                var reader = new BinaryReader(strm);
                mHeader = reader.Read<M2Header>();

                BoundingRadius = mHeader.VertexRadius;

                if ((mHeader.GlobalFlags & 0x08) != 0)
                {
                    mRemapBlend = true;
                    var nBlendMaps = reader.Read<int>();
                    var ofsBlendMaps = reader.Read<int>();
                    strm.Position = ofsBlendMaps;
                    mBlendMap = reader.ReadArray<ushort>(nBlendMaps);
                }

                strm.Position = mHeader.OfsName;
                if (mHeader.LenName > 0)
                    mModelName = Encoding.ASCII.GetString(reader.ReadBytes(mHeader.LenName - 1));

                GlobalSequences = ReadArrayOf<uint>(reader, mHeader.OfsGlobalSequences, mHeader.NGlobalSequences);
                Vertices = ReadArrayOf<M2Vertex>(reader, mHeader.OfsVertices, mHeader.NVertices);

                var minPos = new Vector3(float.MaxValue);
                var maxPos = new Vector3(float.MinValue);
                for (var i = 0; i < Vertices.Length; ++i)
                {
                    var p = Vertices[i].position;
                    p = new Vector3(p.X, -p.Y, p.Z);
                    Vertices[i].position = p;
                    Vertices[i].normal = new Vector3(Vertices[i].normal.X, -Vertices[i].normal.Y, Vertices[i].normal.Z);
                    if (p.X < minPos.X) minPos.X = p.X;
                    if (p.Y < minPos.Y) minPos.Y = p.Y;
                    if (p.Z < minPos.Z) minPos.Z = p.Z;
                    if (p.X > maxPos.X) maxPos.X = p.X;
                    if (p.Y > maxPos.Y) maxPos.Y = p.Y;
                    if (p.Z > maxPos.Z) maxPos.Z = p.Z;
                }

                BoundingBox = new BoundingBox(minPos, maxPos);

                LoadCreatureVariations();

                var textures = ReadArrayOf<M2Texture>(reader, mHeader.OfsTextures, mHeader.NTextures);
                mTextures = new Graphics.Texture[textures.Length];
                TextureInfos = new TextureInfo[textures.Length];
                for (var i = 0; i < textures.Length; ++i)
                {
                    var tex = textures[i];
                    if (tex.type == 0 && tex.lenName > 0)
                    {
                        var texName = Encoding.ASCII.GetString(ReadArrayOf<byte>(reader, tex.ofsName, tex.lenName - 1)).Trim();
                        mTextures[i] = Scene.Texture.TextureManager.Instance.GetTexture(texName);
                    }
                    else
                    {
                        switch ((TextureType)tex.type)
                        {
                            //case TextureType.Skin:
                            //    string skinTexName = $"{mDirectoryParts[1]}{mDirectoryParts[2]}NakedTorsoSkin00_{DisplayOptions.SkinId.ToString("00")}.blp";
                            //    skinTexName = Path.Combine(Path.GetDirectoryName(mFileName), skinTexName);
                            //    mTextures[i] = Scene.Texture.TextureManager.Instance.GetTexture(skinTexName);
                            //    break;

                            ////case TextureType.ObjectSkin:
                            ////    break;

                            //case TextureType.CharacterHair:
                            //    string hairTexName = $"{mDirectoryParts[1]}NakedTorsoSkin00_{DisplayOptions.SkinId.ToString("00")}.blp";
                            //    skinTexName = Path.Combine(Path.GetDirectoryName(mFileName), hairTexName);
                            //    mTextures[i] = Scene.Texture.TextureManager.Instance.GetTexture(skinTexName);
                            //    break;

                            case TextureType.MonsterSkin1:
                                if (DisplayOptions.TextureVariationFiles.Count > DisplayOptions.TextureVariation)
                                    if (!string.IsNullOrEmpty(DisplayOptions.TextureVariationFiles[DisplayOptions.TextureVariation].Item1))
                                        mTextures[i] = Scene.Texture.TextureManager.Instance.GetTexture(DisplayOptions.TextureVariationFiles[DisplayOptions.TextureVariation].Item1);
                                break;
                            case TextureType.MonsterSkin2:
                                if (DisplayOptions.TextureVariationFiles.Count > DisplayOptions.TextureVariation)
                                    if (!string.IsNullOrEmpty(DisplayOptions.TextureVariationFiles[DisplayOptions.TextureVariation].Item2))
                                        mTextures[i] = Scene.Texture.TextureManager.Instance.GetTexture(DisplayOptions.TextureVariationFiles[DisplayOptions.TextureVariation].Item2);
                                break;
                            case TextureType.MonsterSkin3:
                                if (DisplayOptions.TextureVariationFiles.Count > DisplayOptions.TextureVariation)
                                    if (!string.IsNullOrEmpty(DisplayOptions.TextureVariationFiles[DisplayOptions.TextureVariation].Item3))
                                        mTextures[i] = Scene.Texture.TextureManager.Instance.GetTexture(DisplayOptions.TextureVariationFiles[DisplayOptions.TextureVariation].Item3);
                                break;
                            default:
                                mTextures[i] = Scene.Texture.TextureManager.Instance.GetTexture("default_texture");
                                break;
                        }
                    }

                    Graphics.Texture.SamplerFlagType samplerFlags;

                    if (tex.flags == 3) samplerFlags = Graphics.Texture.SamplerFlagType.WrapBoth;
                    else if (tex.flags == 2) samplerFlags = Graphics.Texture.SamplerFlagType.WrapV;
                    else if (tex.flags == 1) samplerFlags = Graphics.Texture.SamplerFlagType.WrapU;
                    else samplerFlags = Graphics.Texture.SamplerFlagType.ClampBoth;

                    TextureInfos[i] = new TextureInfo
                    {
                        Texture = mTextures[i],
                        TextureType = (TextureType)tex.type,
                        SamplerFlags = samplerFlags
                    };
                }

                LoadSkins(reader);
                LoadAnimations(reader);
            }

            return true;
        }

        private void LoadCreatureVariations()
        {
            Func<string, string> FormatPath = f =>
            {
                if (f == "") return string.Empty; //Ignore blank
                if (Path.GetExtension(f) != ".blp") f += ".blp"; //Append filetype
                return Path.Combine(Path.GetDirectoryName(mFileName), f); //Add full directory location
            };

            DisplayOptions.TextureVariationFiles = new List<Tuple<string, string, string>>();
            HashSet<Tuple<string, string, string>> variations = new HashSet<Tuple<string, string, string>>(); //Unique combinations only

            //First check creatures/characters 
            if (mDirectoryParts.Length > 0 && mDirectoryParts[0].ToLower() != "character" && mDirectoryParts[0].ToLower() != "creature")
                return;

            //Second check MDX exists
            string mdx = Path.ChangeExtension(mFileName, ".mdx").ToLower();
            if (!Storage.DbcStorage.CreatureModelData.GetAllRows<Wotlk.CreatureModelDataEntry>().Any(x => x.ModelPath.ToLower() == mdx))
                return;

            var modelDisplay = from cmd in Storage.DbcStorage.CreatureModelData.GetAllRows<Wotlk.CreatureModelDataEntry>()
                               join cdi in Storage.DbcStorage.CreatureDisplayInfo.GetAllRows<Wotlk.CreatureDisplayInfoEntry>()
                               on cmd.ID equals cdi.ModelId
                               where cmd.ModelPath.ToLower() == mdx
                               select cdi;

            if (modelDisplay.Count() == 0)
            {
                variations.Add(new Tuple<string, string, string>("default_texture", "", ""));
            }
            else
            {
                foreach (var display in modelDisplay)
                {
                    variations.Add(new Tuple<string, string, string>(FormatPath(display.TextureVariation1),
                                                                     FormatPath(display.TextureVariation2),
                                                                     FormatPath(display.TextureVariation3)));
                }

                //Get Extra display info
                var extraDisplay = from md in modelDisplay
                                   join cdi in Storage.DbcStorage.CreatureDisplayInfoExtra.GetAllRows<Wotlk.CreatureDisplayInfoExtraEntry>()
                                   on md.ExtendedDisplayInfoId equals cdi.ID
                                   select cdi;

                if(extraDisplay.Count() > 0)
                {
                    DisplayOptions.FaceOptions = extraDisplay.Select(x => x.FaceID).Distinct().ToArray();
                    DisplayOptions.FacialHairOptions = extraDisplay.Select(x => x.FacialHairID).Distinct().ToArray();
                    DisplayOptions.SkinOptions = extraDisplay.Select(x => x.SkinID).Distinct().ToArray();
                    DisplayOptions.HairColorOptions = extraDisplay.Select(x => x.HairColorID).Distinct().ToArray();
                    DisplayOptions.HairStyleOptions = extraDisplay.Select(x => x.HairStyleID).Distinct().ToArray();
                }
            }

            DisplayOptions.TextureVariationFiles.AddRange(variations);
        }

        private void LoadSkins(BinaryReader reader)
        {
            mSkin = new M2SkinFile(ModelRoot, mModelName, 0);
            if (mSkin.Load() == false)
                throw new InvalidOperationException("Unable to load skin file");

            Indices = mSkin.Indices;

            var texLookup = ReadArrayOf<ushort>(reader, mHeader.OfsTexLookup, mHeader.NTexLookup);
            var renderFlags = ReadArrayOf<uint>(reader, mHeader.OfsRenderFlags, mHeader.NRenderFlags);
            var uvAnimLookup = ReadArrayOf<short>(reader, mHeader.OfsUvAnimLookup, mHeader.NUvAnimLookup);

            mSubMeshes = mSkin.SubMeshes.Select(sm => new M2SubMeshInfo
            {
                BoundingSphere =
                    new BoundingSphere(new Vector3(sm.centerBoundingBox.X, -sm.centerBoundingBox.Y, sm.centerBoundingBox.Z), sm.radius),
                NumIndices = sm.nTriangles,
                StartIndex = sm.startTriangle + (((sm.unk1 & 1) != 0) ? (ushort.MaxValue + 1) : 0)
            }).ToArray();

            foreach (var texUnit in mSkin.TexUnits)
            {
                var mesh = mSkin.SubMeshes[texUnit.submeshIndex];

                int uvIndex;
                if (texUnit.textureAnimIndex >= uvAnimLookup.Length || uvAnimLookup[texUnit.textureAnimIndex] < 0)
                    uvIndex = -1;
                else
                    uvIndex = uvAnimLookup[texUnit.textureAnimIndex];

                var startTriangle = (int)mesh.startTriangle;
                if ((mesh.unk1 & 1) != 0)
                    startTriangle += ushort.MaxValue + 1;

                var textures = new List<Graphics.Texture>();
                var texIndices = new List<int>();
                switch (texUnit.op_count)
                {
                    case 2:
                        textures.Add(mTextures[texLookup[texUnit.texture]]);
                        textures.Add(mTextures[texLookup[texUnit.texture + 1]]);
                        texIndices.Add(texLookup[texUnit.texture]);
                        texIndices.Add(texLookup[texUnit.texture + 1]);
                        break;
                    case 3:
                        textures.Add(mTextures[texLookup[texUnit.texture]]);
                        textures.Add(mTextures[texLookup[texUnit.texture + 1]]);
                        textures.Add(mTextures[texLookup[texUnit.texture + 2]]);
                        texIndices.Add(texLookup[texUnit.texture]);
                        texIndices.Add(texLookup[texUnit.texture + 1]);
                        texIndices.Add(texLookup[texUnit.texture + 2]);
                        break;
                    case 4:
                        textures.Add(mTextures[texLookup[texUnit.texture]]);
                        textures.Add(mTextures[texLookup[texUnit.texture + 1]]);
                        textures.Add(mTextures[texLookup[texUnit.texture + 2]]);
                        textures.Add(mTextures[texLookup[texUnit.texture + 3]]);
                        texIndices.Add(texLookup[texUnit.texture]);
                        texIndices.Add(texLookup[texUnit.texture + 1]);
                        texIndices.Add(texLookup[texUnit.texture + 2]);
                        texIndices.Add(texLookup[texUnit.texture + 3]);
                        break;
                    default:
                        textures.Add(mTextures[texLookup[texUnit.texture]]);
                        texIndices.Add(texLookup[texUnit.texture]);
                        break;
                }

                var flags = renderFlags[texUnit.renderFlags];
                var blendMode = flags >> 16;
                var flag = flags & 0xFFFF;

                if (mRemapBlend && texUnit.shaderId < mBlendMap.Length)
                    blendMode = mBlendMap[texUnit.shaderId];

                blendMode %= 7;

                if (blendMode != 0 && blendMode != 1)
                    HasBlendPass = true;
                else
                    HasOpaquePass = true;

                Passes.Add(new M2RenderPass
                {
                    TextureIndices = texIndices,
                    Textures = textures,
                    AlphaAnimIndex = texUnit.transparencyIndex,
                    ColorAnimIndex = texUnit.colorIndex,
                    IndexCount = mesh.nTriangles,
                    RenderFlag = flag,
                    BlendMode = blendMode,
                    StartIndex = startTriangle,
                    TexAnimIndex = uvIndex,
                    TexUnitNumber = texUnit.texUnitNumber,
                    OpCount = texUnit.op_count,
                    VertexShaderType = M2ShadersClass.GetVertexShaderTypeOld(texUnit.shaderId, texUnit.op_count),
                    PixelShaderType = M2ShadersClass.GetPixelShaderTypeOld(texUnit.shaderId, texUnit.op_count),
                });
            }

            SortPasses();
        }

        private void LoadAnimations(BinaryReader reader)
        {
            var bones = ReadArrayOf<M2Bone>(reader, mHeader.OfsBones, mHeader.NBones);
            Bones = bones.Select(b => new M2AnimationBone(this, ref b, reader)).ToArray();

            if (Bones.Any(b => b.IsBillboarded))
                NeedsPerInstanceAnimation = true;

            AnimationLookup = ReadArrayOf<short>(reader, mHeader.OfsAnimLookup, mHeader.NAnimLookup);
            Animations = ReadArrayOf<AnimationEntry>(reader, mHeader.OfsAnimations, mHeader.NAnimations);

            AnimationIds = Animations.Select(x => x.animationID).ToArray();

            var uvAnims = ReadArrayOf<M2TexAnim>(reader, mHeader.OfsUvAnimation, mHeader.NUvAnimation);
            UvAnimations = uvAnims.Select(uv => new M2UVAnimation(this, ref uv, reader)).ToArray();

            var colorAnims = ReadArrayOf<M2ColorAnim>(reader, mHeader.OfsSubmeshAnimations, mHeader.NSubmeshAnimations);
            ColorAnimations = colorAnims.Select(c => new M2TexColorAnimation(this, ref c, reader)).ToArray();

            var transparencies = ReadArrayOf<AnimationBlock>(reader, mHeader.OfsTransparencies, mHeader.NTransparencies);
            Transparencies = transparencies.Select(t => new M2AlphaAnimation(this, ref t, reader)).ToArray();
        }

        private void SortPasses()
        {
            Passes.Sort((e1, e2) =>
            {
                if (e1.BlendMode == 0 && e2.BlendMode != 0)
                    return -1;

                if (e1.BlendMode != 0 && e2.BlendMode == 0)
                    return 1;

                if (e1.BlendMode == e2.BlendMode && e1.BlendMode == 0)
                    return e1.TexUnitNumber.CompareTo(e2.TexUnitNumber);

                if (e1.BlendMode == 2 && e2.BlendMode != 2)
                    return -1;

                if (e2.BlendMode == 2 && e1.BlendMode != 2)
                    return 1;

                var is1Additive = e1.BlendMode == 1 || e1.BlendMode == 6 || e1.BlendMode == 3;
                var is2Additive = e2.BlendMode == 1 || e2.BlendMode == 6 || e2.BlendMode == 3;

                if (is1Additive && !is2Additive)
                    return -1;

                if (is2Additive && !is1Additive)
                    return 1;

                return e1.TexUnitNumber.CompareTo(e2.TexUnitNumber);
            });
        }

        public override int GetNumberOfBones()
        {
            return Bones.Length;
        }

        private static T[] ReadArrayOf<T>(BinaryReader reader, int offset, int count) where T : struct
        {
            if (count == 0)
                return new T[0];

            reader.BaseStream.Position = offset;
            return reader.ReadArray<T>(count);
        }

    }
}
