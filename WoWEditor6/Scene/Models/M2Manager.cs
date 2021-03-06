using System;
using System.Collections.Generic;
using System.Threading;
using SharpDX;
using WoWEditor6.IO.Files.Models;
using WoWEditor6.Scene.Models.M2;
using System.Windows.Forms;

namespace WoWEditor6.Scene.Models
{
    class M2Manager
    {
        private class InstanceSortComparer : IComparer<int>
        {
            private readonly IDictionary<int, M2RenderInstance> mInstances;

            public InstanceSortComparer(IDictionary<int, M2RenderInstance> dict)
            {
                mInstances = dict;
            }

            public int Compare(int first, int second)
            {
                M2RenderInstance renderA, renderB;
                if (mInstances.TryGetValue(first, out renderA) &&
                    mInstances.TryGetValue(second, out renderB))
                {
                    int compare = renderB.Depth.CompareTo(renderA.Depth);
                    if (compare != 0)
                        return compare;
                }
                return first.CompareTo(second);
            }
        }

        private readonly Dictionary<int, M2Renderer> mRenderer = new Dictionary<int, M2Renderer>();
        private readonly Dictionary<int, M2RenderInstance> mVisibleInstances = new Dictionary<int, M2RenderInstance>();
        private readonly Dictionary<int, M2RenderInstance> mNonBatchedInstances = new Dictionary<int, M2RenderInstance>();
        private readonly SortedDictionary<int, M2RenderInstance> mSortedInstances;

        private readonly object mAddLock = new object();
        private Thread mUnloadThread;
        private bool mIsRunning;
        private readonly List<M2Renderer> mUnloadList = new List<M2Renderer>();

        public static bool IsViewDirty { get; private set; }

        public M2Manager()
        {
            mSortedInstances = new SortedDictionary<int, M2RenderInstance>(
                new InstanceSortComparer(mVisibleInstances));
        }

        public void Initialize()
        {
            mIsRunning = true;
            mUnloadThread = new Thread(UnloadProc);
            mUnloadThread.Start();
        }

        public void Shutdown()
        {
            mIsRunning = false;
            mUnloadThread.Join();
        }

        public void Intersect(IntersectionParams parameters)
        {
            if (mVisibleInstances == null || mNonBatchedInstances == null || mSortedInstances == null)
                return;

            var globalRay = Picking.Build(ref parameters.ScreenPosition, ref parameters.InverseView,
                ref parameters.InverseProjection);

            var minDistance = float.MaxValue;
            M2RenderInstance selectedInstance = null;

            lock (mVisibleInstances)
            {
                foreach (var pair in mVisibleInstances)
                {
                    if (pair.Value.Uuid == Editing.ModelSpawnManager.M2InstanceUuid)
                        continue;

                    float dist;
                    if (pair.Value.Intersects(parameters, ref globalRay, out dist) && dist < minDistance)
                    {
                        minDistance = dist;
                        selectedInstance = pair.Value;
                    }
                }
            }

            lock (mNonBatchedInstances)
            {
                foreach (var pair in mNonBatchedInstances)
                {
                    float dist;
                    if (pair.Value.Intersects(parameters, ref globalRay, out dist) && dist < minDistance)
                    {
                        minDistance = dist;
                        selectedInstance = pair.Value;
                    }
                }
            }

            lock (mSortedInstances)
            {
                foreach (var pair in mSortedInstances)
                {
                    float dist;
                    if (pair.Value.Intersects(parameters, ref globalRay, out dist) && dist < minDistance)
                    {
                        minDistance = dist;
                        selectedInstance = pair.Value;
                    }
                }
            }

            if (selectedInstance != null)
            {
                parameters.M2Instance = selectedInstance;
                parameters.M2Model = selectedInstance.Model;
                parameters.M2Position = globalRay.Position + minDistance * globalRay.Direction;
                parameters.M2Distance = minDistance;
            }

            parameters.M2Hit = selectedInstance != null;
        }

        public void OnFrame(Camera camera)
        {
            if (WorldFrame.Instance.HighlightModelsInBrush)
            {
                var brushPosition = Editing.EditManager.Instance.MousePosition;
                var highlightRadius = Editing.EditManager.Instance.OuterRadius;
                UpdateBrushHighlighting(brushPosition, highlightRadius);
            }

            lock (mAddLock)
            {
                M2BatchRenderer.BeginDraw();
                // First draw all the instance batches
                foreach (var renderer in mRenderer.Values)
                    renderer.RenderBatch();

                M2SingleRenderer.BeginDraw();
                // Now draw those objects that need per instance animation
                foreach (var instance in mNonBatchedInstances.Values)
                    instance.Renderer.RenderSingleInstance(instance);

                // Then draw those that have alpha blending and need ordering
                foreach (var instance in mSortedInstances.Values)
                    instance.Renderer.RenderSingleInstance(instance);
            }

            IsViewDirty = false;
        }

        public void PushMapReferences(M2Instance[] instances)
        {
            foreach (var instance in instances)
            {
                if (instance == null || instance.RenderInstance == null || instance.RenderInstance.IsUpdated)
                    continue;

                lock (mAddLock)
                {
                    M2Renderer renderer;
                    if (!mRenderer.TryGetValue(instance.Hash, out renderer))
                        continue;

                    renderer.PushMapReference(instance);
                    mVisibleInstances.Add(instance.Uuid, instance.RenderInstance);

                    var model = renderer.Model;
                    if (model.HasBlendPass)
                    {
                        // The model has an alpha pass and therefore needs to be ordered by depth
                        mSortedInstances.Add(instance.Uuid, instance.RenderInstance);
                    }
                    else if (model.NeedsPerInstanceAnimation)
                    {
                        // The model needs per instance animation and therefore cannot be batched
                        mNonBatchedInstances.Add(instance.Uuid, instance.RenderInstance);
                    }
                }
            }
        }

        private void UpdateBrushHighlighting(Vector3 brushPosition, float radius)
        {
            lock (mAddLock)
            {
                foreach (var instance in mVisibleInstances.Values)
                    instance.UpdateBrushHighlighting(brushPosition, radius);
            }
        }

        public void ViewChanged()
        {
            IsViewDirty = true;
            lock(mAddLock)
            {
                mSortedInstances.Clear();
                mNonBatchedInstances.Clear();
                mVisibleInstances.Clear();

                foreach (var renderer in mRenderer.Values)
                    renderer.ViewChanged();
            }
        }

        public void RemoveInstance(string model, int uuid)
        {
            try
            {
                var hash = model.ToUpperInvariant().GetHashCode();
                RemoveInstance(hash, uuid);
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public void RemoveInstance(int hash, int uuid)
        {
            if (mRenderer == null || mAddLock == null || mSortedInstances == null || mNonBatchedInstances == null ||
                mVisibleInstances == null)
                return;

            lock (mRenderer)
            {
                lock (mAddLock)
                {
                    mSortedInstances.Remove(uuid);
                    mNonBatchedInstances.Remove(uuid);
                    mVisibleInstances.Remove(uuid);
                }

                M2Renderer renderer;
                if (!mRenderer.TryGetValue(hash, out renderer))
                    return;

                if (renderer.RemoveInstance(uuid))
                {
                    lock (mAddLock)
                        mRenderer.Remove(hash);

                    lock (mUnloadList)
                        mUnloadList.Add(renderer);
                }
            }
        }

        public M2RenderInstance AddInstance(string model, int uuid, Vector3 position, Vector3 rotation, Vector3 scaling)
        {
            var hash = model.ToUpperInvariant().GetHashCode();
            lock(mRenderer)
            {
                M2Renderer renderer;
                if (mRenderer.TryGetValue(hash, out renderer))
                    return renderer.AddInstance(uuid, position, rotation, scaling);

                var file = LoadModel(model);
                if (file == null)
                    return null;

                var render = new M2Renderer(file);
                lock (mAddLock)
                    mRenderer.Add(hash, render);

                return render.AddInstance(uuid, position, rotation, scaling);
            }
        }

        private void UnloadProc()
        {
            while(mIsRunning)
            {
                M2Renderer element = null;
                lock(mUnloadList)
                {
                    if(mUnloadList.Count > 0)
                    {
                        element = mUnloadList[0];
                        mUnloadList.RemoveAt(0);
                    }
                }

                if (element != null)
                    element.Dispose();

                if (element == null)
                    Thread.Sleep(200);
            }
        }

        private static M2File LoadModel(string fileName)
        {
            var file = ModelFactory.Instance.CreateM2(fileName);
            try
            {
                return file.Load() == false ? null : file;
            }
            catch(Exception)
            {
                return null;
            }
        }
    }
}
