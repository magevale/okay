using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;

namespace WoWEditor6.Graphics
{
    class ShaderProgram : IDisposable
    {
        private static readonly ShaderResourceView[] ShaderViews = new ShaderResourceView[64];

        private VertexShader mVertexShader;
        private PixelShader mPixelShader;
        private GxContext mContext;

        public ShaderBytecode VertexShaderCode { get; private set; }

        private static Hashtable mVertexShaderCache = new Hashtable();
        private static Hashtable mPixelShaderCache = new Hashtable();

        public ShaderProgram(GxContext context)
        {
            mContext = context;
        }

        public void SetVertexSampler(int slot, Sampler sampler)
        {
            mContext.Context.VertexShader.SetSampler(slot, sampler.Native);
        }

        public void SetPixelSampler(int slot, Sampler sampler)
        {
            mContext.Context.PixelShader.SetSampler(slot, sampler.Native);
        }

        public void SetVertexTexture(int slot, Texture texture)
        {
            mContext.Context.VertexShader.SetShaderResource(slot, texture.NativeView);
        }

        public void SetVertexTextures(int slot, params Texture[] textures)
        {
            for (var i = 0; i < textures.Length; ++i)
                ShaderViews[i] = textures[i].NativeView;

            mContext.Context.VertexShader.SetShaderResources(slot, textures.Length, ShaderViews);
        }

        public void SetVertexTexture(int slot, ShaderResourceView view)
        {
            mContext.Context.VertexShader.SetShaderResource(slot, view);
        }

        public void SetPixelTexture(int slot, Texture texture)
        {
            mContext.Context.PixelShader.SetShaderResource(slot, texture.NativeView);
        }

        public void SetPixelTexture(int slot, ShaderResourceView view)
        {
            mContext.Context.PixelShader.SetShaderResource(slot, view);
        }

        public void SetPixelTextures(int slot, params Texture[] textures)
        {
            for (var i = 0; i < textures.Length; ++i)
                ShaderViews[i] = textures[i].NativeView;

            mContext.Context.PixelShader.SetShaderResources(slot, textures.Length, ShaderViews);
        }

        public void SetPixelTextures(int slot, params ShaderResourceView[] textures)
        {
            mContext.Context.PixelShader.SetShaderResources(slot, textures);
        }

        public void SetVertexConstantBuffer(int slot, ConstantBuffer buffer)
        {
            mContext.Context.VertexShader.SetConstantBuffer(slot, buffer.Native);
        }

        public void SetVertexConstantBuffers(int slot, params ConstantBuffer[] buffers)
        {
            mContext.Context.VertexShader.SetConstantBuffers(slot, buffers.Select(b => b.Native).ToArray());
        }

        public void SetPixelConstantBuffer(int slot, ConstantBuffer buffer)
        {
            mContext.Context.PixelShader.SetConstantBuffer(slot, buffer.Native);
        }

        public void SetPixelConstantBuffers(int slot, params ConstantBuffer[] buffers)
        {
            mContext.Context.PixelShader.SetConstantBuffers(slot, buffers.Select(b => b.Native).ToArray());
        }

        public void SetVertexShader(byte[] code)
        {
            if (!mVertexShaderCache.ContainsKey(code))
            {
                var result = ShaderBytecode.FromStream(new MemoryStream(code));

                if (mVertexShader != null)
                    mVertexShader.Dispose();

                if (VertexShaderCode != null)
                    VertexShaderCode.Dispose();

                VertexShaderCode = result;
                mVertexShaderCache[code] = new VertexShader(mContext.Device, VertexShaderCode.Data);
            }
            mVertexShader = (VertexShader)mVertexShaderCache[code];
        }

        public void SetPixelShader(byte[] code)
        {
            if (!mPixelShaderCache.ContainsKey(code))
            {
                using (var result = ShaderBytecode.FromStream(new MemoryStream(code)))
                {
                    if (mPixelShader != null)
                        mPixelShader.Dispose();

                    mPixelShaderCache[code] = new PixelShader(mContext.Device, result.Data);
                }
            }
            mPixelShader = (PixelShader)mPixelShaderCache[code];
        }

        public void SetPixelShader(PixelShader ps)
        {
            mPixelShader = ps;
        }

        public void SetVertexShader(VertexShader vs)
        {
            mVertexShader = vs;
        }

        public void Bind()
        {
            if (mContext.Context.VertexShader.Get() != mVertexShader)
                mContext.Context.VertexShader.Set(mVertexShader);

            if (mContext.Context.PixelShader.Get() != mPixelShader)
                mContext.Context.PixelShader.Set(mPixelShader);
        }

        ~ShaderProgram()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (mVertexShader != null)
                mVertexShader.Dispose();

            if (mPixelShader != null)
                mPixelShader.Dispose();

            if (VertexShaderCode != null)
                VertexShaderCode.Dispose();

            mContext = null;
        }

        public virtual void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
