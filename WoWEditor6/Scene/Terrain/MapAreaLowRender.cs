using System;
using SharpDX;
using WoWEditor6.Graphics;

namespace WoWEditor6.Scene.Terrain
{
    class MapAreaLowRender : IDisposable
    {
        private const float StepX = Metrics.TileSize / 16.0f;
        private const float StepY = Metrics.TileSize / 32.0f;

        private bool mAsyncLoaded;
        private bool mSyncLoaded;

        private Vector3[] mVertexData;
        private BoundingBox mBoudingBox;
        private VertexBuffer mVertexBuffer;

        public static Mesh Mesh { get; private set; }

        public int IndexX { get; private set; }
        public int IndexY { get; private set; }

        public MapAreaLowRender(int indexX, int indexY)
        {
            IndexX = indexX;
            IndexY = indexY;
        }

        ~MapAreaLowRender()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (mVertexBuffer != null)
            {
                var vertexBuffer = mVertexBuffer;
                WorldFrame.Instance.Dispatcher.BeginInvoke(() =>
                {
                    if (vertexBuffer != null)
                        vertexBuffer.Dispose();
                });

                mVertexBuffer = null;
            }

            mVertexData = null;
        }

        public virtual void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void OnFrame()
        {
            if (mAsyncLoaded == false)
                return;

            if (WorldFrame.Instance.ActiveCamera.Contains(ref mBoudingBox) == false)
                return;

            if(mSyncLoaded == false)
            {
                mVertexBuffer = new VertexBuffer(WorldFrame.Instance.GraphicsContext);
                mVertexBuffer.UpdateData(mVertexData);
                mSyncLoaded = true;
            }

            Mesh.UpdateVertexBuffer(mVertexBuffer);
            Mesh.Draw();
        }

        public unsafe void InitFromHeightData(IO.Files.Terrain.MareEntry entry)
        {
            mVertexData = new Vector3[17 * 17 + 16 * 16];

            var counter = 0;

            var posMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var posMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for(var y = 0; y < 33; ++y)
            {
                for(var x = 0; x < ((y % 2 != 0) ? 16 : 17); ++x)
                {
                    var inner = (y % 2) != 0;
                    float height = inner ? entry.inner[(y / 2) * 16 + x] : entry.outer[(y / 2) * 17 + x];

                    var v = new Vector3(x * StepX, y * StepY, height);
                    v.X += IndexX * Metrics.TileSize;
                    if (inner)
                        v.X += 0.5f * StepX;

                    v.Y += IndexY * Metrics.TileSize;
                    if (IO.FileManager.Instance.Version < IO.FileDataVersion.Lichking)
                        v.Y = 64.0f * Metrics.TileSize - v.Y;

                    if (v.X < posMin.X) posMin.X = v.X;
                    if (v.X > posMax.X) posMax.X = v.X;
                    if (v.Y < posMin.Y) posMin.Y = v.Y;
                    if (v.Y > posMax.Y) posMax.Y = v.Y;
                    if (v.Z < posMin.Z) posMin.Z = v.Z;
                    if (v.Z > posMax.Z) posMax.Z = v.Z;

                    mVertexData[counter++] = v;
                }
            }

            mBoudingBox = new BoundingBox(posMin, posMax);
            mAsyncLoaded = true;
        }

        public static void Initialize(GxContext context)
        {
            Mesh = new Mesh(context)
            {
                IndexCount = 16 * 16 * 4 * 3,
                Stride = IO.SizeCache<Vector3>.Size,
                DepthState = {DepthEnabled = false}
            };

            Mesh.AddElement("POSITION", 0, 3);

            var indices = new uint[16 * 16 * 12];
            for (var y = 0u; y < 16; ++y)
            {
                for (var x = 0u; x < 16; ++x)
                {
                    var i = y * 16 * 12 + x * 12;

                    indices[i + 0] = y * 33 + x;
                    indices[i + 2] = y * 33 + x + 1;
                    indices[i + 1] = y * 33 + x + 17;

                    indices[i + 3] = y * 33 + x + 1;
                    indices[i + 5] = y * 33 + x + 34;
                    indices[i + 4] = y * 33 + x + 17;

                    indices[i + 6] = y * 33 + x + 34;
                    indices[i + 8] = y * 33 + x + 33;
                    indices[i + 7] = y * 33 + x + 17;

                    indices[i + 9] = y * 33 + x + 33;
                    indices[i + 11] = y * 33 + x;
                    indices[i + 10] = y * 33 + x + 17;
                }
            }

            Mesh.IndexBuffer.UpdateData(indices);
            Mesh.IndexBuffer.IndexFormat = SharpDX.DXGI.Format.R32_UInt;

            var program = new ShaderProgram(context);
            program.SetVertexShader(Resources.Shaders.MapLowVertex);
            program.SetPixelShader(Resources.Shaders.MapLowPixel);
            Mesh.Program = program;
            Mesh.InitLayout(program);
        }
    }
}
