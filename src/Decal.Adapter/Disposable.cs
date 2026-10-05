using System;

namespace Decal.Adapter
{
    /// <summary>
    /// The base of Decal's wrappers: disposable, and raising <see cref="Disposing"/> when it is.
    /// </summary>
    /// <remarks>
    /// Decal's wrappers held COM objects, and disposing them released those. These hold
    /// nothing unmanaged - they are views onto the host's world - so disposing one only marks
    /// it, but the shape is kept because plugins dispose them religiously and some listen for
    /// it. Decal's finalizer is not: with nothing to release it would only make every
    /// short-lived wrapper cost a trip through the finalizer queue.
    /// </remarks>
    public class DisposableByRefObject : MarshalByRefObject, IDisposable
    {
        private bool _disposed;

        internal DisposableByRefObject()
        {
        }

        public event EventHandler Disposing;

        internal bool IsDisposed => _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            Disposing?.Invoke(this, EventArgs.Empty);
            Dispose(true);
        }

        protected virtual void Dispose(bool userCalled)
        {
            _disposed = true;
        }

        /// <summary>Throws if the object has been disposed, as Decal's wrappers did on use after release.</summary>
        protected void EnforceDisposedOnce()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
        }
    }
}
