using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SharpDX;
using WoWEditor6.Editing;
using WoWEditor6.Graphics;
using WoWEditor6.Scene.Models;
using WoWEditor6.Scene.Models.M2;
using WoWEditor6.Scene.Models.WMO;
using WoWEditor6.Scene.Terrain;
using WoWEditor6.Scene.Texture;
using WoWEditor6.UI;
using WoWEditor6.Utils;

namespace WoWEditor6.Scene
{
    class WorldFrame
    {
        [StructLayout(LayoutKind.Sequential)]
        struct GlobalParamsBuffer
        {
            public Matrix matView;
            public Matrix matProj;
            public Vector4 viewport;

            public Color4 ambientLight;
            public Color4 diffuseLight;

            public Color4 fogColor;
            // x -> fogStart
            // y -> fotEnd
            // z -> farClip
            public Vector4 fogParams;

            public Vector4 mousePosition;
            public Vector4 eyePosition;

            // x -> innerRadius
            // y -> outerRadius
            // z -> brushTime
            public Vector4 brushParams;
        }

        private IModelInstance mSelectedInstance;
        private BoundingBoxInstance mSelectedBoundingBox;

        public static WorldFrame Instance { get; private set; }

        public MapManager MapManager { get; private set; }
        public WmoManager WmoManager { get; private set; }
        public M2Manager M2Manager { get; private set; }
        public WorldTextManager WorldTextManager { get; private set; }
        public BoundingBoxDrawManager BoundingBoxDrawManager { get; private set; }

        public bool LeftHandedCamera
        {
            get { return mMainCamera.LeftHanded; }
            set
            {
                if (mMainCamera.LeftHanded != value)
                    CamControl.InvertX = !CamControl.InvertX;

                mMainCamera.LeftHanded = value;
                mMainCamera.Update();
            }
        }

        private AppState mState;
        private readonly PerspectiveCamera mMainCamera = new PerspectiveCamera();
        public Camera ActiveCamera { get; private set; }

        private ConstantBuffer mGlobalBuffer;
        private GlobalParamsBuffer mGlobalBufferStore;
        private bool mGlobalBufferChanged;

        public CameraControl CamControl { get; private set; }
        private Point mLastCursorPosition;
        private RenderControl mWindow;

        public AppState State { get { return mState; } set { UpdateAppState(value); } }
        public GxContext GraphicsContext { get; private set; }
        public GraphicsDispatcher Dispatcher { get; private set; }

        public IntersectionParams LastMouseIntersection { get; private set; }

        public event Action<IntersectionParams, MouseEventArgs> OnWorldClicked;

        public bool HighlightModelsInBrush { get; set; }
        public bool HideWMO { get; set; } = false;
        public bool HideM2 { get; set; } = false;

        static WorldFrame()
        {
            Instance = new WorldFrame();
        }

        private WorldFrame()
        {
            MapManager = new MapManager();
            WmoManager = new WmoManager();
            M2Manager = new M2Manager();
            WorldTextManager = new WorldTextManager();
            BoundingBoxDrawManager = new BoundingBoxDrawManager();
            mState = AppState.Idle;

            // set the settings on creation
            HighlightModelsInBrush = Properties.Settings.Default.HighlightModelsInBrush;
            //this.UpdateDrawBrushOnModels = Properties.Settings.Default.UpdateDrawBrushOnModels; // todo: notimplemented!
        }

        public void ClearSelection()
        {
            if (mSelectedBoundingBox != null)
                BoundingBoxDrawManager.RemoveDrawableBox(mSelectedBoundingBox);

            ModelEditManager.Instance.SelectedModel = null;
        }

        public void UpdateBrush(float innerRadius, float outerRadius)
        {
            mGlobalBufferStore.brushParams.X = innerRadius;
            mGlobalBufferStore.brushParams.Y = outerRadius;
            mGlobalBufferChanged = true;
        }

        public void OnResize(int width, int height)
        {
            if (width == 0 || height == 0)
                return;

            mMainCamera.SetAspect((float) width / height);
        }

        public void UpdatePosition(Vector3 position)
        {
            lock (mGlobalBuffer)
            {
                mGlobalBufferStore.eyePosition = new Vector4(position, 1.0f);
                mGlobalBufferChanged = true;
            }
        }

        public void Initialize(RenderControl window, GxContext context)
        {
            GraphicsContext = context;
            mWindow = window;
            context.Resize += (w, h) => OnResize((int) w, (int) h);
            mGlobalBuffer = new ConstantBuffer(context);
            mGlobalBuffer = new ConstantBuffer(context);
            mGlobalBufferStore = new GlobalParamsBuffer
            {
                matView = Matrix.Identity,
                matProj = Matrix.Identity,
                viewport = Vector4.Zero,
                ambientLight = new Color4(0.5f, 0.5f, 0.5f, 1.0f),
                diffuseLight = new Color4(0.25f, 0.5f, 1.0f, 1.0f),
                fogColor = new Color4(0.25f, 0.5f, 1.0f, 1.0f),
                fogParams = new Vector4(500.0f, 900.0f, mMainCamera.FarClip, 0.0f),
                mousePosition = new Vector4(float.MaxValue),
                eyePosition = Vector4.Zero,
                brushParams = new Vector4(45.0f, 55.0f, 0.0f, 0.0f)
            };

            mGlobalBuffer.UpdateData(mGlobalBufferStore);

            Dispatcher = new GraphicsDispatcher();
            Dispatcher.AssignToThread();
            MapChunkRender.Initialize(context);
            MapAreaLowRender.Initialize(context);
            WmoGroupRender.Initialize(context);
            M2BatchRenderer.Initialize(context);
            M2SingleRenderer.Initialize(context);
            M2PortraitRenderer.Initialize(context);
            WorldText.Initialize(context);
            BoundingBoxDrawManager.Initialize();
            ChunkEditManager.Instance.Initialize();

            StaticAnimationThread.Instance.Initialize();

            WmoManager.Initialize();
            M2Manager.Initialize();
            WorldTextManager.Initialize();

            SetActiveCamera(mMainCamera);
            TextureManager.Instance.Initialize(context);

            MapManager.Initialize();

            mMainCamera.ViewChanged += ViewChanged;
            mMainCamera.ProjectionChanged += ProjectionChanged;

            OnResize(mWindow.Width, mWindow.Height);

            ViewChanged(mMainCamera, mMainCamera.View);
            ProjectionChanged(mMainCamera, mMainCamera.Projection);

            CamControl = new CameraControl(window);
            CamControl.PositionChanged += MapManager.UpdatePosition;

            if (!LeftHandedCamera)
                CamControl.InvertY = false;

            window.MouseDown += OnRenderWindowMouseDown;
        }

        public void OnEnterWorld(Vector3 position)
        {
            State = AppState.World;
            TimeManager.Instance.Reset();
            mMainCamera.SetParameters(position, position + Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY);
            UpdatePosition(position);
        }

        public void Shutdown()
        {
            TextureManager.Instance.Shutdown();
            MapManager.Shutdown();
            WmoManager.Shutdown();
            StaticAnimationThread.Instance.Shutdown();
            M2Manager.Shutdown();
            WorldTextManager.Shutdown();
        }

        public void OnFrame()
        {
            Dispatcher.ProcessFrame();

            CamControl.Update(ActiveCamera, State != AppState.World);

            EditManager.Instance.UpdateChanges();

            // do not move before mCamControl.Update to have the latest view/projection
            if (State == AppState.World)
            {
                UpdateCursorPosition();
                UpdateBrushTime(TimeManager.Instance.GetTime());
                CheckUpdateGlobalBuffer();
            }

            GraphicsContext.Context.VertexShader.SetConstantBuffer(0, mGlobalBuffer.Native);
            GraphicsContext.Context.PixelShader.SetConstantBuffer(0, mGlobalBuffer.Native);

            MapManager.OnFrame(ActiveCamera);

            if (!HideWMO)
                WmoManager.OnFrame(ActiveCamera);

            if (!HideM2)
                M2Manager.OnFrame(ActiveCamera);

            WorldTextManager.OnFrame(ActiveCamera);
            BoundingBoxDrawManager.OnFrame();
        }

        public void OnMouseWheel(int delta)
        {
            CamControl.HandleMouseWheel(delta);
        }

        public void UpdateMapAmbient(Color3 ambient)
        {
            lock (mGlobalBuffer)
            {
                mGlobalBufferStore.ambientLight = new Color4(ambient, 1.0f);
                mGlobalBufferChanged = true;
            }
        }

        public void UpdateMapDiffuse(Color3 diffuse)
        {
            lock (mGlobalBuffer)
            {
                mGlobalBufferStore.diffuseLight = new Color4(diffuse, 1.0f);
                mGlobalBufferChanged = true;
            }
        }

        public void UpdateFogParams(Color3 fogColor, float fogStart)
        {
            fogStart = Math.Min(fogStart, 899.0f);
            lock (mGlobalBuffer)
            {
                mGlobalBufferStore.fogColor = new Color4(fogColor, 1.0f);
                mGlobalBufferStore.fogParams = new Vector4(fogStart, 900.0f, mMainCamera.FarClip, 0.0f);
                mGlobalBufferChanged = true;
            }
        }

        public void UpdateSelectedBoundingBox()
        {
            if(mSelectedBoundingBox != null && mSelectedInstance != null)
                mSelectedBoundingBox.UpdateBoundingBox(mSelectedInstance.InstanceCorners);
        }

        private void UpdateBrushTime(TimeSpan frameTime)
        {
            var timeSecs = frameTime.TotalMilliseconds / 1000.0;
            mGlobalBufferStore.brushParams.Z = (float)timeSecs;
            mGlobalBufferChanged = true;
        }

        private void UpdateCursorPosition(bool forced = false)
        {
            var pos = mWindow.PointToClient(Cursor.Position);
            if (LastMouseIntersection == null || pos.X != mLastCursorPosition.X || pos.Y != mLastCursorPosition.Y || forced)
            {
                mLastCursorPosition = new Point(pos.X, pos.Y);
                LastMouseIntersection = new IntersectionParams(ActiveCamera.ViewInverse, ActiveCamera.ProjectionInverse,
                    new Vector2(mLastCursorPosition.X, mLastCursorPosition.Y));

                MapManager.Intersect(LastMouseIntersection);

                EditManager.Instance.MousePosition = LastMouseIntersection.TerrainPosition;
                mGlobalBufferStore.mousePosition = new Vector4(LastMouseIntersection.TerrainPosition, 0.0f);
                mGlobalBufferChanged = true;

                EditManager.Instance.IsTerrainHovered = LastMouseIntersection.TerrainHit;
                EditManager.Instance.MousePosition = LastMouseIntersection.TerrainPosition;

                ChunkEditManager.Instance.OnFrame();
            }
        }

        private void UpdateAppState(AppState newState)
        {
            mState = newState;
        }

        private void SetActiveCamera(Camera camera)
        {
            ActiveCamera = camera;
        }

        private void ViewChanged(Camera camera, Matrix matView)
        {
            if (camera != ActiveCamera)
                return;

            mGlobalBufferStore.matView = matView;
            mGlobalBufferChanged = true;

            M2Manager.ViewChanged();

            UpdateCursorPosition(true);
        }

        private void ProjectionChanged(Camera camera, Matrix matProj)
        {
            if (camera != ActiveCamera)
                return;

            var vp = GraphicsContext.Viewport;
            mGlobalBufferStore.matProj = matProj;
            mGlobalBufferStore.viewport = new Vector4(vp.Width, vp.Height, vp.MinDepth, vp.MaxDepth);

            var perspectiveCamera = camera as PerspectiveCamera;
            if (perspectiveCamera != null)
                mGlobalBufferStore.fogParams.Z = perspectiveCamera.FarClip;

            mGlobalBufferChanged = true;
            M2Manager.ViewChanged();
        }

        private void CheckUpdateGlobalBuffer()
        {
            if (mGlobalBufferChanged)
            {
                lock (mGlobalBuffer)
                {
                    mGlobalBuffer.UpdateData(mGlobalBufferStore);
                    mGlobalBufferChanged = false;
                }
            }
        }

        public bool RenderWindowContainsMouse()
        {
            return mWindow.ClientRectangle.Contains(mWindow.PointToClient(Cursor.Position));
        }

        private void OnRenderWindowMouseDown(object sender, MouseEventArgs mouseEventArgs)
        {
            var pos = mWindow.PointToClient(Cursor.Position);
            var intersection = new IntersectionParams(ActiveCamera.ViewInverse, ActiveCamera.ProjectionInverse,
                new Vector2(pos.X, pos.Y));

            MapManager.Intersect(intersection);
            M2Manager.Intersect(intersection);
            WmoManager.Intersect(intersection);

            if (mouseEventArgs.Button == MouseButtons.Left)
            {
                IModelInstance selected = null;
                if (intersection.M2Hit)
                    selected = intersection.M2Instance;
                else if (intersection.WmoHit)
                    selected = intersection.WmoInstance;

                if (selected != mSelectedInstance)
                {
                    if(mSelectedBoundingBox != null)
                        BoundingBoxDrawManager.RemoveDrawableBox(mSelectedBoundingBox);

                    mSelectedBoundingBox = null;

                    if (mSelectedInstance != null)
                        mSelectedInstance.DestroyModelNameplate();

                    if (ModelEditManager.Instance.IsCopying && selected != ModelEditManager.Instance.SelectedModel)
                    {
                        selected = ModelEditManager.Instance.SelectedModel;
                        mSelectedBoundingBox = BoundingBoxDrawManager.AddDrawableBox(selected.InstanceCorners);
                    }
                    else if (selected != null && selected.IsSpecial == false)
                    {
                        selected.CreateModelNameplate();
                        mSelectedBoundingBox = BoundingBoxDrawManager.AddDrawableBox(selected.InstanceCorners);
                        ModelEditManager.Instance.SelectedModel = selected;
                    }
                    else if (selected == null)
                    {
                        ModelEditManager.Instance.SelectedModel = null;
                    }

                    if(EditManager.Instance.CurrentMode != EditMode.Chunk)
                    {
                        mSelectedInstance = selected;
                        ModelSpawnManager.Instance.ClickedInstance = selected as M2RenderInstance;
                    }
                }
            }

            if (OnWorldClicked != null)
                OnWorldClicked(intersection, mouseEventArgs);
        }
    }
}
